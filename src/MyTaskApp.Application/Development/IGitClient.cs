namespace MyTaskApp.Application.Development;

/// <summary>
/// Uma execução do Git, como aconteceu: o comando, o código de saída e as duas
/// saídas. É o que a tela mostra em "Ver detalhes" — esconder o erro real do Git
/// deixaria o usuário sem como diagnosticar (ADR-027).
/// </summary>
public sealed record GitCommandResult(
    string Command,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>O Git que o app encontrou, ou por que não encontrou.</summary>
public sealed record GitInstallation(bool IsInstalled, string? Version, string? ExecutablePath)
{
    public static readonly GitInstallation Missing = new(false, null, null);
}

/// <summary>
/// A pasta está dentro de um repositório? <see cref="TopLevel"/> é a raiz do
/// working tree que contém a pasta — que pode ser uma subpasta do que o
/// usuário digitou, ou um worktree ligado.
/// </summary>
public sealed record GitRepositoryInfo(bool IsRepository, string? TopLevel, GitCommandResult Result);

/// <summary>Uma branch local (<c>refs/heads/…</c>) ou remota (<c>refs/remotes/…</c>).</summary>
public sealed record GitBranch(
    string FullRef,
    string ShortName,
    bool IsRemote,
    string? Remote,
    string? UpstreamRef,
    bool IsHead)
{
    public const string LocalPrefix = "refs/heads/";

    public const string RemotePrefix = "refs/remotes/";

    public static GitBranch Local(string name, string? upstreamRef = null, bool isHead = false) =>
        new(LocalPrefix + name, name, false, null, upstreamRef, isHead);

    public static GitBranch RemoteTracking(string remote, string name) =>
        new($"{RemotePrefix}{remote}/{name}", $"{remote}/{name}", true, remote, null, false);
}

/// <summary>Uma entrada de <c>git worktree list --porcelain</c>.</summary>
public sealed record GitWorktree(
    string Path,
    string? BranchRef,
    bool IsDetached = false,
    bool IsBare = false,
    bool IsLocked = false,
    bool IsPrunable = false)
{
    /// <summary>O nome curto da branch em checkout, sem <c>refs/heads/</c>.</summary>
    public string? BranchName =>
        BranchRef is not null && BranchRef.StartsWith(GitBranch.LocalPrefix, StringComparison.Ordinal)
            ? BranchRef[GitBranch.LocalPrefix.Length..]
            : BranchRef;
}

/// <summary>
/// As linhas de <c>git status --porcelain</c>, já sem o código de controle do
/// <c>-z</c>: <c>" M src/App.cs"</c>, <c>"?? novo.txt"</c>.
/// </summary>
public sealed record GitStatus(IReadOnlyList<string> Entries)
{
    public static readonly GitStatus Clean = new([]);

    public bool IsClean => Entries.Count == 0;
}

/// <summary>Quantos commits a branch tem que o upstream não tem, e vice-versa.</summary>
public sealed record GitDivergence(int Ahead, int Behind);

/// <summary>
/// O Git rodou e recusou. Carrega a execução inteira para a área de detalhes;
/// quem chama decide a mensagem, porque só ele sabe em que etapa estava.
/// </summary>
public sealed class GitCommandFailedException(GitCommandResult result)
    : Exception($"git falhou ({result.ExitCode}): {result.Command}")
{
    public GitCommandResult Result { get; } = result;
}

/// <summary>
/// O Git CLI, visto pelos casos de uso (ADR-027). Um método por pergunta ou
/// operação — nunca "rode estes argumentos" —, para que só a Infrastructure
/// saiba montar linha de comando e só ela precise ser revisada quando o assunto
/// é segurança de execução.
/// </summary>
/// <remarks>
/// <para>
/// Nenhuma operação destrutiva existe aqui, e isso é a regra, não um esquecimento:
/// não há reset, clean, stash, checkout forçado nem <c>--force</c> em lugar
/// nenhum. Atualizar a origem é só fast-forward; remover worktree é o
/// <c>git worktree remove</c> sem força, que o próprio Git recusa com alterações.
/// </para>
/// <para>
/// Os métodos que devolvem dado lançam <see cref="GitCommandFailedException"/>
/// quando o Git sai com erro; os que devolvem <see cref="GitCommandResult"/>
/// deixam o caso de uso decidir. Cancelar lança
/// <see cref="OperationCanceledException"/> depois de matar o processo.
/// </para>
/// </remarks>
public interface IGitClient
{
    /// <summary>
    /// <c>git --version</c>. Procura o executável de novo a cada chamada: depois
    /// de instalar o Git, o PATH deste processo continua o antigo.
    /// </summary>
    Task<GitInstallation> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary><c>git -C path rev-parse --is-inside-work-tree --show-toplevel</c>.</summary>
    Task<GitRepositoryInfo> InspectAsync(string path, CancellationToken cancellationToken = default);

    /// <summary><c>git worktree list --porcelain</c>. O primeiro é o worktree principal.</summary>
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repository, CancellationToken cancellationToken = default);

    /// <summary>Branches locais e remotas, sem os <c>origin/HEAD</c> simbólicos.</summary>
    Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// A branch para a qual <c>refs/remotes/{remote}/HEAD</c> aponta, ou <c>null</c>.
    /// É a sugestão de origem: quem clonou quer, quase sempre, partir dela.
    /// </summary>
    Task<string?> GetRemoteDefaultBranchAsync(string repository, string remote, CancellationToken cancellationToken = default);

    /// <summary><c>git fetch --all --prune</c>. Só atualiza referências remotas.</summary>
    Task<GitCommandResult> FetchAsync(string repository, CancellationToken cancellationToken = default);

    /// <summary><c>git rev-parse --verify --quiet {revision}^{commit}</c>.</summary>
    Task<bool> CommitExistsAsync(string repository, string revision, CancellationToken cancellationToken = default);

    /// <summary><c>git rev-list --left-right --count local...upstream</c>.</summary>
    Task<GitDivergence> CompareAsync(
        string repository,
        string localRef,
        string upstreamRef,
        CancellationToken cancellationToken = default);

    /// <summary><c>git status --porcelain</c> no working tree, incluindo arquivos novos.</summary>
    Task<GitStatus> GetStatusAsync(string workingTree, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git merge --ff-only {upstream}</c> dentro do worktree onde a branch
    /// está em checkout. Só quem chamou sabe se o working tree está limpo.
    /// </summary>
    Task<GitCommandResult> FastForwardCheckedOutAsync(
        string workingTree,
        string upstreamRef,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git fetch . {upstream}:refs/heads/{branch}</c> — sem <c>+</c>, então o
    /// Git só aceita se for fast-forward. Para branch fora de checkout.
    /// </summary>
    Task<GitCommandResult> FastForwardBranchAsync(
        string repository,
        string branch,
        string upstreamRef,
        CancellationToken cancellationToken = default);

    /// <summary><c>git check-ref-format --branch {name}</c>: a palavra final é do Git.</summary>
    Task<bool> IsValidBranchNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git -C repo worktree add --no-track -b {newBranch} {path} {startRef}</c>.
    /// </summary>
    Task<GitCommandResult> AddWorktreeAsync(
        string repository,
        string path,
        string newBranch,
        string startRef,
        CancellationToken cancellationToken = default);

    /// <summary><c>git rev-parse --abbrev-ref HEAD</c> no worktree.</summary>
    Task<string?> GetCurrentBranchAsync(string workingTree, CancellationToken cancellationToken = default);

    /// <summary><c>git -C repo worktree remove {path}</c>, sem <c>--force</c>.</summary>
    Task<GitCommandResult> RemoveWorktreeAsync(
        string repository,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary><c>git worktree prune</c>: esquece worktrees cuja pasta já sumiu.</summary>
    Task<GitCommandResult> PruneWorktreesAsync(string repository, CancellationToken cancellationToken = default);
}
