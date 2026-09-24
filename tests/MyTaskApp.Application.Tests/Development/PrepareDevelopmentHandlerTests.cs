using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// A preparação de "Iniciar implementação" (ADR-027): cada etapa, o que ela
/// recusa, e o que ela nunca faz — gravar no banco ou mexer em alterações locais.
/// </summary>
public class PrepareDevelopmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Main = "refs/heads/main";
    private const string Develop = "refs/heads/develop";
    private const string OriginDevelop = "refs/remotes/origin/develop";
    private const string Expected = @"C:\Projects\ecossistema-core-feature-123-corrigir-animais";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly RecordingProgress _progress = new();
    private readonly TaskItem _task;

    public PrepareDevelopmentHandlerTests()
    {
        _task = TaskItem.Create("Corrigir cálculo de animais", Now);
        _tasks.Seed(_task);
    }

    private PrepareDevelopmentHandler Handler() =>
        new(_tasks, _git, _disk, NullLogger<PrepareDevelopmentHandler>.Instance);

    private Task<DevelopmentPlan> PrepareAsync(
        string source = OriginDevelop,
        string branch = "feature/123-corrigir-animais",
        string directory = FakeGitClient.Repository) =>
        Handler().HandleAsync(new PrepareDevelopment(_task.Id, directory, source, branch), _progress, Ct);

    private async Task<DevelopmentStepException> FailsAt(DevelopmentStep step, Func<Task> act)
    {
        var failure = (await FluentActions.Awaiting(act).Should().ThrowAsync<DevelopmentStepException>()).Which;
        failure.Step.Should().Be(step);
        return failure;
    }

    [Fact]
    public async Task AValidRepository_ProducesThePlan_WithoutSavingAnything()
    {
        var plan = await PrepareAsync();

        plan.RepositoryPath.Should().Be(FakeGitClient.Repository);
        plan.Source.FullRef.Should().Be(OriginDevelop);
        plan.NewBranch.Should().Be("feature/123-corrigir-animais");
        plan.WorktreePath.Should().Be(Expected);
        plan.Conflict.Should().BeNull();
        _tasks.SaveCount.Should().Be(0);
        _git.Calls.Should().Contain("fetch");
    }

    [Fact]
    public async Task Progress_FollowsThePipelineOrder()
    {
        await PrepareAsync();

        _progress.Reports.Select(report => report.Step).Distinct().Should().Equal(
            DevelopmentStep.CheckGit,
            DevelopmentStep.ValidateDirectory,
            DevelopmentStep.ValidateRepository,
            DevelopmentStep.Fetch,
            DevelopmentStep.ValidateSource,
            DevelopmentStep.CheckChanges,
            DevelopmentStep.UpdateSource,
            DevelopmentStep.ValidateBranchName,
            DevelopmentStep.PlanWorktreePath);
        _progress.Last(DevelopmentStep.CheckGit)!.Note.Should().Be("2.51.0");
    }

    [Fact]
    public async Task GitNotInstalled_StopsAtTheFirstStep()
    {
        _git.Installation = GitInstallation.Missing;

        var failure = await FailsAt(DevelopmentStep.CheckGit, () => PrepareAsync());

        failure.Message.Should().Contain("Git CLI não foi encontrado");
        _git.Calls.Should().NotContain("fetch");
    }

    [Fact]
    public async Task AnOldGit_IsRefused()
    {
        _git.Installation = new GitInstallation(true, "2.9.1", "git");

        await FailsAt(DevelopmentStep.CheckGit, () => PrepareAsync());
    }

    [Fact]
    public async Task AMissingDirectory_StopsBeforeGit()
    {
        var failure = await FailsAt(DevelopmentStep.ValidateDirectory, () => PrepareAsync(directory: @"C:\Nao\Existe"));

        failure.Message.Should().Contain(@"C:\Nao\Existe");
        _git.Calls.Should().NotContain("fetch");
    }

    [Fact]
    public async Task ARelativeDirectory_IsRefused() =>
        await FailsAt(DevelopmentStep.ValidateDirectory, () => PrepareAsync(directory: "ecossistema-core"));

    [Fact]
    public async Task AFolderThatIsNotARepository_IsRefused_WithGitsAnswer()
    {
        _disk.Existing.Add(@"C:\Projects\docs");

        var failure = await FailsAt(DevelopmentStep.ValidateRepository, () => PrepareAsync(directory: @"C:\Projects\docs"));

        failure.Message.Should().Contain("não é um repositório Git");
        failure.Command!.StandardError.Should().Contain("not a git repository");
    }

    [Fact]
    public async Task ASubfolder_UsesTheMainWorktreeForTheSiblingPath()
    {
        _disk.Existing.Add(@"C:\Projects\ecossistema-core\src");
        _git.Repositories[@"C:\Projects\ecossistema-core\src"] = "C:/Projects/ecossistema-core";

        var plan = await PrepareAsync(directory: @"C:\Projects\ecossistema-core\src");

        plan.RepositoryPath.Should().Be(FakeGitClient.Repository);
        plan.WorktreePath.Should().Be(Expected);
    }

    [Fact]
    public async Task AFailedFetch_StopsWithTheGitError()
    {
        _git.FetchResult = FakeGitClient.Failed("git fetch --all --prune", "fatal: unable to access 'https://…': Could not resolve host");

        var failure = await FailsAt(DevelopmentStep.Fetch, () => PrepareAsync());

        failure.Command!.StandardError.Should().Contain("Could not resolve host");
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFetchTimeout_SaysSo()
    {
        _git.FetchResult = new GitCommandResult("git fetch --all --prune", -1, string.Empty, string.Empty, TimedOut: true);

        var failure = await FailsAt(DevelopmentStep.Fetch, () => PrepareAsync());

        failure.Message.Should().Contain("demorou demais");
    }

    [Fact]
    public async Task AMissingSourceBranch_IsRefused()
    {
        var failure = await FailsAt(DevelopmentStep.ValidateSource, () => PrepareAsync(source: "refs/remotes/origin/release/9.9"));

        failure.Message.Should().Contain("origin/release/9.9");
    }

    [Fact]
    public async Task ARemoteSource_IsNotUpdated_TheFetchAlreadyDidIt()
    {
        await PrepareAsync(source: OriginDevelop);

        _progress.Last(DevelopmentStep.UpdateSource)!.State.Should().Be(DevelopmentStepState.Skipped);
        _git.Calls.Should().NotContain(call => call.StartsWith("merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARemoteSource_WithLocalChanges_OnlyWarns()
    {
        _git.Statuses[FakeGitClient.Repository] = new GitStatus([" M src/App.cs", "?? novo.txt"]);

        var plan = await PrepareAsync(source: OriginDevelop);

        plan.RepositoryChanges.Should().Equal(" M src/App.cs", "?? novo.txt");
        _progress.Last(DevelopmentStep.CheckChanges)!.State.Should().Be(DevelopmentStepState.Warning);
    }

    [Fact]
    public async Task ALocalSourceUpToDate_IsLeftAlone()
    {
        await PrepareAsync(source: Develop);

        _progress.Last(DevelopmentStep.UpdateSource)!.State.Should().Be(DevelopmentStepState.Done);
        _git.Calls.Should().NotContain(call => call.Contains("ff-only", StringComparison.Ordinal) || call.StartsWith("fetch .", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalSourceBehind_NotCheckedOut_IsFastForwardedByFetchDot()
    {
        _git.Divergence = new GitDivergence(0, 3);

        await PrepareAsync(source: Develop);

        _git.Calls.Should().Contain("fetch . refs/remotes/origin/develop:refs/heads/develop");
        _progress.Last(DevelopmentStep.UpdateSource)!.Note.Should().Contain("3 commits");
    }

    [Fact]
    public async Task ALocalSourceBehind_CheckedOutAndClean_IsFastForwardedInPlace()
    {
        _git.Divergence = new GitDivergence(0, 2);

        await PrepareAsync(source: Main);

        _git.Calls.Should().Contain(call => call.StartsWith("merge --ff-only refs/remotes/origin/main", StringComparison.Ordinal));
    }

    /// <summary>O caso crítico: atualizar mexeria nos arquivos do usuário.</summary>
    [Fact]
    public async Task ALocalSourceBehind_CheckedOutWithChanges_IsBlocked_AndNothingIsTouched()
    {
        _git.Divergence = new GitDivergence(0, 2);
        _git.Statuses[FakeGitClient.Repository] = new GitStatus([" M src/App.cs"]);

        var failure = await FailsAt(DevelopmentStep.UpdateSource, () => PrepareAsync(source: Main));

        failure.Changes.Should().Equal(" M src/App.cs");
        failure.Message.Should().Contain("alteração não commitada");
        _git.Calls.Should().NotContain(call => call.StartsWith("merge", StringComparison.Ordinal));
        _git.Calls.Should().NotContain(call => call.StartsWith("fetch .", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalSourceThatDiverged_IsRefused_WithoutTouchingIt()
    {
        _git.Divergence = new GitDivergence(1, 4);

        var failure = await FailsAt(DevelopmentStep.UpdateSource, () => PrepareAsync(source: Develop));

        failure.Message.Should().Contain("divergiram");
        _git.Calls.Should().NotContain(call => call.StartsWith("fetch .", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedFastForward_ReportsTheGitError()
    {
        _git.Divergence = new GitDivergence(0, 1);
        _git.FastForwardResult = FakeGitClient.Failed("git fetch .", "! [rejected] (non-fast-forward)", 1);

        var failure = await FailsAt(DevelopmentStep.UpdateSource, () => PrepareAsync(source: Develop));

        failure.Command!.StandardError.Should().Contain("rejected");
    }

    [Theory]
    [InlineData("feature com espaço")]
    [InlineData("feature..x")]
    [InlineData("-x")]
    public async Task AnInvalidNewBranch_IsRefused(string name) =>
        await FailsAt(DevelopmentStep.ValidateBranchName, () => PrepareAsync(branch: name));

    [Fact]
    public async Task ANameGitRejects_IsRefused()
    {
        _git.AcceptsBranchName = _ => false;

        await FailsAt(DevelopmentStep.ValidateBranchName, () => PrepareAsync());
    }

    [Fact]
    public async Task AnExistingLocalBranch_IsReused_InItsOwnSpelling()
    {
        var existing = GitBranch.Local("Feature/123-Corrigir-Animais");
        _git.Branches.Add(existing);

        var plan = await PrepareAsync();

        plan.ExistingBranch.Should().Be(existing);
        plan.NewBranch.Should().Be("Feature/123-Corrigir-Animais");
        var report = _progress.Last(DevelopmentStep.ValidateBranchName)!;
        report.State.Should().Be(DevelopmentStepState.Warning);
        report.Note.Should().Contain("já existe");
    }

    [Fact]
    public async Task AnExistingBranch_OpenInAnotherWorktree_IsRefused()
    {
        _git.Branches.Add(GitBranch.Local("feature/123-corrigir-animais"));
        _git.Worktrees.Add(new GitWorktree("C:/Outro/lugar", "refs/heads/feature/123-corrigir-animais"));

        var failure = await FailsAt(DevelopmentStep.ValidateBranchName, () => PrepareAsync());

        failure.Message.Should().Contain("aberta em").And.Contain(@"C:\Outro\lugar");
    }

    [Fact]
    public async Task AnExistingBranch_OpenAtThePlannedPath_BecomesAnAdoptableConflict()
    {
        _git.Branches.Add(GitBranch.Local("feature/123-corrigir-animais"));
        _git.Worktrees.Add(new GitWorktree(Expected.Replace('\\', '/'), "refs/heads/feature/123-corrigir-animais"));
        _disk.Existing.Add(Expected);

        var plan = await PrepareAsync();

        plan.Conflict!.CanAdopt.Should().BeTrue();
    }

    [Fact]
    public async Task ABranchOnlyOnTheRemote_IsFollowed_PreferringTheSourceRemote()
    {
        _git.Branches.Add(GitBranch.RemoteTracking("fork", "feature/123-corrigir-animais"));
        _git.Branches.Add(GitBranch.RemoteTracking("origin", "Feature/123-Corrigir-Animais"));

        var plan = await PrepareAsync();

        plan.ExistingBranch!.FullRef.Should().Be("refs/remotes/origin/Feature/123-Corrigir-Animais");
        plan.NewBranch.Should().Be("Feature/123-Corrigir-Animais");
        _progress.Last(DevelopmentStep.ValidateBranchName)!.Note.Should().Contain("origin/Feature/123-Corrigir-Animais");
    }

    [Fact]
    public async Task ANewBranch_HasNoExistingBranch()
    {
        var plan = await PrepareAsync();

        plan.ExistingBranch.Should().BeNull();
        _progress.Last(DevelopmentStep.ValidateBranchName)!.State.Should().Be(DevelopmentStepState.Done);
    }

    [Fact]
    public async Task ABranchThatWouldClashAsFolder_IsRefused()
    {
        _git.Branches.Add(GitBranch.Local("feature"));

        await FailsAt(DevelopmentStep.ValidateBranchName, () => PrepareAsync(branch: "feature/x"));
    }

    [Fact]
    public async Task AnOccupiedPath_ComesBackAsAConflict_WithAFreeSuggestion()
    {
        _disk.Existing.Add(Expected);

        var plan = await PrepareAsync();

        plan.Conflict.Should().NotBeNull();
        plan.Conflict!.Path.Should().Be(Expected);
        plan.Conflict.CanAdopt.Should().BeFalse();
        plan.Conflict.SuggestedPath.Should().Be(Expected + "-2");
        _progress.Last(DevelopmentStep.PlanWorktreePath)!.State.Should().Be(DevelopmentStepState.Warning);
    }

    [Fact]
    public async Task AnOccupiedPath_ThatIsAWorktreeOfTheRepository_CanBeAdopted()
    {
        _disk.Existing.Add(Expected);
        _git.Worktrees.Add(new GitWorktree(Expected.Replace('\\', '/'), "refs/heads/feature/antiga"));

        var plan = await PrepareAsync();

        plan.Conflict!.CanAdopt.Should().BeTrue();
        plan.Conflict.Registered!.BranchName.Should().Be("feature/antiga");
    }

    [Fact]
    public async Task AnArchivedTask_IsRefusedBeforeAnyGitCall()
    {
        _task.Archive(Now);

        await FluentActions.Awaiting(() => PrepareAsync()).Should().ThrowAsync<DomainException>();
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryingAReadyEnvironment_IsRefused_BeforeAnyGit()
    {
        var ready = _task.BeginDevelopment(
            null, FakeGitClient.Repository, "main", "feature/x", @"C:\Projects\ecossistema-core-feature-x", Now);
        _task.MarkDevelopmentReady(ready.Id, Now);

        var failure = (await FluentActions.Awaiting(() => Handler().HandleAsync(
                new PrepareDevelopment(_task.Id, FakeGitClient.Repository, OriginDevelop, "feature/y", ready.Id),
                _progress,
                Ct))
            .Should().ThrowAsync<DomainException>()).Which;

        failure.Message.Should().Contain("já tem um worktree pronto");
        _git.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Um ambiente por repositório (ADR-031). A recusa sai na etapa do
    /// repositório — é ali que se sabe qual é — e antes do fetch, que demora.
    /// </summary>
    [Fact]
    public async Task ARepositoryWithAReadyEnvironment_IsRefusedAtTheRepositoryStep()
    {
        var ready = _task.BeginDevelopment(
            null, FakeGitClient.Repository, "main", "feature/x", @"C:\Projects\ecossistema-core-feature-x", Now);
        _task.MarkDevelopmentReady(ready.Id, Now);

        var failure = await FailsAt(DevelopmentStep.ValidateRepository, () => PrepareAsync());

        failure.Message.Should().Contain("já tem um ambiente em");
        _git.Calls.Should().NotContain("fetch");
    }

    [Fact]
    public async Task AnotherRepository_IsPrepared_WhileTheFirstStaysReady()
    {
        var other = _task.BeginDevelopment(
            null, @"C:\Projects\ecossistema-api", "main", "feature/x", @"C:\Projects\ecossistema-api-feature-x", Now);
        _task.MarkDevelopmentReady(other.Id, Now);

        var plan = await PrepareAsync();

        plan.RepositoryPath.Should().Be(FakeGitClient.Repository);
        plan.DevelopmentId.Should().BeNull();
    }

    [Fact]
    public async Task ARetry_CarriesTheEnvironmentIntoThePlan()
    {
        var failed = _task.BeginDevelopment(
            null, FakeGitClient.Repository, "main", "feature/x", @"C:\Projects\ecossistema-core-feature-x", Now);
        _task.MarkDevelopmentFailed(failed.Id, "falhou", Now);

        var plan = await Handler().HandleAsync(
            new PrepareDevelopment(_task.Id, FakeGitClient.Repository, OriginDevelop, "feature/123-corrigir-animais", failed.Id),
            _progress,
            Ct);

        plan.DevelopmentId.Should().Be(failed.Id);
    }

    [Fact]
    public async Task Cancelling_StopsThePreparation_WithoutCreatingAnything()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions.Awaiting(() => Handler().HandleAsync(
                new PrepareDevelopment(_task.Id, FakeGitClient.Repository, OriginDevelop, "feature/x"),
                _progress,
                cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        _tasks.SaveCount.Should().Be(0);
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGitErrorInsideAStep_IsReportedAsThatStep()
    {
        _git.Failures[nameof(IGitClient.ListBranchesAsync)] = FakeGitClient.Failed("git for-each-ref", "fatal: bad object");

        var failure = await FailsAt(DevelopmentStep.ValidateSource, () => PrepareAsync());

        failure.Command!.Command.Should().Be("git for-each-ref");
    }

    [Fact]
    public void GitVersions_AreParsedForTheMinimum()
    {
        PrepareDevelopmentHandler.ParseVersion("2.40.1.windows.1").Should().Be(new Version(2, 40));
        PrepareDevelopmentHandler.ParseVersion("2.17.0").Should().Be(new Version(2, 17));
        PrepareDevelopmentHandler.ParseVersion("x").Should().BeNull();
    }
}
