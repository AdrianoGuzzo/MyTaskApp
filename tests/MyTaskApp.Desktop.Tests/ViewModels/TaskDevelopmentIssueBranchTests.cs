using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A branch da tarefa vinculada (ADR-045): a convenção sugere
/// <c>bug/GAECO-1234</c>, o nome escrito pelo usuário vale mais, e a tela
/// avisa antes do clique quando a branch já existe — local ou no remoto.
/// </summary>
public class TaskDevelopmentIssueBranchTests
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private async Task<TaskDevelopmentViewModel> WithRepositoryAsync()
    {
        _runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        _runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(Branches, Branches[0]);

        var viewModel = TestDevelopment.Environment(_runner, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);
        await viewModel.ActivateAsync(null, Ct);

        viewModel.DirectoryText = Repository;
        await viewModel.InspectDirectoryAsync(Ct);

        return viewModel;
    }

    [Fact]
    public void WithoutAnIssue_TheSuggestionIsTheTitleSlug()
    {
        var viewModel = TestDevelopment.Environment(_runner);

        viewModel.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);

        viewModel.NewBranchName.Should().Be("feature/corrigir-erro-de-sincronizacao");
    }

    [Fact]
    public void TheIssueConvention_ReplacesTheSlug()
    {
        var viewModel = TestDevelopment.Environment(_runner);
        viewModel.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);

        viewModel.SuggestBranch("bug/GAECO-1234");

        viewModel.NewBranchName.Should().Be("bug/GAECO-1234");
    }

    [Fact]
    public void ANameTheUserTyped_IsNotReplaced()
    {
        var viewModel = TestDevelopment.Environment(_runner);
        viewModel.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);
        viewModel.NewBranchName = "hotfix/sync-urgente";

        viewModel.SuggestBranch("bug/GAECO-1234");

        viewModel.NewBranchName.Should().Be("hotfix/sync-urgente");
    }

    [Fact]
    public void Unlinking_GoesBackToTheSlug_WhenTheNameWasStillTheSuggestion()
    {
        var viewModel = TestDevelopment.Environment(_runner);
        viewModel.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);
        viewModel.SuggestBranch("bug/GAECO-1234");

        viewModel.SuggestBranch(null);

        viewModel.NewBranchName.Should().Be("feature/corrigir-erro-de-sincronizacao");
    }

    [Fact]
    public async Task AnExistingLocalBranch_IsAnnounced_AndWillBeUsed()
    {
        var viewModel = await WithRepositoryAsync();

        viewModel.SuggestBranch("bug/GAECO-1234");

        viewModel.ExistingBranchNotice.Should().Contain("já existe").And.Contain("usá-la");
    }

    [Fact]
    public async Task ABranchOnlyOnTheRemote_IsOfferedForCheckout()
    {
        var viewModel = await WithRepositoryAsync();

        viewModel.NewBranchName = "feature/GAECO-1300";

        viewModel.ExistingBranchNotice.Should().Contain("origin").And.Contain("checkout");
    }

    [Fact]
    public async Task ANewBranch_HasNoNotice()
    {
        var viewModel = await WithRepositoryAsync();

        viewModel.NewBranchName = "task/GAECO-1400";

        viewModel.ExistingBranchNotice.Should().BeNull();
    }

    [Fact]
    public void BeforeTheRepositoryIsKnown_NothingIsClaimed()
    {
        var viewModel = TestDevelopment.Environment(_runner);
        viewModel.Load(TaskId, "x", isReadOnly: false);

        viewModel.NewBranchName = "bug/GAECO-1234";

        viewModel.ExistingBranchNotice.Should().BeNull();
    }

    [Fact]
    public void TheTabs_PassTheSuggestionToEveryEnvironment_AndToNewOnes()
    {
        var tabs = TestDevelopment.For(_runner);
        tabs.Load(TaskId, "Corrigir erro de sincronização", isReadOnly: false);

        tabs.SuggestBranch("bug/GAECO-1234");
        tabs.AddRepository();

        tabs.Items.Should().OnlyContain(item => item.NewBranchName == "bug/GAECO-1234");
    }
}
