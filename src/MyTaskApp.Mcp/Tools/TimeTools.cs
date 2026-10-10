using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>
/// Cronômetro e lançamentos (ADR-052, ADR-059). Os mesmos casos de uso da aba
/// "Tempo": um cronômetro só no app inteiro, sem sobreposição na mesma tarefa,
/// período sempre com fim depois do início e nunca no futuro.
/// </summary>
[McpServerToolType]
public sealed class TimeTools(McpGateway gateway, IUserClock clock, TimeProvider timeProvider)
{
    [McpServerTool(Name = "time_tracking_get_active", Title = "Cronômetro ativo", ReadOnly = true, Idempotent = true)]
    [Description("O cronômetro que corre agora, se houver — há no máximo um no app inteiro.")]
    public Task<ActiveTimerResult> GetActiveAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "time_tracking_get_active",
            async (runner, token) => new ActiveTimerResult(await Active(runner, token) is { } timer ? Describe(timer, clock, timeProvider) : null),
            cancellationToken);

    [McpServerTool(Name = "time_tracking_get_status", Title = "Situação do tempo", ReadOnly = true, Idempotent = true)]
    [Description("O cronômetro ativo, o total de hoje (com o que corre) e o último período encerrado.")]
    public Task<TimeStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "time_tracking_get_status",
            async (runner, token) =>
            {
                var today = clock.Today;
                var active = await Active(runner, token);
                var summary = await runner.RunAsync<TimeReportHandlers, TimeSummary>(
                    (handler, cancel) => handler.HandleAsync(new GetTimeSummary(today, today, TimeGrouping.Task), cancel), token);
                var last = await LastClosed(runner, token);

                return new TimeStatus(
                    Text.Date(today),
                    active is null ? null : Describe(active, clock, timeProvider),
                    Duration.Of(summary.Total),
                    summary.Groups.Select(group => new TimeGroup(group.Key, group.Label, Duration.Of(group.Total), group.EntryCount, group.EntryIds)).ToList(),
                    last is null ? null : TimeEntryInfo.Of(last));
            },
            cancellationToken);

    [McpServerTool(Name = "time_tracking_start", Title = "Iniciar cronômetro", Idempotent = false, Destructive = false)]
    [Description(
        "Começa a contar o tempo numa tarefa pendente. Já correndo nela, não faz nada. Correndo em outra, recusa — " +
        "a não ser com replaceRunning=true, que encerra o outro agora e começa este.")]
    public Task<TimerChangeResult> StartAsync(
        [Description("O id da tarefa.")] string taskId,
        [Description("Encerrar o cronômetro de outra tarefa, se houver, e começar este.")] bool replaceRunning = false,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "time_tracking_start",
            DataArea.Time | DataArea.Tasks,
            async (runner, token) =>
            {
                var task = await TaskTools.Details(runner, McpInput.Id(taskId, "o id da tarefa"), token);
                var before = await Active(runner, token);

                var started = await runner.RunAsync<StartTimerHandler, ActiveTimerView>(
                    (handler, cancel) => handler.HandleAsync(new StartTimer(task.Task.OccurrenceId, replaceRunning), cancel), token);

                var replaced = before is not null && before.EntryId != started.EntryId ? before : null;

                return new TimerChangeResult(
                    replaced is null
                        ? before?.EntryId == started.EntryId ? "O cronômetro já corria nesta tarefa." : "Cronômetro iniciado."
                        : $"Cronômetro de \"{replaced.TaskTitle}\" encerrado; iniciado em \"{started.TaskTitle}\".",
                    Describe(started, clock, timeProvider),
                    replaced is null ? null : await Entry(runner, replaced.EntryId, token));
            },
            cancellationToken);

    [McpServerTool(Name = "time_tracking_stop", Title = "Parar cronômetro", Idempotent = true, Destructive = false)]
    [Description("Encerra o cronômetro ativo agora e devolve o período gravado. Sem taskId, para o que estiver correndo.")]
    public Task<TimerChangeResult> StopAsync(
        [Description("O id da tarefa; vazio = a que estiver com o cronômetro.")] string? taskId = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "time_tracking_stop",
            DataArea.Time | DataArea.Tasks,
            async (runner, token) =>
            {
                var active = await Active(runner, token)
                    ?? throw new DomainException("Nenhum cronômetro está correndo.");

                if (McpInput.OptionalId(taskId, "o id da tarefa") is { } id && id != active.TaskId)
                {
                    throw new DomainException($"O cronômetro corre em \"{active.TaskTitle}\", e não nesta tarefa.");
                }

                await runner.RunAsync<StopTimerHandler>(
                    (handler, cancel) => handler.HandleAsync(new StopTimer(active.OccurrenceId), cancel), token);

                return new TimerChangeResult("Cronômetro parado.", null, await Entry(runner, active.EntryId, token));
            },
            cancellationToken);

    [McpServerTool(Name = "time_tracking_resume", Title = "Retomar cronômetro", Idempotent = false, Destructive = false)]
    [Description(
        "Começa um período novo na tarefa do último período encerrado (ou na informada). O app não estende períodos antigos: " +
        "retomar é iniciar outra vez, e o intervalo entre os dois não conta.")]
    public Task<TimerChangeResult> ResumeAsync(
        [Description("O id da tarefa; vazio = a do último período encerrado.")] string? taskId = null,
        [Description("Encerrar o cronômetro de outra tarefa, se houver.")] bool replaceRunning = false,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "time_tracking_resume",
            DataArea.Time | DataArea.Tasks,
            async (runner, token) =>
            {
                Guid occurrenceId;

                if (McpInput.OptionalId(taskId, "o id da tarefa") is { } id)
                {
                    occurrenceId = (await TaskTools.Details(runner, id, token)).Task.OccurrenceId;
                }
                else
                {
                    occurrenceId = (await LastClosed(runner, token)
                        ?? throw new DomainException("Não há período encerrado para retomar: use time_tracking_start.")).OccurrenceId;
                }

                var started = await runner.RunAsync<StartTimerHandler, ActiveTimerView>(
                    (handler, cancel) => handler.HandleAsync(new StartTimer(occurrenceId, replaceRunning), cancel), token);

                return new TimerChangeResult($"Cronômetro retomado em \"{started.TaskTitle}\".", Describe(started, clock, timeProvider), null);
            },
            cancellationToken);

    [McpServerTool(Name = "time_entry_create", Title = "Lançar horas", Idempotent = false, Destructive = false)]
    [Description(
        "Lança um período manual numa tarefa, no fuso do usuário. O fim precisa ser depois do início e não pode estar no futuro, " +
        "e o período não pode se sobrepor a outro da mesma tarefa. Vale também para tarefa concluída; não para arquivada ou na lixeira.")]
    public Task<TimeEntryInfo> CreateEntryAsync(
        [Description("O id da tarefa.")] string taskId,
        [Description("Dia do início (AAAA-MM-DD).")] string startDate,
        [Description("Hora do início (HH:mm).")] string startTime,
        [Description("Hora do fim (HH:mm).")] string endTime,
        [Description("Dia do fim (AAAA-MM-DD); vazio = o dia do início.")] string? endDate = null,
        [Description("Uma nota (até 500 caracteres).")] string? note = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "time_entry_create",
            DataArea.Time | DataArea.Tasks,
            async (runner, token) =>
            {
                var task = await TaskTools.Details(runner, McpInput.Id(taskId, "o id da tarefa"), token);
                var start = McpInput.Date(startDate, "o dia do início");

                var id = await runner.RunAsync<AddTimeEntryHandler, Guid>(
                    (handler, cancel) => handler.HandleAsync(
                        new AddTimeEntry(
                            task.Task.OccurrenceId,
                            start,
                            McpInput.Time(startTime, "a hora do início"),
                            McpInput.OptionalDate(endDate, "o dia do fim") ?? start,
                            McpInput.Time(endTime, "a hora do fim"),
                            note),
                        cancel),
                    token);

                return await Entry(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "time_entry_list", Title = "Listar apontamentos", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lista períodos por intervalo de dias locais, tarefa, etiqueta, issue ou projeto do Jira, origem (Timer ou Manual) e situação. " +
        "Cada um traz a duração inteira e a parte dentro do intervalo; o total é a soma dessas partes, de todos os que casaram.")]
    public Task<TimeEntryPage> ListEntriesAsync(
        [Description("Primeiro dia (AAAA-MM-DD), inclusive.")] string? from = null,
        [Description("Último dia (AAAA-MM-DD), inclusive.")] string? to = null,
        [Description("O id da tarefa.")] string? taskId = null,
        [Description("Etiqueta por nome ou id.")] string? tag = null,
        [Description("Chave da issue (ECO-123) ou do projeto (ECO).")] string? issueKey = null,
        [Description("Timer ou Manual.")] string? source = null,
        [Description("true = só o que corre; false = só os encerrados.")] bool? running = null,
        [Description("Incluir tarefas na lixeira.")] bool includeTrashed = false,
        [Description("Quantos por página, de 1 a 500.")] int limit = GetTimeEntries.DefaultLimit,
        [Description("Quantos pular.")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "time_entry_list",
            async (runner, token) =>
            {
                var request = new GetTimeEntries(
                    McpInput.OptionalDate(from, "o primeiro dia"),
                    McpInput.OptionalDate(to, "o último dia"),
                    McpInput.OptionalId(taskId, "o id da tarefa"),
                    tag is null ? null : (await TagLookup.ResolveAsync(runner, [tag], token)).Single(),
                    issueKey,
                    McpInput.OptionalEnum<TimeEntrySource>(source, "a origem"),
                    running,
                    includeTrashed,
                    limit,
                    offset);

                var report = await runner.RunAsync<TimeReportHandlers, TimeEntryReport>(
                    (handler, cancel) => handler.HandleAsync(request, cancel), token);

                return new TimeEntryPage(
                    Text.Date(report.From),
                    Text.Date(report.To),
                    report.Entries.Select(TimeEntryInfo.Of).ToList(),
                    report.Count,
                    Duration.Of(report.Total),
                    report.Offset,
                    report.Limit);
            },
            cancellationToken);

    [McpServerTool(Name = "time_entry_get", Title = "Consultar apontamento", ReadOnly = true, Idempotent = true)]
    [Description("Um período pelo id: tarefa, início, fim, duração, origem, nota, quando foi criado e quando foi alterado.")]
    public Task<TimeEntryInfo> GetEntryAsync(
        [Description("O id do período.")] string entryId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "time_entry_get",
            (runner, token) => Entry(runner, McpInput.Id(entryId, "o id do período"), token),
            cancellationToken);

    [McpServerTool(Name = "time_entry_update", Title = "Editar apontamento", Idempotent = true)]
    [Description(
        "Corrige um período encerrado: só os campos informados mudam. As mesmas regras do lançamento valem, e a mudança é " +
        "auditada com o antes e o depois. O período que ainda corre não se edita: pare o cronômetro antes.")]
    public Task<TimeEntryInfo> UpdateEntryAsync(
        [Description("O id do período.")] string entryId,
        [Description("Novo dia do início (AAAA-MM-DD).")] string? startDate = null,
        [Description("Nova hora do início (HH:mm).")] string? startTime = null,
        [Description("Novo dia do fim (AAAA-MM-DD).")] string? endDate = null,
        [Description("Nova hora do fim (HH:mm).")] string? endTime = null,
        [Description("Nova nota.")] string? note = null,
        [Description("Apaga a nota.")] bool clearNote = false,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "time_entry_update",
            DataArea.Time | DataArea.Tasks,
            async (runner, token) =>
            {
                var id = McpInput.Id(entryId, "o id do período");

                // O registro bruto, e não o texto da resposta: "08:31:47" não pode
                // virar "08:31" só porque a nota mudou.
                var current = await runner.RunAsync<TimeReportHandlers, TimeEntryReportItem>(
                    (handler, cancel) => handler.HandleAsync(new GetTimeEntry(id), cancel), token);

                if (current.IsRunning || current.EndDate is not { } currentEndDate || current.EndTime is not { } currentEndTime)
                {
                    throw new DomainException("Este período ainda corre: pare o cronômetro antes de corrigi-lo.");
                }

                await runner.RunAsync<UpdateTimeEntryHandler>(
                    (handler, cancel) => handler.HandleAsync(
                        new UpdateTimeEntry(
                            id,
                            McpInput.OptionalDate(startDate, "o dia do início") ?? current.StartDate,
                            McpInput.OptionalTime(startTime, "a hora do início") ?? current.StartTime,
                            McpInput.OptionalDate(endDate, "o dia do fim") ?? currentEndDate,
                            McpInput.OptionalTime(endTime, "a hora do fim") ?? currentEndTime,
                            clearNote ? null : note ?? current.Note),
                        cancel),
                    token);

                return await Entry(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "time_entry_delete", Title = "Excluir apontamento", Destructive = true, Idempotent = false)]
    [Description("Exclui um período. A exclusão é auditada na trilha da tarefa com o período que existia.")]
    public Task<TimeEntryDeleted> DeleteEntryAsync(
        [Description("O id do período.")] string entryId,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "time_entry_delete",
            DataArea.Time | DataArea.Tasks,
            async (runner, token) =>
            {
                var id = McpInput.Id(entryId, "o id do período");
                var current = await Entry(runner, id, token);

                await runner.RunAsync<DeleteTimeEntryHandler>(
                    (handler, cancel) => handler.HandleAsync(new DeleteTimeEntry(id), cancel), token);

                return new TimeEntryDeleted("Período excluído.", current);
            },
            cancellationToken);

    [McpServerTool(Name = "time_entry_get_summary", Title = "Resumo de horas", ReadOnly = true, Idempotent = true)]
    [Description(
        "Totais de um intervalo de dias locais agrupados por Day, Task, Tag, External (issue do Jira) ou Source (Timer/Manual), " +
        "com os ids dos períodos de cada grupo. Por etiqueta, um período com duas etiquetas conta nas duas; o total geral conta uma vez.")]
    public Task<TimeSummaryResult> GetSummaryAsync(
        [Description("Primeiro dia (AAAA-MM-DD), inclusive.")] string from,
        [Description("Último dia (AAAA-MM-DD), inclusive. Até 366 dias.")] string to,
        [Description("Day (padrão), Task, Tag, External ou Source.")] string? groupBy = null,
        [Description("O id da tarefa.")] string? taskId = null,
        [Description("Etiqueta por nome ou id.")] string? tag = null,
        [Description("Chave da issue (ECO-123) ou do projeto (ECO).")] string? issueKey = null,
        [Description("Timer ou Manual.")] string? source = null,
        [Description("Contar o cronômetro que corre até agora.")] bool includeRunning = true,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "time_entry_get_summary",
            async (runner, token) =>
            {
                var request = new GetTimeSummary(
                    McpInput.Date(from, "o primeiro dia"),
                    McpInput.Date(to, "o último dia"),
                    McpInput.OptionalEnum<TimeGrouping>(groupBy, "o agrupamento") ?? TimeGrouping.Day,
                    McpInput.OptionalId(taskId, "o id da tarefa"),
                    tag is null ? null : (await TagLookup.ResolveAsync(runner, [tag], token)).Single(),
                    issueKey,
                    McpInput.OptionalEnum<TimeEntrySource>(source, "a origem"),
                    includeRunning);

                var summary = await runner.RunAsync<TimeReportHandlers, TimeSummary>(
                    (handler, cancel) => handler.HandleAsync(request, cancel), token);

                return new TimeSummaryResult(
                    Text.Date(summary.From),
                    Text.Date(summary.To),
                    summary.GroupBy.ToString(),
                    Duration.Of(summary.Total),
                    summary.EntryCount,
                    summary.Groups.Select(group => new TimeGroup(group.Key, group.Label, Duration.Of(group.Total), group.EntryCount, group.EntryIds)).ToList());
            },
            cancellationToken);

    [McpServerTool(Name = "time_entry_get_task_log", Title = "Tempo da tarefa", ReadOnly = true, Idempotent = true)]
    [Description("A aba \"Tempo\" de uma tarefa: os períodos por dia, o total registrado, o que corre e a estimativa.")]
    public Task<TaskTimeLogView> GetTaskLogAsync(
        [Description("O id da tarefa.")] string taskId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "time_entry_get_task_log",
            async (runner, token) =>
            {
                var task = await TaskTools.Details(runner, McpInput.Id(taskId, "o id da tarefa"), token);

                return await runner.RunAsync<GetTaskTimeLogHandler, TaskTimeLogView>(
                    (handler, cancel) => handler.HandleAsync(new GetTaskTimeLog(task.Task.OccurrenceId), cancel), token);
            },
            cancellationToken);

    internal static TimerInfo Describe(ActiveTimerView timer, IUserClock clock, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(timer.StartedAt, clock.TimeZone);

        return new TimerInfo(
            timer.EntryId,
            timer.TaskId,
            timer.OccurrenceId,
            timer.TaskTitle,
            timer.StartedAt,
            local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Duration.Of(now > timer.StartedAt ? now - timer.StartedAt : TimeSpan.Zero));
    }

    private static Task<ActiveTimerView?> Active(IUseCaseRunner runner, CancellationToken cancellationToken) =>
        runner.RunAsync<GetActiveTimerHandler, ActiveTimerView?>((handler, token) => handler.HandleAsync(token), cancellationToken);

    private static async Task<TimeEntryInfo> Entry(IUseCaseRunner runner, Guid entryId, CancellationToken cancellationToken) =>
        TimeEntryInfo.Of(await runner.RunAsync<TimeReportHandlers, TimeEntryReportItem>(
            (handler, token) => handler.HandleAsync(new GetTimeEntry(entryId), token),
            cancellationToken));

    private static async Task<TimeEntryReportItem?> LastClosed(IUseCaseRunner runner, CancellationToken cancellationToken)
    {
        var report = await runner.RunAsync<TimeReportHandlers, TimeEntryReport>(
            (handler, token) => handler.HandleAsync(new GetTimeEntries(Running: false, Limit: 1), token),
            cancellationToken);

        return report.Entries.FirstOrDefault();
    }
}

public sealed record ActiveTimerResult(TimerInfo? Timer);

public sealed record TimeGroup(string Key, string Label, Duration Total, int EntryCount, IReadOnlyList<Guid> EntryIds);

public sealed record TimeStatus(string Today, TimerInfo? ActiveTimer, Duration TodayTotal, IReadOnlyList<TimeGroup> TodayByTask, TimeEntryInfo? LastClosedEntry);

/// <param name="Timer">O cronômetro que ficou correndo, se algum.</param>
/// <param name="ClosedEntry">O período que esta operação encerrou, relido do banco.</param>
public sealed record TimerChangeResult(string Message, TimerInfo? Timer, TimeEntryInfo? ClosedEntry);

public sealed record TimeEntryPage(string? From, string? To, IReadOnlyList<TimeEntryInfo> Entries, int Count, Duration Total, int Offset, int Limit);

public sealed record TimeEntryDeleted(string Message, TimeEntryInfo Deleted);

public sealed record TimeSummaryResult(string From, string To, string GroupBy, Duration Total, int EntryCount, IReadOnlyList<TimeGroup> Groups);
