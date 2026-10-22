using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Consultations;

/// <summary>Monta a <see cref="TaskConsultationView"/>: a configuração resolvida e as sessões.</summary>
/// <remarks>
/// Uma consulta "ativa" cujo terminal já fechou é encerrada aqui antes de
/// voltar (ADR-030): a aba nunca diz "Sessão ativa" sem processo.
/// </remarks>
public sealed class ConsultationViewFactory(
    ConsultationResolver resolver,
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentCliProviders providers,
    IAgentProcessTracker processes,
    IAgentSessionWatcher watcher,
    IConsultationToolsEndpoint tools,
    TimeProvider timeProvider)
{
    /// <summary>Quantas consultas anteriores a aba mostra.</summary>
    public const int HistoryLimit = 10;

    public async Task<TaskConsultationView> BuildAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var state = await resolver.ResolveAsync(taskId, cancellationToken);
        var history = await sessions.ListConsultationsForTaskAsync(taskId, HistoryLimit, cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (history.Where(session => session.IsActive).Count(session => AgentSessionReconciler.EndIfGone(session, processes, now)) > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            watcher.NotifyChanged(taskId);
        }

        var active = history.FirstOrDefault(session => session.IsActive);
        var provider = providers.Default;

        return new TaskConsultationView(
            state.Task.Id,
            state.Task.Title,
            state.IsReadOnly,
            state.Consultation is not null,
            state.TagId,
            state.TagName,
            state.Tags,
            state.Sources,
            state.Database,
            state.Databases,
            state.Consultation?.Instructions,
            state.Consultation?.Model,
            state.Consultation?.Effort,
            state.Consultation?.LastQuestion,
            state.Problems,
            state.Notes,
            active is null ? null : AgentSessionView.From(active, providers),
            history
                .Select(session => new ConsultationSessionView(
                    session.Id,
                    session.ExternalSessionId,
                    session.StartedAt,
                    session.EndedAt,
                    session.Status,
                    session.FailureReason))
                .ToList())
        {
            Models = provider.Models,
            Efforts = provider.Efforts,
            ToolsAvailable = tools.Address is not null,
        };
    }
}

/// <summary>A aba Consulta Inteligente de uma tarefa (ADR-081).</summary>
public sealed record GetTaskConsultation(Guid TaskId);

public sealed class GetTaskConsultationHandler(ConsultationViewFactory views)
{
    public Task<TaskConsultationView> HandleAsync(GetTaskConsultation query, CancellationToken cancellationToken = default) =>
        views.BuildAsync(query.TaskId, cancellationToken);
}

/// <summary>
/// Grava a configuração da consulta da tarefa — a cada mudança na aba.
/// </summary>
/// <param name="Sources">
/// As pastas, na ordem da tela; <c>null</c> = escolher por mim: todas as
/// prontas da etiqueta, mais os ambientes que já estavam escolhidos. É o que a
/// tela manda ao trocar de etiqueta.
/// </param>
/// <param name="DatabaseConnectionId"><c>null</c> = somente o código-fonte.</param>
public sealed record SaveTaskConsultation(
    Guid TaskId,
    Guid? TagId,
    IReadOnlyList<ConsultationSourceReference>? Sources,
    Guid? DatabaseConnectionId,
    string? DatabaseName,
    string? Instructions,
    string? Model,
    string? Effort);

/// <remarks>
/// As referências são conferidas aqui, e não só na tela: uma pasta de outra
/// etiqueta, um ambiente de outra tarefa ou um banco de produção são recusados
/// antes de gravar.
/// </remarks>
public sealed class SaveTaskConsultationHandler(
    ITaskItemRepository tasks,
    ITaskConsultationRepository consultations,
    ITagQuery tags,
    IDirectoryProbe directories,
    IDatabaseConnectionRepository connections,
    IAgentCliProviders providers,
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    ConsultationViewFactory views,
    TimeProvider timeProvider)
{
    public async Task<TaskConsultationView> HandleAsync(SaveTaskConsultation command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        if (task.IsOutOfTheMainList)
        {
            throw new DomainException("A tarefa está arquivada ou na lixeira: a consulta dela não muda.");
        }

        var consultation = await consultations.FindAsync(task.Id, cancellationToken);

        if (consultation is null)
        {
            consultation = TaskConsultation.Create(task.Id, now);
            await consultations.AddAsync(consultation, cancellationToken);
        }

        var tagDirectories = command.TagId is { } tagId
            ? await tags.ListDirectoriesAsync(tagId, cancellationToken)
            : [];

        consultation.SetSubject(command.TagId, now);
        consultation.SetSources(await SourcesAsync(command, task, consultation, tagDirectories, cancellationToken), now);
        await SetDatabaseAsync(consultation, command, now, cancellationToken);
        consultation.SetInstructions(command.Instructions, now);
        consultation.SetLaunchPreferences(
            Checked(providers.Default.Models, command.Model, "Modelo"),
            Checked(providers.Default.Efforts, command.Effort, "Nível de esforço"),
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await views.BuildAsync(task.Id, cancellationToken);
    }

    private async Task<IReadOnlyList<ConsultationSourceReference>> SourcesAsync(
        SaveTaskConsultation command,
        TaskItem task,
        TaskConsultation consultation,
        IReadOnlyList<TagDirectoryRow> tagDirectories,
        CancellationToken cancellationToken)
    {
        if (command.Sources is { } requested)
        {
            foreach (var source in requested)
            {
                var known = source.Kind switch
                {
                    ConsultationSourceKind.TagDirectory => tagDirectories.Any(directory => directory.Id == source.ReferenceId),
                    ConsultationSourceKind.Worktree => task.Developments.Any(development => development.Id == source.ReferenceId),
                    _ => false,
                };

                if (!known)
                {
                    throw new DomainException("Uma das pastas escolhidas não é desta etiqueta nem desta tarefa.");
                }
            }

            return requested;
        }

        var chosen = consultation.Sources
            .Where(source => source.Kind == ConsultationSourceKind.Worktree)
            .Select(source => source.Reference)
            .ToList();

        foreach (var directory in tagDirectories)
        {
            if (directory.Path is { } path && await directories.ExistsAsync(path, cancellationToken))
            {
                chosen.Add(new ConsultationSourceReference(ConsultationSourceKind.TagDirectory, directory.Id));
            }
        }

        return chosen.Take(TaskConsultation.MaxSources).ToList();
    }

    private async Task SetDatabaseAsync(
        TaskConsultation consultation,
        SaveTaskConsultation command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var unchanged = command.DatabaseConnectionId == consultation.DatabaseConnectionId
            && (command.DatabaseConnectionId is null
                || string.Equals(Blank(command.DatabaseName), consultation.DatabaseName, StringComparison.Ordinal));

        if (unchanged)
        {
            return;
        }

        // O banco de uma sessão aberta é o que ela recebeu nas instruções: trocar
        // no meio faria a IA ler outro banco achando que é o mesmo.
        var open = await sessions.ListConsultationsForTaskAsync(consultation.TaskItemId, 1, cancellationToken);

        if (open is [{ IsActive: true }])
        {
            throw new DomainException("Há uma consulta aberta com este banco. Feche o terminal dela para trocar o banco.");
        }

        if (command.DatabaseConnectionId is not { } connectionId)
        {
            consultation.ClearDatabase(now);
            return;
        }

        var all = await connections.ListAsync(cancellationToken);
        var connection = all.FirstOrDefault(item => item.Id == connectionId)
            ?? throw new DomainException("Conexão de banco não encontrada.");

        var snapshot = connection.Snapshot();
        var database = string.IsNullOrWhiteSpace(command.DatabaseName) ? connection.Database : command.DatabaseName;

        if (database is not null)
        {
            snapshot = snapshot.WithDatabase(database);
        }

        var protectedEndpoints = all.Where(item => item.IsProtected).Select(item => item.Snapshot().EndpointKey).ToHashSet(StringComparer.Ordinal);

        if (snapshot.IsProtected || snapshot.IsAmong(protectedEndpoints))
        {
            throw new DomainException($"{connection.Name} é produção: a Consulta Inteligente só lê cópias locais.");
        }

        consultation.SetDatabase(connectionId, database, now);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Checked(IReadOnlyList<AgentCliOption> options, string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null
        : AgentCliOption.Find(options, value.Trim())?.Value ?? throw new DomainException($"{name} desconhecido: {value.Trim()}.");
}

/// <summary>Esquece a configuração da consulta da tarefa.</summary>
public sealed record ClearTaskConsultation(Guid TaskId);

/// <summary>
/// Tira só as linhas da consulta: etiquetas, pastas, repositórios, bancos e
/// conexões continuam onde estão, e as sessões antigas ficam no histórico.
/// </summary>
public sealed class ClearTaskConsultationHandler(
    ITaskConsultationRepository consultations,
    IUnitOfWork unitOfWork,
    ConsultationViewFactory views)
{

    public async Task<TaskConsultationView> HandleAsync(ClearTaskConsultation command, CancellationToken cancellationToken = default)
    {
        if (await consultations.FindAsync(command.TaskId, cancellationToken) is { } consultation)
        {
            consultations.Remove(consultation);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await views.BuildAsync(command.TaskId, cancellationToken);
    }
}

/// <summary>As instruções que o agente receberia agora, para a prévia da aba.</summary>
public sealed record PreviewConsultationContext(Guid TaskId);

/// <summary>
/// Monta o mesmo texto do "Iniciar", sem ir ao servidor de banco: a versão do
/// PostgreSQL só aparece na hora de abrir.
/// </summary>
public sealed class PreviewConsultationContextHandler(ConsultationResolver resolver)
{
    public async Task<string> HandleAsync(PreviewConsultationContext query, CancellationToken cancellationToken = default)
    {
        var state = await resolver.ResolveAsync(query.TaskId, cancellationToken);
        return ConsultationContextBuilder.Build(ConsultationLauncher.ContextOf(state, serverVersion: null));
    }
}

/// <summary>Confere no servidor o banco escolhido: alcança? O usuário só lê?</summary>
public sealed record CheckConsultationDatabase(Guid TaskId);

public sealed class CheckConsultationDatabaseHandler(
    ConsultationResolver resolver,
    ConsultationReadAccess readAccess,
    IDatabaseSecurityPolicy policy)
{
    public async Task<ConsultationDatabaseCheck> HandleAsync(CheckConsultationDatabase query, CancellationToken cancellationToken = default)
    {
        var state = await resolver.ResolveAsync(query.TaskId, cancellationToken);

        if (state.Database is null)
        {
            throw new DomainException("Nenhum banco escolhido: a consulta usa só o código-fonte.");
        }

        if (state.Connection is not { } chosen)
        {
            return new ConsultationDatabaseCheck(false, null, null, false, [state.Database.Problem!], false, state.Database.Problem);
        }

        return await DescribeAsync(chosen, state.ProtectedEndpoints, cancellationToken);
    }

    public async Task<ConsultationDatabaseCheck> DescribeAsync(
        DatabaseConnectionSnapshot chosen,
        IReadOnlyCollection<string> protectedEndpoints,
        CancellationToken cancellationToken)
    {
        var canPrepare = !ConsultationResolver.IsReader(chosen.Username)
            && policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.PrepareReadAccess, Destination: chosen, ProtectedEndpoints: protectedEndpoints)).IsAllowed;

        var reader = await readAccess.ReaderForAsync(chosen, cancellationToken);
        ReadAccessCheck check;

        try
        {
            check = await readAccess.CheckAsync(reader, cancellationToken);
        }
        catch (DomainException exception)
        {
            return new ConsultationDatabaseCheck(
                false, null, null, false, [], canPrepare,
                $"Não foi possível conferir o acesso ao banco: {exception.Message}");
        }

        if (check.IsReadOnly)
        {
            return new ConsultationDatabaseCheck(
                true, check.ServerVersion, check.User, true, [], false,
                $"Acesso de leitura confirmado: PostgreSQL {check.ServerVersion}, usuário {check.User}, que só lê.");
        }

        return new ConsultationDatabaseCheck(
            true, check.ServerVersion, check.User, false, check.Problems, canPrepare,
            canPrepare
                ? $"O usuário {check.User} pode alterar dados. Prepare o acesso de leitura para consultar este banco."
                : $"O usuário {check.User} pode alterar dados, e o MyTaskApp só prepara acesso de leitura em servidores de Desenvolvimento ou Teste.");
    }
}

/// <summary>
/// "Preparar acesso de leitura": cria ou acerta o usuário somente leitura no
/// servidor do banco escolhido e cadastra a conexão dele (ADR-081). Só depois
/// da confirmação do usuário na tela.
/// </summary>
public sealed record PrepareConsultationReadAccess(Guid TaskId);

/// <remarks>
/// <para>
/// A senha é nova a cada vez: gerada aqui, trocada no servidor e guardada no
/// cofre — ninguém neste computador sabe a anterior, e nenhuma tela a mostra.
/// </para>
/// <para>
/// Passa pela política (só Desenvolvimento ou Teste) e fica na auditoria das
/// operações de banco, como uma cópia.
/// </para>
/// </remarks>
public sealed class PrepareConsultationReadAccessHandler(
    ConsultationResolver resolver,
    IConsultationReadAccessProvisioner provisioner,
    IDatabaseConnectionRepository connections,
    IDatabaseCredentialStore credentials,
    IDatabaseOperationAuditLog audit,
    IDatabaseSecurityPolicy policy,
    CheckConsultationDatabaseHandler check,
    ConsultationReadAccessCache cache,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<PrepareConsultationReadAccessHandler> logger)
{
    public async Task<ConsultationDatabaseCheck> HandleAsync(
        PrepareConsultationReadAccess command,
        CancellationToken cancellationToken = default)
    {
        var state = await resolver.ResolveAsync(command.TaskId, cancellationToken);

        if (state.Connection is not { } admin)
        {
            throw new DomainException(state.Database?.Problem ?? "Escolha o banco antes de preparar o acesso de leitura.");
        }

        if (ConsultationResolver.IsReader(admin.Username))
        {
            throw new DomainException("Esta já é a conexão de leitura. Escolha o banco pela conexão que o criou.");
        }

        var host = Environment.MachineName;
        var user = currentUser.Name ?? Environment.UserName;
        var decision = policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.PrepareReadAccess, Destination: admin, ProtectedEndpoints: state.ProtectedEndpoints));

        if (!decision.IsAllowed)
        {
            await audit.RecordAsync(
                DatabaseOperationAudit.Blocked(
                    DatabaseOperationType.PrepareReadAccess, null, admin, null, null, host, user, decision.Describe(), timeProvider.GetUtcNow()),
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            throw new DatabaseSecurityException(decision);
        }

        var entry = DatabaseOperationAudit.Start(
            DatabaseOperationType.PrepareReadAccess, null, admin, null, null, host, user, timeProvider.GetUtcNow());
        await audit.RecordAsync(entry, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var password = new SecretText(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        bool created;

        try
        {
            created = await provisioner.PrepareAsync(admin, password, cancellationToken);
        }
        catch (Exception exception) when (exception is DomainException or OperationCanceledException)
        {
            entry.Fail(exception is OperationCanceledException ? "Cancelada." : exception.Message, timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        // O servidor já mudou: daqui em diante, nada é cancelado no meio.
        var reader = await ReaderConnectionAsync(admin, CancellationToken.None);
        reader.AttachSecret(await credentials.StoreAsync(reader.Id, password, CancellationToken.None), timeProvider.GetUtcNow());

        entry.RecordSummary(created
            ? $"Usuário {IConsultationReadAccessProvisioner.RoleName} criado, com SELECT em {admin.Database}."
            : $"Usuário {IConsultationReadAccessProvisioner.RoleName} acertado, com SELECT em {admin.Database} e senha nova.");
        entry.Succeed(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        cache.Clear();

        logger.LogInformation(
            "ConsultationReadAccessReady {TaskId} {AuditId} {ConnectionId} {ReaderConnectionId} {Created}",
            command.TaskId,
            entry.Id,
            admin.Id,
            reader.Id,
            created);

        return await check.DescribeAsync(admin, state.ProtectedEndpoints, cancellationToken);
    }

    /// <summary>
    /// A conexão do usuário de leitura daquele servidor: a que já existe, ou
    /// uma nova, sem banco fixo, só com leitura, no mesmo ambiente da origem.
    /// </summary>
    private async Task<DatabaseConnection> ReaderConnectionAsync(DatabaseConnectionSnapshot admin, CancellationToken cancellationToken)
    {
        var all = await connections.ListAsync(cancellationToken);
        var existing = all.FirstOrDefault(connection =>
            ConsultationResolver.IsReader(connection.Username)
            && string.Equals(connection.Snapshot().ServerKey, admin.ServerKey, StringComparison.Ordinal));

        if (existing is not null)
        {
            existing.SetEnabled(true, timeProvider.GetUtcNow());
            return existing;
        }

        var baseName = $"{Shorten(admin.Name)} (leitura)";
        var name = baseName;

        for (var suffix = 2; await connections.NameExistsAsync(name, null, cancellationToken); suffix++)
        {
            name = $"{baseName} {suffix}";
        }

        var reader = DatabaseConnection.Create(
            name,
            admin.Host,
            admin.Port,
            database: null,
            IConsultationReadAccessProvisioner.RoleName,
            admin.Environment,
            admin.SslMode,
            "Usuário somente leitura da Consulta Inteligente, criado pelo MyTaskApp (ADR-081).",
            ConnectionPermissions.FromFlags(ConnectionPermission.Read),
            timeProvider.GetUtcNow());

        await connections.AddAsync(reader, cancellationToken);
        return reader;
    }

    private static string Shorten(string name) =>
        name.Length <= DatabaseConnection.MaxNameLength - 15 ? name : name[..(DatabaseConnection.MaxNameLength - 15)];
}
