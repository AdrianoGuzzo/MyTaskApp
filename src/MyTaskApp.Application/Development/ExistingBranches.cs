namespace MyTaskApp.Application.Development;

/// <summary>Onde a branch pedida já existe, se existe.</summary>
public enum ExistingBranchKind
{
    None,

    /// <summary>Local: o worktree faz checkout dela, sem criar outra.</summary>
    Local,

    /// <summary>Só no remoto: o worktree cria a local acompanhando a remota.</summary>
    Remote,
}

/// <summary>
/// A branch pedida já existe? A mesma regra para o pipeline, que reaproveita a
/// existente em vez de criar outra (ADR-027), e para a tela, que avisa antes de
/// o usuário clicar (ADR-045). Função pura, como <see cref="GitBranchName"/>.
/// </summary>
/// <remarks>
/// Sem distinguir maiúsculas: no Windows as refs soltas são arquivos, e
/// <c>Bug/GAECO-1</c> e <c>bug/GAECO-1</c> são a mesma. A existente vale na
/// grafia dela.
/// </remarks>
public static class ExistingBranches
{
    public static GitBranch? Local(IReadOnlyList<GitBranch> branches, string name) =>
        branches.FirstOrDefault(branch =>
            !branch.IsRemote && string.Equals(branch.ShortName, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Com mais de um remoto: o da origem escolhida, depois <c>origin</c>, depois o primeiro.</summary>
    public static GitBranch? Remote(IReadOnlyList<GitBranch> branches, string name, string? preferredRemote) =>
        branches
            .Where(branch => branch.IsRemote
                && string.Equals(branch.ShortName, $"{branch.Remote}/{name.Trim()}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(branch => branch.Remote == preferredRemote ? 0 : branch.Remote == "origin" ? 1 : 2)
            .FirstOrDefault();

    /// <summary>A local primeiro: com ela, a remota não importa.</summary>
    public static (ExistingBranchKind Kind, GitBranch? Branch) Find(
        IReadOnlyList<GitBranch> branches,
        string name,
        string? preferredRemote = null)
    {
        if (Local(branches, name) is { } local)
        {
            return (ExistingBranchKind.Local, local);
        }

        return Remote(branches, name, preferredRemote) is { } remote
            ? (ExistingBranchKind.Remote, remote)
            : (ExistingBranchKind.None, null);
    }
}
