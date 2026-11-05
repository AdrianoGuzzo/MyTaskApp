using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Gráfico ou tabela: a mesma informação, duas leituras.</summary>
public enum HoursReportView
{
    Chart,
    Table,
}

/// <summary>Uma linha da tabela, já escrita.</summary>
/// <param name="Ratio">A barra da linha: o bloco contra o maior do período, de 0 a 1.</param>
/// <param name="IsMuted">Sem horas ou no futuro: a linha fica apagada, mas continua lá.</param>
public sealed record HoursReportRow(
    HoursBucket Bucket,
    string Label,
    string Duration,
    string Days,
    string Entries,
    string Share,
    double Ratio,
    bool IsMuted);

/// <summary>Uma tarefa no painel "Horas por tarefa".</summary>
/// <param name="TaskId">A tarefa cujos períodos o clique abre (ADR-102).</param>
public sealed record TaskHoursRow(Guid TaskId, string Title, string Duration, string Share, double Ratio);

/// <summary>
/// Um período da tarefa aberta no painel (ADR-102): "ter, 14/10 · 14:00 → 15:30",
/// com o que dele entra no bloco escolhido.
/// </summary>
/// <param name="Duration">
/// O que conta no bloco; o período que passa da borda (a meia-noite, o fim do
/// mês) diz também o tamanho inteiro: "1h de 3h".
/// </param>
public sealed record ReportEntryRow(
    TimeEntryReportItem Item,
    string DayLabel,
    string RangeLabel,
    string Duration,
    string SourceLabel)
{
    /// <summary>"1h 30min · Manual".</summary>
    public string Meta => $"{Duration} · {SourceLabel}";

    public string? Note => Item.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Item.Note);

    public bool IsRunning => Item.IsRunning;

    /// <summary>O que corre se para antes de corrigir, como na aba Tempo; excluir vale para os dois.</summary>
    public bool CanEdit => !Item.IsRunning;

    public string EditGlyph => "";

    public string DeleteGlyph => "";
}

/// <summary>Um passo da trilha "Anos › 2026 › outubro"; o último é onde se está.</summary>
public sealed record HoursCrumb(string Label, HoursScale Scale, bool IsCurrent, bool IsFirst);

/// <summary>Uma escolha do filtro de etiqueta; sem id, todas.</summary>
public sealed record TagFilterOption(Guid? Id, string Name)
{
    public static TagFilterOption All { get; } = new(null, "Todas as etiquetas");

    public override string ToString() => Name;
}

/// <summary>
/// A janela "Relatório de horas" (ADR-097): as horas lançadas por dia, mês ou
/// ano, em gráfico ou tabela, com as tarefas do bloco escolhido. Os números
/// vêm do <see cref="GetHoursHistoryHandler"/>, que já entrega os blocos
/// escritos — "hoje", "anterior" e "próximo" inclusive.
/// </summary>
/// <remarks>
/// Reajustar as horas (ADR-102): a tarefa clicada no painel mostra os períodos
/// dela no bloco, e cada um se corrige, exclui ou ganha um irmão pelos mesmos
/// casos de uso e o mesmo diálogo da aba Tempo. As regras (sem sobreposição,
/// fim depois do início, nada no futuro) continuam da Application.
/// </remarks>
public sealed partial class HoursReportViewModel(
    IUseCaseRunner runner,
    ITimeEntryEditor editor,
    IConfirmationDialog confirmation,
    IUserClock clock,
    ILogger<HoursReportViewModel> logger) : ObservableObject
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Num dia passado, o período novo começa aqui: o "agora" de lá não existe.</summary>
    private static readonly TimeOnly PastDayStart = new(9, 0);

    /// <summary>Descarta a resposta de uma carga que outra mais nova já superou.</summary>
    private int _load;

    /// <summary>O mesmo, para as horas por tarefa do bloco escolhido.</summary>
    private int _breakdown;

    /// <summary>O mesmo, para os períodos da tarefa aberta.</summary>
    private int _entriesLoad;

    /// <summary>
    /// A ocorrência em que um período novo entra: a do período mais recente que
    /// se viu da tarefa. Fica mesmo depois de o bloco esvaziar — é ali que o
    /// usuário volta a lançar.
    /// </summary>
    private Guid? _occurrenceId;

    /// <summary>O bloco a escolher quando a próxima carga chegar (o dia clicado no mapa).</summary>
    private string? _pendingKey;

    /// <summary>Trocar a lista ou a escolha por código não é o usuário escolhendo.</summary>
    private bool _applying;

    /// <summary>
    /// O mesmo seletor da lista (ADR-062), como no histórico de 7 dias: trocar
    /// aqui troca a lista também.
    /// </summary>
    public DeviceViewSelectorViewModel? Devices { get; private set; }

    /// <summary>Segue o seletor da lista: recarrega quando ele muda, se a janela estiver aberta.</summary>
    public void FollowDevices(DeviceViewSelectorViewModel devices)
    {
        Devices = devices;
        devices.ViewChanged += (_, _) => _ = RefreshAsync(CancellationToken.None);
        OnPropertyChanged(nameof(Devices));
    }

    /// <summary>A janela está à vista: só então as mudanças de fora recarregam.</summary>
    public bool IsShown { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayScale), nameof(IsMonthScale), nameof(IsYearScale), nameof(BestCaption))]
    private HoursScale _scale = HoursScale.Day;

    /// <summary>Um dia do período mostrado; <c>null</c> = hoje.</summary>
    [ObservableProperty]
    private DateOnly? _anchor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChart), nameof(IsTable))]
    private HoursReportView _view = HoursReportView.Chart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(PeriodTitle),
        nameof(Crumbs),
        nameof(Buckets),
        nameof(HeatmapDays),
        nameof(IsHeatmapVisible),
        nameof(Average),
        nameof(IsEmpty),
        nameof(HasData),
        nameof(EmptyMessage),
        nameof(TotalText),
        nameof(ComparisonText),
        nameof(ComparisonDetail),
        nameof(AverageText),
        nameof(ActiveDaysText),
        nameof(BestText),
        nameof(BestDetail),
        nameof(RunningNote),
        nameof(TotalEntriesText),
        nameof(TotalDaysText),
        nameof(Readout))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand), nameof(ExportCsvCommand))]
    private HoursHistory? _history;

    [ObservableProperty]
    private IReadOnlyList<HoursReportRow> _rows = [];

    /// <summary>O bloco escolhido no gráfico ou na tabela; -1 = o período inteiro.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Readout))]
    private int _selectedIndex = -1;

    /// <summary>O bloco sob o mouse no gráfico; -1 = nenhum.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Readout))]
    private int _hoveredIndex = -1;

    [ObservableProperty]
    private IReadOnlyList<TagFilterOption> _tags = [TagFilterOption.All];

    [ObservableProperty]
    private TagFilterOption _selectedTag = TagFilterOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTaskRows))]
    private IReadOnlyList<TaskHoursRow> _taskRows = [];

    [ObservableProperty]
    private string _taskPanelTitle = "Horas por tarefa";

    /// <summary>A tarefa cujos períodos o painel mostra (ADR-102); <c>null</c> = a lista de tarefas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskOpen), nameof(IsTaskListShown))]
    [NotifyCanExecuteChangedFor(nameof(AddEntryCommand))]
    private TaskHoursRow? _openTask;

    /// <summary>Os períodos da tarefa aberta que tocam o bloco, do mais recente ao mais antigo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEntries))]
    private IReadOnlyList<ReportEntryRow> _entries = [];

    /// <summary>Quanto a tarefa aberta tem no bloco; "—" quando não tem nada.</summary>
    [ObservableProperty]
    private string _openTaskTotal = string.Empty;

    /// <summary>"Mostrando os 500 mais recentes de 812.", quando não cabem todos.</summary>
    [ObservableProperty]
    private string? _entriesNote;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>"Exportado: horas-2026-10-por-dia.csv".</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>A janela atribui: o seletor de "Salvar como" é serviço dela. Recebe o nome sugerido e o texto; devolve o nome gravado.</summary>
    public Func<string, string, Task<string?>>? SaveCsv { get; set; }

    public bool IsDayScale => Scale == HoursScale.Day;

    public bool IsMonthScale => Scale == HoursScale.Month;

    public bool IsYearScale => Scale == HoursScale.Year;

    public bool IsChart => View == HoursReportView.Chart;

    public bool IsTable => View == HoursReportView.Table;

    public string PeriodTitle => History?.Title ?? string.Empty;

    public IReadOnlyList<HoursBucket> Buckets => History?.Buckets ?? [];

    /// <summary>O mapa de calor só na escala de meses: é o ano inteiro de relance.</summary>
    public bool IsHeatmapVisible => History is { Scale: HoursScale.Month };

    public IReadOnlyList<HoursBucket> HeatmapDays => IsHeatmapVisible ? History!.Days : [];

    /// <summary>A linha de média do gráfico: a dos blocos com horas.</summary>
    public TimeSpan Average => History?.AveragePerActiveBucket ?? TimeSpan.Zero;

    public bool IsEmpty => History is { IsEmpty: true };

    public bool HasData => History is { IsEmpty: false };

    public string EmptyMessage => History switch
    {
        null => string.Empty,
        { Scale: HoursScale.Year } => "Nenhuma hora lançada ainda.",
        _ => $"Nenhuma hora lançada em {History.Title}.",
    };

    /// <summary>"Anos › 2026 › outubro": cada passo volta para aquela escala.</summary>
    public IReadOnlyList<HoursCrumb> Crumbs => History switch
    {
        null => [],
        { Scale: HoursScale.Year } => [new("Todos os anos", HoursScale.Year, IsCurrent: true, IsFirst: true)],
        { Scale: HoursScale.Month } h =>
        [
            new("Anos", HoursScale.Year, IsCurrent: false, IsFirst: true),
            new(h.From.Year.ToString(CultureInfo.InvariantCulture), HoursScale.Month, IsCurrent: true, IsFirst: false),
        ],
        var h =>
        [
            new("Anos", HoursScale.Year, IsCurrent: false, IsFirst: true),
            new(h.From.Year.ToString(CultureInfo.InvariantCulture), HoursScale.Month, IsCurrent: false, IsFirst: false),
            new(ReportCalendar.MonthName(h.From.Month), HoursScale.Day, IsCurrent: true, IsFirst: false),
        ],
    };

    public string TotalText => WorkTimeFormatter.Duration(History?.Total ?? TimeSpan.Zero);

    /// <summary>"▲ 12% vs 1–27 de setembro". Neutro de propósito: trabalhar mais não é "melhor".</summary>
    public string ComparisonText => History?.Comparison switch
    {
        null => History is null ? string.Empty : ReportCalendar.Entries(History.EntryCount),
        { Change: null } c => $"{c.Label}: sem horas",
        { Change: > 0.005 } c => $"▲ {Percent(c.Change.Value)} {c.Label}",
        { Change: < -0.005 } c => $"▼ {Percent(-c.Change.Value)} {c.Label}",
        var c => $"= {c.Label}",
    };

    public string? ComparisonDetail => History?.Comparison is { } c
        ? $"No período anterior: {WorkTimeFormatter.Duration(c.Previous)}"
        : null;

    public string AverageText => WorkTimeFormatter.Duration(History?.AveragePerActiveDay ?? TimeSpan.Zero);

    /// <summary>"18 de 27": dias com horas, dos que já passaram.</summary>
    public string ActiveDaysText => History is null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"{History.ActiveDays} de {History.ElapsedDays}");

    public string BestCaption => Scale switch
    {
        HoursScale.Day => "Maior dia",
        HoursScale.Month => "Maior mês",
        _ => "Maior ano",
    };

    public string BestText => History?.Best is { } best ? WorkTimeFormatter.Duration(best.Total) : "—";

    public string BestDetail => History?.Best?.LongLabel ?? string.Empty;

    public string? RunningNote => History is { IncludesRunning: true } ? "Inclui o cronômetro em andamento, contado até agora." : null;

    public bool HasNoTaskRows => TaskRows.Count == 0;

    public bool IsTaskOpen => OpenTask is not null;

    public bool IsTaskListShown => OpenTask is null;

    public bool HasNoEntries => Entries.Count == 0;

    /// <summary>Um período mudou por aqui: a lista precisa recarregar o total e o relógio da linha.</summary>
    public event Action? Changed;

    /// <summary>A linha de total da tabela: o número, como nas linhas.</summary>
    public string TotalEntriesText => (History?.EntryCount ?? 0).ToString(CultureInfo.InvariantCulture);

    public string TotalDaysText => ReportCalendar.Days(History?.ActiveDays ?? 0);

    /// <summary>O que se lê acima do gráfico: o bloco sob o mouse, senão o escolhido, senão o período.</summary>
    public string Readout
    {
        get
        {
            if (History is not { } history)
            {
                return string.Empty;
            }

            var index = HoveredIndex >= 0 ? HoveredIndex : SelectedIndex;

            return index >= 0 && index < history.Buckets.Count
                ? history.Buckets[index].Summary
                : $"{history.Title} · {WorkTimeFormatter.Duration(history.Total)} · {ReportCalendar.Days(history.ActiveDays)} · {ReportCalendar.Entries(history.EntryCount)}";
        }
    }

    private HoursBucket? SelectedBucket =>
        History is { } history && SelectedIndex >= 0 && SelectedIndex < history.Buckets.Count ? history.Buckets[SelectedIndex] : null;

    /// <summary>A janela abriu: etiquetas e horas, do zero.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        IsShown = true;
        StatusMessage = null;
        await LoadTagsAsync(cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>Algo mudou fora daqui (cronômetro, lançamento, outro computador): recarrega se estiver à vista.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!IsShown)
        {
            return;
        }

        await LoadTagsAsync(cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Carrega o período. O conteúdo anterior fica na tela até a resposta
    /// chegar, e a escolha volta pelo bloco — recarregar não perde o que o
    /// usuário estava olhando.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var load = ++_load;
        IsLoading = true;
        ErrorMessage = null;

        var request = new GetHoursHistory(Scale, Anchor, SelectedTag.Id, Devices?.View);

        try
        {
            var history = await runner.RunAsync<GetHoursHistoryHandler, HoursHistory>(
                (handler, token) => handler.HandleAsync(request, token),
                cancellationToken);

            if (load != _load)
            {
                return;
            }

            Apply(history);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "HoursReportLoadFailed {Scale} {Anchor}", request.Scale, request.Anchor);

            if (load == _load)
            {
                ErrorMessage = "Não foi possível carregar o relatório.";
            }
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Dia, mês ou ano, mantendo a data em foco: o mês escolhido vira os dias dele.</summary>
    [RelayCommand]
    public Task SetScaleAsync(HoursScale scale)
    {
        if (scale == Scale && History is not null)
        {
            return Task.CompletedTask;
        }

        var focus = FocusDate();
        _pendingKey = SelectedBucket is not null && focus is { } date ? KeyOf(scale, date) : null;
        Scale = scale;
        Anchor = focus;

        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    public Task PreviousAsync() => GoTo(History?.PreviousAnchor);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    public Task NextAsync() => GoTo(History?.NextAnchor);

    /// <summary>De volta ao período de hoje, na mesma escala.</summary>
    [RelayCommand]
    public Task TodayAsync() => GoTo(null);

    /// <summary>Duplo clique ou Enter num bloco: o ano vira os meses dele; o mês, os dias.</summary>
    [RelayCommand]
    public Task DrillAsync(int index)
    {
        if (History is not { } history || index < 0 || index >= history.Buckets.Count || Scale == HoursScale.Day)
        {
            return Task.CompletedTask;
        }

        var bucket = history.Buckets[index];
        _pendingKey = null;
        Scale = Scale == HoursScale.Year ? HoursScale.Month : HoursScale.Day;
        Anchor = bucket.From;
        SelectNone();

        return LoadAsync(CancellationToken.None);
    }

    /// <summary>Um dia do mapa de calor: abre o mês dele, com o dia escolhido.</summary>
    [RelayCommand]
    public Task OpenDayAsync(DateOnly day)
    {
        _pendingKey = KeyOf(HoursScale.Day, day);
        Scale = HoursScale.Day;
        Anchor = day;

        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    public void SetView(HoursReportView view) => View = view;

    /// <summary>Grava o período como CSV, no lugar que o usuário escolher.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task ExportCsvAsync()
    {
        if (History is not { } history || SaveCsv is not { } save)
        {
            return;
        }

        StatusMessage = null;
        var name = HoursHistoryCsv.SuggestedFileName(history);
        var saved = await save(name, HoursHistoryCsv.Build(history));

        if (saved is not null)
        {
            StatusMessage = $"Exportado: {saved}";
        }
    }

    /// <summary>
    /// Clique numa tarefa do painel: os períodos dela no bloco escolhido (ou
    /// no período inteiro), para reajustar. Ela continua aberta ao trocar de
    /// bloco, de período ou de escala.
    /// </summary>
    [RelayCommand]
    public Task OpenTaskAsync(TaskHoursRow task)
    {
        if (task.TaskId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        if (OpenTask?.TaskId != task.TaskId)
        {
            _occurrenceId = null;
            Entries = [];
            EntriesNote = null;
        }

        OpenTask = task;
        OpenTaskTotal = task.Duration;

        return LoadEntriesAsync();
    }

    /// <summary>"‹ Tarefas" (ou Esc): de volta à lista de tarefas do bloco.</summary>
    [RelayCommand]
    public void CloseTask()
    {
        ++_entriesLoad;
        _occurrenceId = null;
        OpenTask = null;
        Entries = [];
        EntriesNote = null;
    }

    /// <summary>Corrige um período encerrado, no mesmo diálogo da aba Tempo.</summary>
    [RelayCommand]
    public async Task EditEntryAsync(ReportEntryRow row)
    {
        if (!row.CanEdit)
        {
            return;
        }

        var item = row.Item;

        var saved = await editor.EditAsync(new TimeEntryEditorRequest(
            "Editar período",
            "Salvar",
            new TimeEntryDraft(item.StartDate, item.StartTime, item.EndDate!.Value, item.EndTime!.Value, item.Note),
            clock.Today,
            (draft, token) => SaveAsync(
                () => runner.RunAsync<UpdateTimeEntryHandler>(
                    (handler, inner) => handler.HandleAsync(
                        new UpdateTimeEntry(item.Id, draft.StartDate, draft.StartTime, draft.EndDate, draft.EndTime, draft.Note),
                        inner),
                    token))));

        if (saved)
        {
            await AfterChangeAsync();
        }
    }

    /// <summary>Exclui um período, depois de perguntar: é a única mudança sem volta.</summary>
    [RelayCommand]
    public async Task DeleteEntryAsync(ReportEntryRow row)
    {
        if (!await confirmation.AskAsync(DeletePrompt(row)))
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            await runner.RunAsync<DeleteTimeEntryHandler>(
                (handler, token) => handler.HandleAsync(new DeleteTimeEntry(row.Item.Id), token),
                CancellationToken.None);
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "HoursReportEntryDeleteFailed {EntryId}", row.Item.Id);
            ErrorMessage = "Não foi possível excluir este período.";
            return;
        }

        await AfterChangeAsync();
    }

    /// <summary>
    /// "+ Adicionar tempo" na tarefa aberta. Com um dia passado escolhido, o
    /// período já nasce nele; senão, é a última hora, como na aba Tempo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddEntry))]
    public async Task AddEntryAsync()
    {
        if (_occurrenceId is not { } occurrenceId)
        {
            return;
        }

        var saved = await editor.EditAsync(NewEntryRequest(
            (draft, token) => SaveAsync(
                () => runner.RunAsync<AddTimeEntryHandler, Guid>(
                    (handler, inner) => handler.HandleAsync(
                        new AddTimeEntry(occurrenceId, draft.StartDate, draft.StartTime, draft.EndDate, draft.EndTime, draft.Note),
                        inner),
                    token))));

        if (saved)
        {
            await AfterChangeAsync();
        }
    }

    /// <summary>"Excluir registro de tempo?" com a tarefa, o período e a duração; o foco fica em Cancelar.</summary>
    public static ConfirmationRequest DeletePrompt(ReportEntryRow row) =>
        new(
            "Excluir registro de tempo?",
            $"{row.Item.TaskTitle}{Environment.NewLine}{row.DayLabel} · {row.RangeLabel}{Environment.NewLine}{WorkTimeFormatter.Duration(row.Item.Duration)}",
            "Excluir",
            IsIrreversible: true);

    private bool CanAddEntry() => OpenTask is not null && _occurrenceId is not null;

    private TimeEntryEditorRequest NewEntryRequest(Func<TimeEntryDraft, CancellationToken, Task<string?>> save)
    {
        if (History is { Scale: HoursScale.Day } && SelectedBucket is { } day && day.From < clock.Today)
        {
            return new TimeEntryEditorRequest(
                "Adicionar tempo",
                "Adicionar",
                new TimeEntryDraft(day.From, PastDayStart, day.From, PastDayStart.AddHours(1), null),
                clock.Today,
                save);
        }

        return TimeEntryEditorRequest.NewEntry(clock, save);
    }

    /// <summary>
    /// Depois de gravar: o relatório recarrega (os números, as tarefas e os
    /// períodos), e a lista fica sabendo.
    /// </summary>
    private async Task AfterChangeAsync()
    {
        await LoadAsync(CancellationToken.None);
        Changed?.Invoke();
    }

    /// <summary>A gravação do diálogo: a mensagem do domínio volta para ele, que fica aberto.</summary>
    private async Task<string?> SaveAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (DomainException exception)
        {
            return exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "HoursReportEntrySaveFailed {TaskId}", OpenTask?.TaskId);
            return "Não foi possível salvar este período.";
        }
    }

    private bool CanGoPrevious() => History?.PreviousAnchor is not null;

    private bool CanGoNext() => History?.NextAnchor is not null;

    private bool CanExport() => History is not null;

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_applying)
        {
            _ = LoadBreakdownAsync();
        }
    }

    partial void OnSelectedTagChanged(TagFilterOption value)
    {
        // O ComboBox zera a escolha quando a lista troca; a etiqueta não sumiu por isso.
        if (value is null)
        {
            _applying = true;
            SelectedTag = TagFilterOption.All;
            _applying = false;
            return;
        }

        if (!_applying)
        {
            _ = LoadAsync(CancellationToken.None);
        }
    }

    private Task GoTo(DateOnly? anchor)
    {
        _pendingKey = null;
        Anchor = anchor;
        SelectNone();

        return LoadAsync(CancellationToken.None);
    }

    private void SelectNone()
    {
        _applying = true;
        SelectedIndex = -1;
        _applying = false;
    }

    /// <summary>De onde partir ao trocar de escala: o bloco escolhido, senão o atual, senão o começo.</summary>
    private DateOnly? FocusDate()
    {
        if (SelectedBucket is { } selected)
        {
            return selected.From;
        }

        if (History is not { } history)
        {
            return Anchor;
        }

        return history.Buckets.FirstOrDefault(bucket => bucket.IsCurrent)?.From ?? history.From;
    }

    private static string KeyOf(HoursScale scale, DateOnly date) => scale switch
    {
        HoursScale.Day => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        HoursScale.Month => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => date.Year.ToString(CultureInfo.InvariantCulture),
    };

    private void Apply(HoursHistory history)
    {
        var key = _pendingKey ?? SelectedBucket?.Key;
        _pendingKey = null;

        _applying = true;

        try
        {
            SelectedIndex = -1;
            HoveredIndex = -1;
            Scale = history.Scale;
            History = history;
            Rows = BuildRows(history);
            SelectedIndex = key is null ? -1 : history.Buckets.ToList().FindIndex(bucket => bucket.Key == key);
        }
        finally
        {
            _applying = false;
        }

        _ = LoadBreakdownAsync();
    }

    private static IReadOnlyList<HoursReportRow> BuildRows(HoursHistory history)
    {
        var max = history.Buckets.Count == 0 ? TimeSpan.Zero : history.Buckets.Max(bucket => bucket.Total);

        return history.Buckets
            .Select(bucket => new HoursReportRow(
                bucket,
                bucket.LongLabel,
                bucket.IsFuture ? string.Empty : bucket.Total <= TimeSpan.Zero ? "—" : WorkTimeFormatter.Duration(bucket.Total),

                // Na escala de dias, "1 dia" em cada linha não diz nada.
                bucket.IsFuture || history.Scale == HoursScale.Day ? string.Empty : ReportCalendar.Days(bucket.ActiveDays),
                bucket.IsFuture ? string.Empty : bucket.EntryCount.ToString(CultureInfo.InvariantCulture),
                bucket.IsFuture || history.Total <= TimeSpan.Zero ? string.Empty : Percent(bucket.Total / history.Total),
                max > TimeSpan.Zero ? bucket.Total / max : 0,
                bucket.IsFuture || bucket.Total <= TimeSpan.Zero))
            .ToList();
    }

    /// <summary>
    /// "Horas por tarefa": do bloco escolhido, perguntando ao relatório; sem
    /// escolha, do período inteiro, que já veio junto.
    /// </summary>
    private async Task LoadBreakdownAsync()
    {
        var call = ++_breakdown;

        if (History is not { } history)
        {
            TaskRows = [];
            return;
        }

        if (SelectedBucket is not { } bucket)
        {
            TaskPanelTitle = $"Horas por tarefa · {history.Title}";
            TaskRows = TaskRowsOf(history.Tasks, history.Total);
            await FollowOpenTaskAsync();
            return;
        }

        TaskPanelTitle = $"Horas por tarefa · {bucket.LongLabel}";

        if (bucket.Total <= TimeSpan.Zero)
        {
            TaskRows = [];
            await FollowOpenTaskAsync();
            return;
        }

        try
        {
            var summary = await runner.RunAsync<TimeReportHandlers, TimeSummary>(
                (handler, token) => handler.HandleAsync(
                    new GetTimeSummary(bucket.From, bucket.To, TimeGrouping.Task, TagId: SelectedTag.Id, Device: Devices?.View),
                    token),
                CancellationToken.None);

            if (call == _breakdown)
            {
                TaskRows = TaskRowsOf(summary.Groups, summary.Total);
                await FollowOpenTaskAsync();
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "HoursReportBreakdownFailed {Key}", bucket.Key);

            if (call == _breakdown)
            {
                ErrorMessage = "Não foi possível carregar as horas por tarefa.";
            }
        }
    }

    /// <summary>
    /// A tarefa aberta acompanha o bloco: o total e o título vêm das tarefas que
    /// acabaram de chegar, e os períodos são os do bloco novo.
    /// </summary>
    private Task FollowOpenTaskAsync()
    {
        if (OpenTask is not { } open)
        {
            return Task.CompletedTask;
        }

        var row = TaskRows.FirstOrDefault(task => task.TaskId == open.TaskId);
        OpenTaskTotal = row?.Duration ?? "—";

        if (row is not null)
        {
            OpenTask = row;
        }

        return LoadEntriesAsync();
    }

    /// <summary>Os períodos da tarefa aberta que tocam o bloco escolhido, ou o período inteiro.</summary>
    private async Task LoadEntriesAsync()
    {
        var call = ++_entriesLoad;

        if (OpenTask is not { } task || History is not { } history)
        {
            Entries = [];
            EntriesNote = null;
            return;
        }

        var (from, to) = SelectedBucket is { } bucket ? (bucket.From, bucket.To) : (history.From, history.To);

        try
        {
            var report = await runner.RunAsync<TimeReportHandlers, TimeEntryReport>(
                (handler, token) => handler.HandleAsync(
                    new GetTimeEntries(from, to, TaskId: task.TaskId, TagId: SelectedTag.Id, Limit: GetTimeEntries.MaxLimit),
                    token),
                CancellationToken.None);

            if (call != _entriesLoad)
            {
                return;
            }

            // O período que só encosta na borda não tem nada no bloco.
            var items = report.Entries.Where(item => item.DurationInRange > TimeSpan.Zero || item.IsRunning).ToList();
            var withYear = from.Year != to.Year;

            _occurrenceId = items.FirstOrDefault()?.OccurrenceId ?? _occurrenceId;
            Entries = items.Select(item => Describe(item, withYear)).ToList();
            EntriesNote = report.Count > report.Entries.Count
                ? string.Create(CultureInfo.InvariantCulture, $"Mostrando os {report.Entries.Count} mais recentes de {report.Count}.")
                : null;
            AddEntryCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "HoursReportEntriesFailed {TaskId}", task.TaskId);

            if (call == _entriesLoad)
            {
                ErrorMessage = "Não foi possível carregar os períodos desta tarefa.";
            }
        }
    }

    /// <summary>"ter, 14/10" e "14:00 → 15:30"; com o ano quando o intervalo atravessa anos.</summary>
    private static ReportEntryRow Describe(TimeEntryReportItem item, bool withYear)
    {
        var start = item.StartDate.ToDateTime(item.StartTime);
        DateTime? end = item.EndDate is { } endDate && item.EndTime is { } endTime ? endDate.ToDateTime(endTime) : null;
        var inRange = WorkTimeFormatter.Duration(item.DurationInRange);

        return new ReportEntryRow(
            item,
            withYear ? ReportCalendar.LongDay(item.StartDate) : ReportCalendar.ShortDay(item.StartDate),
            WorkTimeFormatter.Range(start, end),
            item.IsRunning ? "em andamento"
                : item.DurationInRange < item.Duration ? $"{inRange} de {WorkTimeFormatter.Duration(item.Duration)}"
                : inRange,
            WorkTimeFormatter.Source(item.Source));
    }

    private static IReadOnlyList<TaskHoursRow> TaskRowsOf(IReadOnlyList<TimeSummaryGroup> groups, TimeSpan total)
    {
        var max = groups.Count == 0 ? TimeSpan.Zero : groups.Max(group => group.Total);

        return groups
            .Select(group => new TaskHoursRow(
                Guid.TryParse(group.Key, out var taskId) ? taskId : Guid.Empty,
                group.Label,
                WorkTimeFormatter.Duration(group.Total),
                total > TimeSpan.Zero ? Percent(group.Total / total) : string.Empty,
                max > TimeSpan.Zero ? group.Total / max : 0))
            .ToList();
    }

    /// <summary>As etiquetas do filtro, mantendo a escolhida enquanto ela existir.</summary>
    private async Task LoadTagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tags = await runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
                (handler, token) => handler.HandleAsync(new GetTags(Devices?.View), token),
                cancellationToken);

            List<TagFilterOption> options = [TagFilterOption.All, .. tags.Select(tag => new TagFilterOption(tag.Id, tag.Name))];
            var keep = options.FirstOrDefault(option => option.Id == SelectedTag.Id) ?? TagFilterOption.All;

            _applying = true;

            try
            {
                Tags = options;
                SelectedTag = keep;
            }
            finally
            {
                _applying = false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem a lista, o filtro fica em "Todas": o relatório continua.
            logger.LogWarning(exception, "HoursReportTagsFailed");
        }
    }

    private static string Percent(double fraction) =>
        (fraction * 100).ToString(fraction * 100 >= 10 ? "0" : "0.#", Brazil) + "%";
}
