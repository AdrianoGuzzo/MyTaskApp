using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.External;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O cartão da issue na janela da tarefa (ADR-045): o que é a tarefa, sem
/// abrir o Jira — tipo, chave, título, status, o link e a branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Offline primeiro.</b> O cartão nasce do retrato que veio com a linha,
/// antes de qualquer consulta: a tarefa se desenha igual com o Jira fora do ar.
/// "Atualizar do Jira" é o único caminho até a rede, e é manual.
/// </para>
/// <para>
/// <b>Vincular depois.</b> Uma tarefa criada sem issue ganha uma busca no
/// próprio cartão — a mesma do autocomplete da captura.
/// </para>
/// </remarks>
public sealed partial class TaskIssueViewModel(
    IUseCaseRunner runner,
    IShellLauncher shell,
    IClipboardWriter clipboard,
    IConfirmationDialog confirmation,
    TimeProvider timeProvider,
    ILogger<TaskIssueViewModel> logger) : ObservableObject
{
    private Guid _taskId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasLink), nameof(Key), nameof(IssueTitle), nameof(TypeLabel), nameof(Status), nameof(HasStatus),
        nameof(IsBug), nameof(IsStory), nameof(IsTask), nameof(SyncedLabel), nameof(CanLink), nameof(CanEdit),
        nameof(ProviderName), nameof(OpenLabel))]
    private ExternalLink? _link;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedBranch))]
    private string? _suggestedBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLink), nameof(CanEdit))]
    private bool _isReadOnly;

    /// <summary>A busca do "Vincular ao Jira" está aberta no cartão.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLink))]
    private bool _isLinking;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>A busca do vínculo: a mesma do autocomplete da captura.</summary>
    public IssueSuggestionsViewModel Suggestions { get; } = new(runner, timeProvider, logger);

    public bool HasLink => Link is not null;

    public string Key => Link?.Id ?? string.Empty;

    public string IssueTitle => Link?.Title ?? string.Empty;

    public string ProviderName => Link?.Provider ?? "Jira";

    public string OpenLabel => $"{ProviderName} ↗";

    public string TypeLabel => IssueTypes.Label(Link?.IssueType);

    public string? Status => Link?.Status;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Link?.Status);

    public bool IsBug => IssueTypes.KindOf(Link?.IssueType) == IssueKind.Bug;

    public bool IsStory => IssueTypes.KindOf(Link?.IssueType) == IssueKind.Story;

    public bool IsTask => IssueTypes.KindOf(Link?.IssueType) == IssueKind.Task;

    public bool HasSuggestedBranch => !string.IsNullOrWhiteSpace(SuggestedBranch);

    /// <summary>Concluída, a tarefa é histórico: dá para abrir e copiar, não para mudar o vínculo.</summary>
    public bool CanEdit => HasLink && !IsReadOnly;

    public bool CanLink => !HasLink && !IsReadOnly && !IsLinking;

    /// <summary>"Atualizado há 3 h": quanto o retrato pode estar velho.</summary>
    public string SyncedLabel => Link is null ? string.Empty : $"Lido do {ProviderName} {Ago(Link.SyncedAt)}";

    /// <summary>
    /// A branch mudou: a aba Desenvolvimento passa a sugerir a da convenção.
    /// <c>null</c> = a tarefa não tem issue, e vale a sugestão de sempre.
    /// </summary>
    public event Action<string?>? BranchSuggested;

    /// <summary>O vínculo mudou: a lista redesenha a chave da linha.</summary>
    public event Action? Changed;

    /// <summary>O retrato da linha, na hora — sem esperar banco nem rede.</summary>
    public void Load(Guid taskId, ExternalLink? snapshot, bool isReadOnly)
    {
        _taskId = taskId;
        Link = snapshot;
        IsReadOnly = isReadOnly;
        SuggestedBranch = null;
        IsLinking = false;
        ErrorMessage = null;
        StatusMessage = null;
    }

    /// <summary>O vínculo como está no banco, e a branch da convenção.</summary>
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var context = await runner.RunAsync<GetTaskExternalContextHandler, TaskExternalContext>(
                (handler, token) => handler.HandleAsync(new GetTaskExternalContext(_taskId), token),
                cancellationToken);

            Show(context);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // O retrato da linha continua na tela: é só a branch sugerida que fica de fora.
            logger.LogWarning(exception, "TaskIssueContextFailed {TaskId}", _taskId);
        }
    }

    [RelayCommand]
    public async Task OpenInJiraAsync()
    {
        if (Link is not { } link || !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!await shell.OpenUriAsync(uri))
        {
            ErrorMessage = "Não foi possível abrir o navegador.";
        }
    }

    [RelayCommand]
    public Task CopyKeyAsync() => CopyAsync(Link?.Id, "Chave copiada.");

    [RelayCommand]
    public Task CopyUrlAsync() => CopyAsync(Link?.Url, "Link copiado.");

    [RelayCommand]
    public Task CopyBranchAsync() => CopyAsync(SuggestedBranch, "Nome da branch copiado.");

    /// <summary>"Atualizar do Jira": título, tipo e status como estão agora.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        ExternalLink? fresh = null;

        var refreshed = await TryAsync(
            async () => fresh = await runner.RunAsync<RefreshExternalTaskHandler, ExternalLink>(
                (handler, token) => handler.HandleAsync(new RefreshExternalTask(_taskId), token)),
            "Não foi possível atualizar do Jira agora. A tarefa continua como estava.");

        if (refreshed)
        {
            Link = fresh;
            StatusMessage = "Atualizado do Jira.";
            Changed?.Invoke();
            await ActivateAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    public async Task UnlinkAsync()
    {
        if (Link is not { } link)
        {
            return;
        }

        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            $"Desvincular de {link.Id}?",
            "A tarefa, o título, a anotação e os worktrees continuam. Só a chave e o link do Jira saem dela.",
            "Desvincular"));

        if (!confirmed)
        {
            return;
        }

        var unlinked = await TryAsync(
            () => runner.RunAsync<UnlinkTaskFromExternalHandler>(
                (handler, token) => handler.HandleAsync(new UnlinkTaskFromExternal(_taskId), token)),
            "Não foi possível desvincular a tarefa.");

        if (unlinked)
        {
            Link = null;
            SuggestedBranch = null;
            BranchSuggested?.Invoke(null);
            Changed?.Invoke();
        }
    }

    /// <summary>"Vincular ao Jira": abre a busca no cartão, se houver Jira conectado.</summary>
    [RelayCommand]
    public async Task StartLinkingAsync()
    {
        ErrorMessage = null;

        try
        {
            var connection = await runner.RunAsync<GetJiraConnectionHandler, JiraConnection>(
                (handler, token) => handler.HandleAsync(new GetJiraConnection(), token));

            Suggestions.IsAvailable = connection.IsConnected;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TaskIssueConnectionCheckFailed");
            Suggestions.IsAvailable = false;
        }

        if (!Suggestions.IsAvailable)
        {
            ErrorMessage = "Conecte o Jira em ☰ → Integrações… para vincular esta tarefa.";
            return;
        }

        SearchText = string.Empty;
        Suggestions.Reset();
        IsLinking = true;
    }

    [RelayCommand]
    public void CancelLinking()
    {
        Suggestions.Reset();
        IsLinking = false;
    }

    /// <summary>Enter na busca: a marcada, ou a primeira — aqui Enter não tem outro sentido.</summary>
    public Task<bool> AcceptSuggestionAsync() =>
        Suggestions.Accept(orFirst: true) is { } issue ? LinkAsync(issue) : Task.FromResult(false);

    public async Task<bool> LinkAsync(ExternalTask issue)
    {
        ExternalLink? linked = null;

        var done = await TryAsync(
            async () => linked = await runner.RunAsync<LinkTaskToExternalHandler, ExternalLink>(
                (handler, token) => handler.HandleAsync(new LinkTaskToExternal(_taskId, issue), token)),
            "Não foi possível vincular a tarefa.");

        if (!done)
        {
            return false;
        }

        Link = linked;
        IsLinking = false;
        Suggestions.Reset();
        StatusMessage = $"Vinculada a {issue.Id}.";
        Changed?.Invoke();
        await ActivateAsync(CancellationToken.None);
        return true;
    }

    partial void OnSearchTextChanged(string value) => Suggestions.UpdateQuery(value);

    private void Show(TaskExternalContext context)
    {
        Link = context.Link;
        SuggestedBranch = context.SuggestedBranch;
        BranchSuggested?.Invoke(context.SuggestedBranch);
    }

    private async Task CopyAsync(string? text, string done)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await clipboard.WriteAsync(text);
            ErrorMessage = null;
            StatusMessage = done;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TaskIssueCopyFailed");
            ErrorMessage = "Não foi possível copiar.";
        }
    }

    private string Ago(DateTimeOffset instant)
    {
        var elapsed = timeProvider.GetUtcNow() - instant;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "agora há pouco",
            { TotalMinutes: < 60 } => $"há {(int)elapsed.TotalMinutes} min",
            { TotalHours: < 24 } => $"há {(int)elapsed.TotalHours} h",
            _ => "em " + TimeZoneInfo.ConvertTime(instant, timeProvider.LocalTimeZone)
                .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        };
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TaskIssueOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
