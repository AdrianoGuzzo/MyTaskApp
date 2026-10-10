using System.ComponentModel;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>Tarefas: consultar, criar, editar e mudar de estado (ADR-059).</summary>
[McpServerToolType]
public sealed class TaskTools(McpGateway gateway, IUserClock clock, TimeProvider timeProvider)
{
    private const string TagsHelp = "Etiquetas por nome ou id (veja tag_list).";

    [McpServerTool(Name = "task_list", Title = "Listar tarefas", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lista tarefas com filtros. Sem filtro de ciclo de vida, mostra a lista principal (ativas e concluídas, sem arquivadas nem lixeira). " +
        "Datas em AAAA-MM-DD, no fuso do usuário. Paginado: use offset para as próximas.")]
    public Task<TaskPage> ListAsync(
        [Description("Status da ocorrência: Pending, Completed, Cancelled.")] string[]? statuses = null,
        [Description("Ciclo de vida: Active, Completed, Archived, Trashed.")] string[]? lifecycles = null,
        [Description(TagsHelp + " Basta ter uma delas.")] string[]? tags = null,
        [Description("Prioridades: Low, Normal, High, Urgent.")] string[]? priorities = null,
        [Description("Agendadas a partir deste dia (AAAA-MM-DD).")] string? scheduledFrom = null,
        [Description("Agendadas até este dia (AAAA-MM-DD).")] string? scheduledTo = null,
        [Description("true = só com data marcada; false = só sem data.")] bool? hasSchedule = null,
        [Description("true = só com prazo; false = só sem prazo.")] bool? hasDeadline = null,
        [Description("Ordem: Created (padrão, mais novas primeiro), Scheduled, Deadline, Priority, Title.")] string? sort = null,
        [Description("Quantas por página, de 1 a 200.")] int limit = SearchTasks.DefaultLimit,
        [Description("Quantas pular.")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            "task_list",
            null, statuses, lifecycles, tags, priorities, scheduledFrom, scheduledTo, hasSchedule,
            null, null, hasDeadline, null, overdueOnly: false, sort, limit, offset, cancellationToken);

    [McpServerTool(Name = "task_search", Title = "Buscar tarefas", ReadOnly = true, Idempotent = true)]
    [Description(
        "Busca tarefas por texto (título, anotação, próxima ação, chave e título da issue), status, etiqueta, prioridade, " +
        "issue do Jira ou projeto (prefixo da chave, como ECO) e faixas de agendamento e prazo.")]
    public Task<TaskPage> SearchAsync(
        [Description("Texto a procurar; vazio = sem filtro de texto.")] string? text = null,
        [Description("Status da ocorrência: Pending, Completed, Cancelled.")] string[]? statuses = null,
        [Description("Ciclo de vida: Active, Completed, Archived, Trashed. Vazio = lista principal.")] string[]? lifecycles = null,
        [Description(TagsHelp + " Basta ter uma delas.")] string[]? tags = null,
        [Description("Prioridades: Low, Normal, High, Urgent.")] string[]? priorities = null,
        [Description("Agendadas a partir deste dia (AAAA-MM-DD).")] string? scheduledFrom = null,
        [Description("Agendadas até este dia (AAAA-MM-DD).")] string? scheduledTo = null,
        [Description("Prazo a partir deste dia (AAAA-MM-DD).")] string? deadlineFrom = null,
        [Description("Prazo até este dia (AAAA-MM-DD).")] string? deadlineTo = null,
        [Description("Chave da issue (ECO-123) ou do projeto (ECO).")] string? issueKey = null,
        [Description("Só as atrasadas.")] bool overdueOnly = false,
        [Description("Ordem: Created, Scheduled, Deadline, Priority, Title.")] string? sort = null,
        [Description("Quantas por página, de 1 a 200.")] int limit = SearchTasks.DefaultLimit,
        [Description("Quantas pular.")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            "task_search",
            text, statuses, lifecycles, tags, priorities, scheduledFrom, scheduledTo, null,
            deadlineFrom, deadlineTo, null, issueKey, overdueOnly, sort, limit, offset, cancellationToken);

    [McpServerTool(Name = "task_get", Title = "Consultar tarefa", ReadOnly = true, Idempotent = true)]
    [Description(
        "Todos os campos de uma tarefa, em qualquer estado (inclusive arquivada e na lixeira): anotação, prioridade, data, prazo, " +
        "etiquetas, issue, próxima ação, estimativa, tempo registrado, lembrete e worktrees. Consulte antes de editar.")]
    public Task<TaskDetail> GetAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "task_get",
            async (runner, token) => TaskDetail.Of(await Details(runner, McpInput.Id(taskId, "o id da tarefa"), token)),
            cancellationToken);

    [McpServerTool(Name = "task_get_today", Title = "Tarefas de hoje", ReadOnly = true, Idempotent = true)]
    [Description("O quadro Hoje, como a tela mostra: atrasadas, agora, hoje, sem horário, prazos e concluídas hoje, e o cronômetro ativo.")]
    public Task<TodayResult> GetTodayAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "task_get_today",
            async (runner, token) =>
            {
                var board = await runner.RunAsync<GetTodayBoardHandler, TodayBoard>(
                    (handler, cancel) => handler.HandleAsync(cancel), token);

                IReadOnlyList<TaskSummary> Section(IReadOnlyList<TodayTask> tasks, bool overdue = false, bool completed = false) =>
                    tasks.Select(task => TaskSummary.Of(task, completed, overdue || task.IsLate)).ToList();

                return new TodayResult(
                    Text.Date(board.Date),
                    Section(board.Overdue, overdue: true),
                    Section(board.Now),
                    Section(board.Today),
                    Section(board.Unscheduled),
                    Section(board.Deadlines),
                    Section(board.Completed, completed: true),
                    board.RemainingCount,
                    board.ActiveTimer is { } timer ? TimeTools.Describe(timer, clock, timeProvider) : null);
            },
            cancellationToken);

    [McpServerTool(Name = "task_get_overdue", Title = "Tarefas atrasadas", ReadOnly = true, Idempotent = true)]
    [Description("As tarefas pendentes atrasadas, pelo critério do quadro Hoje (data passada, hora de hoje passada ou prazo vencido), pelo prazo.")]
    public Task<TaskPage> GetOverdueAsync(
        [Description("Quantas, de 1 a 200.")] int limit = SearchTasks.MaxLimit,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            "task_get_overdue",
            null, [nameof(TaskItemStatus.Pending)], null, null, null, null, null, null,
            null, null, null, null, overdueOnly: true, nameof(TaskSort.Deadline), limit, 0, cancellationToken);

    [McpServerTool(Name = "task_get_history", Title = "Histórico da tarefa", ReadOnly = true, Idempotent = true)]
    [Description(
        "A trilha de auditoria de uma tarefa: criação, conclusão, reabertura, arquivo, lixeira, restauração, exclusão definitiva e " +
        "lançamentos de tempo, com data, autor e origem (\"(MCP)\" quando veio daqui). Sobrevive à exclusão da tarefa.")]
    public Task<IReadOnlyList<AuditEntryInfo>> GetHistoryAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "task_get_history",
            async (runner, token) => (IReadOnlyList<AuditEntryInfo>)(await runner.RunAsync<GetChecklistAuditHandler, IReadOnlyList<TaskAuditEntry>>(
                    (handler, cancel) => handler.HandleAsync(new GetChecklistAudit(McpInput.Id(taskId, "o id da tarefa")), cancel),
                    token))
                .Select(AuditEntryInfo.Of)
                .ToList(),
            cancellationToken);

    [McpServerTool(Name = "task_get_weekly_history", Title = "Atividades da semana", ReadOnly = true, Idempotent = true)]
    [Description("O que foi concluído e trabalhado em cada um dos últimos dias (padrão: 7), no fuso do usuário. Lixeira fora, arquivo dentro.")]
    public Task<ActivityHistoryView> GetWeeklyHistoryAsync(
        [Description("Quantos dias para trás, contando hoje (1 a 31).")] int days = GetActivityHistory.Week,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "task_get_weekly_history",
            (runner, token) =>
            {
                if (days is < 1 or > 31)
                {
                    throw new DomainException("O histórico cobre de 1 a 31 dias.");
                }

                return runner.RunAsync<GetActivityHistoryHandler, ActivityHistoryView>(
                    (handler, cancel) => handler.HandleAsync(new GetActivityHistory(days), cancel), token);
            },
            cancellationToken);

    [McpServerTool(Name = "task_get_statistics", Title = "Estatísticas", ReadOnly = true, Idempotent = true)]
    [Description("Contagens da lista inteira: por ciclo de vida e prioridade, atrasadas, para hoje, concluídas recentes e horas trabalhadas hoje e em 7 dias.")]
    public Task<TaskStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "task_get_statistics",
            (runner, token) => runner.RunAsync<GetTaskStatisticsHandler, TaskStatistics>(
                (handler, cancel) => handler.HandleAsync(new GetTaskStatistics(), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_create", Title = "Criar tarefa", Idempotent = false, Destructive = false)]
    [Description(
        "Cria uma tarefa. Só o título é obrigatório; o resto é opcional. Sem lembrete informado, vale o padrão do usuário. " +
        "Retorna a tarefa como ficou gravada.")]
    public Task<TaskOperationResult> CreateAsync(
        [Description("O título (até 200 caracteres).")] string title,
        [Description("A anotação, em Markdown.")] string? description = null,
        [Description("Low, Normal (padrão), High ou Urgent.")] string? priority = null,
        [Description("Dia marcado (AAAA-MM-DD).")] string? scheduledDate = null,
        [Description("Hora marcada (HH:mm); exige o dia.")] string? scheduledTime = null,
        [Description("Dia do prazo (AAAA-MM-DD); precisa ficar no futuro.")] string? deadlineDate = null,
        [Description("Hora do prazo (HH:mm); sem ela, vale o horário padrão do prazo.")] string? deadlineTime = null,
        [Description(TagsHelp)] string[]? tags = null,
        [Description("A próxima ação (até 200 caracteres).")] string? nextAction = null,
        [Description("A estimativa em minutos (1 a 59940).")] int? estimateMinutes = null,
        [Description("Lembrete: Default, Urgent ou None. Vazio = o padrão do usuário.")] string? reminder = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "task_create",
            DataArea.Tasks,
            async (runner, token) =>
            {
                var details = new EditTask(Guid.Empty)
                {
                    Deadline = await Deadline(runner, deadlineDate, deadlineTime, token),
                    TagIds = tags is null ? null : await TagLookup.ResolveAsync(runner, tags, token),
                    NextAction = nextAction is null ? null : new Change<string?>(nextAction),
                    Estimate = Estimate(estimateMinutes),
                };

                var created = await runner.RunAsync<CreateDetailedTaskHandler, CreateTaskResult>(
                    (handler, cancel) => handler.HandleAsync(
                        new CreateDetailedTask(
                            new CreateTask(
                                title,
                                description,
                                McpInput.OptionalEnum<TaskPriority>(priority, "a prioridade") ?? TaskPriority.Normal,
                                McpInput.OptionalDate(scheduledDate, "o dia marcado"),
                                McpInput.OptionalTime(scheduledTime, "a hora marcada"),
                                Reminder(reminder)),
                            details),
                        cancel),
                    token);

                return new TaskOperationResult("Tarefa criada.", TaskDetail.Of(await Details(runner, created.TaskId, token)));
            },
            cancellationToken);

    [McpServerTool(Name = "task_update", Title = "Editar tarefa", Destructive = true, Idempotent = true)]
    [Description(
        "Altera só os campos informados de uma tarefa, numa operação só: se um campo for recusado, nenhum muda. " +
        "Para apagar um campo, use o clear correspondente. Etiquetas substituem o conjunto inteiro. Consulte task_get antes.")]
    public Task<TaskOperationResult> UpdateAsync(
        [Description("O id da tarefa.")] string taskId,
        [Description("Novo título.")] string? title = null,
        [Description("Nova anotação, em Markdown.")] string? description = null,
        [Description("Apaga a anotação.")] bool clearDescription = false,
        [Description("Low, Normal, High ou Urgent.")] string? priority = null,
        [Description("Novo dia marcado (AAAA-MM-DD).")] string? scheduledDate = null,
        [Description("Nova hora marcada (HH:mm); exige o dia.")] string? scheduledTime = null,
        [Description("Tira a data marcada.")] bool clearSchedule = false,
        [Description("Novo dia do prazo (AAAA-MM-DD), no futuro.")] string? deadlineDate = null,
        [Description("Nova hora do prazo (HH:mm).")] string? deadlineTime = null,
        [Description("Remove o prazo.")] bool clearDeadline = false,
        [Description(TagsHelp + " Substitui todas; lista vazia tira todas.")] string[]? tags = null,
        [Description("Nova próxima ação.")] string? nextAction = null,
        [Description("Apaga a próxima ação.")] bool clearNextAction = false,
        [Description("Nova estimativa em minutos (1 a 59940).")] int? estimateMinutes = null,
        [Description("Apaga a estimativa.")] bool clearEstimate = false,
        [Description("Lembrete: Default, Urgent ou None.")] string? reminder = null,
        CancellationToken cancellationToken = default) =>
        EditAsync("task_update", taskId, title, description, clearDescription, priority, scheduledDate, scheduledTime, clearSchedule, deadlineDate, deadlineTime, clearDeadline, tags, nextAction, clearNextAction, estimateMinutes, clearEstimate, reminder, cancellationToken);

    [McpServerTool(Name = "task_complete", Title = "Concluir tarefa", Idempotent = false, Destructive = false)]
    [Description("Conclui a tarefa. Se o cronômetro corria nela, o período fecha agora.")]
    public Task<TaskOperationResult> CompleteAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_complete", taskId, "Tarefa concluída.",
            (runner, occurrenceId, token) => runner.RunAsync<CompleteOccurrenceHandler>(
                (handler, cancel) => handler.HandleAsync(new CompleteOccurrence(occurrenceId), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_reopen", Title = "Reabrir tarefa", Idempotent = false, Destructive = false)]
    [Description("Reabre uma tarefa concluída ou cancelada. Não retoma o cronômetro. Arquivada ou na lixeira precisa ser restaurada antes.")]
    public Task<TaskOperationResult> ReopenAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_reopen", taskId, "Tarefa reaberta.",
            (runner, occurrenceId, token) => runner.RunAsync<ReopenOccurrenceHandler>(
                (handler, cancel) => handler.HandleAsync(new ReopenOccurrence(occurrenceId), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_cancel", Title = "Cancelar tarefa", Destructive = true, Idempotent = false)]
    [Description("Cancela a tarefa (não conta como concluída). Para o cronômetro dela. Pode ser reaberta depois.")]
    public Task<TaskOperationResult> CancelAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_cancel", taskId, "Tarefa cancelada.",
            (runner, occurrenceId, token) => runner.RunAsync<CancelOccurrenceHandler>(
                (handler, cancel) => handler.HandleAsync(new CancelOccurrence(occurrenceId), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_archive", Title = "Arquivar tarefa", Idempotent = false, Destructive = false)]
    [Description("Arquiva a tarefa: sai da lista principal, fica só leitura e pode ser restaurada com task_restore.")]
    public Task<TaskOperationResult> ArchiveAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_archive", taskId, "Tarefa arquivada.",
            (runner, _, token) => runner.RunAsync<ArchiveChecklistHandler>(
                (handler, cancel) => handler.HandleAsync(new ArchiveChecklist(McpInput.Id(taskId, "o id da tarefa")), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_restore", Title = "Restaurar do arquivo", Idempotent = false, Destructive = false)]
    [Description("Traz uma tarefa arquivada de volta para a lista principal.")]
    public Task<TaskOperationResult> RestoreAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_restore", taskId, "Tarefa restaurada do arquivo.",
            (runner, _, token) => runner.RunAsync<RestoreChecklistHandler>(
                (handler, cancel) => handler.HandleAsync(new RestoreChecklist(McpInput.Id(taskId, "o id da tarefa")), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_delete", Title = "Excluir tarefa (lixeira)", Destructive = true, Idempotent = false)]
    [Description(
        "Manda a tarefa para a lixeira, como a tela faz: recuperável com task_restore_from_trash até a retenção da lixeira vencer. " +
        "A exclusão definitiva não é oferecida pelo MCP. A operação é auditada.")]
    public Task<TaskOperationResult> DeleteAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_delete", taskId, "Tarefa enviada para a lixeira.",
            (runner, _, token) => runner.RunAsync<MoveChecklistToTrashHandler>(
                (handler, cancel) => handler.HandleAsync(new MoveChecklistToTrash(McpInput.Id(taskId, "o id da tarefa")), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_restore_from_trash", Title = "Restaurar da lixeira", Idempotent = false, Destructive = false)]
    [Description("Tira a tarefa da lixeira e a devolve ao estado de antes (uma arquivada volta para o arquivo).")]
    public Task<TaskOperationResult> RestoreFromTrashAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        Transition("task_restore_from_trash", taskId, "Tarefa restaurada da lixeira.",
            (runner, _, token) => runner.RunAsync<RestoreChecklistFromTrashHandler>(
                (handler, cancel) => handler.HandleAsync(new RestoreChecklistFromTrash(McpInput.Id(taskId, "o id da tarefa")), cancel), token),
            cancellationToken);

    [McpServerTool(Name = "task_set_deadline", Title = "Definir prazo", Idempotent = true)]
    [Description("Define o prazo da tarefa. Precisa ficar no futuro. Sem hora, vale o horário padrão do prazo (configuração do usuário).")]
    public Task<TaskOperationResult> SetDeadlineAsync(
        [Description("O id da tarefa.")] string taskId,
        [Description("Dia do prazo (AAAA-MM-DD).")] string date,
        [Description("Hora do prazo (HH:mm).")] string? time = null,
        CancellationToken cancellationToken = default) =>
        EditAsync("task_set_deadline", taskId, deadlineDate: date, deadlineTime: time ?? string.Empty, cancellationToken: cancellationToken);

    [McpServerTool(Name = "task_clear_deadline", Title = "Remover prazo", Idempotent = true)]
    [Description("Remove o prazo da tarefa.")]
    public Task<TaskOperationResult> ClearDeadlineAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        EditAsync("task_clear_deadline", taskId, clearDeadline: true, cancellationToken: cancellationToken);

    [McpServerTool(Name = "task_set_reminder", Title = "Definir lembrete", Idempotent = true)]
    [Description("Troca o lembrete da tarefa por um dos padrões: Default (1 h depois, repete a cada 15 min), Urgent ou None (sem lembrete).")]
    public Task<TaskOperationResult> SetReminderAsync(
        [Description("O id da tarefa.")] string taskId,
        [Description("Default, Urgent ou None.")] string reminder,
        CancellationToken cancellationToken = default) =>
        EditAsync("task_set_reminder", taskId, reminder: reminder, cancellationToken: cancellationToken);

    /// <summary>A edição de <c>task_update</c>, com o nome da ferramenta que pediu — o log diz qual foi.</summary>
    private Task<TaskOperationResult> EditAsync(
        string operation,
        string taskId,
        string? title = null,
        string? description = null,
        bool clearDescription = false,
        string? priority = null,
        string? scheduledDate = null,
        string? scheduledTime = null,
        bool clearSchedule = false,
        string? deadlineDate = null,
        string? deadlineTime = null,
        bool clearDeadline = false,
        string[]? tags = null,
        string? nextAction = null,
        bool clearNextAction = false,
        int? estimateMinutes = null,
        bool clearEstimate = false,
        string? reminder = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            operation,
            DataArea.Tasks,
            async (runner, token) =>
            {
                var id = McpInput.Id(taskId, "o id da tarefa");
                var current = await Details(runner, id, token);

                if (description is not null && clearDescription)
                {
                    throw new DomainException("Informe a anotação nova ou peça para apagá-la, não as duas coisas.");
                }

                var schedule = clearSchedule
                    ? new Change<(DateOnly?, TimeOnly?)>((null, null))
                    : scheduledDate is not null || scheduledTime is not null
                        // Só o dia muda: a hora marcada fica. Só a hora muda: o dia fica.
                        ? new Change<(DateOnly?, TimeOnly?)>((
                            McpInput.OptionalDate(scheduledDate, "o dia marcado") ?? current.Task.ScheduledDate,
                            McpInput.OptionalTime(scheduledTime, "a hora marcada") ?? current.Task.ScheduledTime))
                        : (Change<(DateOnly?, TimeOnly?)>?)null;

                var edit = new EditTask(id)
                {
                    Title = title,
                    Description = clearDescription ? new Change<string?>(null) : description is null ? null : new Change<string?>(description),
                    Priority = McpInput.OptionalEnum<TaskPriority>(priority, "a prioridade"),
                    Schedule = schedule,
                    Deadline = clearDeadline
                        ? new Change<TaskDeadline?>(null)
                        : await Deadline(runner, deadlineDate, deadlineTime, token, current.Task.Deadline?.Deadline),
                    TagIds = tags is null ? null : await TagLookup.ResolveAsync(runner, tags, token),
                    NextAction = clearNextAction ? new Change<string?>(null) : nextAction is null ? null : new Change<string?>(nextAction),
                    Estimate = clearEstimate ? new Change<TimeSpan?>(null) : Estimate(estimateMinutes),
                    Reminder = Reminder(reminder),
                };

                if (edit.IsEmpty)
                {
                    throw new DomainException("Nada para alterar: informe ao menos um campo.");
                }

                await runner.RunAsync<EditTaskHandler>((handler, cancel) => handler.HandleAsync(edit, cancel), token);

                return new TaskOperationResult("Tarefa alterada.", TaskDetail.Of(await Details(runner, id, token)));
            },
            cancellationToken);

    private Task<TaskPage> SearchAsync(
        string operation,
        string? text,
        string[]? statuses,
        string[]? lifecycles,
        string[]? tags,
        string[]? priorities,
        string? scheduledFrom,
        string? scheduledTo,
        bool? hasSchedule,
        string? deadlineFrom,
        string? deadlineTo,
        bool? hasDeadline,
        string? issueKey,
        bool overdueOnly,
        string? sort,
        int limit,
        int offset,
        CancellationToken cancellationToken) =>
        gateway.ReadAsync(
            operation,
            async (runner, token) =>
            {
                var request = new SearchTasks(
                    text,
                    McpInput.Enums<TaskLifecycle>(lifecycles, "o ciclo de vida"),
                    McpInput.Enums<TaskItemStatus>(statuses, "o status"),
                    tags is null or { Length: 0 } ? null : await TagLookup.ResolveAsync(runner, tags, token),
                    McpInput.Enums<TaskPriority>(priorities, "a prioridade"),
                    McpInput.OptionalDate(scheduledFrom, "o início do agendamento"),
                    McpInput.OptionalDate(scheduledTo, "o fim do agendamento"),
                    hasSchedule,
                    McpInput.OptionalDate(deadlineFrom, "o início do prazo"),
                    McpInput.OptionalDate(deadlineTo, "o fim do prazo"),
                    hasDeadline,
                    issueKey,
                    overdueOnly,
                    McpInput.OptionalEnum<TaskSort>(sort, "a ordem") ?? TaskSort.Created,
                    limit,
                    offset);

                var result = await runner.RunAsync<SearchTasksHandler, TaskSearchResult>(
                    (handler, cancel) => handler.HandleAsync(request, cancel), token);

                return new TaskPage(
                    result.Items.Select(TaskSummary.Of).ToList(),
                    result.Total,
                    result.Offset,
                    result.Limit,
                    result.Truncated,
                    result.Truncated
                        ? $"A busca parou em {TaskSearchCriteria.MaxRows} tarefas: o total é pelo menos isso. Use filtros mais estreitos."
                        : result.Total > result.Offset + result.Items.Count
                            ? $"Há mais {result.Total - result.Offset - result.Items.Count}: use offset={result.Offset + result.Items.Count}."
                            : null);
            },
            cancellationToken);

    private Task<TaskOperationResult> Transition(
        string operation,
        string taskId,
        string message,
        Func<IUseCaseRunner, Guid, CancellationToken, Task> change,
        CancellationToken cancellationToken) =>
        gateway.WriteAsync(
            operation,
            DataArea.Tasks | DataArea.Time,
            async (runner, token) =>
            {
                var current = await Details(runner, McpInput.Id(taskId, "o id da tarefa"), token);

                await change(runner, current.Task.OccurrenceId, token);

                return new TaskOperationResult(message, TaskDetail.Of(await Details(runner, current.Task.TaskId, token)));
            },
            cancellationToken);

    internal static Task<TaskListItem> Details(IUseCaseRunner runner, Guid taskId, CancellationToken cancellationToken) =>
        runner.RunAsync<GetTaskDetailsHandler, TaskListItem>(
            (handler, token) => handler.HandleAsync(new GetTaskDetails(taskId), token),
            cancellationToken);

    /// <summary>O prazo pedido; sem hora, a do prazo atual ou o horário padrão do usuário.</summary>
    private static async Task<Change<TaskDeadline?>?> Deadline(
        IUseCaseRunner runner,
        string? date,
        string? time,
        CancellationToken cancellationToken,
        TaskDeadline? current = null)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return string.IsNullOrWhiteSpace(time) || current is null
                ? string.IsNullOrWhiteSpace(time) ? null : throw new DomainException("A hora do prazo precisa do dia.")
                : new Change<TaskDeadline?>(new TaskDeadline(current.Date, McpInput.Time(time, "a hora do prazo")));
        }

        var day = McpInput.Date(date, "o dia do prazo");

        var hour = McpInput.OptionalTime(time, "a hora do prazo")
            ?? current?.Time
            ?? (await runner.RunAsync<GetDeadlineSettingsHandler, DeadlineSettings>(
                (handler, token) => handler.HandleAsync(new GetDeadlineSettings(), token),
                cancellationToken)).DefaultTime;

        return new Change<TaskDeadline?>(new TaskDeadline(day, hour));
    }

    private static Change<TimeSpan?>? Estimate(int? minutes) =>
        minutes is { } value ? new Change<TimeSpan?>(McpInput.Minutes(value, "a estimativa", 1, 999 * 60)) : null;

    private static ReminderPolicy? Reminder(string? preset) =>
        McpInput.OptionalEnum<ReminderPreset>(preset, "o lembrete") switch
        {
            ReminderPreset.Default => ReminderPolicy.Default,
            ReminderPreset.Urgent => ReminderPolicy.Urgent,
            ReminderPreset.None => ReminderPolicy.None,
            _ => null,
        };

    private enum ReminderPreset
    {
        Default,
        Urgent,
        None,
    }
}

public sealed record TodayResult(
    string Date,
    IReadOnlyList<TaskSummary> Overdue,
    IReadOnlyList<TaskSummary> Now,
    IReadOnlyList<TaskSummary> Today,
    IReadOnlyList<TaskSummary> Unscheduled,
    IReadOnlyList<TaskSummary> Deadlines,
    IReadOnlyList<TaskSummary> CompletedToday,
    int Remaining,
    TimerInfo? ActiveTimer);

public sealed record AuditEntryInfo(
    DateTimeOffset OccurredAt,
    string Operation,
    string Actor,
    string? ActorName,
    string TaskTitle,
    string? Details)
{
    public static AuditEntryInfo Of(TaskAuditEntry entry) =>
        new(entry.OccurredAt, entry.Operation.ToString(), entry.Actor.ToString(), entry.ActorName, entry.TaskTitle, entry.Details);
}

/// <summary>Etiquetas por nome ou id, com recusa explícita do que não existe.</summary>
internal static class TagLookup
{
    public static async Task<IReadOnlyCollection<Guid>> ResolveAsync(
        IUseCaseRunner runner,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return [];
        }

        var all = await runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
            (handler, token) => handler.HandleAsync(new GetTags(), token),
            cancellationToken);

        return tags
            .Select(wanted =>
            {
                var trimmed = wanted?.Trim() ?? string.Empty;

                return all.FirstOrDefault(tag =>
                        (Guid.TryParse(trimmed, out var id) && tag.Id == id)
                        || string.Equals(tag.Name, trimmed, StringComparison.CurrentCultureIgnoreCase))
                    ?.Id
                    ?? throw new DomainException($"Etiqueta \"{McpInput.Shown(wanted)}\" não encontrada. Veja tag_list.");
            })
            .Distinct()
            .ToList();
    }
}
