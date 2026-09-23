using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Development;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Como uma etapa aparece na lista de progresso.</summary>
public enum StepVisualState
{
    Pending,
    Running,
    Done,
    Warning,
    Skipped,
    Failed,
}

/// <summary>Uma linha da lista "Preparando ambiente…" (ADR-027).</summary>
public sealed partial class DevelopmentStepViewModel(DevelopmentStep step) : ObservableObject
{
    public DevelopmentStep Step { get; } = step;

    public string Title { get; } = TitleOf(step);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(IsRunning), nameof(IsDone), nameof(IsWarning), nameof(IsFailed), nameof(IsPending))]
    private StepVisualState _state = StepVisualState.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool IsPending => State is StepVisualState.Pending or StepVisualState.Skipped;

    public bool IsRunning => State is StepVisualState.Running;

    public bool IsDone => State is StepVisualState.Done;

    public bool IsWarning => State is StepVisualState.Warning;

    public bool IsFailed => State is StepVisualState.Failed;

    public string Glyph => State switch
    {
        StepVisualState.Running => "→",
        StepVisualState.Done => "✓",
        StepVisualState.Warning => "⚠",
        StepVisualState.Skipped => "–",
        StepVisualState.Failed => "✗",
        _ => "·",
    };

    public static string TitleOf(DevelopmentStep step) => step switch
    {
        DevelopmentStep.CheckGit => "Verificando o Git",
        DevelopmentStep.ValidateDirectory => "Validando o diretório",
        DevelopmentStep.ValidateRepository => "Validando o repositório",
        DevelopmentStep.Fetch => "Atualizando referências remotas",
        DevelopmentStep.ValidateSource => "Validando a branch de origem",
        DevelopmentStep.CheckChanges => "Verificando alterações locais",
        DevelopmentStep.UpdateSource => "Atualizando a branch de origem",
        DevelopmentStep.ValidateBranchName => "Validando a nova branch",
        DevelopmentStep.PlanWorktreePath => "Calculando o caminho do worktree",
        DevelopmentStep.CreateWorktree => "Criando a branch e o worktree",
        DevelopmentStep.ValidateWorktree => "Validando o worktree",
        DevelopmentStep.SaveTask => "Salvando na tarefa",
        DevelopmentStep.RemoveWorktree => "Removendo o worktree",
        _ => step.ToString(),
    };
}

/// <summary>
/// Um item da lista de branches de origem: uma branch, ou o cabeçalho de um
/// grupo ("Branches locais", "origin"), que não pode ser escolhido.
/// </summary>
public sealed class BranchOptionViewModel
{
    private BranchOptionViewModel(GitBranch? branch, string label)
    {
        Branch = branch;
        Label = label;
    }

    public GitBranch? Branch { get; }

    public string Label { get; }

    public bool IsHeader => Branch is null;

    public bool IsSelectable => Branch is not null;

    public static BranchOptionViewModel Header(string label) => new(null, label);

    public static BranchOptionViewModel For(GitBranch branch) => new(branch, branch.ShortName);

    /// <summary>
    /// Locais primeiro, depois um grupo por remoto. A ordem de cada grupo é a do
    /// Git (alfabética), que é a que o usuário conhece do terminal.
    /// </summary>
    public static IReadOnlyList<BranchOptionViewModel> Group(IReadOnlyList<GitBranch> branches)
    {
        var options = new List<BranchOptionViewModel>();
        var local = branches.Where(branch => !branch.IsRemote).ToList();

        if (local.Count > 0)
        {
            options.Add(Header("Branches locais"));
            options.AddRange(local.Select(For));
        }

        foreach (var remote in branches.Where(branch => branch.IsRemote).GroupBy(branch => branch.Remote))
        {
            options.Add(Header($"Branches remotas — {remote.Key}"));
            options.AddRange(remote.Select(For));
        }

        return options;
    }

    public override string ToString() => Label;
}

/// <summary>
/// O que deu errado, para a tela: a mensagem amigável na frente, e o que o Git
/// disse de verdade em "Ver detalhes".
/// </summary>
public sealed class DevelopmentFailureViewModel(
    string stepTitle,
    string reason,
    GitCommandResult? command,
    IReadOnlyList<string> changes)
{
    public string StepTitle { get; } = stepTitle;

    public string Reason { get; } = reason;

    public GitCommandResult? Command { get; } = command;

    public IReadOnlyList<string> Changes { get; } = changes;

    public bool HasCommand => Command is not null;

    public bool HasChanges => Changes.Count > 0;

    public string CommandLine => Command?.Command ?? string.Empty;

    public string ExitCode => Command is null ? string.Empty
        : Command.TimedOut ? $"{Command.ExitCode} (tempo esgotado)"
        : Command.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string StandardOutput => Blank(Command?.StandardOutput);

    public string StandardError => Blank(Command?.StandardError);

    public string ChangesText => string.Join(Environment.NewLine, Changes);

    /// <summary>Tudo junto, para colar num chamado ou numa conversa.</summary>
    public string DetailsText =>
        $"Etapa: {StepTitle}{Environment.NewLine}"
        + $"Motivo: {Reason}{Environment.NewLine}"
        + (Command is null
            ? string.Empty
            : $"Comando: {CommandLine}{Environment.NewLine}"
              + $"Exit code: {ExitCode}{Environment.NewLine}"
              + $"Standard output:{Environment.NewLine}{StandardOutput}{Environment.NewLine}"
              + $"Standard error:{Environment.NewLine}{StandardError}{Environment.NewLine}")
        + (HasChanges ? $"Alterações:{Environment.NewLine}{ChangesText}" : string.Empty);

    private static string Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(vazio)" : text.TrimEnd();
}
