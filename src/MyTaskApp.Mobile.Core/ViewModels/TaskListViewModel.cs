using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>O que a lista está mostrando: cada um tem a sua frase na tela.</summary>
public enum ListState
{
    Loading,
    Ready,

    /// <summary>Não há tarefa nenhuma.</summary>
    Empty,

    /// <summary>Há tarefas, mas nenhuma com estes filtros.</summary>
    NoResults,

    Error,
}

public sealed record SortChoice(TaskSort Sort, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A tela principal do celular (ADR-067): todas as tarefas, de todos os
/// dispositivos, numa lista só, em páginas — a busca é a mesma do desktop e do
/// MCP (<see cref="SearchTasksHandler"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Recarrega por evento.</b> O que chega da sincronização avisa pelo
/// <see cref="IDataChangeNotifier"/>; o que as outras telas gravam também. A
/// recarga traz de novo pelo menos o que já estava carregado, para não pular a
/// rolagem de volta ao topo.
/// </para>
/// <para>
/// <b>Uma resposta por vez.</b> Uma busca que volta depois de outra mais nova
/// é descartada: digitar rápido não mostra o resultado de letras antigas.
/// </para>
/// </remarks>
public sealed partial class TaskListViewModel : MobileViewModel, IDisposable
{
    public const int PageSize = 50;

    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>O quanto "puxar para atualizar" espera o ciclo antes de reler o que já tem.</summary>
    public static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(15);

    /// <summary>Quanto a barra "Desfazer" fica na tela depois de concluir ou reabrir.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(8);

    /// <summary>De quanto em quanto o "há N min" do topo é refeito; o estado em si muda por evento.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

    private readonly IUseCaseRunner _runner;

    private readonly OriginCatalog _origins;

    private readonly ISyncAccount _account;

    private readonly SyncScheduler _scheduler;

    private readonly IDataChangeNotifier _notifier;

    private readonly DevicePlacementState _placements;

    private readonly IUiDispatcher _dispatcher;

    private readonly INavigator _navigator;

    private readonly TimeProvider _time;

    private readonly ITimer _ticker;

    private int _generation;

    private CancellationTokenSource? _searchDelay;

    /// <summary>"Limpar filtros" apaga o texto sem disparar a busca com espera: ele mesmo relê.</summary>
    private bool _clearingSearch;

    private string _appliedOriginLabel = OriginCatalog.AnyOrigin.Label;

    /// <summary>O caminho de volta do último toque em concluir/reabrir, enquanto a barra está na tela.</summary>
    private (Guid OccurrenceId, bool Reopen)? _undo;

    private CancellationTokenSource? _undoTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    private string? _undoMessage;

    [ObservableProperty]
    private TaskFilter _filter = TaskFilter.Default;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private ListState _state = ListState.Loading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isTruncated;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncLine))]
    private SyncBadge? _sync;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private OriginFilterOption _selectedOrigin = OriginCatalog.AnyOrigin;

    [ObservableProperty]
    private SortChoice _selectedSort;

    public TaskListViewModel(
        IUseCaseRunner runner,
        OriginCatalog origins,
        ISyncAccount account,
        SyncScheduler scheduler,
        IDataChangeNotifier notifier,
        DevicePlacementState placements,
        IUiDispatcher dispatcher,
        INavigator navigator,
        IDialogService dialogs,
        TimeProvider time,
        ILogger<TaskListViewModel> logger)
        : base(dialogs, logger)
    {
        _runner = runner;
        _origins = origins;
        _account = account;
        _scheduler = scheduler;
        _notifier = notifier;
        _placements = placements;
        _dispatcher = dispatcher;
        _navigator = navigator;
        _time = time;
        _selectedSort = Sorts[0];

        Presets =
        [
            new(TaskListPreset.All, "Todas") { IsSelected = true },
            new(TaskListPreset.Pending, "Pendentes"),
            new(TaskListPreset.Overdue, "Vencidas"),
            new(TaskListPreset.Completed, "Concluídas"),
            new(TaskListPreset.Cancelled, "Canceladas"),
        ];

        _notifier.Changed += OnDataChanged;
        _placements.Changed += OnPlacementsChanged;
        _scheduler.CycleStarted += OnCycleStarted;
        _scheduler.CycleCompleted += OnCycleCompleted;
        _scheduler.ChangeQueued += OnChangeQueued;
        _ticker = time.CreateTimer(_ => _dispatcher.Post(() => OnPropertyChanged(nameof(SyncLine))), state: null, TickEvery, TickEvery);
    }

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    public IReadOnlyList<PresetChip> Presets { get; }

    public IReadOnlyList<SortChoice> Sorts { get; } =
    [
        new(TaskSort.Created, "Mais novas"),
        new(TaskSort.Deadline, "Prazo"),
        new(TaskSort.Priority, "Prioridade"),
        new(TaskSort.Scheduled, "Data marcada"),
        new(TaskSort.Title, "Título"),
    ];

    public ObservableCollection<PriorityChip> PriorityChoices { get; } = [];

    public ObservableCollection<TagOption> TagChoices { get; } = [];

    public ObservableCollection<OriginFilterOption> OriginChoices { get; } = [];

    /// <summary>Sem conta ligada não há outros dispositivos: a origem nem aparece.</summary>
    public bool ShowsOrigin => _origins.IsActive;

    public bool IsEmpty => State == ListState.Empty;

    public bool IsNoResults => State == ListState.NoResults;

    public bool IsError => State == ListState.Error;

    public bool IsLoading => State == ListState.Loading;

    public bool IsReady => State == ListState.Ready;

    /// <summary>"Filtros (2)": o botão mostra quantos estão ligados na folha.</summary>
    public string FiltersLabel => Filter.SheetCount > 0 ? $"Filtros ({Filter.SheetCount})" : "Filtros";

    public bool HasUndo => UndoMessage is not null;

    /// <summary>"Sincronizado · há 2 min": o estado de relance, com a idade quando está em dia.</summary>
    public string SyncLine => Sync switch
    {
        null or { IsVisible: false } => string.Empty,
        { Kind: SyncIndicatorKind.Synced or SyncIndicatorKind.Review, LastSuccessAt: { } at } => $"{Sync.Title} · {Labels.Ago(at, _time)}",
        _ => Sync.Title,
    };

    [RelayCommand]
    public Task LoadAsync() => LoadPageAsync(reset: true, keepLoaded: false);

    /// <summary>Puxar para atualizar: pede um ciclo, espera um pouco por ele, e relê.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;

        try
        {
            if (Sync is { Kind: not (SyncIndicatorKind.Hidden or SyncIndicatorKind.LocalOnly or SyncIndicatorKind.Paused) })
            {
                await WaitForCycleAsync();
            }

            await LoadPageAsync(reset: true, keepLoaded: false);
            await LoadSyncAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoadingMore || State != ListState.Ready)
        {
            return;
        }

        IsLoadingMore = true;

        try
        {
            await LoadPageAsync(reset: false, keepLoaded: false);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    public Task SelectPresetAsync(TaskListPreset preset)
    {
        foreach (var choice in Presets)
        {
            choice.IsSelected = choice.Value == preset;
        }

        Filter = Filter with { Preset = preset };
        return LoadPageAsync(reset: true, keepLoaded: false);
    }

    /// <summary>
    /// O gesto da linha: concluir a pendente, reabrir a concluída — pelos
    /// mesmos casos de uso do desktop. Em vez de um aviso que some, a barra
    /// "Desfazer" fica <see cref="UndoWindow"/> na tela: um toque errado no
    /// celular acontece, e voltar atrás tem de ser um toque também.
    /// </summary>
    [RelayCommand]
    public async Task ToggleAsync(TaskRow? row)
    {
        if (row is not { CanToggle: true })
        {
            return;
        }

        var reopening = row.IsCompleted;

        if (await SetCompletedAsync(row.OccurrenceId, completed: !reopening))
        {
            OfferUndo(reopening ? "Tarefa reaberta." : "Tarefa concluída.", row.OccurrenceId, reopen: !reopening);
        }
    }

    /// <summary>"Desfazer": o caso de uso oposto, na mesma ocorrência.</summary>
    [RelayCommand]
    public async Task UndoAsync()
    {
        if (_undo is not { } undo)
        {
            return;
        }

        DismissUndo();
        await SetCompletedAsync(undo.OccurrenceId, completed: !undo.Reopen);
    }

    [RelayCommand]
    public void DismissUndo()
    {
        CancelUndoTimer();
        _undo = null;
        UndoMessage = null;
    }

    [RelayCommand]
    public Task OpenAsync(TaskRow? row) => row is null ? Task.CompletedTask : _navigator.OpenTaskAsync(row.TaskId);

    [RelayCommand]
    public Task NewTaskAsync() => _navigator.NewTaskAsync();

    /// <summary>Abre a folha de filtros com o que está aplicado agora.</summary>
    [RelayCommand]
    public async Task OpenFiltersAsync()
    {
        PriorityChoices.Clear();

        foreach (var priority in new[] { TaskPriority.Urgent, TaskPriority.High, TaskPriority.Normal, TaskPriority.Low })
        {
            PriorityChoices.Add(new PriorityChip(priority, Labels.Priority(priority)) { IsSelected = Filter.Priorities.Contains(priority) });
        }

        OriginChoices.Clear();

        foreach (var option in _origins.FilterOptions())
        {
            OriginChoices.Add(option);
        }

        SelectedOrigin = OriginChoices.FirstOrDefault(option => option.Label == _appliedOriginLabel) ?? OriginCatalog.AnyOrigin;
        SelectedSort = Sorts.FirstOrDefault(sort => sort.Sort == Filter.Sort) ?? Sorts[0];
        OnPropertyChanged(nameof(ShowsOrigin));

        await LoadTagChoicesAsync();
        await _navigator.OpenFiltersAsync();
    }

    [RelayCommand]
    public async Task ApplyFiltersAsync()
    {
        Filter = Filter with
        {
            Priorities = [.. PriorityChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)],
            TagIds = [.. TagChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)],
            Origin = SelectedOrigin?.Match,
            Sort = SelectedSort?.Sort ?? TaskSort.Created,
        };

        _appliedOriginLabel = SelectedOrigin?.Label ?? OriginCatalog.AnyOrigin.Label;

        await _navigator.BackAsync();
        await LoadPageAsync(reset: true, keepLoaded: false);
    }

    /// <summary>"Limpar filtros": volta a todas as tarefas, com a ordem escolhida.</summary>
    [RelayCommand]
    public Task ClearFiltersAsync()
    {
        _clearingSearch = true;
        SearchText = null;
        _clearingSearch = false;
        _appliedOriginLabel = OriginCatalog.AnyOrigin.Label;

        foreach (var choice in Presets)
        {
            choice.IsSelected = choice.Value == TaskListPreset.All;
        }

        Filter = TaskFilter.Default with { Sort = Filter.Sort };
        return LoadPageAsync(reset: true, keepLoaded: false);
    }

    [RelayCommand]
    public Task OpenAccountAsync() => _navigator.OpenAccountAsync();

    public async Task LoadSyncAsync()
    {
        try
        {
            var status = await _runner.RunAsync<GetSyncStatusHandler, SyncStatusView>(
                (handler, token) => handler.HandleAsync(new GetSyncStatus(), token));

            Sync = SyncStatusText.Describe(status, IsSyncing);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // O indicador é acessório: a lista continua mesmo sem ele.
            Logger.LogWarning(exception, "MobileSyncStatusFailed");
        }
    }

    /// <summary>
    /// O tema mudou: o estado é o mesmo, mas a tela pinta a bolinha a partir
    /// dele e precisa reler. Reler o status não basta: o <see cref="SyncBadge"/>
    /// novo é igual ao velho, e a propriedade não avisaria.
    /// </summary>
    public void RepaintSync() => OnPropertyChanged(nameof(Sync));

    public void Dispose()
    {
        _notifier.Changed -= OnDataChanged;
        _placements.Changed -= OnPlacementsChanged;
        _scheduler.CycleStarted -= OnCycleStarted;
        _scheduler.CycleCompleted -= OnCycleCompleted;
        _scheduler.ChangeQueued -= OnChangeQueued;
        _ticker.Dispose();
        CancelPendingSearch();
        CancelUndoTimer();
    }

    partial void OnSearchTextChanged(string? value)
    {
        CancelPendingSearch();

        if (_clearingSearch)
        {
            return;
        }

        var delay = new CancellationTokenSource();
        _searchDelay = delay;
        _ = SearchLaterAsync(value, delay.Token);
    }

    partial void OnStateChanged(ListState value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsNoResults));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsReady));
    }

    partial void OnFilterChanged(TaskFilter value) => OnPropertyChanged(nameof(FiltersLabel));

    private async Task SearchLaterAsync(string? text, CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDelay, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Filter = Filter with { Text = text };
        await LoadPageAsync(reset: true, keepLoaded: false);
    }

    private void CancelPendingSearch()
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        _searchDelay = null;
    }

    private async Task<bool> SetCompletedAsync(Guid occurrenceId, bool completed)
    {
        var done = await TryAsync(
            () => completed
                ? _runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(occurrenceId), token))
                : _runner.RunAsync<ReopenOccurrenceHandler>((handler, token) => handler.HandleAsync(new ReopenOccurrence(occurrenceId), token)),
            "MobileToggleFailed");

        if (done)
        {
            _notifier.NotifyChanged(DataArea.Tasks);
        }

        return done;
    }

    /// <summary>Uma barra nova substitui a anterior, e o prazo recomeça.</summary>
    private void OfferUndo(string message, Guid occurrenceId, bool reopen)
    {
        CancelUndoTimer();
        _undo = (occurrenceId, reopen);
        UndoMessage = message;

        var timer = new CancellationTokenSource();
        _undoTimer = timer;
        _ = HideUndoLaterAsync(timer.Token);
    }

    private async Task HideUndoLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UndoWindow, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!token.IsCancellationRequested)
            {
                DismissUndo();
            }
        });
    }

    private void CancelUndoTimer()
    {
        _undoTimer?.Cancel();
        _undoTimer?.Dispose();
        _undoTimer = null;
    }

    /// <summary>
    /// Uma página. <paramref name="reset"/> troca a lista; <paramref name="keepLoaded"/>
    /// relê tanto quanto já estava carregado (até o teto da busca).
    /// </summary>
    private async Task LoadPageAsync(bool reset, bool keepLoaded)
    {
        var generation = reset ? Interlocked.Increment(ref _generation) : Volatile.Read(ref _generation);
        var offset = reset ? 0 : Tasks.Count;
        var limit = keepLoaded ? Math.Clamp(Tasks.Count, PageSize, SearchTasks.MaxLimit) : PageSize;
        var filter = Filter;

        if (reset && Tasks.Count == 0)
        {
            State = ListState.Loading;
        }

        try
        {
            var result = await _runner.RunAsync<SearchTasksHandler, TaskSearchResult>(
                (handler, token) => handler.HandleAsync(filter.ToSearch(offset, limit), token));

            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            if (reset)
            {
                Tasks.Clear();
            }

            foreach (var item in result.Items)
            {
                Tasks.Add(TaskRow.From(item, _origins));
            }

            Total = result.Total;
            HasMore = Tasks.Count < result.Total;
            IsTruncated = result.Truncated;
            ErrorMessage = null;
            State = Tasks.Count > 0 ? ListState.Ready : filter.IsNarrowed ? ListState.NoResults : ListState.Empty;
        }
        catch (DomainException refused)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                ErrorMessage = refused.Message;
                State = ListState.Error;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileTaskListFailed");

            if (generation == Volatile.Read(ref _generation))
            {
                ErrorMessage = "Não foi possível ler as tarefas deste celular.";
                State = ListState.Error;
            }
        }
    }

    private async Task LoadTagChoicesAsync()
    {
        var selected = Filter.TagIds.ToHashSet();
        TagChoices.Clear();

        try
        {
            var tags = await _runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
                (handler, token) => handler.HandleAsync(new GetTags(DeviceView.All), token));

            foreach (var tag in tags.OrderBy(tag => tag.Name, StringComparer.Create(Labels.Culture, ignoreCase: true)))
            {
                TagChoices.Add(new TagOption(tag.Id, tag.Name, tag.ColorHex) { IsSelected = selected.Contains(tag.Id) });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "MobileTagsFailed");
        }
    }

    private async Task WaitForCycleAsync()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(SyncCycleResult _) => finished.TrySetResult();

        _scheduler.CycleCompleted += OnFinished;

        try
        {
            _account.SyncNow();
            await finished.Task.WaitAsync(RefreshWait, _time);
        }
        catch (TimeoutException)
        {
            // Sem resposta a tempo: relê o que já tem, e o ciclo termina sozinho depois.
        }
        finally
        {
            _scheduler.CycleCompleted -= OnFinished;
        }
    }

    private void OnDataChanged(DataArea areas)
    {
        if ((areas & (DataArea.Tasks | DataArea.Tags)) != 0)
        {
            _dispatcher.Post(() => _ = LoadPageAsync(reset: true, keepLoaded: true));
        }
    }

    private void OnPlacementsChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(ShowsOrigin));
        _ = LoadPageAsync(reset: true, keepLoaded: true);
    });

    private void OnCycleStarted() => _dispatcher.Post(() =>
    {
        IsSyncing = true;
        _ = LoadSyncAsync();
    });

    private void OnCycleCompleted(SyncCycleResult result) => _dispatcher.Post(() =>
    {
        IsSyncing = false;
        _ = LoadSyncAsync();
    });

    private void OnChangeQueued() => _dispatcher.Post(() => _ = LoadSyncAsync());
}
