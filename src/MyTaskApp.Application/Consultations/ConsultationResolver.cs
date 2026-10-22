using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Consultations;

/// <summary>
/// A consulta de uma tarefa com as referências já resolvidas (ADR-081): o que
/// existe, o que sumiu e o que impede de abrir. É o que a aba mostra e o que o
/// "Iniciar" usa — os dois leem pelo mesmo caminho.
/// </summary>
/// <param name="Consultation"><c>null</c> enquanto nada foi salvo: o resto traz as sugestões.</param>
/// <param name="TagId">A etiqueta escolhida, ou a sugerida enquanto não há escolha.</param>
/// <param name="Connection">O banco escolhido, já com o nome do banco; <c>null</c> = só o código, ou banco inválido.</param>
public sealed record ConsultationState(
    TaskItem Task,
    TaskConsultation? Consultation,
    Guid? TagId,
    IReadOnlyList<ConsultationTagOption> Tags,
    IReadOnlyList<ConsultationSourceOption> Sources,
    IReadOnlyList<ConsultationDatabaseOption> Databases,
    ConsultationDatabaseSelection? Database,
    DatabaseConnectionSnapshot? Connection,
    IReadOnlyCollection<string> ProtectedEndpoints,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes)
{
    /// <summary>Arquivada ou na lixeira: a aba só mostra.</summary>
    public bool IsReadOnly => Task.IsOutOfTheMainList;

    public string? TagName => Tags.FirstOrDefault(tag => tag.Id == TagId)?.Name;

    /// <summary>As pastas que entram na consulta: escolhidas e presentes neste computador, na ordem da lista.</summary>
    public IReadOnlyList<ConsultationSourceOption> ReadySources =>
        Sources.Where(source => source.IsSelected && source.IsReady).ToList();
}

/// <summary>
/// Resolve a <see cref="TaskConsultation"/> contra o que existe agora:
/// etiquetas, pastas (e se existem neste computador), ambientes da tarefa e
/// conexões. Nada aqui vai ao servidor de banco — conferir o acesso é do
/// <see cref="CheckConsultationDatabaseHandler"/>.
/// </summary>
public sealed class ConsultationResolver(
    ITaskItemRepository tasks,
    ITaskConsultationRepository consultations,
    ITagQuery tags,
    IDirectoryProbe directories,
    IDatabaseConnectionRepository connections,
    ISavedDatabaseRepository savedDatabases,
    IDatabaseSecurityPolicy policy)
{
    public async Task<ConsultationState> ResolveAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await tasks.GetByIdAsync(taskId, cancellationToken);
        var consultation = await consultations.FindAsync(taskId, cancellationToken);

        var problems = new List<string>();
        var notes = new List<string>();

        var tagOptions = await TagsAsync(task, cancellationToken);
        var tagId = consultation is null ? Suggested(tagOptions) : consultation.TagId;

        if (tagId is { } chosenTag && tagOptions.All(tag => tag.Id != chosenTag))
        {
            notes.Add("A etiqueta escolhida foi excluída. Escolha outra.");
            tagId = null;
        }

        var sources = await SourcesAsync(task, consultation, tagId, notes, cancellationToken);
        var all = await connections.ListAsync(cancellationToken);
        var protectedEndpoints = all
            .Where(connection => connection.IsProtected)
            .Select(connection => connection.Snapshot().EndpointKey)
            .ToHashSet(StringComparer.Ordinal);

        var databases = await DatabasesAsync(task, all, protectedEndpoints, cancellationToken);
        var (selection, snapshot) = Selection(consultation, all, databases, protectedEndpoints);

        if (task.IsOutOfTheMainList)
        {
            problems.Add("A tarefa está arquivada ou na lixeira: restaure-a para consultar.");
        }

        if (!sources.Any(source => source.IsSelected && source.IsReady))
        {
            problems.Add(sources.Count == 0
                ? "Escolha o assunto: uma etiqueta com projetos, ou um ambiente da tarefa."
                : "Escolha pelo menos uma pasta disponível neste computador.");
        }

        foreach (var missing in sources.Where(source => source.IsSelected && !source.IsReady))
        {
            notes.Add($"{missing.Name}: {Describe(missing.State)} Fica de fora da consulta.");
        }

        if (selection?.Problem is { } databaseProblem)
        {
            problems.Add(databaseProblem);
        }

        return new ConsultationState(
            task, consultation, tagId, tagOptions, sources, databases, selection,
            selection?.Problem is null ? snapshot : null, protectedEndpoints, problems, notes);
    }

    /// <summary>Por que uma pasta não entra, para a tela e para as notas.</summary>
    public static string Describe(ConsultationSourceState state) => state switch
    {
        ConsultationSourceState.FolderMissing => "a pasta não foi encontrada neste computador.",
        ConsultationSourceState.NotCloned => "o repositório não foi clonado neste computador.",
        ConsultationSourceState.NotMapped => "não há pasta neste computador.",
        ConsultationSourceState.Removed => "foi removida.",
        _ => "pronta.",
    };

    /// <summary>As da tarefa primeiro, porque o assunto quase sempre é uma delas.</summary>
    private async Task<IReadOnlyList<ConsultationTagOption>> TagsAsync(TaskItem task, CancellationToken cancellationToken)
    {
        var onTask = task.Tags.Select(tag => tag.TagId).ToHashSet();
        var rows = await tags.ListAsync(PlacementFilter.All, onTask, cancellationToken);

        return rows
            .Select(row => new ConsultationTagOption(row.Id, row.Name, row.ColorHex, onTask.Contains(row.Id), row.DirectoryCount))
            .OrderByDescending(option => option.IsOnTask)
            .ThenBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Sem escolha ainda: a primeira etiqueta da tarefa que tem projetos.</summary>
    private static Guid? Suggested(IReadOnlyList<ConsultationTagOption> options) =>
        options.FirstOrDefault(option => option.IsOnTask && option.DirectoryCount > 0)?.Id;

    private async Task<IReadOnlyList<ConsultationSourceOption>> SourcesAsync(
        TaskItem task,
        TaskConsultation? consultation,
        Guid? tagId,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var chosen = consultation?.Sources.Select(source => source.Reference).ToList() ?? [];
        var options = new List<ConsultationSourceOption>();

        if (tagId is { } tag)
        {
            foreach (var row in await tags.ListDirectoriesAsync(tag, cancellationToken))
            {
                var state = row.Path is { } path
                    ? await directories.ExistsAsync(path, cancellationToken) ? ConsultationSourceState.Ready : ConsultationSourceState.FolderMissing
                    : row.RepositoryUrl is not null ? ConsultationSourceState.NotCloned : ConsultationSourceState.NotMapped;

                var reference = new ConsultationSourceReference(ConsultationSourceKind.TagDirectory, row.Id);

                // Ainda sem escolha: todas as pastas prontas da etiqueta.
                var selected = consultation is null ? state == ConsultationSourceState.Ready : chosen.Contains(reference);

                options.Add(new ConsultationSourceOption(
                    ConsultationSourceKind.TagDirectory,
                    row.Id,
                    row.Name ?? row.Alias,
                    row.Alias,
                    row.Path,
                    SafeUrl(row.RepositoryUrl),
                    row.DefaultBranch,
                    state,
                    selected));
            }
        }

        foreach (var development in task.Developments.Where(development => development.Status == TaskDevelopmentStatus.Ready))
        {
            var state = await directories.ExistsAsync(development.WorktreePath, cancellationToken)
                ? ConsultationSourceState.Ready
                : ConsultationSourceState.FolderMissing;

            options.Add(new ConsultationSourceOption(
                ConsultationSourceKind.Worktree,
                development.Id,
                $"{RepositoryName(development.RepositoryPath)} · {development.Branch}",
                null,
                development.WorktreePath,
                null,
                development.Branch,
                state,
                chosen.Contains(new ConsultationSourceReference(ConsultationSourceKind.Worktree, development.Id))));
        }

        // A ordem é a da escolha: a primeira é onde o agente abre.
        var ordered = options
            .OrderBy(option => chosen.IndexOf(option.Reference) is var index and >= 0 ? index : int.MaxValue)
            .ToList();

        if (chosen.Any(reference => ordered.All(option => option.Reference != reference)))
        {
            notes.Add("Uma pasta escolhida antes foi removida (ou é de outra etiqueta) e ficou de fora.");
        }

        return ordered;
    }

    /// <summary>
    /// Os bancos que a consulta pode usar, nunca de produção: as cópias dos
    /// ambientes da tarefa, as conexões com banco fixo e os apelidos.
    /// </summary>
    private async Task<IReadOnlyList<ConsultationDatabaseOption>> DatabasesAsync(
        TaskItem task,
        IReadOnlyList<DatabaseConnection> all,
        IReadOnlyCollection<string> protectedEndpoints,
        CancellationToken cancellationToken)
    {
        var byId = all.ToDictionary(connection => connection.Id);
        var options = new List<ConsultationDatabaseOption>();

        void Offer(Guid connectionId, string database, string label, ConsultationDatabaseOrigin origin)
        {
            if (!byId.TryGetValue(connectionId, out var connection)
                || options.Any(option => option.Matches(connectionId, database)))
            {
                return;
            }

            var snapshot = connection.Snapshot();

            if (!connection.IsEnabled || snapshot.IsProtected || snapshot.WithDatabase(database).IsAmong(protectedEndpoints))
            {
                return;
            }

            options.Add(new ConsultationDatabaseOption(
                connection.Id,
                database,
                label,
                connection.Name,
                Server(snapshot, database),
                connection.Environment,
                origin,
                IsReader(connection.Username)));
        }

        foreach (var development in task.Developments)
        {
            if (development.Database is { } copy)
            {
                Offer(copy.ConnectionId, copy.Name, $"{copy.Name} (ambiente {development.Branch})", ConsultationDatabaseOrigin.TaskEnvironment);
            }
        }

        foreach (var connection in all.Where(connection => connection.Database is not null && !IsReader(connection.Username)))
        {
            Offer(connection.Id, connection.Database!, $"{connection.Database} ({connection.Name})", ConsultationDatabaseOrigin.Connection);
        }

        foreach (var saved in await savedDatabases.ListAsync(cancellationToken))
        {
            Offer(saved.ConnectionId, saved.DatabaseName, $"{saved.Alias} ({saved.DatabaseName})", ConsultationDatabaseOrigin.SavedDatabase);
        }

        return options;
    }

    private (ConsultationDatabaseSelection? Selection, DatabaseConnectionSnapshot? Snapshot) Selection(
        TaskConsultation? consultation,
        IReadOnlyList<DatabaseConnection> all,
        IReadOnlyList<ConsultationDatabaseOption> options,
        IReadOnlyCollection<string> protectedEndpoints)
    {
        if (consultation?.DatabaseConnectionId is not { } connectionId)
        {
            return (null, null);
        }

        var database = consultation.DatabaseName;
        var connection = all.FirstOrDefault(item => item.Id == connectionId);

        if (connection is null)
        {
            return (new ConsultationDatabaseSelection(
                connectionId, database, database ?? "Banco", null, null, null, false,
                "O banco escolhido não existe mais: a conexão dele foi excluída. Escolha outro, ou consulte só o código."), null);
        }

        var snapshot = connection.Snapshot();

        if (database is not null)
        {
            snapshot = snapshot.WithDatabase(database);
        }

        var label = options.FirstOrDefault(option => option.Matches(connectionId, database))?.Label
            ?? (database is null ? connection.Name : $"{database} ({connection.Name})");

        var decision = policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.QueryData, Source: snapshot, ProtectedEndpoints: protectedEndpoints));

        var problem = decision.IsAllowed ? null : string.Join(" ", decision.Violations.Select(violation => violation.Message));

        return (new ConsultationDatabaseSelection(
            connection.Id,
            database ?? connection.Database,
            label,
            connection.Name,
            Server(snapshot, database ?? connection.Database),
            connection.Environment,
            IsReader(connection.Username),
            problem), snapshot);
    }

    /// <summary>Servidor, porta e banco — sem usuário, sem senha.</summary>
    public static string Server(DatabaseConnectionSnapshot connection, string? database) =>
        database is null ? connection.ServerKey : $"{connection.ServerKey}/{database}";

    public static bool IsReader(string username) =>
        string.Equals(username, IConsultationReadAccessProvisioner.RoleName, StringComparison.Ordinal);

    public static string RepositoryName(string path) =>
        System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;

    /// <summary>A URL cadastrada, sem usuário nem senha — mesmo uma antiga, de antes da limpeza.</summary>
    public static string? SafeUrl(string? url)
    {
        try
        {
            return GitRemoteUrl.Normalize(url);
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
