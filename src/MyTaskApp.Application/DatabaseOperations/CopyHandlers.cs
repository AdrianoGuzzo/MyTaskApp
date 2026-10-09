using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>O resultado do [Validar]: o que a política diz, o que o ambiente diz, e o que a confirmação vai pedir.</summary>
public sealed record DatabaseCopyValidation(
    SecurityDecision Decision,
    IReadOnlyList<CheckResult> Checks,
    string SourceName,
    string DestinationName,
    string? AnonymizationProfileName,
    DatabaseEnvironment SourceEnvironment,
    DatabaseEnvironment DestinationEnvironment,
    bool RequiresProductionConfirmation,
    bool RequiresTypedConfirmation,
    string ConfirmationText)
{
    public bool CanRun => Decision.IsAllowed && Checks.All(check => check.Outcome != CheckOutcome.Fail);
}

/// <summary>A simulação completa de uma cópia, sem processo nenhum: política, ferramentas, servidores e Anonymizer.</summary>
public sealed record ValidateDatabaseCopy(DatabaseCopyRequest Request);

public sealed class ValidateDatabaseCopyHandler(
    DatabaseCopyPlanner planner,
    IDatabaseSecurityPolicy policy,
    IPostgresToolLocator locator,
    IPostgresServerInspector inspector,
    IPostgresAnonymizationService anonymization)
{
    public async Task<DatabaseCopyValidation> HandleAsync(ValidateDatabaseCopy query, CancellationToken cancellationToken = default)
    {
        var plan = await planner.PlanAsync(query.Request, cancellationToken);
        var decision = policy.Evaluate(plan.ToPolicyRequest());
        var checks = new List<CheckResult>();

        if (decision.IsAllowed)
        {
            var tools = await locator.DetectAsync(refresh: false, cancellationToken);
            var source = await inspector.TestAsync(plan.Source, cancellationToken: cancellationToken);
            var destination = await inspector.TestAsync(DatabaseCopyRun.Reachable(plan), cancellationToken: cancellationToken);

            checks.Add(Connection("Origem", plan.Source, source));
            checks.Add(Connection("Destino", plan.Destination, destination));
            checks.AddRange(PostgresCompatibility.Evaluate(tools, source.ServerVersion, destination.ServerVersion, plan.Anonymizes));

            if (plan is { Anonymizes: true, AnonymizationProfile: { } profile, MaskedConnection: { } masked } && source.Connected)
            {
                var validation = await anonymization.ValidateAsync(profile, masked, cancellationToken);
                checks.AddRange(validation.Problems().Select(problem =>
                    new CheckResult("Anonymizer", "Perfil", CheckOutcome.Fail, problem)));

                if (validation.UncoveredCandidates.Count > 0)
                {
                    checks.Add(new CheckResult("Anonymizer", "Colunas sem regra", CheckOutcome.Warning,
                        $"{validation.UncoveredCandidates.Count} coluna(s) candidata(s) sem regra, {validation.UncoveredHigh} de alta probabilidade."));
                }

                decision = policy.Evaluate(plan.ToPolicyRequest(DatabaseCopyRun.ServerFacts(plan, validation)));
            }
        }

        return new DatabaseCopyValidation(
            decision,
            checks,
            plan.Source.Name,
            plan.Destination.Name,
            plan.AnonymizationProfile?.Name,
            plan.Source.Environment,
            plan.Destination.Environment,
            plan.Source.IsProtected,
            plan.SourceRules.RequiresTypedConfirmation,
            plan.Source.Database);
    }

    private static CheckResult Connection(string side, DatabaseConnectionSnapshot connection, ServerDiagnostics result) =>
        result.Connected
            ? new CheckResult("Conexão", side, CheckOutcome.Pass, $"{connection.Name}: PostgreSQL {result.ServerVersion}")
            : new CheckResult("Conexão", side, CheckOutcome.Fail, $"{connection.Name}: {result.Error}");
}

/// <summary>
/// Executar a cópia. <see cref="ProductionConfirmed"/> e
/// <see cref="TypedConfirmation"/> são conferidos aqui, e não só na tela:
/// pular a janela de confirmação não pula a confirmação.
/// </summary>
public sealed record RunDatabaseCopy(DatabaseCopyRequest Request, bool ProductionConfirmed, string? TypedConfirmation = null);

/// <summary>Como terminou: o estado de cada etapa, a verificação e o dump anônimo mantido, se foi.</summary>
public sealed record DatabaseCopyResult(
    DatabaseOperationStatus Status,
    Guid? AuditId,
    IReadOnlyDictionary<DatabaseCopyStep, CommandStepState> Steps,
    VerificationReport? Verification,
    string? Error,
    string? KeptArtifactPath)
{
    public bool Succeeded => Status == DatabaseOperationStatus.Succeeded;
}

/// <summary>
/// O fluxo Copiar + Anonimizar (ADR-056), de ponta a ponta: validar origem,
/// destino, política e Anonymizer; o canário; o dump anônimo num diretório
/// isolado; conferir o arquivo; preparar o destino; restaurar; verificar;
/// auditar; limpar — a limpeza sempre, com sucesso, falha ou cancelamento.
/// </summary>
public sealed class RunDatabaseCopyHandler(
    DatabaseCopyPlanner planner,
    IDatabaseSecurityPolicy policy,
    IPostgresToolLocator locator,
    IPostgresServerInspector inspector,
    IPostgresAnonymizationService anonymization,
    IPostgresDumpService dumps,
    IPostgresRestoreService restores,
    IDatabaseOperationWorkspaceFactory workspaces,
    IDatabaseOperationAuditLog audit,
    IUnitOfWork unitOfWork,
    DatabaseOperationGate gate,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<RunDatabaseCopyHandler> logger)
{
    public async Task<DatabaseCopyResult> HandleAsync(
        RunDatabaseCopy command,
        IProgress<DatabaseCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await planner.PlanAsync(command.Request, cancellationToken);
        var host = Environment.MachineName;
        var user = currentUser.Name ?? Environment.UserName;

        var refusal = ConfirmationRefusal(plan, command) is { } missing
            ? new SecurityDecision([missing])
            : policy.Evaluate(plan.ToPolicyRequest());

        if (!refusal.IsAllowed)
        {
            var blocked = DatabaseOperationAudit.Blocked(
                plan.Request.Operation, plan.Source, plan.Destination, plan.Request.CopyProfileId, plan.CopyProfileName,
                host, user, refusal.Describe(), timeProvider.GetUtcNow());
            await audit.RecordAsync(blocked, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogWarning("DatabaseCopyBlocked {AuditId} {Codes}", blocked.Id, string.Join(",", refusal.Violations.Select(violation => violation.Code)));
            throw new DatabaseSecurityException(refusal);
        }

        var entry = DatabaseOperationAudit.Start(
            plan.Request.Operation, plan.Source, plan.Destination, plan.Request.CopyProfileId, plan.CopyProfileName,
            host, user, timeProvider.GetUtcNow());

        using var turn = gate.Enter(entry.Id);

        // Gravada antes do primeiro processo: uma queda deixa rastro.
        await audit.RecordAsync(entry, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var run = new DatabaseCopyRun(
            plan, entry, policy, locator, inspector, anonymization, dumps, restores, workspaces, timeProvider, logger, progress);

        var result = await run.ExecuteAsync(cancellationToken);

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("DatabaseCopyFinished {AuditId} {Status}", entry.Id, result.Status);
        return result;
    }

    /// <summary>Produção pede confirmação explícita; produção crítica, o nome do banco digitado.</summary>
    private static SecurityViolation? ConfirmationRefusal(DatabaseCopyPlan plan, RunDatabaseCopy command)
    {
        if (!plan.Source.IsProtected)
        {
            return null;
        }

        if (!command.ProductionConfirmed)
        {
            return new SecurityViolation(
                SecurityViolationCode.ConfirmationRequired,
                "A cópia de dados de produção precisa ser confirmada explicitamente.");
        }

        if (plan.SourceRules.RequiresTypedConfirmation
            && !string.Equals(command.TypedConfirmation?.Trim(), plan.Source.Database, StringComparison.Ordinal))
        {
            return new SecurityViolation(
                SecurityViolationCode.ConfirmationRequired,
                $"Produção crítica: digite o nome do banco ({plan.Source.Database}) para confirmar.");
        }

        return null;
    }
}

/// <summary>Uma execução, com o estado que atravessa as etapas. Interno ao handler.</summary>
internal sealed class DatabaseCopyRun(
    DatabaseCopyPlan plan,
    DatabaseOperationAudit entry,
    IDatabaseSecurityPolicy policy,
    IPostgresToolLocator locator,
    IPostgresServerInspector inspector,
    IPostgresAnonymizationService anonymization,
    IPostgresDumpService dumps,
    IPostgresRestoreService restores,
    IDatabaseOperationWorkspaceFactory workspaces,
    TimeProvider timeProvider,
    ILogger logger,
    IProgress<DatabaseCopyProgress>? progress)
{
    /// <summary>Acima disto (estimado), a contagem de linhas fica com a estimativa do catálogo.</summary>
    internal const long ExactCountLimit = 1_000_000;

    /// <summary>Folga sobre o tamanho do banco: o dump comprime, mas índices e TOAST variam.</summary>
    internal const double DiskMargin = 1.2;

    private readonly Dictionary<DatabaseCopyStep, CommandStepState> _states =
        DatabaseCopySteps.All.ToDictionary(step => step, _ => CommandStepState.Waiting);

    private CopyProgressEstimator _estimator = new([]);

    private DatabaseCopyStep _current = DatabaseCopyStep.ValidateSource;

    private IDatabaseOperationWorkspace? _workspace;

    private IReadOnlyList<TableInfo> _tables = [];

    private PostgresClientTools? _tools;

    private ServerDiagnostics? _sourceServer;

    private ServerDiagnostics? _destinationServer;

    private AnonymizationFacts _facts = plan.RegisteredFacts;

    private VerificationReport? _verification;

    private string ArchiveDirectory => Path.Combine(
        plan.Anonymizes ? _workspace!.AnonymizedDirectory : _workspace!.DumpDirectory,
        "archive");

    /// <summary>Quando o destino vai ser recriado, o banco dele pode nem existir ainda: o teste vai ao banco de manutenção.</summary>
    public static DatabaseConnectionSnapshot Reachable(DatabaseCopyPlan plan) =>
        plan.Request.Options.RecreateDestination ? plan.Destination with { Database = "postgres" } : plan.Destination;

    public static AnonymizationFacts ServerFacts(DatabaseCopyPlan plan, AnonymizationValidation validation) =>
        plan.RegisteredFacts.WithServer(
            installed: validation.Status.Installed && validation.Status.IsSupportedVersion,
            roleMasked: validation.Status.CurrentRoleMasked,
            transparentOn: validation.Status.TransparentMaskingOn,
            uncoveredHigh: validation.UncoveredHigh);

    public async Task<DatabaseCopyResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        DatabaseOperationStatus status;
        string? error = null;
        string? kept = null;

        try
        {
            await ValidateSourceAsync(cancellationToken);
            await ValidateDestinationAsync(cancellationToken);
            await ValidatePermissionsAsync(cancellationToken);
            await ValidateAnonymizerAsync(cancellationToken);
            await DumpAsync(cancellationToken);
            await CheckArtifactAsync(cancellationToken);
            await PrepareDestinationAsync(cancellationToken);
            await RestoreAsync(cancellationToken);
            await VerifyAsync(cancellationToken);

            if (_verification is { Succeeded: false })
            {
                status = DatabaseOperationStatus.Failed;
                error = "A verificação depois do restore falhou.";
            }
            else
            {
                status = DatabaseOperationStatus.Succeeded;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = DatabaseOperationStatus.Canceled;
            error = "Cancelada. O destino pode ter ficado incompleto.";
            Finish(CommandStepState.Canceled, error);
        }
        catch (DomainException exception)
        {
            status = DatabaseOperationStatus.Failed;
            error = exception.Message;
            Finish(CommandStepState.Failed, error);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseCopyFailed {AuditId} {Step}", entry.Id, _current);
            status = DatabaseOperationStatus.Failed;
            error = "A cópia falhou por um erro inesperado; os detalhes estão no log do app.";
            Finish(CommandStepState.Failed, error);
        }
        finally
        {
            kept = await CleanupAsync(entry.Id);
        }

        var at = timeProvider.GetUtcNow();
        entry.RecordSummary(_verification?.Summarize());

        switch (status)
        {
            case DatabaseOperationStatus.Succeeded:
                entry.Succeed(at);
                break;

            case DatabaseOperationStatus.Canceled:
                entry.Cancel(at);
                break;

            default:
                entry.Fail(error, at);
                break;
        }

        await WriteMetadataAsync(status, kept);
        Report(DatabaseCopyStep.Cleanup, CommandStepState.Succeeded, _estimator.Complete(), status == DatabaseOperationStatus.Succeeded ? "Concluído." : error);

        return new DatabaseCopyResult(status, entry.Id, new Dictionary<DatabaseCopyStep, CommandStepState>(_states), _verification, error, kept);
    }

    private async Task ValidateSourceAsync(CancellationToken cancellationToken)
    {
        Begin(DatabaseCopyStep.ValidateSource, $"Conectando a {plan.Source.Name}…");

        var source = await inspector.TestAsync(plan.Source, cancellationToken: cancellationToken);

        if (!source.Connected)
        {
            throw new DomainException($"Não foi possível conectar à origem ({plan.Source.Name}): {source.Error}");
        }

        entry.RecordServerVersions(source.ServerVersionText ?? source.ServerVersion?.ToString(), null);
        _tables = await inspector.ListTablesAsync(plan.Source, cancellationToken);
        _estimator = new CopyProgressEstimator(_tables);
        _sourceServer = source;

        Done(DatabaseCopyStep.ValidateSource, $"PostgreSQL {source.ServerVersion}, {_tables.Count} tabela(s).");
    }

    private async Task ValidateDestinationAsync(CancellationToken cancellationToken)
    {
        Begin(DatabaseCopyStep.ValidateDestination, $"Conectando a {plan.Destination.Name}…");

        var destination = await inspector.TestAsync(Reachable(plan), cancellationToken: cancellationToken);

        if (!destination.Connected)
        {
            throw new DomainException($"Não foi possível conectar ao destino ({plan.Destination.Name}): {destination.Error}");
        }

        _destinationServer = destination;
        entry.RecordServerVersions(null, destination.ServerVersionText ?? destination.ServerVersion?.ToString());
        Done(DatabaseCopyStep.ValidateDestination, $"PostgreSQL {destination.ServerVersion}.");
    }

    private async Task ValidatePermissionsAsync(CancellationToken cancellationToken)
    {
        Begin(DatabaseCopyStep.ValidatePermissions, "Política de segurança e ferramentas…");

        policy.Demand(plan.ToPolicyRequest(_facts));

        _tools = await locator.DetectAsync(refresh: false, cancellationToken);
        entry.RecordTools(_tools.Describe(PostgresTool.PgDump, PostgresTool.PgRestore, PostgresTool.CreateDb, PostgresTool.DropDb));

        if (plan.Request.Options.RecreateDestination
            && (!_tools.Find(PostgresTool.CreateDb).Found || !_tools.Find(PostgresTool.DropDb).Found))
        {
            throw new DomainException("Recriar o destino precisa de createdb e dropdb instalados.");
        }

        var failures = PostgresCompatibility
            .Evaluate(_tools, _sourceServer?.ServerVersion, _destinationServer?.ServerVersion, plan.Anonymizes)
            .Where(check => check.Outcome == CheckOutcome.Fail)
            .ToList();

        if (failures.Count > 0)
        {
            throw new DomainException(string.Join(" ", failures.Select(check => check.Detail)));
        }

        Done(DatabaseCopyStep.ValidatePermissions, _tools.Describe(PostgresTool.PgDump, PostgresTool.PgRestore));
    }

    private async Task ValidateAnonymizerAsync(CancellationToken cancellationToken)
    {
        if (!plan.Anonymizes)
        {
            Skip(DatabaseCopyStep.ValidateAnonymizer, "Cópia sem anonimização.");
            return;
        }

        Begin(DatabaseCopyStep.ValidateAnonymizer, "Conferindo extensão, role mascarada e regras…");

        var profile = plan.AnonymizationProfile!;
        var masked = plan.MaskedConnection!;
        var validation = await anonymization.ValidateAsync(profile, masked, cancellationToken);

        if (!validation.IsValid)
        {
            throw new DomainException(string.Join(" ", validation.Problems()));
        }

        _facts = ServerFacts(plan, validation);
        policy.Demand(plan.ToPolicyRequest(_facts));

        Report(DatabaseCopyStep.ValidateAnonymizer, CommandStepState.Running, _estimator.At(DatabaseCopyStep.ValidateAnonymizer, 0.5),
            "Lendo amostras pelas duas conexões para confirmar a máscara…");

        if (!await anonymization.IsMaskingActiveAsync(profile, plan.Source, masked, cancellationToken))
        {
            throw new DomainException(
                "A conexão mascarada devolveu os mesmos valores da origem: a máscara não está ativa para essa role. Nenhum dump foi feito.");
        }

        entry.RecordAnonymization(profile.Name, validation.MaskedColumns);
        Done(DatabaseCopyStep.ValidateAnonymizer, $"{validation.MaskedColumns} coluna(s) mascarada(s) no servidor.");
    }

    private async Task DumpAsync(CancellationToken cancellationToken)
    {
        Begin(DatabaseCopyStep.Dump, "Preparando diretório temporário…");

        _workspace = await workspaces.CreateAsync(entry.Id, timeProvider.GetUtcNow(), cancellationToken);

        var needed = (long)(_tables.Sum(table => table.Bytes) * DiskMargin);

        if (_workspace.AvailableBytes() is { } available && available < needed)
        {
            throw new DomainException(
                $"Espaço insuficiente em {_workspace.Root}: o dump pode precisar de {PostgresEnvironmentDiagnostics.FormatBytes(needed)}.");
        }

        var options = plan.Request.Options;
        var connection = plan.Anonymizes ? plan.MaskedConnection! : plan.Source;
        var request = new PgDumpRequest(
            connection,
            ArchiveDirectory,
            plan.Anonymizes,
            options.IncludeSchema,
            options.IncludeData,
            Jobs(options),
            plan.ProtectedEndpoints);

        var events = Forward(DatabaseCopyStep.Dump);

        PgToolRun run;

        if (plan.Anonymizes)
        {
            run = await anonymization.CreateAnonymousDumpAsync(plan.ToPolicyRequest(_facts), request, events, cancellationToken);
        }
        else
        {
            policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.Dump, plan.Source, ProtectedEndpoints: plan.ProtectedEndpoints));
            run = await dumps.DumpAsync(request, events, cancellationToken);
        }

        EnsureSucceeded("pg_dump", run);

        var size = _workspace.SizeOf(ArchiveDirectory);
        entry.RecordSizes(plan.Anonymizes ? null : size, plan.Anonymizes ? size : null);
        Done(DatabaseCopyStep.Dump, $"{PostgresEnvironmentDiagnostics.FormatBytes(size)} em {Seconds(run.Duration)}.");
    }

    private async Task CheckArtifactAsync(CancellationToken cancellationToken)
    {
        Begin(DatabaseCopyStep.CheckArtifact, "Lendo o índice do dump…");

        var summary = await dumps.ListArchiveAsync(ArchiveDirectory, cancellationToken);

        if (plan.Anonymizes && (summary.SecurityLabelEntries > 0 || summary.HasAnonExtension))
        {
            throw new DomainException("O dump trouxe regras ou a extensão do anon; ele não será restaurado.");
        }

        if (plan.Request.Options.IncludeData && summary.TableDataEntries == 0 && _tables.Count > 0)
        {
            throw new DomainException("O dump saiu sem dados de tabela nenhuma.");
        }

        _estimator.ExpectedPostDataItems = summary.IndexEntries + summary.ConstraintEntries;
        Done(DatabaseCopyStep.CheckArtifact, plan.Anonymizes
            ? $"Dump anônimo com {summary.TableDataEntries} tabela(s) de dados, sem regras nem extensão."
            : $"{summary.TableDataEntries} tabela(s) de dados.");
    }

    private async Task PrepareDestinationAsync(CancellationToken cancellationToken)
    {
        if (!plan.Request.Options.RecreateDestination)
        {
            Skip(DatabaseCopyStep.PrepareDestination, "O banco de destino é mantido.");
            return;
        }

        Begin(DatabaseCopyStep.PrepareDestination, $"Recriando {plan.Destination.Database}…");

        policy.Demand(new DatabaseOperationRequest(
            DatabaseOperationType.DropDatabase, Destination: plan.Destination, ProtectedEndpoints: plan.ProtectedEndpoints));
        EnsureSucceeded("dropdb", await restores.DropDatabaseAsync(
            plan.Destination,
            plan.ProtectedEndpoints,
            PostgresCompatibility.SupportsForceDrop(_destinationServer?.ServerVersion),
            cancellationToken));

        policy.Demand(new DatabaseOperationRequest(
            DatabaseOperationType.CreateDatabase, Destination: plan.Destination, ProtectedEndpoints: plan.ProtectedEndpoints));
        EnsureSucceeded("createdb", await restores.CreateDatabaseAsync(plan.Destination, plan.ProtectedEndpoints, cancellationToken));

        Done(DatabaseCopyStep.PrepareDestination, $"{plan.Destination.Database} recriado vazio.");
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        Begin(DatabaseCopyStep.Restore, $"Restaurando em {plan.Destination.Name}…");

        policy.Demand(new DatabaseOperationRequest(
            DatabaseOperationType.Restore, Destination: plan.Destination, ProtectedEndpoints: plan.ProtectedEndpoints));

        var options = plan.Request.Options;
        var run = await restores.RestoreAsync(
            new PgRestoreRequest(plan.Destination, ArchiveDirectory, options.IncludeSchema, options.IncludeData, Jobs(options), plan.ProtectedEndpoints),
            Forward(DatabaseCopyStep.Restore),
            cancellationToken);

        EnsureSucceeded("pg_restore", run);
        Done(DatabaseCopyStep.Restore, $"Concluído em {Seconds(run.Duration)}.");
    }

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        if (!plan.Request.Options.VerifyAfterRestore)
        {
            Skip(DatabaseCopyStep.Verify, "Verificação desligada no perfil.");
            return;
        }

        Begin(DatabaseCopyStep.Verify, "Comparando origem e destino…");
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.Verify, plan.Source, plan.Destination));

        var checks = new List<CheckResult>();
        var source = await inspector.GetStructureAsync(plan.Source, cancellationToken);
        var destination = await inspector.GetStructureAsync(plan.Destination, cancellationToken);
        checks.AddRange(VerificationEvaluator.CompareStructure(source, destination));

        if (plan.Request.Options.IncludeData)
        {
            var tables = _tables.Select(table => table.QualifiedName).ToList();
            var before = await inspector.CountRowsAsync(plan.Source, tables, ExactCountLimit, cancellationToken);
            var after = await inspector.CountRowsAsync(plan.Destination, tables, ExactCountLimit, cancellationToken);
            checks.Add(VerificationEvaluator.CompareRows(before, after));
            entry.RecordRows(after.Where(count => !count.IsEstimate).Sum(count => count.Rows));

            if (plan is { Anonymizes: true, AnonymizationProfile: { } profile })
            {
                checks.AddRange(await anonymization.VerifyResultAsync(profile, plan.Source, plan.Destination, cancellationToken));
            }
        }

        _verification = new VerificationReport(checks);

        if (_verification.Succeeded)
        {
            Done(DatabaseCopyStep.Verify, _verification.Result);
        }
        else
        {
            Report(DatabaseCopyStep.Verify, CommandStepState.Failed, _estimator.At(DatabaseCopyStep.Verify, 1), _verification.Result);
            _states[DatabaseCopyStep.Verify] = CommandStepState.Failed;
        }
    }

    /// <summary>Sempre: com sucesso, falha ou cancelamento. Sem token, para o cancelamento não impedir a limpeza.</summary>
    private async Task<string?> CleanupAsync(Guid operationId)
    {
        if (_workspace is null)
        {
            return null;
        }

        try
        {
            Report(DatabaseCopyStep.Cleanup, CommandStepState.Running, _estimator.At(DatabaseCopyStep.Cleanup, 0), "Apagando o que é sensível…");

            var keep = plan.Request.Options.KeepAnonymizedArtifact
                && plan.Anonymizes
                && plan.SourceRules.AllowsKeepingArtifact
                && _states.Values.All(state => state is not (CommandStepState.Failed or CommandStepState.Canceled));

            return await _workspace.CleanupAsync(keep, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Um antivírus segurando o arquivo: a varredura da próxima abertura termina o serviço.
            logger.LogWarning("DatabaseWorkspaceCleanupFailed {AuditId} {Error}", operationId, exception.GetType().Name);
            return null;
        }
    }

    private async Task WriteMetadataAsync(DatabaseOperationStatus status, string? kept)
    {
        if (_workspace is null)
        {
            return;
        }

        try
        {
            await _workspace.WriteMetadataAsync(new DatabaseOperationMetadata(
                entry.Id,
                entry.OperationType,
                status,
                entry.StartedAt,
                entry.CompletedAt,
                entry.SourceConnectionName,
                entry.DestinationConnectionName,
                entry.AnonymizationProfile,
                entry.ToolVersions,
                entry.AnonymousDumpSize,
                entry.MaskedColumnsCount,
                _states.Select(pair => $"{DatabaseCopySteps.Label(pair.Key, plan.Anonymizes)}: {pair.Value}").ToList(),
                kept is not null), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("DatabaseWorkspaceMetadataFailed {AuditId} {Error}", entry.Id, exception.GetType().Name);
        }
    }

    private ToolEvents Forward(DatabaseCopyStep step) => new(toolEvent =>
        Report(step, CommandStepState.Running, _estimator.At(step, _estimator.Observe(step, toolEvent)), null, toolEvent.Line));

    private void EnsureSucceeded(string tool, PgToolRun run)
    {
        if (run.Succeeded)
        {
            return;
        }

        var reason = run.TimedOut
            ? "o tempo limite estourou"
            : $"terminou com código {run.ExitCode.ToString(CultureInfo.InvariantCulture)}";
        var tail = string.IsNullOrWhiteSpace(run.ErrorTail) ? string.Empty : $": {SensitiveText.Mask(run.ErrorTail)}";
        throw new DomainException($"{tool} {reason}{tail}");
    }

    private static int Jobs(DatabaseCopyOptions options) =>
        options.IncludeData ? Math.Clamp(Environment.ProcessorCount, 1, 4) : 1;

    private static string Seconds(TimeSpan duration) =>
        string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{duration.TotalSeconds:0.#} s");

    private void Begin(DatabaseCopyStep step, string detail)
    {
        _current = step;
        _states[step] = CommandStepState.Running;
        Report(step, CommandStepState.Running, _estimator.At(step, 0), detail);
    }

    private void Done(DatabaseCopyStep step, string? detail)
    {
        _states[step] = CommandStepState.Succeeded;
        Report(step, CommandStepState.Succeeded, _estimator.At(step, 1), detail);
    }

    private void Skip(DatabaseCopyStep step, string detail)
    {
        _current = step;
        _states[step] = CommandStepState.NotRun;
        Report(step, CommandStepState.NotRun, _estimator.At(step, 1), detail);
    }

    /// <summary>A etapa atual leva o estado final; as que não chegaram a vez ficam "não executada".</summary>
    private void Finish(CommandStepState state, string? detail)
    {
        _states[_current] = state;
        Report(_current, state, _estimator.At(_current, 0), detail);

        foreach (var step in DatabaseCopySteps.All.Where(step => step > _current && step != DatabaseCopyStep.Cleanup))
        {
            if (_states[step] == CommandStepState.Waiting)
            {
                _states[step] = CommandStepState.NotRun;
                Report(step, CommandStepState.NotRun, _estimator.At(_current, 0));
            }
        }
    }

    private void Report(DatabaseCopyStep step, CommandStepState state, double percent, string? detail = null, CommandOutputLine? line = null)
    {
        if (step == DatabaseCopyStep.Cleanup)
        {
            _states[step] = state;
        }

        progress?.Report(new DatabaseCopyProgress(step, state, percent, detail, line));
    }

    /// <summary>Repassa os eventos da ferramenta na mesma thread, sem o contexto de sincronização do <see cref="Progress{T}"/>.</summary>
    private sealed class ToolEvents(Action<PgToolEvent> report) : IProgress<PgToolEvent>
    {
        public void Report(PgToolEvent value) => report(value);
    }
}
