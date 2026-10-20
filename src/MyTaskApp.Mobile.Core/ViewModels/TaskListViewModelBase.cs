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
/// Uma lista de tarefas do celular (ADR-067, ADR-076), em páginas — a busca é
/// a mesma do desktop e do MCP (<see cref="SearchTasksHandler"/>). As abas
/// Tarefas (<see cref="TaskListViewModel"/>) e Histórico
/// (<see cref="HistoryViewModel"/>) mudam só os atalhos de status.
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
/// <para>
/// <b>O dispositivo é do app, não da aba.</b> O título da lista (ADR-079) lê
/// e grava o <see cref="DeviceFocus"/>: escolher numa aba muda a outra, e a
/// escolha volta na próxima abertura.
/// </para>
/// </remarks>
public abstract partial class TaskListViewModelBase : MobileViewModel, IDisposable
{
    public const int PageSize = 50;

    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>O quanto "puxar para atualizar" espera o ciclo antes de reler o que já tem.</summary>
    public static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(15);

    /// <summary>Quanto a barra "Desfazer" fica na tela depois de concluir ou reabrir.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(8);

    /// <summary>De quanto em quanto o "há N min" do topo é refeito; o estado em si muda por evento.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

    /// <summary>O título da folha de dispositivos.</summary>
    public const string DeviceSheetTitle = "Mostrar as tarefas de";

    private readonly IUseCaseRunner _runner;

    private readonly OriginCatalog _origins;

    private readonly DeviceFocus _focus;

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

    /// <summary>A troca de dispositivo feita aqui já relê: o aviso do <see cref="DeviceFocus"/> é para a outra aba.</summary>
    private bool _choosingDevice;

    /// <summary>O caminho de volta do último toque em concluir/reabrir, enquanto a barra está na tela.</summary>
    private (Guid OccurrenceId, bool Reopen)? _undo;

    private CancellationTokenSource? _undoTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    private string? _undoMessage;

    [ObservableProperty]
    private TaskFilter _filter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSearch))]
    private string? _searchText;

    /// <summary>A busca fica atrás da lupa; com texto, continua aberta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSearch))]
    private bool _isSearchOpen;

    /// <summary>"Só as vencidas" na folha, antes de aplicar.</summary>
    [ObservableProperty]
    private bool _overdueChoice;

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
    private SortChoice _selectedSort;

    protected TaskListViewModelBase(
        IReadOnlyList<PresetChip> presets,
        IUseCaseRunner runner,
        OriginCatalog origins,
        DeviceFocus focus,
        ISyncAccount account,
        SyncScheduler scheduler,
        IDataChangeNotifier notifier,
        DevicePlacementState placements,
        IUiDispatcher dispatcher,
        INavigator navigator,
        IDialogService dialogs,
        TimeProvider time,
        ILogger logger)
        : base(dialogs, logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(presets.Count);

        _runner = runner;
        _origins = origins;
        _focus = focus;
        _account = account;
        _scheduler = scheduler;
        _notifier = notifier;
        _placements = placements;
        _dispatcher = dispatcher;
        _navigator = navigator;
        _time = time;
        _selectedSort = Sorts[0];

        Presets = presets;
        Presets[0].IsSelected = true;
        _filter = TaskFilter.For(BasePreset);
        RefreshDevices();

        _notifier.Changed += OnDataChanged;
        _placements.Changed += OnPlacementsChanged;
        _focus.Changed += OnFocusChanged;
        _scheduler.CycleStarted += OnCycleStarted;
        _scheduler.CycleCompleted += OnCycleCompleted;
        _scheduler.ChangeQueued += OnChangeQueued;
        _ticker = time.CreateTimer(_ => _dispatcher.Post(() => OnPropertyChanged(nameof(SyncLine))), state: null, TickEvery, TickEvery);
    }

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    /// <summary>Os atalhos de status desta aba; o primeiro é o de partida.</summary>
    public IReadOnlyList<PresetChip> Presets { get; }

    /// <summary>Os dispositivos da conta, as opções da folha do título; só "Todos os dispositivos" sem conta ligada.</summary>
    public ObservableCollection<DeviceChip> Devices { get; } = [];

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

    /// <summary>Sem conta ligada não há outros dispositivos: o título é o da aba, e não abre folha.</summary>
    public bool ShowsDevices => _origins.IsActive;

    /// <summary>"Notebook": o dispositivo em foco, grande no topo; sem conta, o nome da aba.</summary>
    public string DeviceTitle => ShowsDevices
        ? Devices.FirstOrDefault(chip => chip.IsSelected)?.Label ?? OriginCatalog.AnyOrigin.Label
        : TabTitle;

    /// <summary>Uma fileira de atalhos só quando há o que escolher: no Histórico.</summary>
    public bool ShowsPresets => Presets.Count > 1;

    public bool ShowsSearch => IsSearchOpen || !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>O ponto no funil: a folha tem algo ligado.</summary>
    public bool HasSheetFilters => Filter.SheetCount > 0;

    /// <summary>A folha de filtros desta aba é a do Histórico.</summary>
    public abstract bool IsHistory { get; }

    /// <summary>O nome da aba, no título quando não há dispositivo para escolher.</summary>
    public abstract string TabTitle { get; }

    /// <summary>A legenda acima do dispositivo: "Pendentes", "Histórico".</summary>
    public abstract string HeaderCaption { get; }

    /// <summary>A frase de "não há nada aqui".</summary>
    public abstract string EmptyTitle { get; }

    public abstract string EmptyMessage { get; }

    /// <summary>"Entrar com sua conta" no vazio: só onde faz sentido trazer tarefas de outros dispositivos.</summary>
    public virtual bool OffersSignInWhenEmpty => false;

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

    private TaskListPreset BasePreset => Presets[0].Value;

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

    /// <summary>Um dispositivo escolhido: filtra as duas abas e fica gravado para a próxima abertura.</summary>
    [RelayCommand]
    public Task SelectDeviceAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.CompletedTask;
        }

        ChooseDevice(key);
        return LoadPageAsync(reset: true, keepLoaded: false);
    }

    /// <summary>
    /// O toque no título: a folha do sistema com os dispositivos e o atual
    /// marcado. Cancelar não muda nada; sem conta, não há o que escolher.
    /// </summary>
    [RelayCommand]
    public async Task ChooseDeviceAsync()
    {
        if (!ShowsDevices || Devices.Count == 0)
        {
            return;
        }

        // A folha do sistema devolve o texto tocado: dois computadores com o mesmo nome ganham "(2)" para não se confundirem.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var options = Devices.Select(chip =>
        {
            var count = seen[chip.Label] = seen.GetValueOrDefault(chip.Label) + 1;
            var label = count > 1 ? $"{chip.Label} ({count})" : chip.Label;
            return (chip.IsSelected ? "✓ " : "    ") + label;
        }).ToList();
        var chosen = await Dialogs.ChooseAsync(DeviceSheetTitle, options);

        if (chosen is { } index && index >= 0 && index < Devices.Count)
        {
            await SelectDeviceAsync(Devices[index].Value);
        }
    }

    /// <summary>A lupa: abre a busca; aberta, fecha e apaga o que foi digitado.</summary>
    [RelayCommand]
    public Task ToggleSearchAsync()
    {
        if (!ShowsSearch)
        {
            IsSearchOpen = true;
            return Task.CompletedTask;
        }

        var hadText = !string.IsNullOrWhiteSpace(Filter.Text) || !string.IsNullOrWhiteSpace(SearchText);
        CloseSearch();

        if (!hadText)
        {
            return Task.CompletedTask;
        }

        Filter = Filter with { Text = null };
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

        SelectedSort = Sorts.FirstOrDefault(sort => sort.Sort == Filter.Sort) ?? Sorts[0];
        OverdueChoice = Filter.OverdueOnly;

        await LoadTagChoicesAsync();
        await _navigator.OpenFiltersAsync(IsHistory);
    }

    [RelayCommand]
    public async Task ApplyFiltersAsync()
    {
        Filter = Filter with
        {
            Priorities = [.. PriorityChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)],
            TagIds = [.. TagChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)],
            OverdueOnly = OverdueChoice && !IsHistory,
            Sort = SelectedSort?.Sort ?? TaskSort.Created,
        };

        await _navigator.BackAsync();
        await LoadPageAsync(reset: true, keepLoaded: false);
    }

    /// <summary>"Limpar filtros": volta ao atalho de partida e a todos os dispositivos, com a ordem escolhida.</summary>
    [RelayCommand]
    public Task ClearFiltersAsync()
    {
        CloseSearch();

        foreach (var choice in Presets)
        {
            choice.IsSelected = choice.Value == BasePreset;
        }

        Filter = TaskFilter.For(BasePreset) with { Sort = Filter.Sort };
        ChooseDevice(OriginCatalog.AnyKey);
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
        _focus.Changed -= OnFocusChanged;
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

    partial void OnFilterChanged(TaskFilter value)
    {
        OnPropertyChanged(nameof(FiltersLabel));
        OnPropertyChanged(nameof(HasSheetFilters));
    }

    /// <summary>Apaga o texto sem a busca com espera, e fecha a barra; quem chama relê.</summary>
    private void CloseSearch()
    {
        CancelPendingSearch();
        _clearingSearch = true;
        SearchText = null;
        _clearingSearch = false;
        IsSearchOpen = false;
    }

    private void ChooseDevice(string key)
    {
        _choosingDevice = true;

        try
        {
            _focus.Select(key);
        }
        finally
        {
            _choosingDevice = false;
        }

        RefreshDevices();
    }

    /// <summary>
    /// As opções a partir do catálogo, marcando a escolha. Só refaz a lista
    /// quando os dispositivos mudaram; senão, só troca a marca.
    /// </summary>
    private void RefreshDevices()
    {
        var options = _origins.FilterOptions();
        var selected = _focus.SelectedKey;

        if (!Devices.Select(chip => (chip.Value, chip.Label)).SequenceEqual(options.Select(option => (option.Key, option.Label))))
        {
            Devices.Clear();

            foreach (var option in options)
            {
                Devices.Add(new DeviceChip(option.Key, option.Label));
            }
        }

        foreach (var chip in Devices)
        {
            chip.IsSelected = chip.Value == selected;
        }

        OnPropertyChanged(nameof(ShowsDevices));
        OnPropertyChanged(nameof(DeviceTitle));
    }

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

        // O dispositivo sai da escolha gravada a cada busca: a conta pode ter ligado ou desligado no meio.
        var filter = Filter with { Origin = _focus.Match };

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
        RefreshDevices();
        _ = LoadPageAsync(reset: true, keepLoaded: true);
    });

    private void OnFocusChanged(object? sender, EventArgs e)
    {
        if (_choosingDevice)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            RefreshDevices();
            _ = LoadPageAsync(reset: true, keepLoaded: false);
        });
    }

    private void OnCycleStarted() => _dispatcher.Post(() =>
    {
        IsSyncing = true;
        _ = LoadSyncAsync();
    });

    /// <summary>O ciclo renova o diretório: um computador novo ou renomeado entra na folha, e o título acompanha.</summary>
    private void OnCycleCompleted(SyncCycleResult result) => _dispatcher.Post(() =>
    {
        IsSyncing = false;
        RefreshDevices();
        _ = LoadSyncAsync();
    });

    private void OnChangeQueued() => _dispatcher.Post(() => _ = LoadSyncAsync());
}
