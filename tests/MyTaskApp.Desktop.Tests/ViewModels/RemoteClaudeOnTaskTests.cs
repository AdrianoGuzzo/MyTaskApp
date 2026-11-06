using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Remote;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O Claude de outra máquina na linha da tarefa e no aviso do canto (ADR-103):
/// o selo diz onde roda e o que faz, a moldura pulsa a cada pedido novo e o
/// "Seguir" só aparece quando a máquina aceita o terminal remoto.
/// </summary>
public class RemoteClaudeOnTaskTests
{
    private static readonly DateOnly Date = new(2026, 11, 5);

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUseCaseRunner _runner = new();

    private readonly Guid _taskId = Guid.CreateVersion7();

    private readonly Guid _sessionId = Guid.CreateVersion7();

    private RemoteClaudeState State(
        RemoteClaudeAttention attention,
        DateTimeOffset? since = null,
        bool canFollow = true,
        RemoteClaudeKind kind = RemoteClaudeKind.Terminal) => new(
        Guid.CreateVersion7(),
        "Notebook",
        _sessionId,
        _taskId,
        Guid.CreateVersion7(),
        kind,
        attention switch
        {
            RemoteClaudeAttention.Waiting => RemoteAgentActivity.WaitingForUser,
            RemoteClaudeAttention.Review => RemoteAgentActivity.WaitingReview,
            RemoteClaudeAttention.Failed => RemoteAgentActivity.Failed,
            _ => RemoteAgentActivity.Working,
        },
        HasDialog: false,
        "Posso rodar os testes?",
        canFollow,
        attention,
        attention == RemoteClaudeAttention.None ? null : since ?? Now);

    private TodayTask Listed(params ActiveAgent[] agents) =>
        new(Guid.CreateVersion7(), _taskId, "Corrigir o login", TaskPriority.Normal, Date, null, false, ActiveAgents: agents);

    private async Task<TodayViewModel> Board(TodayViewModel? viewModel = null, TodayTask? task = null)
    {
        _runner.Result = new TodayBoard(Date, [], [], [task ?? Listed()], [], []);
        viewModel ??= new TodayViewModel(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(),
            TimeProvider.System, NullLogger<TodayViewModel>.Instance);
        await viewModel.LoadAsync(CancellationToken.None);
        return viewModel;
    }

    private static TaskRowViewModel OnlyRow(TodayViewModel viewModel) => viewModel.Sections.Single().Items.Single();

    [Theory]
    [InlineData(RemoteClaudeAttention.None, "● Claude em Notebook · trabalhando", false)]
    [InlineData(RemoteClaudeAttention.Waiting, "⚠ Claude em Notebook · aguardando você", true)]
    [InlineData(RemoteClaudeAttention.Review, "✓ Claude em Notebook · revisar", true)]
    [InlineData(RemoteClaudeAttention.Failed, "⚠ Claude em Notebook · com erro", true)]
    public async Task TheBadge_SaysWhereAndWhat(RemoteClaudeAttention attention, string label, bool pulses)
    {
        var viewModel = await Board();

        viewModel.ApplyRemoteClaude([State(attention)]);

        var row = OnlyRow(viewModel);
        row.HasRemoteAgent.Should().BeTrue();
        row.RemoteAgentLabel.Should().Be(label);
        row.RemoteAgentNeedsAttention.Should().Be(pulses);
        row.IsAgentAlerting.Should().Be(pulses);
    }

    [Fact]
    public async Task TheRefresh_KeepsTheBadge_AndWhatWasSeen()
    {
        var viewModel = await Board();
        viewModel.ApplyRemoteClaude([State(RemoteClaudeAttention.Waiting)]);
        viewModel.SeeAgentAlerts(OnlyRow(viewModel));

        await Board(viewModel);

        OnlyRow(viewModel).HasRemoteAgent.Should().BeTrue("a linha recriada já nasce com o selo de longe");
        OnlyRow(viewModel).IsAgentAlerting.Should().BeFalse("o refresh de 60 s não pode reacender o que já foi visto");
        OnlyRow(viewModel).IsAgentAlertSeen.Should().BeTrue();
    }

    [Fact]
    public async Task ANewRequest_PulsesAgain()
    {
        var viewModel = await Board();
        viewModel.ApplyRemoteClaude([State(RemoteClaudeAttention.Waiting)]);
        viewModel.SeeAgentAlerts(OnlyRow(viewModel));

        viewModel.ApplyRemoteClaude([State(RemoteClaudeAttention.Waiting, Now.AddMinutes(3))]);

        OnlyRow(viewModel).IsAgentAlerting.Should().BeTrue();
    }

    [Fact]
    public async Task LocalAndRemote_ShowSideBySide()
    {
        var local = new ActiveAgent(Guid.CreateVersion7(), "Claude Code", "eco-core", "feature/x", AgentActivity.Working, Now);
        var viewModel = await Board(task: Listed(local));

        viewModel.ApplyRemoteClaude([State(RemoteClaudeAttention.Review)]);

        var row = OnlyRow(viewModel);
        row.AgentLabel.Should().Be("● Claude Code");
        row.RemoteAgentLabel.Should().Be("✓ Claude em Notebook · revisar");
        row.IsAgentAlerting.Should().BeTrue("o de longe pede alguém, mesmo com o daqui trabalhando");
    }

    [Fact]
    public async Task WhenItEnds_TheBadgeLeaves()
    {
        var viewModel = await Board();
        viewModel.ApplyRemoteClaude([State(RemoteClaudeAttention.Waiting)]);

        viewModel.ApplyRemoteClaude([]);

        OnlyRow(viewModel).HasRemoteAgent.Should().BeFalse();
        OnlyRow(viewModel).IsAgentAlerting.Should().BeFalse();
    }

    [Fact]
    public async Task FollowingAMachineWithoutTheRemoteTerminal_SaysWhy()
    {
        var viewModel = await Board();

        await viewModel.FollowRemoteAsync(State(RemoteClaudeAttention.Waiting, canFollow: false));

        viewModel.StatusMessage.Should().Be("Notebook mostra o estado, mas não aceita o terminal remoto.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [AvaloniaFact]
    public void TheRow_DrawsTheRemoteBadge()
    {
        var row = new TaskRowViewModel(Listed(), false);
        row.ApplyRemoteAgents([State(RemoteClaudeAttention.Waiting)]);
        var template = (Avalonia.Controls.Templates.IDataTemplate)new TodayView().Resources["TaskRow"]!;
        var window = new Window { Content = new ContentControl { Content = row, ContentTemplate = template } };
        window.Show();
        window.UpdateLayout();

        var badge = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "RemoteAgentBadge");

        badge.IsVisible.Should().BeTrue();
        badge.Text.Should().Be("⚠ Claude em Notebook · aguardando você");
        badge.Classes.Should().Contain("attention");
    }

    // --- O aviso do canto ------------------------------------------------------

    private static AgentAttention Attention(RemoteAgentOrigin remote, string? message = "Posso rodar os testes?") =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Corrigir o login", "Claude Code",
            "eco-core", "feature/x", AgentActivity.WaitingForUser, message, Now, Remote: remote);

    private AgentAlertViewModel Alert(AgentAttention attention)
    {
        var viewModel = new AgentAlertViewModel(_runner, NullLogger<AgentAlertViewModel>.Instance);
        viewModel.Show(attention);
        return viewModel;
    }

    [Fact]
    public void TheAlert_NamesTheMachine_AndOffersToFollow()
    {
        var alert = Alert(Attention(new RemoteAgentOrigin(Guid.CreateVersion7(), "Notebook", CanFollow: true, IsConversation: false)));

        alert.Heading.Should().Be("Claude Code em Notebook · aguardando você");
        alert.OpenLabel.Should().Be("Seguir");
        alert.CanOpen.Should().BeTrue();
    }

    [Fact]
    public void WithoutTheRemoteTerminal_TheAlertHasNoButton_AndSaysWhereToAnswer()
    {
        var alert = Alert(Attention(new RemoteAgentOrigin(Guid.CreateVersion7(), "Notebook", CanFollow: false, IsConversation: false), message: null));

        alert.CanOpen.Should().BeFalse();
        alert.MessageText.Should().Be("Na máquina Notebook, que não aceita o terminal remoto: responda por lá.");
    }

    [Fact]
    public void AConversation_OpensTheConversation()
    {
        var alert = Alert(Attention(new RemoteAgentOrigin(Guid.CreateVersion7(), "Notebook", CanFollow: false, IsConversation: true)));

        alert.OpenLabel.Should().Be("Abrir conversa");
        alert.CanOpen.Should().BeTrue();
    }
}
