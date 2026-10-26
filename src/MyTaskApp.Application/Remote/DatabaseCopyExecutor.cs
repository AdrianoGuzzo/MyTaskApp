using System.Text.Json;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using CopyRequest = MyTaskApp.Application.DatabaseOperations.DatabaseCopyRequest;
using RemoteCopyRequest = MyTaskApp.Remote.Contracts.DatabaseCopyRequest;

namespace MyTaskApp.Application.Remote;

/// <summary>A cópia pedida de longe, já traduzida no pedido da janela Bancos de Dados.</summary>
/// <param name="DevelopmentId">O ambiente da tarefa que recebe o banco (ADR-060), se for uma.</param>
public sealed record RemoteCopyTarget(CopyRequest Request, Guid? TaskId, Guid? DevelopmentId);

/// <summary>
/// Confere um <c>database.copy</c> antes de tocar em servidor (4.4, item 6; ADR-090):
/// as duas conexões existem, estão ligadas e foram liberadas para cópias de
/// longe nesta máquina; o perfil, se veio, é dessas conexões; o ambiente, se
/// veio, é da tarefa e está pronto. Daqui sai o mesmo pedido que a tela faz.
/// </summary>
public sealed class RemoteDatabaseCopyPlanner(
    IDatabaseConnectionRepository connections,
    IDatabaseCopyProfileRepository copyProfiles,
    IAnonymizationProfileRepository anonymizationProfiles,
    ITaskItemRepository tasks)
{
    public async Task<RemoteCopyTarget> PlanAsync(Guid? taskId, RemoteCopyRequest request, CancellationToken cancellationToken = default)
    {
        var source = await AllowedAsync(request.SourceConnectionId, "de origem", cancellationToken);
        var destination = await AllowedAsync(request.TargetConnectionId, "de destino", cancellationToken);

        var options = DatabaseCopyOptions.Default;
        var anonymization = request.AnonymizationProfileId;
        var sourceDatabase = request.SourceDatabase;

        if (request.CopyProfileId is { } copyProfileId)
        {
            var profile = await copyProfiles.FindByIdAsync(copyProfileId, cancellationToken)
                ?? throw new DomainException("O perfil de cópia não existe nesta máquina.");

            if (profile.SourceConnectionId != source.Id || profile.DestinationConnectionId != destination.Id)
            {
                throw new DomainException($"O perfil {profile.Name} é de outras conexões.");
            }

            options = profile.Options;
            anonymization ??= profile.AnonymizationProfileId;
            sourceDatabase ??= profile.SourceDatabase;
        }

        if (anonymization is { } anonymizationId && await anonymizationProfiles.FindByIdAsync(anonymizationId, cancellationToken) is null)
        {
            throw new DomainException("O perfil de anonimização não existe nesta máquina.");
        }

        // O nome no pedido só confere: um destino de banco fixo recebe aquele banco, e um de servidor, um nome novo.
        if (destination.Database is { } fixedDatabase
            && !string.IsNullOrWhiteSpace(request.TargetDatabase)
            && !string.Equals(request.TargetDatabase.Trim(), fixedDatabase, StringComparison.Ordinal))
        {
            throw new DomainException($"{destination.Name} só recebe o banco {fixedDatabase}.");
        }

        if (request.DevelopmentId is { } developmentId)
        {
            var task = await tasks.FindByIdAsync(taskId ?? Guid.Empty, cancellationToken)
                ?? throw new DomainException("A tarefa não existe nesta máquina.");

            if (task.Developments.FirstOrDefault(item => item.Id == developmentId) is not { Status: TaskDevelopmentStatus.Ready })
            {
                throw new DomainException("O worktree desse ambiente não está pronto nesta máquina.");
            }
        }

        return new RemoteCopyTarget(
            new CopyRequest(
                source.Id,
                destination.Id,
                anonymization is null ? DatabaseOperationType.Copy : DatabaseOperationType.CopyAndAnonymize,
                anonymization,
                options,
                request.CopyProfileId,
                sourceDatabase),
            taskId,
            request.DevelopmentId);
    }

    private async Task<DatabaseConnection> AllowedAsync(Guid id, string side, CancellationToken cancellationToken) =>
        await connections.FindByIdAsync(id, cancellationToken) is { RemoteAllowed: true, IsEnabled: true } connection
            ? connection
            : throw new DomainException($"A conexão {side} não está liberada para cópias pedidas de longe nesta máquina.");
}

/// <summary>
/// <c>database.copy</c> (ADR-090): o mesmo <see cref="RunDatabaseCopyHandler"/>
/// da tela, com a política, o processo e a auditoria de sempre. A confirmação
/// de produção, que na tela é um diálogo, vira aprovação de quem pediu; a
/// produção crítica pede o nome do banco digitado.
/// </summary>
public sealed class DatabaseCopyExecutor(IUseCaseRunner runner) : IRemoteOperationExecutor
{
    public const string ValidateStep = "validar";

    public string Kind => RemoteOperationKinds.DatabaseCopy;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await PlanAsync(request, cancellationToken);
            return null;
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }
    }

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var target = await PlanAsync(execution.Request, cancellationToken);

        await execution.EmitAsync(RemoteEventKind.StepStarted, "Conferindo origem, destino, ferramentas e máscaras.", ValidateStep, cancellationToken: cancellationToken);
        var validation = await runner.RunAsync<ValidateDatabaseCopyHandler, DatabaseCopyValidation>(
            (handler, token) => handler.HandleAsync(new ValidateDatabaseCopy(target.Request), token),
            cancellationToken);

        if (!validation.CanRun)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, Refusal(validation));
        }

        string? confirmedBy = null;

        if (validation.RequiresProductionConfirmation)
        {
            var approval = await execution.RequestApprovalAsync(Ask(validation), cancellationToken);

            if (!approval.IsAllowed)
            {
                return approval.AsFailure("A cópia de produção");
            }

            confirmedBy = $"Remoto: {approval.ControllerNickname ?? execution.ControllerNickname}";
        }

        var typed = validation.RequiresTypedConfirmation ? validation.ConfirmationText : null;
        var progress = new RemoteCopyProgress(execution);
        DatabaseCopyResult result;

        try
        {
            result = target is { DevelopmentId: { } developmentId, TaskId: { } taskId }
                ? (await runner.RunAsync<CopyDevelopmentDatabaseHandler, DevelopmentDatabaseCopyResult>(
                    (handler, token) => handler.HandleAsync(
                        new CopyDevelopmentDatabase(taskId, developmentId, target.Request, confirmedBy is not null, typed, confirmedBy),
                        progress,
                        token),
                    cancellationToken)).Copy
                : await runner.RunAsync<RunDatabaseCopyHandler, DatabaseCopyResult>(
                    (handler, token) => handler.HandleAsync(new RunDatabaseCopy(target.Request, confirmedBy is not null, typed, confirmedBy), progress, token),
                    cancellationToken);
        }
        finally
        {
            await progress.FlushAsync();
        }

        var data = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["auditId"] = result.AuditId,
                ["destinationDatabase"] = result.DestinationDatabase,
                ["status"] = result.Status.ToString(),
            },
            RemoteJson.Options);

        return result.Succeeded
            ? RemoteOperationResult.Success($"Cópia pronta em {validation.DestinationName}: {result.DestinationDatabase}.", data)
            : new RemoteOperationResult(false, Clip(SensitiveText.Mask(result.Error ?? "A cópia não terminou.")), RemoteErrorCodes.ExecutionFailed, data);
    }

    /// <summary>
    /// O que a pessoa lê antes de permitir (o risco é dar sem ler, num celular):
    /// origem, destino, perfil de anonimização e o que a validação avisou.
    /// </summary>
    private static RemoteApprovalAsk Ask(DatabaseCopyValidation validation)
    {
        var warnings = validation.Checks
            .Where(check => check.Outcome == CheckOutcome.Warning)
            .Select(check => Clip(SensitiveText.Mask($"{check.Name}: {check.Detail}")))
            .ToList();
        var anonymization = validation.AnonymizationProfileName is { } name ? $", anonimizada por {name}" : ", sem anonimização";

        return new RemoteApprovalAsk(
            ApprovalKind.ProductionDatabase,
            Clip($"Copiar {validation.SourceName} ({validation.ConfirmationText}) para {validation.DestinationName}{anonymization}."),
            JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["source"] = validation.SourceName,
                    ["sourceDatabase"] = validation.ConfirmationText,
                    ["sourceEnvironment"] = validation.SourceEnvironment.ToString(),
                    ["destination"] = validation.DestinationName,
                    ["destinationDatabase"] = validation.DestinationDatabase,
                    ["anonymizationProfile"] = validation.AnonymizationProfileName,
                    ["warnings"] = warnings,
                },
                RemoteJson.Options),
            validation.RequiresTypedConfirmation ? validation.ConfirmationText : null);
    }

    private static string Refusal(DatabaseCopyValidation validation)
    {
        if (!validation.Decision.IsAllowed)
        {
            return Clip(validation.Decision.Describe());
        }

        var failed = validation.Checks.Where(check => check.Outcome == CheckOutcome.Fail).Select(check => $"{check.Name}: {check.Detail}");
        return Clip(SensitiveText.Mask(string.Join(" ", failed)));
    }

    private Task<RemoteCopyTarget> PlanAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<RemoteDatabaseCopyPlanner, RemoteCopyTarget>(
            (planner, token) => planner.PlanAsync(request.TaskId, RemoteOperationRules.PayloadOf<RemoteCopyRequest>(request), token),
            cancellationToken);

    private static string Clip(string text) =>
        text.Length <= RemoteLimits.MaxEventTextLength ? text : $"{text[..(RemoteLimits.MaxEventTextLength - 1)]}…";

    /// <summary>
    /// As etapas da cópia como eventos, na ordem: o <see cref="IProgress{T}"/>
    /// é síncrono, e cada evento espera o anterior. As linhas das ferramentas
    /// não saem: só a etapa, o que a tela mostra no cabeçalho.
    /// </summary>
    private sealed class RemoteCopyProgress(RemoteExecution execution) : IProgress<DatabaseCopyProgress>
    {
        private readonly Lock _gate = new();

        private Task _tail = Task.CompletedTask;

        private DatabaseCopyStep? _step;

        public void Report(DatabaseCopyProgress value)
        {
            lock (_gate)
            {
                if (value.State != CommandStepState.Running || value.Step == _step)
                {
                    return;
                }

                _step = value.Step;
                var text = DatabaseCopySteps.Label(value.Step);
                _tail = _tail.ContinueWith(
                    _ => execution.EmitAsync(RemoteEventKind.StepStarted, text, value.Step.ToString(), cancellationToken: CancellationToken.None),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
        }

        public Task FlushAsync()
        {
            lock (_gate)
            {
                return _tail;
            }
        }
    }
}
