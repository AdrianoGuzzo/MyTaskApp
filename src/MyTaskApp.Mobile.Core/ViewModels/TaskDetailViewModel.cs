using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>Uma linha do histórico local: o que, quando e quem.</summary>
public sealed record HistoryLine(string What, string When, string? Who);

/// <summary>Um repositório em que a tarefa é desenvolvida — só leitura no celular.</summary>
public sealed record DevelopmentLine(string Repository, string Branch, string Status);

/// <summary>
/// A tarefa inteira (ADR-067): o que o domínio sabe dela e as operações que a
/// regra permite agora. Cada botão chama o mesmo caso de uso do desktop; a
/// tela só esconde o que a regra recusaria, e a regra continua decidindo.
/// </summary>
/// <remarks>
/// O desenvolvimento (repositórios e worktrees) aparece como informação. Não há
/// sessão de agente aqui: tarefa em andamento não quer dizer IA rodando.
/// </remarks>
public sealed partial class TaskDetailViewModel(
    IUseCaseRunner runner,
    OriginCatalog origins,
    IDataChangeNotifier notifier,
    INavigator navigator,
    TimeProvider time,
    IDialogService dialogs,
    ILogger<TaskDetailViewModel> logger) : MobileViewModel(dialogs, logger)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete), nameof(CanReopen), nameof(CanCancel), nameof(CanArchive), nameof(CanPrepareRemotely))]
    [NotifyPropertyChangedFor(nameof(CanRestoreFromArchive), nameof(CanMoveToTrash), nameof(CanRestoreFromTrash), nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(HasNotes), nameof(HasDeadline), nameof(HasTags))]
    private TaskListItem? _item;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private string _statusLabel = string.Empty;

    [ObservableProperty]
    private string _priorityLabel = string.Empty;

    [ObservableProperty]
    private string _origin = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TagChip> _tags = [];

    [ObservableProperty]
    private string? _deadline;

    [ObservableProperty]
    private IReadOnlyList<string> _dates = [];

    [ObservableProperty]
    private string? _provenance;

    [ObservableProperty]
    private IReadOnlyList<HistoryLine> _history = [];

    [ObservableProperty]
    private IReadOnlyList<DevelopmentLine> _developments = [];

    [ObservableProperty]
    private string? _loadError;

    /// <summary>O controle remoto está ligado aqui: a tarefa pode ser preparada em outra máquina (E7).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrepareRemotely))]
    private bool _remoteEnabled;

    public Guid TaskId { get; private set; }

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public bool HasDeadline => Deadline is not null;

    public bool HasTags => Tags.Count > 0;

    private bool InMainList => Item is { Lifecycle: TaskLifecycle.Active or TaskLifecycle.Completed };

    public bool CanComplete => InMainList && Item!.Status == TaskItemStatus.Pending;

    public bool CanReopen => InMainList && Item!.Status != TaskItemStatus.Pending;

    public bool CanCancel => InMainList && Item!.Status == TaskItemStatus.Pending;

    public bool CanArchive => InMainList;

    public bool CanRestoreFromArchive => Item is { Lifecycle: TaskLifecycle.Archived };

    public bool CanMoveToTrash => Item is not null and not { Lifecycle: TaskLifecycle.Trashed };

    public bool CanRestoreFromTrash => Item is { Lifecycle: TaskLifecycle.Trashed };

    /// <summary>Editar é coisa da lista principal: arquivada e lixeira se restauram antes.</summary>
    public bool CanEdit => InMainList;

    public bool CanPrepareRemotely => RemoteEnabled && CanComplete;

    public async Task LoadAsync(Guid taskId)
    {
        TaskId = taskId;

        try
        {
            var item = await runner.RunAsync<GetTaskDetailsHandler, TaskListItem>(
                (handler, token) => handler.HandleAsync(new GetTaskDetails(taskId), token));

            Show(item);
            LoadError = null;
        }
        catch (DomainException refused)
        {
            Item = null;
            LoadError = refused.Message;
            return;
        }

        // O resto é complemento: sem ele, a tarefa continua na tela.
        Provenance = await TryReadAsync(ReadProvenanceAsync, "MobileTaskOriginFailed");
        History = await TryReadAsync(ReadHistoryAsync, "MobileTaskHistoryFailed") ?? [];
        Developments = await TryReadAsync(ReadDevelopmentsAsync, "MobileTaskDevelopmentFailed") ?? [];
        RemoteEnabled = await TryReadAsync(
            async () => (await runner.RunAsync<GetRemoteSettingsHandler, RemoteSettings>(
                (handler, token) => handler.HandleAsync(new GetRemoteSettings(), token))).Enabled,
            "MobileTaskRemoteSettingsFailed");
    }

    /// <summary>O "Preparar em…": worktree, comandos, banco e Claude numa máquina pareada.</summary>
    [RelayCommand]
    public Task PrepareRemotelyAsync() => navigator.OpenRemotePrepareAsync(TaskId);

    [RelayCommand]
    public Task CompleteAsync() => ChangeAsync(
        "Tarefa concluída.",
        () => runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(Item!.Task.OccurrenceId), token)));

    [RelayCommand]
    public Task ReopenAsync() => ChangeAsync(
        "Tarefa reaberta.",
        () => runner.RunAsync<ReopenOccurrenceHandler>((handler, token) => handler.HandleAsync(new ReopenOccurrence(Item!.Task.OccurrenceId), token)));

    [RelayCommand]
    public async Task CancelTaskAsync()
    {
        if (await Dialogs.ConfirmAsync("Cancelar tarefa", "A tarefa fica como cancelada, e pode ser reaberta depois.", "Cancelar tarefa", "Voltar"))
        {
            await ChangeAsync(
                "Tarefa cancelada.",
                () => runner.RunAsync<CancelOccurrenceHandler>((handler, token) => handler.HandleAsync(new CancelOccurrence(Item!.Task.OccurrenceId), token)));
        }
    }

    [RelayCommand]
    public Task ArchiveAsync() => ChangeAsync(
        "Tarefa arquivada.",
        () => runner.RunAsync<ArchiveChecklistHandler>((handler, token) => handler.HandleAsync(new ArchiveChecklist(TaskId), token)));

    [RelayCommand]
    public Task RestoreFromArchiveAsync() => ChangeAsync(
        "Tarefa de volta à lista.",
        () => runner.RunAsync<RestoreChecklistHandler>((handler, token) => handler.HandleAsync(new RestoreChecklist(TaskId), token)));

    [RelayCommand]
    public async Task MoveToTrashAsync()
    {
        if (await Dialogs.ConfirmAsync("Mover para a lixeira", "A tarefa sai da lista e pode ser restaurada da lixeira.", "Mover", "Voltar"))
        {
            await ChangeAsync(
                "Tarefa na lixeira.",
                () => runner.RunAsync<MoveChecklistToTrashHandler>((handler, token) => handler.HandleAsync(new MoveChecklistToTrash(TaskId), token)));
        }
    }

    [RelayCommand]
    public Task RestoreFromTrashAsync() => ChangeAsync(
        "Tarefa restaurada da lixeira.",
        () => runner.RunAsync<RestoreChecklistFromTrashHandler>((handler, token) => handler.HandleAsync(new RestoreChecklistFromTrash(TaskId), token)));

    [RelayCommand]
    public Task EditAsync() => navigator.EditTaskAsync(TaskId);

    private async Task ChangeAsync(string done, Func<Task> operation)
    {
        if (Item is null)
        {
            return;
        }

        if (await TryAsync(operation, "MobileTaskChangeFailed"))
        {
            await Dialogs.ToastAsync(done);
            notifier.NotifyChanged(DataArea.Tasks);
            await LoadAsync(TaskId);
        }
    }

    private void Show(TaskListItem item)
    {
        var task = item.Task;
        var zone = time.LocalTimeZone;

        Item = item;
        Title = task.Title;
        Notes = string.IsNullOrWhiteSpace(task.Notes) ? null : task.Notes;
        StatusLabel = Labels.Status(item.Status, item.Lifecycle);
        PriorityLabel = Labels.Priority(task.Priority);
        Origin = origins.Label(task.Placement);
        Tags = task.Tags?.Select(tag => new TagChip(tag.Id, tag.Name, tag.ColorHex)).ToList() ?? [];
        Deadline = task.Deadline is { } deadline ? $"{deadline.Label} ({deadline.DateLabel})" : null;

        var dates = new List<string> { "Criada em " + Labels.Instant(item.CreatedAt, zone) };

        if (item.CompletedAt is { } completed)
        {
            dates.Add("Concluída em " + Labels.Instant(completed, zone));
        }

        if (item.ArchivedAt is { } archived)
        {
            dates.Add("Arquivada em " + Labels.Instant(archived, zone));
        }

        if (item.DeletedAt is { } deleted)
        {
            dates.Add("Na lixeira desde " + Labels.Instant(deleted, zone) + (item.DeletedBy is { } by ? $" ({by})" : string.Empty));
        }

        Dates = dates;
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasDeadline));
        OnPropertyChanged(nameof(HasTags));
    }

    private async Task<string?> ReadProvenanceAsync()
    {
        var origin = await runner.RunAsync<GetTaskOriginHandler, TaskOriginView?>(
            (handler, token) => handler.HandleAsync(new GetTaskOrigin(TaskId), token));

        if (origin is null)
        {
            return null;
        }

        var zone = time.LocalTimeZone;
        var created = $"Criada em {Device(origin.CreatedOn)}" + (origin.CreatedAt is { } at ? $", {Labels.Instant(at, zone)}" : string.Empty);

        return origin.UpdatedOn is { } updated
            ? created + $" · alterada em {Device(updated)}" + (origin.UpdatedAt is { } changed ? $", {Labels.Instant(changed, zone)}" : string.Empty)
            : created;
    }

    private async Task<IReadOnlyList<HistoryLine>> ReadHistoryAsync()
    {
        var entries = await runner.RunAsync<GetChecklistAuditHandler, IReadOnlyList<TaskAuditEntry>>(
            (handler, token) => handler.HandleAsync(new GetChecklistAudit(TaskId), token));

        var zone = time.LocalTimeZone;

        return [.. entries
            .OrderByDescending(entry => entry.OccurredAt)
            .Select(entry => new HistoryLine(
                Labels.Audit(entry.Operation),
                Labels.Instant(entry.OccurredAt, zone),
                entry.Actor == AuditActor.System ? "automático" : entry.ActorName))];
    }

    private async Task<IReadOnlyList<DevelopmentLine>> ReadDevelopmentsAsync()
    {
        var developments = await runner.RunAsync<GetTaskDevelopmentsHandler, IReadOnlyList<TaskDevelopmentView>>(
            (handler, token) => handler.HandleAsync(new GetTaskDevelopments(TaskId), token));

        return [.. developments
            .Where(development => development.Status != TaskDevelopmentStatus.Removed)
            .Select(development => new DevelopmentLine(
                RepositoryName(development.RepositoryPath),
                development.Branch,
                Labels.Development(development.Status)))];
    }

    private async Task<T?> TryReadAsync<T>(Func<Task<T>> read, string logEvent)
    {
        try
        {
            return await read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "{Event}", logEvent);
            return default;
        }
    }

    /// <summary>"este computador" do caso de uso é este aparelho, aqui.</summary>
    private static string Device(string name) =>
        name == GetTaskOriginHandler.ThisComputer ? "este celular" : name;

    public const string RepositoryElsewhere = "Repositório de outro computador";

    /// <summary>
    /// O nome da pasta, que é o que identifica o repositório. O caminho é por
    /// computador e não sincroniza (ADR-061): o worktree de outro chega sem ele.
    /// </summary>
    internal static string RepositoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return RepositoryElsewhere;
        }

        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);

        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
    }
}
