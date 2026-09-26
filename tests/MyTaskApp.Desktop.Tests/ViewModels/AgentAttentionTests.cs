using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O que o usuário vê quando o agente para esperando por ele (ADR-036): o
/// aviso no canto, o selo da linha e o status do card.
/// </summary>
public class AgentAttentionTests
{
    private static readonly DateOnly Date = new(2026, 9, 25);

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 22, 0, 0, TimeSpan.Zero);

    private readonly FakeUseCaseRunner _runner = new();

    private static AgentAttention Attention(AgentActivity activity, string? message = "Redis ou MemoryCache?") =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Implementar cache", "Claude Code",
            "eco-core", "feature/cache", activity, message, Now);

    private AgentAlertViewModel Alert(AgentAttention attention)
    {
        var viewModel = new AgentAlertViewModel(_runner, NullLogger<AgentAlertViewModel>.Instance);
        viewModel.Show(attention);
        return viewModel;
    }

    private static AgentSessionView SessionView(AgentSessionStatus status, bool focused = true) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "claude-code", "Claude Code", @"C:\claude.exe",
            @"C:\wt", 15432, Now, null, status, null);

    // --- O aviso -------------------------------------------------------------

    [Theory]
    [InlineData(AgentActivity.WaitingForUser, "Claude Code · aguardando você")]
    [InlineData(AgentActivity.WaitingReview, "Claude Code · pronto para revisão")]
    [InlineData(AgentActivity.Failed, "Claude Code · a resposta falhou")]
    public void TheHeading_SaysWhatTheAgentIsWaitingFor(AgentActivity activity, string expected)
    {
        Alert(Attention(activity)).Heading.Should().Be(expected);
    }

    [Fact]
    public void TheAlert_ShowsTheTask_TheRepository_AndTheQuestion()
    {
        var alert = Alert(Attention(AgentActivity.WaitingForUser));

        alert.Title.Should().Be("Implementar cache");
        alert.Context.Should().Be("eco-core · feature/cache");
        alert.MessageText.Should().Be("Redis ou MemoryCache?");
        alert.IsQuestion.Should().BeTrue();
        alert.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void ALongAnswer_BecomesAnExcerpt_OnOneLine()
    {
        var excerpt = AgentAlertViewModel.Excerpt("Implementação concluída.\n\n- testes ok\n" + new string('x', 400));

        excerpt.Should().StartWith("Implementação concluída. - testes ok ");
        excerpt.Should().EndWith("…");
        excerpt!.Length.Should().BeLessThanOrEqualTo(221);
        AgentAlertViewModel.Excerpt("  ").Should().BeNull();
    }

    [Fact]
    public async Task OpenTerminal_GoesToThatAgentsTerminal_AndClosesTheAlert()
    {
        var attention = Attention(AgentActivity.WaitingReview);
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] =
            new AgentFocusResult(SessionView(AgentSessionStatus.Running), Focused: true);
        var alert = Alert(attention);
        var closed = false;
        alert.Closed += _ => closed = true;

        await alert.OpenTerminalCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(FocusAgentSessionHandler));
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task OpenTerminal_WithoutAWindowToFind_StaysAndSaysSo()
    {
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] =
            new AgentFocusResult(SessionView(AgentSessionStatus.Running), Focused: false);
        var alert = Alert(Attention(AgentActivity.WaitingReview));
        var closed = false;
        alert.Closed += _ => closed = true;

        await alert.OpenTerminalCommand.ExecuteAsync(null);

        closed.Should().BeFalse();
        alert.ErrorMessage.Should().Contain("barra de tarefas");
    }

    [Fact]
    public void Dismiss_OnlyClosesTheAlert()
    {
        var alert = Alert(Attention(AgentActivity.WaitingReview));
        var closed = false;
        alert.Closed += _ => closed = true;

        alert.DismissCommand.Execute(null);

        closed.Should().BeTrue();
        _runner.Invoked.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void TheAlertWindow_DrawsEverything_WithoutStealingFocus()
    {
        var window = new AgentAlertWindow { DataContext = Alert(Attention(AgentActivity.WaitingForUser)) };
        window.Show();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();

        texts.Should().Contain(["Claude Code · aguardando você", "Implementar cache", "eco-core · feature/cache", "Redis ou MemoryCache?"]);
        window.GetVisualDescendants().OfType<Button>().Should().HaveCount(2)
            .And.AllSatisfy(button => button.Command.Should().NotBeNull());
        window.ShowActivated.Should().BeFalse();
        window.ShowInTaskbar.Should().BeFalse();
        window.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("card"))
            .Classes.Should().Contain("question");
    }

    // --- O selo da linha -----------------------------------------------------

    private static TaskRowViewModel Row(params AgentActivity[] activities) =>
        new(
            new TodayTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "Implementar cache", TaskPriority.Normal, Date, null, false,
                ActiveAgents: [.. activities.Select((activity, index) => new ActiveAgent(
                    Guid.CreateVersion7(), "Claude Code", index == 0 ? "eco-core" : "eco-api", "feature/x", activity))]),
            false);

    [Theory]
    [InlineData(AgentActivity.Unknown, "● Claude Code", false)]
    [InlineData(AgentActivity.Working, "● Claude Code", false)]
    [InlineData(AgentActivity.WaitingForUser, "⚠ Claude Code · aguardando você", true)]
    [InlineData(AgentActivity.WaitingReview, "✓ Claude Code · revisar", true)]
    [InlineData(AgentActivity.Failed, "⚠ Claude Code · erro", true)]
    public void TheBadge_SaysWhatTheAgentIsDoing(AgentActivity activity, string label, bool attention)
    {
        var row = Row(activity);

        row.AgentLabel.Should().Be(label);
        row.AgentNeedsAttention.Should().Be(attention);
    }

    /// <summary>Com vários agentes, o selo mostra o que mais pede o usuário.</summary>
    [Fact]
    public void WithSeveralAgents_TheBadgeShowsTheMostUrgent()
    {
        var row = Row(AgentActivity.Working, AgentActivity.WaitingForUser);

        row.AgentLabel.Should().Be("⚠ Claude Code ×2 · aguardando você");
        row.AgentTip.Should().Contain("eco-core · feature/x: trabalhando").And.Contain("eco-api · feature/x: aguardando você");
    }

    // --- A moldura da linha ---------------------------------------------------

    private static TodayTask Waiting(Guid taskId, Guid developmentId, DateTimeOffset since) =>
        new(Guid.CreateVersion7(), taskId, "Implementar cache", TaskPriority.Normal, Date, null, false,
            ActiveAgents: [new ActiveAgent(developmentId, "Claude Code", "eco-core", "feature/x",
                AgentActivity.WaitingForUser, since)]);

    private async Task<TodayViewModel> BoardWith(TodayViewModel? viewModel, TodayTask task)
    {
        _runner.Result = new TodayBoard(Date, [], [], [task], [], []);
        viewModel ??= new TodayViewModel(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(),
            TimeProvider.System, NullLogger<TodayViewModel>.Instance);
        await viewModel.LoadAsync(CancellationToken.None);
        return viewModel;
    }

    private static TaskRowViewModel OnlyRow(TodayViewModel viewModel) => viewModel.Sections.Single().Items.Single();

    [Theory]
    [InlineData(AgentActivity.Working, false)]
    [InlineData(AgentActivity.WaitingForUser, true)]
    [InlineData(AgentActivity.WaitingReview, true)]
    public void APendingAgent_MakesTheRowPulse(AgentActivity activity, bool alerting)
    {
        var row = Row(activity);

        row.IsAgentAlerting.Should().Be(alerting);
        row.IsAgentAlertSeen.Should().BeFalse();
    }

    [Fact]
    public async Task ClickingTheBadge_StopsThePulse_AndTheRefreshKeepsItStopped()
    {
        var (taskId, developmentId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var viewModel = await BoardWith(null, Waiting(taskId, developmentId, Now));

        viewModel.SeeAgentAlerts(OnlyRow(viewModel));

        OnlyRow(viewModel).IsAgentAlerting.Should().BeFalse();
        OnlyRow(viewModel).IsAgentAlertSeen.Should().BeTrue();

        await BoardWith(viewModel, Waiting(taskId, developmentId, Now));

        OnlyRow(viewModel).IsAgentAlerting.Should().BeFalse("o refresh de 60 s não pode reacender o que já foi visto");
        OnlyRow(viewModel).IsAgentAlertSeen.Should().BeTrue();
    }

    [Fact]
    public async Task ANewPendency_FromTheSameAgent_PulsesAgain()
    {
        var (taskId, developmentId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var viewModel = await BoardWith(null, Waiting(taskId, developmentId, Now));
        viewModel.SeeAgentAlerts(OnlyRow(viewModel));

        await BoardWith(viewModel, Waiting(taskId, developmentId, Now.AddMinutes(5)));

        OnlyRow(viewModel).IsAgentAlerting.Should().BeTrue();
    }

    [AvaloniaFact]
    public void TheRow_DrawsThePulsingFrame_BehindTheContent()
    {
        var row = Row(AgentActivity.WaitingForUser);
        var template = (Avalonia.Controls.Templates.IDataTemplate)new TodayView().Resources["TaskRow"]!;
        var window = new Window { Content = new ContentControl { Content = row, ContentTemplate = template } };
        window.Show();
        window.UpdateLayout();

        var frame = window.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("agentFrame"));

        frame.Classes.Should().Contain("alerting");
        frame.IsVisible.Should().BeTrue();
        frame.IsHitTestVisible.Should().BeFalse();

        row.SeeAgentAlerts();

        frame.Classes.Should().Contain("seen").And.NotContain("alerting");
        frame.IsVisible.Should().BeTrue();
    }

    // --- O status do card ----------------------------------------------------

    [Theory]
    [InlineData(AgentActivity.Unknown, "● Em execução")]
    [InlineData(AgentActivity.Working, "● Trabalhando")]
    [InlineData(AgentActivity.WaitingForUser, "⚠ Aguardando você")]
    [InlineData(AgentActivity.WaitingReview, "✓ Terminou — aguardando sua revisão")]
    [InlineData(AgentActivity.Failed, "✗ A última resposta terminou em erro")]
    public void TheCard_NamesTheActivity(AgentActivity activity, string expected)
    {
        AgentSessionViewModel.RunningText(activity).Should().Be(expected);
    }
}
