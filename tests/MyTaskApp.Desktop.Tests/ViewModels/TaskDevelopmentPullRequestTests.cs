using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A PR aberta da branch na aba Desenvolvimento (ADR-047): o link no
/// formulário, o balão da aba do repositório e o tutorial do <c>gh</c>.
/// </summary>
/// <remarks>
/// O <see cref="FindPullRequestHandler"/> é o de verdade, com o Git e o GitHub
/// dublados: é o que deixa conferir <i>qual</i> branch a tela perguntou.
/// </remarks>
public class TaskDevelopmentPullRequestTests
{
    private const string Repository = @"C:\Projects\eco-core";

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly IReadOnlyList<GitBranch> Branches =
    [
        GitBranch.Local("develop", "refs/remotes/origin/develop", isHead: true),
        GitBranch.Local("bug/GAECO-1234"),
        GitBranch.RemoteTracking("origin", "develop"),
        GitBranch.RemoteTracking("origin", "feature/GAECO-1300"),
    ];

    private static readonly PullRequestInfo Open =
        new(12, "Corrige a sincronização", new Uri("https://github.com/acme/eco-core/pull/12"), IsDraft: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeTimeProvider _time = new();

    private readonly FakeShellLauncher _shell = new();

    private readonly IGitClient _git = Substitute.For<IGitClient>();

    private readonly IPullRequestClient _github = Substitute.For<IPullRequestClient>();

    public TaskDevelopmentPullRequestTests()
    {
        _git.GetRemoteUrlAsync(Arg.Any<string>(), "origin", Arg.Any<CancellationToken>())
            .Returns("git@github.com:acme/eco-core.git");

        _github.CheckAsync(Arg.Any<GitHubRepository>(), Arg.Any<CancellationToken>())
            .Returns(PullRequestSupport.Ready);

        _github.FindOpenAsync(Arg.Any<GitHubRepository>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestLookup(PullRequestSupport.Ready));

        _runner.Handlers[typeof(FindPullRequestHandler)] =
            new FindPullRequestHandler(_git, _github, NullLogger<FindPullRequestHandler>.Instance);
    }

    private void HasOpenPullRequest(string branch) =>
        _github.FindOpenAsync(Arg.Any<GitHubRepository>(), branch, Arg.Any<CancellationToken>())
            .Returns(new PullRequestLookup(PullRequestSupport.Ready, Open));

    private async Task<TaskDevelopmentViewModel> WithRepositoryAsync()
    {
        _runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        _runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(Branches, Branches[0]);

        var viewModel = TestDevelopment.Environment(_runner, shell: _shell, timeProvider: _time);
        viewModel.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);
        await viewModel.ActivateAsync(null, Ct);

        viewModel.DirectoryText = Repository;
        await viewModel.InspectDirectoryAsync(Ct);

        return viewModel;
    }

    private async Task SettleAsync(TaskDevelopmentViewModel viewModel)
    {
        _time.Advance(TaskDevelopmentViewModel.InspectionDelay);
        await viewModel.PendingPullRequest;
    }

    [Fact]
    public async Task AnExistingBranch_ShowsItsOpenPullRequest()
    {
        HasOpenPullRequest("bug/GAECO-1234");
        var viewModel = await WithRepositoryAsync();

        viewModel.NewBranchName = "bug/GAECO-1234";
        await SettleAsync(viewModel);

        viewModel.HasPullRequest.Should().BeTrue();
        viewModel.PullRequest.Should().Be(Open);
        viewModel.PullRequestTip.Should().Contain("Corrige a sincronização").And.Contain("/pull/12");
        viewModel.ChipTip.Should().Contain(Repository).And.Contain("PR #12 aberta · Corrige a sincronização");
    }

    /// <summary>Só no remoto, a branch vale sem o "origin/" — é o nome que o GitHub conhece.</summary>
    [Fact]
    public async Task ARemoteOnlyBranch_IsAskedByItsOwnName()
    {
        var viewModel = await WithRepositoryAsync();

        viewModel.NewBranchName = "feature/GAECO-1300";
        await SettleAsync(viewModel);

        await _github.Received(1).FindOpenAsync(
            new GitHubRepository("acme", "eco-core"), "feature/GAECO-1300", Arg.Any<CancellationToken>());
    }

    /// <summary>Branch nova não tem PR: nem se pergunta. Só se confere o <c>gh</c>, para o tutorial.</summary>
    [Fact]
    public async Task ANewBranch_NeverAsksForAPullRequest()
    {
        var viewModel = await WithRepositoryAsync();

        viewModel.NewBranchName = "task/GAECO-1400";
        await SettleAsync(viewModel);

        await _github.DidNotReceiveWithAnyArgs().FindOpenAsync(default!, default!, Ct);
        await _github.Received().CheckAsync(Arg.Any<GitHubRepository>(), Arg.Any<CancellationToken>());
        viewModel.HasPullRequest.Should().BeFalse();
    }

    [Fact]
    public async Task TypingFast_AsksOnce_AfterThePause()
    {
        var viewModel = await WithRepositoryAsync();

        viewModel.NewBranchName = "bug/GAECO-1234";
        viewModel.NewBranchName = "feature/GAECO-1300";
        viewModel.NewBranchName = "bug/GAECO-1234";
        await SettleAsync(viewModel);

        await _github.ReceivedWithAnyArgs(1).FindOpenAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task TheLink_OpensThePullRequestInTheBrowser()
    {
        HasOpenPullRequest("bug/GAECO-1234");
        var viewModel = await WithRepositoryAsync();
        viewModel.NewBranchName = "bug/GAECO-1234";
        await SettleAsync(viewModel);

        await viewModel.OpenPullRequestCommand.ExecuteAsync(null);

        _shell.OpenedUris.Should().Equal(Open.Url);
    }

    [Fact]
    public async Task WithoutABrowser_TheAddressIsShown()
    {
        HasOpenPullRequest("bug/GAECO-1234");
        var viewModel = await WithRepositoryAsync();
        viewModel.NewBranchName = "bug/GAECO-1234";
        await SettleAsync(viewModel);
        _shell.Succeeds = false;

        await viewModel.OpenPullRequestCommand.ExecuteAsync(null);

        viewModel.Message.Should().Contain(Open.Url.ToString());
    }

    [Fact]
    public async Task ChangingTheBranch_ClearsTheOldPullRequest()
    {
        HasOpenPullRequest("bug/GAECO-1234");
        var viewModel = await WithRepositoryAsync();
        viewModel.NewBranchName = "bug/GAECO-1234";
        await SettleAsync(viewModel);

        viewModel.NewBranchName = "task/GAECO-1400";

        viewModel.HasPullRequest.Should().BeFalse();
        viewModel.ChipTip.Should().NotContain("PR #12");
    }

    [Fact]
    public async Task WithoutGh_TheTutorialIsOffered()
    {
        _github.CheckAsync(Arg.Any<GitHubRepository>(), Arg.Any<CancellationToken>())
            .Returns(PullRequestSupport.CliMissing);

        var viewModel = await WithRepositoryAsync();
        await SettleAsync(viewModel);

        viewModel.ShowGhGuide.Should().BeTrue();
        viewModel.IsGhMissing.Should().BeTrue();
        viewModel.GhGuideHint.Should().Contain("Instale o GitHub CLI");
        viewModel.GhInstructions.Commands.Should().NotBeEmpty();
        viewModel.GhInstructions.Login.Command.Should().Be("gh auth login");

        viewModel.IsGhGuideOpen.Should().BeFalse();
        viewModel.ToggleGhGuideCommand.Execute(null);
        viewModel.IsGhGuideOpen.Should().BeTrue();
    }

    /// <summary>Instalado mas sem login: o tutorial pula a instalação.</summary>
    [Fact]
    public async Task NotLoggedIn_OnlyTheLoginIsAsked()
    {
        _github.CheckAsync(Arg.Any<GitHubRepository>(), Arg.Any<CancellationToken>())
            .Returns(PullRequestSupport.NotAuthenticated);

        var viewModel = await WithRepositoryAsync();
        await SettleAsync(viewModel);

        viewModel.ShowGhGuide.Should().BeTrue();
        viewModel.IsGhMissing.Should().BeFalse();
        viewModel.GhGuideHint.Should().Contain("gh auth login");
    }

    [Fact]
    public async Task RecheckingAfterInstalling_HidesTheTutorial()
    {
        _github.CheckAsync(Arg.Any<GitHubRepository>(), Arg.Any<CancellationToken>())
            .Returns(PullRequestSupport.CliMissing, PullRequestSupport.Ready);

        var viewModel = await WithRepositoryAsync();
        await SettleAsync(viewModel);
        viewModel.ShowGhGuide.Should().BeTrue();

        await viewModel.RecheckGhCommand.ExecuteAsync(null);

        viewModel.ShowGhGuide.Should().BeFalse();
        _github.Received(1).Reset();
    }

    [Fact]
    public async Task ARepositoryOutsideGitHub_HasNoTutorial()
    {
        _git.GetRemoteUrlAsync(Arg.Any<string>(), "origin", Arg.Any<CancellationToken>())
            .Returns("https://gitlab.com/acme/eco-core.git");

        var viewModel = await WithRepositoryAsync();
        await SettleAsync(viewModel);

        viewModel.ShowGhGuide.Should().BeFalse();
        await _github.DidNotReceiveWithAnyArgs().CheckAsync(default!, Ct);
    }

    /// <summary>Pronto, o balão da aba do repositório conta a PR da branch do ambiente.</summary>
    [Fact]
    public async Task AReadyEnvironment_TellsItsPullRequestInTheTab()
    {
        HasOpenPullRequest("feature/x");

        var viewModel = TestDevelopment.Environment(_runner, shell: _shell, timeProvider: _time);
        viewModel.Load(TaskId, "x", isReadOnly: false);
        await viewModel.ActivateAsync(TestDevelopment.View(TaskId, TaskDevelopmentStatus.Ready, Repository, "feature/x"), Ct);
        await SettleAsync(viewModel);

        viewModel.ChipTip.Should().Be($"{Repository}{Environment.NewLine}PR #12 aberta · Corrige a sincronização");
    }
}
