using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba "Worktrees" do Gerenciamento de dados (ADR-065): a lista dos worktrees
/// sem tarefa e dos de tarefa encerrada, todos marcados, e um botão que apaga
/// os marcados — com as alterações não commitadas junto.
/// </summary>
/// <remarks>
/// A varredura chama o Git em cada repositório, então não roda ao abrir a
/// janela: roda ao abrir a aba, e de novo em "Procurar".
/// </remarks>
public sealed partial class WorktreeCleanupViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<WorktreeCleanupViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClean))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Os repositórios em que o Git falhou: ficaram fora da lista.</summary>
    [ObservableProperty]
    private string? _problemsMessage;

    public ObservableCollection<WorktreeCleanupItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => HasScanned && !IsBusy && Items.Count == 0;

    public int SelectedCount => Items.Count(item => item.IsSelected);

    public bool CanClean => !IsBusy && SelectedCount > 0;

    /// <summary>"Limpar 3 worktrees".</summary>
    public string CleanLabel => SelectedCount == 1 ? "Limpar 1 worktree" : $"Limpar {SelectedCount} worktrees";

    /// <summary>A lista principal precisa ser recarregada: a bolinha de worktree mudou.</summary>
    public event Action? WorktreesChanged;

    /// <summary>
    /// A varredura de quando a aba aparece: a lista de ontem pode mostrar um
    /// worktree que já saiu, ou esconder um que a tarefa concluída de hoje liberou.
    /// </summary>
    public Task ScanIfIdleAsync(CancellationToken cancellationToken) =>
        IsBusy ? Task.CompletedTask : ScanAsync(cancellationToken);

    [RelayCommand]
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        WorktreeCleanupScan? scan = null;

        var scanned = await TryAsync(
            async () => scan = await runner.RunAsync<FindWorktreeCleanupHandler, WorktreeCleanupScan>(
                (handler, token) => handler.HandleAsync(new FindWorktreeCleanup(), token),
                cancellationToken),
            "Não foi possível procurar os worktrees.");

        if (scanned)
        {
            Show(scan!);
        }
    }

    [RelayCommand]
    public void SelectAll() => SetSelection(true);

    [RelayCommand]
    public void SelectNone() => SetSelection(false);

    [RelayCommand]
    public async Task CleanAsync(CancellationToken cancellationToken)
    {
        var selected = Items.Where(item => item.IsSelected).ToList();

        if (selected.Count == 0 || !await confirmation.AskAsync(CleanupPrompt(selected)))
        {
            return;
        }

        WorktreeCleanupResult? result = null;

        var cleaned = await TryAsync(
            async () => result = await runner.RunAsync<CleanWorktreesHandler, WorktreeCleanupResult>(
                (handler, token) => handler.HandleAsync(
                    new CleanWorktrees([.. selected.Select(item => item.Candidate)]),
                    token),
                cancellationToken),
            "Não foi possível limpar os worktrees.");

        if (!cleaned)
        {
            return;
        }

        if (result!.Removed > 0)
        {
            WorktreesChanged?.Invoke();
        }

        // Varre de novo antes das mensagens: a varredura limpa as duas.
        await ScanAsync(cancellationToken);

        StatusMessage = result.Removed switch
        {
            0 => null,
            1 => "1 worktree removido. As branches continuam no repositório.",
            var count => $"{count} worktrees removidos. As branches continuam no repositório.",
        };

        if (result.Failures.Count > 0)
        {
            ErrorMessage = string.Join(
                Environment.NewLine,
                result.Failures.Select(failure => $"{failure.WorktreePath}: {failure.Message}"));
        }
    }

    /// <summary>
    /// A pergunta antes de apagar. Diz quantos, e conta o que se perde: as
    /// alterações não commitadas vão junto, e o commit sem push só existe aqui
    /// (mas a branch fica, então ele também fica).
    /// </summary>
    public static ConfirmationRequest CleanupPrompt(IReadOnlyList<WorktreeCleanupItemViewModel> selected)
    {
        var dirty = selected.Count(item => item.Candidate.Sync.Changes > 0);

        var lines = new List<string>
        {
            selected.Count == 1
                ? "1 worktree será apagado do disco e do repositório:"
                : $"{selected.Count} worktrees serão apagados do disco e do repositório:",
            string.Empty,
        };

        foreach (var item in selected)
        {
            lines.Add($"• {item.Title} — {item.ReasonLabel}");

            if (item.Candidate.Sync.Changes > 0)
            {
                lines.Add($"   ⚠ {item.Warning}");
            }
        }

        lines.Add(string.Empty);

        if (dirty > 0)
        {
            lines.Add(dirty == 1
                ? "⚠ 1 deles tem alterações não commitadas, que serão perdidas."
                : $"⚠ {dirty} deles têm alterações não commitadas, que serão perdidas.");
        }

        lines.Add("As branches continuam no repositório, com os commits. O banco copiado de um ambiente não é apagado.");

        return new ConfirmationRequest(
            selected.Count == 1 ? "Apagar 1 worktree?" : $"Apagar {selected.Count} worktrees?",
            string.Join(Environment.NewLine, lines),
            "Apagar",
            IsIrreversible: true,
            CancelLabel: selected.Count == 1 ? "Manter worktree" : "Manter worktrees");
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnHasScannedChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    private void Show(WorktreeCleanupScan scan)
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
        }

        Items.Clear();

        foreach (var candidate in scan.Candidates)
        {
            var item = new WorktreeCleanupItemViewModel(candidate);
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        ProblemsMessage = scan.Problems.Count == 0
            ? null
            : "Não foi possível ler estes repositórios, que ficaram fora da lista:"
              + Environment.NewLine
              + string.Join(Environment.NewLine, scan.Problems);

        HasScanned = true;
        OnSelectionChanged();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SetSelection(bool selected)
    {
        foreach (var item in Items.Where(item => item.CanSelect))
        {
            item.IsSelected = selected;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorktreeCleanupItemViewModel.IsSelected))
        {
            OnSelectionChanged();
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(CleanLabel));
    }

    /// <summary>Mesmo caminho de erro do resto do app (ADR-008).</summary>
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "WorktreeCleanupOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// Uma linha da limpeza. Vem marcada, a não ser que haja motivo para o usuário
/// olhar duas vezes: travada com <c>git worktree lock</c> (alguém pediu para o
/// Git não mexer) ou com um agente de IA aberto — esta nem pode ser marcada.
/// </summary>
public sealed partial class WorktreeCleanupItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public WorktreeCleanupItemViewModel(WorktreeCleanupCandidate candidate)
    {
        Candidate = candidate;
        IsSelected = CanSelect && !candidate.IsLocked;
    }

    public WorktreeCleanupCandidate Candidate { get; }

    public bool CanSelect => !Candidate.HasActiveAgent;

    /// <summary>"eco-core · feature/x".</summary>
    public string Title => $"{Candidate.RepositoryName} · {Candidate.Branch ?? "HEAD destacado"}";

    public string Path => Candidate.WorktreePath;

    public string ReasonLabel => Candidate.Reason switch
    {
        WorktreeCleanupReason.Completed => $"Tarefa concluída: {Candidate.TaskTitle}",
        WorktreeCleanupReason.Cancelled => $"Tarefa cancelada: {Candidate.TaskTitle}",
        WorktreeCleanupReason.Archived => $"Tarefa arquivada: {Candidate.TaskTitle}",
        WorktreeCleanupReason.Trashed => $"Tarefa na lixeira: {Candidate.TaskTitle}",
        _ => "Sem tarefa",
    };

    /// <summary>"3 alterações não commitadas · 2 commits sem push", como no balão da lista.</summary>
    public string State => Candidate.Exists
        ? WorktreeLineViewModel.Describe(Candidate.Sync)
        : "a pasta já não existe — só falta o Git esquecer";

    public bool IsDirty => Candidate.Sync.Changes > 0;

    /// <summary>O aviso vermelho da linha: o que se perde ao apagar.</summary>
    public string Warning => Candidate.Sync.Changes == 1
        ? "1 alteração não commitada será perdida"
        : $"{Candidate.Sync.Changes} alterações não commitadas serão perdidas";

    public bool HasNote => Candidate.IsLocked || Candidate.HasActiveAgent;

    public string? Note =>
        Candidate.HasActiveAgent ? "Há um agente de IA aberto neste worktree. Encerre-o para poder limpar."
        : Candidate.IsLocked ? "Travado com git worktree lock. Marque se quiser apagar mesmo assim."
        : null;
}
