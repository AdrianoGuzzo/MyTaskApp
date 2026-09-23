using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Development;

/// <summary>
/// A primeira metade de "Iniciar implementação": valida tudo, busca o remoto,
/// atualiza a origem e calcula a pasta do worktree (ADR-027).
/// </summary>
/// <param name="SourceRef">A ref completa da origem: <c>refs/heads/develop</c> ou <c>refs/remotes/origin/develop</c>.</param>
public sealed record PrepareDevelopment(
    Guid TaskId,
    string DirectoryPath,
    string SourceRef,
    string NewBranch);

/// <summary>
/// Não grava nada no banco e pode ser cancelado a qualquer momento: a única
/// mudança que faz no disco é o fast-forward da branch de origem, que é seguro.
/// </summary>
/// <remarks>
/// <para>
/// Separado de <see cref="StartDevelopmentHandler"/> porque entre os dois pode
/// haver uma pergunta ao usuário (a pasta do worktree já existe). Um caso de uso
/// só teria de segurar escopo e DbContext abertos esperando um clique.
/// </para>
/// <para>
/// Alterações locais só <b>bloqueiam</b> quando atualizar a origem mexeria
/// nelas — a origem é uma branch local em checkout, atrás do upstream. Fora
/// disso, o <c>git worktree add</c> não toca no working tree do repositório, e
/// elas viram só um aviso.
/// </para>
/// </remarks>
public sealed class PrepareDevelopmentHandler(
    ITaskItemRepository tasks,
    IGitClient git,
    IDirectoryProbe directories,
    ILogger<PrepareDevelopmentHandler> logger)
{
    /// <summary>A versão que trouxe <c>git worktree remove</c>.</summary>
    public static readonly Version MinimumGitVersion = new(2, 17);

    public async Task<DevelopmentPlan> HandleAsync(
        PrepareDevelopment command,
        IProgress<DevelopmentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        task.EnsureDevelopmentCanBegin();

        var step = DevelopmentStep.CheckGit;

        try
        {
            progress.Report(step, DevelopmentStepState.Running);
            await CheckGitAsync(progress, cancellationToken);

            step = DevelopmentStep.ValidateDirectory;
            progress.Report(step, DevelopmentStepState.Running);
            var directory = await ValidateDirectoryAsync(command.DirectoryPath, cancellationToken);
            progress.Report(step, DevelopmentStepState.Done, directory);

            step = DevelopmentStep.ValidateRepository;
            progress.Report(step, DevelopmentStepState.Running);
            var repository = await ValidateRepositoryAsync(directory, cancellationToken);
            progress.Report(step, DevelopmentStepState.Done, repository);

            step = DevelopmentStep.Fetch;
            progress.Report(step, DevelopmentStepState.Running);
            await FetchAsync(repository, cancellationToken);
            progress.Report(step, DevelopmentStepState.Done);

            step = DevelopmentStep.ValidateSource;
            progress.Report(step, DevelopmentStepState.Running);
            var branches = await git.ListBranchesAsync(repository, cancellationToken);
            var source = branches.FirstOrDefault(branch => branch.FullRef == command.SourceRef)
                ?? throw new DevelopmentStepException(
                    step,
                    $"A branch de origem {ShortName(command.SourceRef)} não existe no repositório.");
            progress.Report(step, DevelopmentStepState.Done, source.ShortName);

            step = DevelopmentStep.CheckChanges;
            progress.Report(step, DevelopmentStepState.Running);
            var worktrees = await git.ListWorktreesAsync(repository, cancellationToken);
            var changes = await git.GetStatusAsync(repository, cancellationToken);
            progress.Report(
                step,
                changes.IsClean ? DevelopmentStepState.Done : DevelopmentStepState.Warning,
                changes.IsClean
                    ? null
                    : $"{Count(changes.Entries.Count)} no repositório principal. Elas não vão para o novo worktree.");

            step = DevelopmentStep.UpdateSource;
            progress.Report(step, DevelopmentStepState.Running);
            await UpdateSourceAsync(repository, source, branches, worktrees, changes, progress, cancellationToken);

            step = DevelopmentStep.ValidateBranchName;
            progress.Report(step, DevelopmentStepState.Running);
            var newBranch = await ValidateNewBranchAsync(command.NewBranch, branches, cancellationToken);
            progress.Report(step, DevelopmentStepState.Done, newBranch);

            step = DevelopmentStep.PlanWorktreePath;
            progress.Report(step, DevelopmentStepState.Running);
            var path = WorktreePathPlanner.Plan(repository, newBranch);
            var conflict = await FindConflictAsync(path, worktrees, cancellationToken);
            progress.Report(
                step,
                conflict is null ? DevelopmentStepState.Done : DevelopmentStepState.Warning,
                conflict is null ? path : $"Já existe algo em {path}.");

            logger.LogInformation(
                "DevelopmentPrepared {TaskId} {Source} {Branch} {HasConflict}",
                task.Id,
                source.FullRef,
                newBranch,
                conflict is not null);

            return new DevelopmentPlan(task.Id, repository, source, newBranch, path, changes.Entries, conflict);
        }
        catch (GitCommandFailedException exception)
        {
            throw new DevelopmentStepException(step, FailureMessage(step), exception.Result);
        }
    }

    private async Task CheckGitAsync(IProgress<DevelopmentProgress>? progress, CancellationToken cancellationToken)
    {
        var installation = await git.DetectAsync(cancellationToken);

        if (!installation.IsInstalled)
        {
            throw new DevelopmentStepException(
                DevelopmentStep.CheckGit,
                "O Git CLI não foi encontrado. Instale o Git e clique em \"Verificar novamente\".");
        }

        if (ParseVersion(installation.Version) is { } version && version < MinimumGitVersion)
        {
            throw new DevelopmentStepException(
                DevelopmentStep.CheckGit,
                $"O Git {installation.Version} é antigo demais. Atualize para a versão {MinimumGitVersion} ou mais nova.");
        }

        progress.Report(DevelopmentStep.CheckGit, DevelopmentStepState.Done, installation.Version);
    }

    private async Task<string> ValidateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var path = directoryPath.Trim().Trim('"').Trim();

        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            throw new DevelopmentStepException(
                DevelopmentStep.ValidateDirectory,
                "Informe o caminho completo do repositório (ex.: C:\\Projetos\\meu-projeto).");
        }

        if (!await directories.ExistsAsync(path, cancellationToken))
        {
            throw new DevelopmentStepException(
                DevelopmentStep.ValidateDirectory,
                $"A pasta {path} não existe.");
        }

        return WorktreePathPlanner.Canonical(path);
    }

    private async Task<string> ValidateRepositoryAsync(string directory, CancellationToken cancellationToken)
    {
        var info = await git.InspectAsync(directory, cancellationToken);

        if (!info.IsRepository)
        {
            throw new DevelopmentStepException(
                DevelopmentStep.ValidateRepository,
                $"A pasta {directory} não é um repositório Git.",
                info.Result);
        }

        return await RepositoryPaths.MainWorktreeAsync(git, info, cancellationToken);
    }

    private async Task FetchAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await git.FetchAsync(repository, cancellationToken);

        if (!result.Succeeded)
        {
            throw new DevelopmentStepException(
                DevelopmentStep.Fetch,
                result.TimedOut
                    ? "O servidor Git demorou demais para responder. Confira a conexão e tente de novo."
                    : "Não foi possível atualizar as referências remotas. Confira a conexão e o acesso ao repositório.",
                result);
        }
    }

    /// <summary>
    /// Deixa a origem em dia, só por fast-forward. Origem remota o fetch já
    /// atualizou; branch local sem upstream não tem de onde vir nada.
    /// </summary>
    private async Task UpdateSourceAsync(
        string repository,
        GitBranch source,
        IReadOnlyList<GitBranch> branches,
        IReadOnlyList<GitWorktree> worktrees,
        GitStatus repositoryChanges,
        IProgress<DevelopmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        const DevelopmentStep step = DevelopmentStep.UpdateSource;

        if (source.IsRemote)
        {
            progress.Report(step, DevelopmentStepState.Skipped, "O fetch já trouxe a versão mais nova.");
            return;
        }

        if (source.UpstreamRef is null)
        {
            progress.Report(step, DevelopmentStepState.Skipped, $"{source.ShortName} não acompanha nenhuma branch remota.");
            return;
        }

        if (!branches.Any(branch => branch.FullRef == source.UpstreamRef))
        {
            progress.Report(
                step,
                DevelopmentStepState.Warning,
                $"A branch remota de {source.ShortName} não existe mais. Usando a local como está.");
            return;
        }

        var divergence = await git.CompareAsync(repository, source.FullRef, source.UpstreamRef, cancellationToken);
        var upstream = ShortName(source.UpstreamRef);

        if (divergence.Behind == 0)
        {
            progress.Report(
                step,
                divergence.Ahead == 0 ? DevelopmentStepState.Done : DevelopmentStepState.Warning,
                divergence.Ahead == 0
                    ? "Já estava atualizada."
                    : $"{source.ShortName} tem {Commits(divergence.Ahead)} que ainda não estão em {upstream}.");
            return;
        }

        if (divergence.Ahead > 0)
        {
            throw new DevelopmentStepException(
                step,
                $"{source.ShortName} e {upstream} divergiram ({Commits(divergence.Ahead)} só na local, "
                + $"{Commits(divergence.Behind)} só na remota). Resolva no repositório (merge ou rebase) "
                + $"ou escolha {upstream} como origem.");
        }

        var checkedOut = worktrees.FirstOrDefault(worktree => worktree.BranchRef == source.FullRef);

        GitCommandResult result;

        if (checkedOut is not null)
        {
            var changes = WorktreePathPlanner.SamePath(checkedOut.Path, repository)
                ? repositoryChanges
                : await git.GetStatusAsync(checkedOut.Path, cancellationToken);

            if (!changes.IsClean)
            {
                throw new DevelopmentStepException(
                    step,
                    $"A branch {source.ShortName} está aberta em {WorktreePathPlanner.Canonical(checkedOut.Path)} "
                    + $"com {Count(changes.Entries.Count)}. Atualizá-la mexeria nesses arquivos. "
                    + $"Faça commit das alterações ou escolha {upstream} como origem.",
                    changes: changes.Entries);
            }

            result = await git.FastForwardCheckedOutAsync(checkedOut.Path, source.UpstreamRef, cancellationToken);
        }
        else
        {
            result = await git.FastForwardBranchAsync(repository, source.ShortName, source.UpstreamRef, cancellationToken);
        }

        if (!result.Succeeded)
        {
            throw new DevelopmentStepException(
                step,
                $"Não foi possível atualizar {source.ShortName} por fast-forward a partir de {upstream}.",
                result);
        }

        progress.Report(step, DevelopmentStepState.Done, $"{Commits(divergence.Behind)} trazidos de {upstream}.");
    }

    private async Task<string> ValidateNewBranchAsync(
        string requested,
        IReadOnlyList<GitBranch> branches,
        CancellationToken cancellationToken)
    {
        const DevelopmentStep step = DevelopmentStep.ValidateBranchName;

        var name = requested.Trim();

        if (GitBranchName.Validate(name) is { } problem)
        {
            throw new DevelopmentStepException(step, problem);
        }

        if (!await git.IsValidBranchNameAsync(name, cancellationToken))
        {
            throw new DevelopmentStepException(step, $"O Git não aceita \"{name}\" como nome de branch.");
        }

        // Sem distinguir maiúsculas: no Windows as refs soltas são arquivos, e
        // "Feature/X" e "feature/x" disputariam o mesmo.
        foreach (var branch in branches.Where(branch => !branch.IsRemote))
        {
            if (string.Equals(branch.ShortName, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new DevelopmentStepException(step, $"A branch {branch.ShortName} já existe.");
            }

            if (branch.ShortName.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(branch.ShortName + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new DevelopmentStepException(
                    step,
                    $"Já existe a branch {branch.ShortName}, e o Git não permite {name} ao lado dela.");
            }
        }

        return name;
    }

    private async Task<WorktreeConflict?> FindConflictAsync(
        string path,
        IReadOnlyList<GitWorktree> worktrees,
        CancellationToken cancellationToken)
    {
        if (!await directories.PathExistsAsync(path, cancellationToken))
        {
            return null;
        }

        var registered = worktrees.FirstOrDefault(worktree => WorktreePathPlanner.SamePath(worktree.Path, path));
        var suggestion = await WorktreePathPlanner.NextFreeAsync(
            path,
            candidate => directories.PathExistsAsync(candidate, cancellationToken));

        return new WorktreeConflict(path, registered, suggestion);
    }

    private static string FailureMessage(DevelopmentStep step) => step switch
    {
        DevelopmentStep.ValidateRepository => "Não foi possível consultar o repositório.",
        DevelopmentStep.ValidateSource => "Não foi possível listar as branches do repositório.",
        DevelopmentStep.CheckChanges => "Não foi possível verificar as alterações locais.",
        DevelopmentStep.UpdateSource => "Não foi possível comparar a origem com a branch remota.",
        _ => "O Git recusou esta etapa.",
    };

    internal static Version? ParseVersion(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var parts = version.Split('.');

        return parts.Length >= 2
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            ? new Version(major, minor)
            : null;
    }

    private static string ShortName(string fullRef) =>
        fullRef.StartsWith(GitBranch.LocalPrefix, StringComparison.Ordinal) ? fullRef[GitBranch.LocalPrefix.Length..]
        : fullRef.StartsWith(GitBranch.RemotePrefix, StringComparison.Ordinal) ? fullRef[GitBranch.RemotePrefix.Length..]
        : fullRef;

    private static string Commits(int count) => count == 1 ? "1 commit" : $"{count} commits";

    private static string Count(int changes) =>
        changes == 1 ? "1 alteração não commitada" : $"{changes} alterações não commitadas";
}
