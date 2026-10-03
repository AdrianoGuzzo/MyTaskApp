using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Recuperar o contexto (ADR-045): voltar a uma tarefa depois de outras dez e
/// saber em segundos o que ela é — com ou sem Jira no ar.
/// </summary>
public class TaskIssueViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

    private static readonly ExternalLink Sync = ExternalLink.Create(
        "Jira",
        "GAECO-1234",
        "Corrigir erro de sincronização",
        "https://empresa.atlassian.net/browse/GAECO-1234",
        "Bug",
        "Em andamento",
        Now.AddHours(-3));

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeShellLauncher _shell = new();

    private readonly FakeClipboardWriter _clipboard = new();

    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };

    private readonly FakeTimeProvider _time = new(Now);

    private TaskIssueViewModel ViewModel() =>
        new(_runner, _shell, _clipboard, _confirmation, _time, NullLogger<TaskIssueViewModel>.Instance);

    private TaskIssueViewModel Loaded(ExternalLink? link = null, bool isReadOnly = false)
    {
        var viewModel = ViewModel();
        viewModel.Load(TaskId, link ?? Sync, isReadOnly);
        return viewModel;
    }

    [Fact]
    public void TheSnapshot_DrawsTheCard_WithoutAskingAnything()
    {
        var viewModel = Loaded();

        viewModel.HasLink.Should().BeTrue();
        viewModel.Key.Should().Be("GAECO-1234");
        viewModel.IssueTitle.Should().Be("Corrigir erro de sincronização");
        viewModel.TypeLabel.Should().Be("BUG");
        viewModel.IsBug.Should().BeTrue();
        viewModel.Status.Should().Be("Em andamento");
        viewModel.SyncedLabel.Should().Be("Lido do Jira há 3 h");
        _runner.Invoked.Should().BeEmpty("o cartão desenha do retrato, sem rede nem banco");
    }

    [Fact]
    public async Task Activating_BringsTheConventionBranch_AndTellsTheDevelopmentTab()
    {
        _runner.ResultsByHandler[typeof(GetTaskExternalContextHandler)] = new TaskExternalContext(Sync, "bug/GAECO-1234");
        var viewModel = Loaded();
        string? told = null;
        viewModel.BranchSuggested += branch => told = branch;

        await viewModel.ActivateAsync(TestContext.Current.CancellationToken);

        viewModel.SuggestedBranch.Should().Be("bug/GAECO-1234");
        told.Should().Be("bug/GAECO-1234");
    }

    [Fact]
    public async Task Activating_WhenTheDatabaseFails_KeepsTheSnapshotOnScreen()
    {
        _runner.FailuresByHandler[typeof(GetTaskExternalContextHandler)] = new IOException("banco travado");
        var viewModel = Loaded();

        await viewModel.ActivateAsync(TestContext.Current.CancellationToken);

        viewModel.Key.Should().Be("GAECO-1234");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task OpenInJira_OpensTheIssueLink()
    {
        await Loaded().OpenInJiraAsync();

        _shell.OpenedUris.Should().Equal(new Uri("https://empresa.atlassian.net/browse/GAECO-1234"));
    }

    [Fact]
    public async Task CopyKeyUrlAndBranch_GoToTheClipboard()
    {
        _runner.ResultsByHandler[typeof(GetTaskExternalContextHandler)] = new TaskExternalContext(Sync, "bug/GAECO-1234");
        var viewModel = Loaded();
        await viewModel.ActivateAsync(TestContext.Current.CancellationToken);

        await viewModel.CopyKeyAsync();
        await viewModel.CopyUrlAsync();
        await viewModel.CopyBranchAsync();

        _clipboard.Written.Should().Equal("GAECO-1234", "https://empresa.atlassian.net/browse/GAECO-1234", "bug/GAECO-1234");
    }

    [Fact]
    public async Task Refresh_ShowsTheNewStatus()
    {
        var fresh = ExternalLink.Create("Jira", "GAECO-1234", "Corrigir erro de sincronização", Sync.Url, "Bug", "Em revisão", Now);
        _runner.ResultsByHandler[typeof(RefreshExternalTaskHandler)] = fresh;
        _runner.ResultsByHandler[typeof(GetTaskExternalContextHandler)] = new TaskExternalContext(fresh, "bug/GAECO-1234");
        var viewModel = Loaded();
        var changed = 0;
        viewModel.Changed += () => changed++;

        await viewModel.RefreshAsync();

        viewModel.Status.Should().Be("Em revisão");
        viewModel.SyncedLabel.Should().Be("Lido do Jira agora há pouco");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_Offline_KeepsTheCard_AndSaysWhy()
    {
        _runner.FailuresByHandler[typeof(RefreshExternalTaskHandler)] =
            new DomainException("O Jira não respondeu agora. A tarefa continua com o que foi lido antes.");
        var viewModel = Loaded();

        await viewModel.RefreshAsync();

        viewModel.Status.Should().Be("Em andamento");
        viewModel.ErrorMessage.Should().Contain("não respondeu");
    }

    [Fact]
    public async Task Refresh_ATechnicalFailure_NeverReachesTheScreen()
    {
        _runner.FailuresByHandler[typeof(RefreshExternalTaskHandler)] =
            new HttpRequestException("Connection refused (api.atlassian.com:443)");
        var viewModel = Loaded();

        await viewModel.RefreshAsync();

        viewModel.ErrorMessage.Should().NotContain("api.atlassian.com");
    }

    [Fact]
    public async Task Unlink_AsksFirst_AndGivesTheBranchBack()
    {
        var viewModel = Loaded();
        string? told = "x";
        viewModel.BranchSuggested += branch => told = branch;

        await viewModel.UnlinkAsync();

        _confirmation.LastAsked!.Message.Should().Contain("continuam");
        _runner.Invoked.Should().Contain(typeof(UnlinkTaskFromExternalHandler));
        viewModel.HasLink.Should().BeFalse();
        told.Should().BeNull();
    }

    [Fact]
    public async Task Unlink_Refused_KeepsTheLink()
    {
        _confirmation.Answer = false;
        var viewModel = Loaded();

        await viewModel.UnlinkAsync();

        viewModel.HasLink.Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(UnlinkTaskFromExternalHandler));
    }

    [Fact]
    public async Task ALocalTask_CanBeLinkedLater_FromTheCard()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = new JiraConnection(JiraConnectionState.Connected);
        _runner.ResultsByHandler[typeof(LinkTaskToExternalHandler)] = Sync;
        _runner.ResultsByHandler[typeof(GetTaskExternalContextHandler)] = new TaskExternalContext(Sync, "bug/GAECO-1234");
        var viewModel = ViewModel();
        viewModel.Load(TaskId, null, isReadOnly: false);

        viewModel.CanLink.Should().BeTrue();
        await viewModel.StartLinkingAsync();
        viewModel.IsLinking.Should().BeTrue();

        await viewModel.LinkAsync(ExternalTask.From(Sync));

        viewModel.HasLink.Should().BeTrue();
        viewModel.IsLinking.Should().BeFalse();
        viewModel.SuggestedBranch.Should().Be("bug/GAECO-1234");
    }

    [Fact]
    public async Task Linking_WithoutJira_SaysWhereToConnect()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = JiraConnection.Disconnected(isOAuthAvailable: true);
        var viewModel = ViewModel();
        viewModel.Load(TaskId, null, isReadOnly: false);

        await viewModel.StartLinkingAsync();

        viewModel.IsLinking.Should().BeFalse();
        viewModel.ErrorMessage.Should().Contain("Integrações");
    }

    [Fact]
    public void ACompletedTask_KeepsOpenAndCopy_ButNotTheChanges()
    {
        var viewModel = Loaded(isReadOnly: true);

        viewModel.CanEdit.Should().BeFalse();
        viewModel.HasLink.Should().BeTrue();

        var local = ViewModel();
        local.Load(TaskId, null, isReadOnly: true);
        local.CanLink.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // A janela e a linha
    // ------------------------------------------------------------------

    private static TodayTask LinkedTask() =>
        new(Guid.CreateVersion7(), TaskId, "Corrigir erro de sincronização", TaskPriority.Normal, null, null, false)
        {
            External = Sync,
        };

    [Fact]
    public void TheRow_CarriesTheKeyAndTheType()
    {
        var row = new TaskRowViewModel(LinkedTask(), isCompleted: false);

        row.HasIssue.Should().BeTrue();
        row.IssueKey.Should().Be("GAECO-1234");
        row.IssueTypeLabel.Should().Be("BUG");
        row.IsBugIssue.Should().BeTrue();
        row.IssueTip.Should().Contain("Em andamento").And.Contain("Corrigir erro de sincronização");
        row.HasBranchName.Should().BeTrue();
    }

    [AvaloniaFact]
    public void TheTaskWindow_ShowsTheIssueCard_FromTheSnapshot()
    {
        var runner = new FakeUseCaseRunner();
        runner.FailuresByHandler[typeof(GetTaskExternalContextHandler)] = new IOException("sem banco");
        var issue = new TaskIssueViewModel(
            runner, new FakeShellLauncher(), new FakeClipboardWriter(), new FakeConfirmationDialog(),
            new FakeTimeProvider(Now), NullLogger<TaskIssueViewModel>.Instance);
        var viewModel = new TaskNotesViewModel(
            runner, new FakeDirectoryProbe(), TestDevelopment.For(runner), NullLogger<TaskNotesViewModel>.Instance, issue);

        viewModel.Load(new TaskRowViewModel(LinkedTask(), isCompleted: false));
        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var key = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "IssueKeyLink");
        key.Content.Should().Be("GAECO-1234");
        key.IsEffectivelyVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LinkIssueButton")
            .IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void TheTodayList_ShowsTheKeyAboveTheTitle()
    {
        var board = new TodayBoard(new DateOnly(2026, 10, 3), [], [], [], [LinkedTask()], []);
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        viewModel.LoadCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var key = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "IssueKeyText");

        key.Text.Should().Be("GAECO-1234");
        key.IsEffectivelyVisible.Should().BeTrue();
    }
}
