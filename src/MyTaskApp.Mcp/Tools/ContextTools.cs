using System.ComponentModel;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>
/// O contexto em volta das tarefas (ADR-059): etiquetas, os diretórios de cada
/// uma com o <c>@alias</c>, os worktrees, os comandos cadastrados, prazos,
/// lembretes, o Jira e os post-its. Os comandos só se listam: nenhum é
/// executado pelo MCP.
/// </summary>
[McpServerToolType]
public sealed class ContextTools(McpGateway gateway, IUserClock clock)
{
    [McpServerTool(Name = "tag_list", Title = "Listar etiquetas", ReadOnly = true, Idempotent = true)]
    [Description("As etiquetas: nome, cor, quantas tarefas usam e quantos diretórios (repositórios) estão ligados a cada uma.")]
    public Task<IReadOnlyList<TagRow>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync("tag_list", (runner, token) => Tags(runner, token), cancellationToken);

    [McpServerTool(Name = "tag_get", Title = "Consultar etiqueta", ReadOnly = true, Idempotent = true)]
    [Description("Uma etiqueta por nome ou id, com os diretórios dela (alias, caminho, branch padrão) e os comandos ligados a cada diretório.")]
    public Task<TagDetail> GetTagAsync(
        [Description("Nome ou id da etiqueta.")] string tag,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "tag_get",
            async (runner, token) =>
            {
                var id = (await TagLookup.ResolveAsync(runner, [tag], token)).Single();
                var row = (await Tags(runner, token)).Single(found => found.Id == id);
                var directories = await Directories(runner, id, token);

                var withCommands = new List<DirectoryInfo>();

                foreach (var directory in directories)
                {
                    withCommands.Add(new DirectoryInfo(directory, await Bindings(runner, directory.Id, token)));
                }

                return new TagDetail(row, withCommands);
            },
            cancellationToken);

    [McpServerTool(Name = "directory_alias_list", Title = "Diretórios e aliases", ReadOnly = true, Idempotent = true)]
    [Description(
        "Os diretórios cadastrados nas etiquetas — o equivalente do app a \"repositórios\" e \"projetos\" —, com o @alias usado nas " +
        "anotações, o caminho e a branch padrão. Opcionalmente só de uma etiqueta, ou só os alcançáveis por uma tarefa.")]
    public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
        [Description("Só desta etiqueta (nome ou id).")] string? tag = null,
        [Description("Só os das etiquetas desta tarefa.")] string? taskId = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "directory_alias_list",
            async (runner, token) =>
            {
                if (McpInput.OptionalId(taskId, "o id da tarefa") is { } task)
                {
                    return await runner.RunAsync<GetTaskDirectoriesHandler, IReadOnlyList<TagDirectoryRow>>(
                        (handler, cancel) => handler.HandleAsync(new GetTaskDirectories(task), cancel), token);
                }

                var tagIds = tag is null
                    ? (await Tags(runner, token)).Select(row => row.Id).ToList()
                    : await TagLookup.ResolveAsync(runner, [tag], token);

                var all = new List<TagDirectoryRow>();

                foreach (var id in tagIds)
                {
                    all.AddRange(await Directories(runner, id, token));
                }

                return (IReadOnlyList<TagDirectoryRow>)all;
            },
            cancellationToken);

    [McpServerTool(Name = "worktree_list", Title = "Worktrees", ReadOnly = true, Idempotent = true)]
    [Description(
        "Os ambientes de desenvolvimento (worktrees Git) de uma tarefa — repositório, branch, origem, pasta, estado e comandos " +
        "pós-worktree. Sem tarefa, os worktrees prontos de todas as tarefas da lista principal.")]
    public Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(
        [Description("O id da tarefa; vazio = todas.")] string? taskId = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "worktree_list",
            async (runner, token) =>
            {
                if (McpInput.OptionalId(taskId, "o id da tarefa") is { } id)
                {
                    var task = await TaskTools.Details(runner, id, token);

                    return (IReadOnlyList<WorktreeEntry>)(await Developments(runner, id, token))
                        .Select(view => WorktreeEntry.Of(view, task.Task.Title))
                        .ToList();
                }

                var tasks = await runner.RunAsync<SearchTasksHandler, TaskSearchResult>(
                    (handler, cancel) => handler.HandleAsync(new SearchTasks(Limit: SearchTasks.MaxLimit), cancel), token);

                return tasks.Items
                    .SelectMany(item => (item.Task.Worktrees ?? []).Select(worktree => new WorktreeEntry(
                        worktree.DevelopmentId,
                        item.Task.TaskId,
                        item.Task.Title,
                        worktree.RepositoryName,
                        worktree.Branch,
                        worktree.SourceBranch,
                        worktree.WorktreePath,
                        nameof(TaskDevelopmentStatus.Ready),
                        null,
                        [])))
                    .ToList();
            },
            cancellationToken);

    [McpServerTool(Name = "git_context_get", Title = "Contexto Git da tarefa", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "O contexto de desenvolvimento de uma tarefa: os worktrees com o estado do Git (alterações, commits por enviar, se a branch foi " +
        "publicada), os diretórios das etiquetas e a issue do Jira com a branch sugerida. Só lê: o Git não é alterado.")]
    public Task<GitContext> GetGitContextAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "git_context_get",
            async (runner, token) =>
            {
                var id = McpInput.Id(taskId, "o id da tarefa");
                var task = await TaskTools.Details(runner, id, token);
                var developments = await Developments(runner, id, token);

                var ready = (task.Task.Worktrees ?? []).ToList();
                var sync = ready.Count == 0
                    ? []
                    : await runner.RunAsync<ProbeWorktreesHandler, IReadOnlyList<WorktreeSync>>(
                        (handler, cancel) => handler.HandleAsync(new ProbeWorktrees(ready), cancel), token);

                var directories = await runner.RunAsync<GetTaskDirectoriesHandler, IReadOnlyList<TagDirectoryRow>>(
                    (handler, cancel) => handler.HandleAsync(new GetTaskDirectories(id), cancel), token);

                var external = await runner.RunAsync<GetTaskExternalContextHandler, TaskExternalContext>(
                    (handler, cancel) => handler.HandleAsync(new GetTaskExternalContext(id), cancel), token);

                return new GitContext(
                    id,
                    task.Task.Title,
                    developments.Select(view => WorktreeEntry.Of(view, task.Task.Title)).ToList(),
                    sync,
                    directories,
                    external.Link?.Id,
                    external.SuggestedBranch);
            },
            cancellationToken);

    [McpServerTool(Name = "command_definition_list", Title = "Comandos cadastrados", ReadOnly = true, Idempotent = true)]
    [Description(
        "Os comandos globais (chamados por @alias) e os comandos só de diretório: o comando, o modo (Execute ou Terminal), a pasta, " +
        "os parâmetros e se pedem confirmação. Só lista: o MCP não executa comandos.")]
    public Task<IReadOnlyList<DevelopmentCommandRow>> ListCommandsAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "command_definition_list",
            (runner, token) => runner.RunAsync<GetDevelopmentCommandsHandler, IReadOnlyList<DevelopmentCommandRow>>(
                (handler, cancel) => handler.HandleAsync(new GetDevelopmentCommands(), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "command_binding_list", Title = "Comandos por diretório", ReadOnly = true, Idempotent = true)]
    [Description(
        "Os comandos rápidos ligados a cada diretório de etiqueta, na ordem dos botões, com a personalização (comando e pasta) " +
        "e se estão habilitados. Opcionalmente só de uma etiqueta. Só lista: o MCP não executa comandos.")]
    public Task<IReadOnlyList<DirectoryInfo>> ListBindingsAsync(
        [Description("Só desta etiqueta (nome ou id).")] string? tag = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "command_binding_list",
            async (runner, token) =>
            {
                var tagIds = tag is null
                    ? (await Tags(runner, token)).Select(row => row.Id).ToList()
                    : await TagLookup.ResolveAsync(runner, [tag], token);

                var all = new List<DirectoryInfo>();

                foreach (var id in tagIds)
                {
                    foreach (var directory in await Directories(runner, id, token))
                    {
                        all.Add(new DirectoryInfo(directory, await Bindings(runner, directory.Id, token)));
                    }
                }

                return (IReadOnlyList<DirectoryInfo>)all;
            },
            cancellationToken);

    [McpServerTool(Name = "deadline_list_upcoming", Title = "Próximos prazos", ReadOnly = true, Idempotent = true)]
    [Description("As tarefas pendentes com prazo nos próximos dias (padrão 7), do que vence primeiro. Atrasadas: task_get_overdue.")]
    public Task<TaskPage> ListUpcomingDeadlinesAsync(
        [Description("Quantos dias à frente, de 1 a 366.")] int days = 7,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "deadline_list_upcoming",
            async (runner, token) =>
            {
                if (days is < 1 or > 366)
                {
                    throw new DomainException("Os dias vão de 1 a 366.");
                }

                var today = clock.Today;
                var result = await runner.RunAsync<SearchTasksHandler, TaskSearchResult>(
                    (handler, cancel) => handler.HandleAsync(
                        new SearchTasks(
                            Statuses: [TaskItemStatus.Pending],
                            DeadlineFrom: today,
                            DeadlineTo: today.AddDays(days),
                            HasDeadline: true,
                            Sort: TaskSort.Deadline,
                            Limit: SearchTasks.MaxLimit),
                        cancel),
                    token);

                return new TaskPage(result.Items.Select(TaskSummary.Of).ToList(), result.Total, 0, result.Limit, result.Truncated, null);
            },
            cancellationToken);

    [McpServerTool(Name = "reminder_get_defaults", Title = "Preferências de lembrete", ReadOnly = true, Idempotent = true)]
    [Description("O lembrete padrão das tarefas novas e se os lembretes estão pausados (até quando).")]
    public Task<ReminderDefaults> GetReminderDefaultsAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "reminder_get_defaults",
            async (runner, token) =>
            {
                var settings = await runner.RunAsync<GetReminderDefaultsHandler, ReminderSettings>(
                    (handler, cancel) => handler.HandleAsync(new GetReminderDefaults(), cancel), token);

                return new ReminderDefaults(ReminderInfo.Of(settings.DefaultPolicy)!, settings.PausedUntilUtc);
            },
            cancellationToken);

    [McpServerTool(Name = "jira_search", Title = "Buscar no Jira", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Busca issues no Jira conectado (chave ou texto). Só consulta: nada é alterado no Jira.")]
    public Task<ExternalTaskSearchResult> SearchJiraAsync(
        [Description("A chave (ECO-123) ou o texto.")] string query,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "jira_search",
            (runner, token) => runner.RunAsync<SearchExternalTasksHandler, ExternalTaskSearchResult>(
                (handler, cancel) => handler.HandleAsync(new SearchExternalTasks(query), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_get_external_context", Title = "Issue da tarefa", ReadOnly = true, Idempotent = true)]
    [Description("A issue vinculada à tarefa, como foi lida da última vez, e a branch sugerida pela convenção do tipo.")]
    public Task<TaskExternalContext> GetExternalContextAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "task_get_external_context",
            (runner, token) => runner.RunAsync<GetTaskExternalContextHandler, TaskExternalContext>(
                (handler, cancel) => handler.HandleAsync(new GetTaskExternalContext(McpInput.Id(taskId, "o id da tarefa")), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_link_jira", Title = "Vincular issue do Jira", Idempotent = true, OpenWorld = true)]
    [Description(
        "Vincula a tarefa a uma issue do Jira pela chave: guarda um retrato da issue na tarefa. Só lê o Jira — não muda a issue, " +
        "não lança horas lá e não cria sincronização.")]
    public Task<ExternalLink> LinkJiraAsync(
        [Description("O id da tarefa.")] string taskId,
        [Description("A chave da issue (ECO-123).")] string issueKey,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "task_link_jira",
            DataArea.Tasks,
            async (runner, token) =>
            {
                var id = McpInput.Id(taskId, "o id da tarefa");
                var key = issueKey?.Trim().ToUpperInvariant() ?? string.Empty;

                var found = await runner.RunAsync<SearchExternalTasksHandler, ExternalTaskSearchResult>(
                    (handler, cancel) => handler.HandleAsync(new SearchExternalTasks(key), cancel), token);

                var issue = found.Items.FirstOrDefault(item => string.Equals(item.Id, key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DomainException(found.Failure is { } failure
                        ? $"Não foi possível consultar o Jira: {failure}"
                        : $"A issue {McpInput.Shown(key)} não foi encontrada no Jira.");

                return await runner.RunAsync<LinkTaskToExternalHandler, ExternalLink>(
                    (handler, cancel) => handler.HandleAsync(new LinkTaskToExternal(id, issue), cancel), token);
            },
            cancellationToken);

    [McpServerTool(Name = "task_refresh_jira", Title = "Atualizar issue da tarefa", Idempotent = true, OpenWorld = true)]
    [Description("Relê a issue vinculada no Jira e atualiza o retrato na tarefa (título, tipo, status). Não altera nada no Jira.")]
    public Task<ExternalLink> RefreshJiraAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "task_refresh_jira",
            DataArea.Tasks,
            (runner, token) => runner.RunAsync<RefreshExternalTaskHandler, ExternalLink>(
                (handler, cancel) => handler.HandleAsync(new RefreshExternalTask(McpInput.Id(taskId, "o id da tarefa")), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "sticky_note_list", Title = "Post-its", ReadOnly = true, Idempotent = true)]
    [Description("Os post-its: Active (padrão), Archived ou Trashed — o que ainda não virou tarefa.")]
    public Task<IReadOnlyList<StickyNoteRow>> ListStickyNotesAsync(
        [Description("Active, Archived ou Trashed.")] string? scope = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "sticky_note_list",
            (runner, token) => runner.RunAsync<GetStickyNotesHandler, IReadOnlyList<StickyNoteRow>>(
                (handler, cancel) => handler.HandleAsync(
                    new GetStickyNotes(McpInput.OptionalEnum<StickyNoteScope>(scope, "o escopo") ?? StickyNoteScope.Active), cancel),
                token),
            cancellationToken);

    private static Task<IReadOnlyList<TagRow>> Tags(IUseCaseRunner runner, CancellationToken cancellationToken) =>
        runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>((handler, token) => handler.HandleAsync(new GetTags(), token), cancellationToken);

    private static Task<IReadOnlyList<TagDirectoryRow>> Directories(IUseCaseRunner runner, Guid tagId, CancellationToken cancellationToken) =>
        runner.RunAsync<GetTagDirectoriesHandler, IReadOnlyList<TagDirectoryRow>>(
            (handler, token) => handler.HandleAsync(new GetTagDirectories(tagId), token), cancellationToken);

    private static Task<IReadOnlyList<TagDirectoryCommandRow>> Bindings(IUseCaseRunner runner, Guid directoryId, CancellationToken cancellationToken) =>
        runner.RunAsync<GetTagDirectoryCommandsHandler, IReadOnlyList<TagDirectoryCommandRow>>(
            (handler, token) => handler.HandleAsync(new GetTagDirectoryCommands(directoryId), token), cancellationToken);

    private static Task<IReadOnlyList<TaskDevelopmentView>> Developments(IUseCaseRunner runner, Guid taskId, CancellationToken cancellationToken) =>
        runner.RunAsync<GetTaskDevelopmentsHandler, IReadOnlyList<TaskDevelopmentView>>(
            (handler, token) => handler.HandleAsync(new GetTaskDevelopments(taskId), token), cancellationToken);
}

public sealed record DirectoryInfo(TagDirectoryRow Directory, IReadOnlyList<TagDirectoryCommandRow> Commands);

public sealed record TagDetail(TagRow Tag, IReadOnlyList<DirectoryInfo> Directories);

public sealed record WorktreeEntry(
    Guid DevelopmentId,
    Guid TaskId,
    string TaskTitle,
    string Repository,
    string Branch,
    string SourceBranch,
    string Path,
    string Status,
    string? FailureReason,
    IReadOnlyList<string> Commands)
{
    public static WorktreeEntry Of(TaskDevelopmentView view, string taskTitle) => new(
        view.Id,
        view.TaskId,
        taskTitle,
        view.RepositoryPath,
        view.Branch,
        view.SourceBranch,
        view.WorktreePath,
        view.Status.ToString(),
        view.FailureReason,
        view.Commands ?? []);
}

public sealed record GitContext(
    Guid TaskId,
    string TaskTitle,
    IReadOnlyList<WorktreeEntry> Environments,
    IReadOnlyList<WorktreeSync> GitState,
    IReadOnlyList<TagDirectoryRow> Directories,
    string? IssueKey,
    string? SuggestedBranch);

public sealed record ReminderDefaults(ReminderInfo DefaultPolicy, DateTimeOffset? PausedUntilUtc);
