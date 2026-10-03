using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// As ações rápidas do menu da linha (ADR-045): o Jira e o ambiente a um
/// clique, sem abrir a tarefa.
/// </summary>
public class TodayQuickActionsTests
{
    private static readonly ExternalLink Sync = ExternalLink.Create(
        "Jira",
        "GAECO-1234",
        "Corrigir erro de sincronização",
        "https://empresa.atlassian.net/browse/GAECO-1234",
        "Bug",
        null,
        DateTimeOffset.UnixEpoch);

    private static readonly TaskWorktree Worktree =
        new(Guid.CreateVersion7(), "eco-core", "bug/GAECO-1234", "develop", @"C:\Projects\eco-core-bug-GAECO-1234");

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeClipboardWriter _clipboard = new();

    private readonly FakeShellLauncher _shell = new();

    private TodayViewModel ViewModel() =>
        new(_runner, new FakeConfirmationDialog(), _clipboard, TimeProvider.System, NullLogger<TodayViewModel>.Instance, _shell);

    private static TaskRowViewModel Row(ExternalLink? link = null, TaskWorktree? worktree = null) =>
        new(
            new TodayTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "Corrigir", TaskPriority.Normal, null, null, false)
            {
                External = link,
                Worktrees = worktree is null ? null : [worktree],
            },
            isCompleted: false);

    [Fact]
    public async Task OpenIssue_OpensTheLinkFromTheSnapshot()
    {
        await ViewModel().OpenIssueAsync(Row(Sync));

        _shell.OpenedUris.Should().Equal(new Uri(Sync.Url));
        _runner.Invoked.Should().BeEmpty("abrir não consulta nada");
    }

    [Fact]
    public async Task CopyKeyAndLink()
    {
        var viewModel = ViewModel();

        await viewModel.CopyIssueKeyAsync(Row(Sync));
        await viewModel.CopyIssueUrlAsync(Row(Sync));

        _clipboard.Written.Should().Equal("GAECO-1234", Sync.Url);
        viewModel.StatusMessage.Should().Be("Link do Jira copiado.");
    }

    [Fact]
    public async Task CopyBranch_WithAWorktree_IsItsBranch()
    {
        await ViewModel().CopyBranchNameAsync(Row(Sync, Worktree));

        _clipboard.LastWritten.Should().Be("bug/GAECO-1234");
        _runner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task CopyBranch_WithoutAWorktree_AsksTheConvention()
    {
        _runner.ResultsByHandler[typeof(GetTaskExternalContextHandler)] = new TaskExternalContext(Sync, "fix/GAECO-1234");

        await ViewModel().CopyBranchNameAsync(Row(Sync));

        _clipboard.LastWritten.Should().Be("fix/GAECO-1234");
    }

    [Fact]
    public void Terminal_And_Folder_OpenTheWorktree()
    {
        var viewModel = ViewModel();
        var row = Row(Sync, Worktree);

        viewModel.OpenWorktreeTerminal(row.WorktreeChoices[0]);

        _shell.OpenedTerminals.Should().Equal(Worktree.WorktreePath);
    }

    [Fact]
    public async Task AFolderThatIsGone_SaysSo()
    {
        _shell.Succeeds = false;
        var viewModel = ViewModel();

        await viewModel.OpenWorktreeFolderAsync(Row(Sync, Worktree).WorktreeChoices[0]);

        viewModel.ErrorMessage.Should().Contain(Worktree.WorktreePath);
    }
}
