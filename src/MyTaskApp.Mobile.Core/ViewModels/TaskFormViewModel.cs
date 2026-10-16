using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

public sealed record PriorityChoice(TaskPriority Priority, string Label)
{
    public override string ToString() => Label;
}

public sealed record StatusChoice(TaskItemStatus Status, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Criar e editar uma tarefa no celular (ADR-065). Nenhuma regra é repetida
/// aqui além do que a tela precisa para avisar antes de enviar (título vazio
/// ou longo demais): quem decide é a Application — prazo no passado, status
/// que não pode mudar, etiqueta que não existe —, e a frase dela aparece como veio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Edição mínima.</b> Só vai o que mudou, como o <see cref="EditTask"/> pede:
/// trocar o título não reenvia o prazo, que pode já ter vencido.
/// </para>
/// <para>
/// <b>Status pelas transições do domínio.</b> Voltar para pendente reabre antes
/// de editar (o prazo só muda em pendente); concluir ou cancelar vem depois.
/// Não é uma transação só: se um passo for recusado, o formulário relê a tarefa
/// e mostra o que ficou gravado.
/// </para>
/// </remarks>
public sealed partial class TaskFormViewModel(
    IUseCaseRunner runner,
    OriginCatalog origins,
    IDataChangeNotifier notifier,
    INavigator navigator,
    IUserClock clock,
    IDialogService dialogs,
    ILogger<TaskFormViewModel> logger) : MobileViewModel(dialogs, logger)
{
    public static readonly TimeSpan DefaultDeadlineTime = new(18, 0, 0);

    private FormSnapshot _saved = FormSnapshot.Empty;

    private TaskItemStatus _savedStatus = TaskItemStatus.Pending;

    private DevicePlacement? _savedPlacement;

    private Guid _occurrenceId;

    /// <summary>O aviso do título só aparece depois de mexer nele ou de tentar salvar.</summary>
    private bool _titleTouched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleError), nameof(IsValid), nameof(IsDirty), nameof(CanSave), nameof(TitleCount))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private string? _notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private PriorityChoice _priority = Priorities[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private OriginChoice? _origin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private bool _hasDeadline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private DateTime _deadlineDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private TimeSpan _deadlineTime = DefaultDeadlineTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private StatusChoice _status = Statuses[0];

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string? _loadError;

    public static IReadOnlyList<PriorityChoice> Priorities { get; } =
    [
        new(TaskPriority.Low, Labels.Priority(TaskPriority.Low)),
        new(TaskPriority.Normal, Labels.Priority(TaskPriority.Normal)),
        new(TaskPriority.High, Labels.Priority(TaskPriority.High)),
        new(TaskPriority.Urgent, Labels.Priority(TaskPriority.Urgent)),
    ];

    public static IReadOnlyList<StatusChoice> Statuses { get; } =
    [
        new(TaskItemStatus.Pending, Labels.StatusChoice(TaskItemStatus.Pending)),
        new(TaskItemStatus.Completed, Labels.StatusChoice(TaskItemStatus.Completed)),
        new(TaskItemStatus.Cancelled, Labels.StatusChoice(TaskItemStatus.Cancelled)),
    ];

    public IReadOnlyList<PriorityChoice> PriorityOptions => Priorities;

    public IReadOnlyList<StatusChoice> StatusOptions => Statuses;

    public ObservableCollection<OriginChoice> OriginOptions { get; } = [];

    public ObservableCollection<TagOption> TagChoices { get; } = [];

    /// <summary><c>null</c> = tarefa nova.</summary>
    public Guid? TaskId { get; private set; }

    public bool IsNew => TaskId is null;

    public string PageTitle => IsNew ? "Nova tarefa" : "Editar tarefa";

    /// <summary>Sem conta ligada, tudo é deste celular: não há o que escolher.</summary>
    public bool ShowsOrigin => OriginOptions.Count > 0;

    public bool ShowsStatus => !IsNew;

    /// <summary>Sem etiquetas cadastradas, a tela explica onde se criam.</summary>
    public bool HasTags => TagChoices.Count > 0;

    public string TitleCount => $"{Title.Trim().Length}/{TaskItem.MaxTitleLength}";

    /// <summary>O aviso antes de enviar; a regra de verdade é a do domínio.</summary>
    public string? TitleError =>
        string.IsNullOrWhiteSpace(Title) ? "Dê um título à tarefa."
        : Title.Trim().Length > TaskItem.MaxTitleLength ? $"O título passa de {TaskItem.MaxTitleLength} caracteres."
        : null;

    /// <summary>O aviso que a tela mostra: o formulário novo não abre já reclamando.</summary>
    public string? VisibleTitleError => _titleTouched ? TitleError : null;

    public bool IsValid => TitleError is null;

    public bool IsDirty => IsLoaded && Current() != _saved;

    public bool CanSave => IsValid && (IsNew || IsDirty) && !IsBusy;

    public async Task InitializeNewAsync()
    {
        TaskId = null;
        _occurrenceId = Guid.Empty;
        _savedStatus = TaskItemStatus.Pending;
        _savedPlacement = origins.DefaultForNew();

        Title = string.Empty;
        Notes = null;
        Priority = Priorities[1];
        HasDeadline = false;
        DeadlineDate = clock.Today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        DeadlineTime = DefaultDeadlineTime;
        Status = Statuses[0];

        FillOrigins(_savedPlacement);
        await LoadTagsAsync([]);
        Loaded();
    }

    public async Task InitializeEditAsync(Guid taskId)
    {
        TaskId = taskId;

        TaskListItem item;

        try
        {
            item = await runner.RunAsync<GetTaskDetailsHandler, TaskListItem>(
                (handler, token) => handler.HandleAsync(new GetTaskDetails(taskId), token));
        }
        catch (DomainException refused)
        {
            LoadError = refused.Message;
            return;
        }

        var task = item.Task;
        _occurrenceId = task.OccurrenceId;
        _savedStatus = item.Status;
        _savedPlacement = task.Placement;

        Title = task.Title;
        Notes = task.Notes;
        Priority = Priorities.First(choice => choice.Priority == task.Priority);
        Status = Statuses.First(choice => choice.Status == item.Status);

        if (task.Deadline is { Deadline: var deadline })
        {
            HasDeadline = true;
            DeadlineDate = deadline.Date.ToDateTime(TimeOnly.MinValue);
            DeadlineTime = deadline.Time.ToTimeSpan();
        }
        else
        {
            HasDeadline = false;
            DeadlineDate = clock.Today.AddDays(1).ToDateTime(TimeOnly.MinValue);
            DeadlineTime = DefaultDeadlineTime;
        }

        FillOrigins(task.Placement);
        await LoadTagsAsync(task.Tags?.Select(tag => tag.Id) ?? []);
        LoadError = null;
        Loaded();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!IsValid)
        {
            _titleTouched = true;
            OnPropertyChanged(nameof(VisibleTitleError));
            await Dialogs.AlertAsync("Revise a tarefa", TitleError!);
            return;
        }

        var saved = await TryAsync(IsNew ? CreateAsync : UpdateAsync, "MobileTaskSaveFailed");

        if (saved)
        {
            _saved = Current();
            notifier.NotifyChanged(DataArea.Tasks);
            await Dialogs.ToastAsync(IsNew ? "Tarefa criada." : "Tarefa salva.");
            await navigator.BackAsync();
        }
        else if (!IsNew)
        {
            // Um passo pode ter gravado antes do recusado: mostra o que ficou.
            await InitializeEditAsync(TaskId!.Value);
        }

        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Sair com alterações pergunta antes: o voltar do sistema e o "Cancelar" passam por aqui.</summary>
    public async Task<bool> CanLeaveAsync() =>
        !IsDirty || await Dialogs.ConfirmAsync("Descartar alterações?", "O que você mudou nesta tarefa não foi salvo.", "Descartar", "Continuar editando");

    [RelayCommand]
    public async Task CloseAsync()
    {
        if (await CanLeaveAsync())
        {
            _saved = Current();
            await navigator.BackAsync();
        }
    }

    [RelayCommand]
    public void ToggleTag(TagOption? tag)
    {
        if (tag is not null)
        {
            tag.IsSelected = !tag.IsSelected;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsBusy))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CanSave)));
        }
    }

    private async Task CreateAsync()
    {
        var tags = SelectedTags();

        var details = new EditTask(Guid.Empty)
        {
            TagIds = tags.Count > 0 ? tags : null,
            Deadline = HasDeadline ? new Change<TaskDeadline?>(ChosenDeadline()) : null,
        };

        await runner.RunAsync<CreateDetailedTaskHandler, CreateTaskResult>(
            (handler, token) => handler.HandleAsync(
                new CreateDetailedTask(
                    new CreateTask(Title.Trim(), NormalizedNotes(), Priority.Priority, Placement: Origin?.Placement ?? _savedPlacement),
                    details),
                token));
    }

    private async Task UpdateAsync()
    {
        var taskId = TaskId!.Value;
        var steps = StatusTransitions.Plan(_savedStatus, Status.Status);
        var reopenFirst = steps.Count > 0 && steps[0] == StatusStep.Reopen;

        if (reopenFirst)
        {
            await RunStepAsync(StatusStep.Reopen);
        }

        var edit = Changes(taskId);

        if (!edit.IsEmpty)
        {
            await runner.RunAsync<EditTaskHandler>((handler, token) => handler.HandleAsync(edit, token));
        }

        foreach (var step in reopenFirst ? steps.Skip(1) : steps)
        {
            await RunStepAsync(step);
        }

        // Contra o que a tela mostrava ao abrir: o legado sem lugar aparece como
        // "Compartilhada", e salvar sem mexer não deve mover nada.
        if (Origin?.Placement is { } placement && placement != _saved.Placement)
        {
            await runner.RunAsync<PlaceTaskHandler, PlaceResult>(
                (handler, token) => handler.HandleAsync(new PlaceTask(taskId, placement), token));
        }
    }

    private EditTask Changes(Guid taskId)
    {
        var now = Current();

        return new EditTask(taskId)
        {
            Title = now.Title != _saved.Title ? Title.Trim() : null,
            Description = now.Notes != _saved.Notes ? new Change<string?>(NormalizedNotes()) : null,
            Priority = now.Priority != _saved.Priority ? Priority.Priority : null,
            TagIds = !now.Tags.SetEquals(_saved.Tags) ? SelectedTags() : null,
            Deadline = now.Deadline != _saved.Deadline ? new Change<TaskDeadline?>(HasDeadline ? ChosenDeadline() : null) : null,
        };
    }

    private Task RunStepAsync(StatusStep step) => step switch
    {
        StatusStep.Complete => runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(_occurrenceId), token)),
        StatusStep.Cancel => runner.RunAsync<CancelOccurrenceHandler>((handler, token) => handler.HandleAsync(new CancelOccurrence(_occurrenceId), token)),
        _ => runner.RunAsync<ReopenOccurrenceHandler>((handler, token) => handler.HandleAsync(new ReopenOccurrence(_occurrenceId), token)),
    };

    private void FillOrigins(DevicePlacement? placement)
    {
        OriginOptions.Clear();

        foreach (var choice in origins.FormChoices())
        {
            OriginOptions.Add(choice);
        }

        var current = origins.ChoiceFor(placement);

        if (current is not null && !OriginOptions.Contains(current))
        {
            OriginOptions.Add(current);
        }

        Origin = current;
        OnPropertyChanged(nameof(ShowsOrigin));
    }

    private async Task LoadTagsAsync(IEnumerable<Guid> selected)
    {
        var chosen = selected.ToHashSet();
        TagChoices.Clear();

        var tags = await runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
            (handler, token) => handler.HandleAsync(new GetTags(DeviceView.All, chosen), token));

        foreach (var tag in tags.OrderBy(tag => tag.Name, StringComparer.Create(Labels.Culture, ignoreCase: true)))
        {
            TagChoices.Add(new TagOption(tag.Id, tag.Name, tag.ColorHex) { IsSelected = chosen.Contains(tag.Id) });
        }

        OnPropertyChanged(nameof(HasTags));
    }

    partial void OnTitleChanged(string value)
    {
        if (IsLoaded)
        {
            _titleTouched = true;
        }

        OnPropertyChanged(nameof(VisibleTitleError));
    }

    private void Loaded()
    {
        IsLoaded = true;
        _titleTouched = false;
        OnPropertyChanged(nameof(VisibleTitleError));
        _saved = Current();
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(ShowsStatus));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
    }

    private IReadOnlyCollection<Guid> SelectedTags() =>
        [.. TagChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)];

    private TaskDeadline ChosenDeadline() =>
        new(DateOnly.FromDateTime(DeadlineDate), TimeOnly.FromTimeSpan(DeadlineTime));

    private string? NormalizedNotes() => string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();

    private FormSnapshot Current() => new(
        Title.Trim(),
        string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
        Priority.Priority,
        Origin?.Placement,
        [.. TagChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)],
        HasDeadline ? ChosenDeadline() : null,
        Status.Status);

    /// <summary>O formulário como estava ao abrir ou salvar: é contra ele que "mudou" se mede.</summary>
    private sealed record FormSnapshot(
        string Title,
        string? Notes,
        TaskPriority Priority,
        DevicePlacement? Placement,
        HashSet<Guid> Tags,
        TaskDeadline? Deadline,
        TaskItemStatus Status)
    {
        public static FormSnapshot Empty { get; } = new(string.Empty, null, TaskPriority.Normal, null, [], null, TaskItemStatus.Pending);

        public bool Equals(FormSnapshot? other) =>
            other is not null
            && Title == other.Title
            && Notes == other.Notes
            && Priority == other.Priority
            && Placement == other.Placement
            && Tags.SetEquals(other.Tags)
            && Deadline == other.Deadline
            && Status == other.Status;

        public override int GetHashCode() => HashCode.Combine(Title, Notes, Priority, Placement, Tags.Count, Deadline, Status);
    }
}
