using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Planning;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A bolinha de worktree de uma linha (ADR-034). O quadro diz quais worktrees
/// existem; a cor chega depois, quando o Git responde — até lá a bolinha é só
/// um anel, "existe, ainda não sei como está".
/// </summary>
/// <remarks>
/// A cor nunca é o único sinal: o balão do título e o rótulo da concluída dizem
/// o mesmo em texto. As classes (<c>dirty</c>, <c>unpushed</c>…) ficam na view,
/// que pinta com os tokens do tema.
/// </remarks>
public sealed partial class TaskWorktreeViewModel : ObservableObject
{
    private readonly bool _isCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsDirty),
        nameof(IsUnpushed),
        nameof(IsPushed),
        nameof(IsClean),
        nameof(IsUnknown),
        nameof(DotTip))]
    private WorktreeSyncState _state;

    [ObservableProperty]
    private IReadOnlyList<WorktreeLineViewModel> _lines;

    public TaskWorktreeViewModel(IReadOnlyList<TaskWorktree>? worktrees, bool isCompleted)
    {
        Worktrees = worktrees ?? [];
        _isCompleted = isCompleted;
        _lines = Worktrees.Select(worktree => new WorktreeLineViewModel(worktree, null)).ToList();
    }

    public IReadOnlyList<TaskWorktree> Worktrees { get; }

    public bool HasWorktree => Worktrees.Count > 0;

    /// <summary>
    /// Concluída com o worktree ainda no disco: quem concluiu respondeu "manter".
    /// A linha apaga, a bolinha não — é justamente o que ainda pede atenção.
    /// </summary>
    public bool IsRetained => _isCompleted && HasWorktree;

    public bool IsDirty => State == WorktreeSyncState.Dirty;

    public bool IsUnpushed => State == WorktreeSyncState.Unpushed;

    public bool IsPushed => State == WorktreeSyncState.Pushed;

    public bool IsClean => State == WorktreeSyncState.Clean;

    public bool IsUnknown => State == WorktreeSyncState.Unknown;

    /// <summary>O balão da própria bolinha: o pior estado, em uma linha.</summary>
    public string DotTip => (Worktrees.Count > 1 ? $"{Worktrees.Count} worktrees" : "Worktree") + State switch
    {
        WorktreeSyncState.Dirty => " com alterações não commitadas",
        WorktreeSyncState.Unpushed => " com commits sem push",
        WorktreeSyncState.Pushed => " com commits, tudo enviado",
        WorktreeSyncState.Clean => " criado, sem commits ainda",
        _ => " criado",
    };

    /// <summary>"● Worktree criado · eco-core · feature/x" — a linha embaixo do título da concluída.</summary>
    public string RetainedLabel => Worktrees.Count switch
    {
        0 => string.Empty,
        1 => $"● Worktree criado · {Worktrees[0].RepositoryName} · {Worktrees[0].Branch}",
        var count => $"● {count} worktrees criados",
    };

    public string RetainedTip =>
        "A tarefa foi concluída e o worktree continua no disco. Clique para abrir a tarefa e removê-lo na aba Desenvolvimento.";

    /// <summary>
    /// O que o Git respondeu. Um worktree fora do dicionário continua como
    /// estava — a resposta de uma rodada não apaga o que se sabia.
    /// </summary>
    public void Apply(IReadOnlyDictionary<Guid, WorktreeSync> syncs)
    {
        if (!HasWorktree)
        {
            return;
        }

        var lines = Worktrees
            .Select(worktree => new WorktreeLineViewModel(worktree, syncs.GetValueOrDefault(worktree.DevelopmentId)))
            .ToList();

        Lines = lines;
        State = lines.Max(line => line.State);
    }
}

/// <summary>Um worktree no balão do título: onde está e como está.</summary>
public sealed class WorktreeLineViewModel(TaskWorktree worktree, WorktreeSync? sync)
{
    public TaskWorktree Worktree { get; } = worktree;

    public WorktreeSyncState State { get; } = sync?.State ?? WorktreeSyncState.Unknown;

    public bool IsDirty => State == WorktreeSyncState.Dirty;

    public bool IsUnpushed => State == WorktreeSyncState.Unpushed;

    public bool IsPushed => State == WorktreeSyncState.Pushed;

    public bool IsClean => State == WorktreeSyncState.Clean;

    public bool IsUnknown => State == WorktreeSyncState.Unknown;

    /// <summary>"eco-core · feature/x".</summary>
    public string Label { get; } = $"{worktree.RepositoryName} · {worktree.Branch}";

    /// <summary>"3 alterações não commitadas · 2 commits sem push".</summary>
    public string Detail { get; } = Describe(sync);

    public static string Describe(WorktreeSync? sync)
    {
        if (sync is null)
        {
            return "verificando…";
        }

        if (sync.State == WorktreeSyncState.Unknown)
        {
            return "não foi possível verificar";
        }

        var parts = new List<string>();

        if (sync.Changes > 0)
        {
            parts.Add(Count(sync.Changes, "alteração não commitada", "alterações não commitadas"));
        }

        if (sync.Unpushed > 0)
        {
            parts.Add(Count(sync.Unpushed, "commit sem push", "commits sem push"));
        }
        else if (sync.Commits > 0)
        {
            parts.Add(Count(sync.Commits, "commit, já enviado", "commits, todos enviados"));
        }

        if (parts.Count == 0)
        {
            parts.Add("sem commits ainda");
        }

        return string.Join(" · ", parts);
    }

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
