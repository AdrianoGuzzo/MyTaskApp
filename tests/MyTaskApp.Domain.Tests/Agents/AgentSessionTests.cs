using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Domain.Tests.Agents;

/// <summary>A sessão de agente associada à tarefa (ADR-030).</summary>
public class AgentSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);

    private const string Claude = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private static readonly Guid TaskId = Guid.CreateVersion7(Now);

    private static readonly Guid DevelopmentId = Guid.CreateVersion7(Now);

    private static AgentSession NewSession() =>
        AgentSession.Create(TaskId, DevelopmentId, "claude-code", Claude, Worktree, Now);

    [Fact]
    public void ANewSession_BelongsToTheTask_AndIsStarting()
    {
        var session = NewSession();

        session.Id.Should().NotBe(Guid.Empty);
        session.TaskItemId.Should().Be(TaskId);
        session.TaskDevelopmentId.Should().Be(DevelopmentId);
        session.ProviderId.Should().Be("claude-code");
        session.Command.Should().Be(Claude);
        session.WorkingDirectory.Should().Be(Worktree);
        session.StartedAt.Should().Be(Now);
        session.Status.Should().Be(AgentSessionStatus.Starting);
        session.IsActive.Should().BeTrue();
        session.ProcessId.Should().BeNull();
        session.EndedAt.Should().BeNull();
    }

    [Fact]
    public void WithoutATask_ThereIsNoSession()
    {
        var create = () => AgentSession.Create(Guid.Empty, DevelopmentId, "claude-code", Claude, Worktree, Now);

        create.Should().Throw<DomainException>();
    }

    /// <summary>Um agente por ambiente (ADR-031): a sessão sabe em qual repositório roda.</summary>
    [Fact]
    public void WithoutAnEnvironment_ThereIsNoSession()
    {
        var create = () => AgentSession.Create(TaskId, Guid.Empty, "claude-code", Claude, Worktree, Now);

        create.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("", Claude, Worktree)]
    [InlineData("claude-code", " ", Worktree)]
    [InlineData("claude-code", Claude, "")]
    public void EveryFieldIsRequired(string provider, string command, string directory)
    {
        var create = () => AgentSession.Create(TaskId, DevelopmentId, provider, command, directory, Now);

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void Running_RecordsTheProcessAndWhenItStarted()
    {
        var session = NewSession();
        var processStart = Now.AddMilliseconds(120);

        session.MarkRunning(15432, processStart);

        session.Status.Should().Be(AgentSessionStatus.Running);
        session.ProcessId.Should().Be(15432);
        session.ProcessStartedAt.Should().Be(processStart);
        session.IsActive.Should().BeTrue();
    }

    [Fact]
    public void AnInvalidProcess_IsRefused()
    {
        var session = NewSession();

        var running = () => session.MarkRunning(0, Now);

        running.Should().Throw<DomainException>();
        session.Status.Should().Be(AgentSessionStatus.Starting);
    }

    [Fact]
    public void Exiting_EndsTheSession()
    {
        var session = NewSession();
        session.MarkRunning(15432, Now);

        session.MarkExited(Now.AddMinutes(35));

        session.Status.Should().Be(AgentSessionStatus.Exited);
        session.EndedAt.Should().Be(Now.AddMinutes(35));
        session.IsActive.Should().BeFalse();
    }

    /// <summary>O evento do processo e a reconciliação podem chegar os dois.</summary>
    [Fact]
    public void ExitingTwice_KeepsTheFirstEnd()
    {
        var session = NewSession();
        session.MarkRunning(15432, Now);
        session.MarkExited(Now.AddMinutes(35));

        session.MarkExited(Now.AddMinutes(90));

        session.EndedAt.Should().Be(Now.AddMinutes(35));
    }

    [Fact]
    public void AnEndedSession_CannotRunAgain()
    {
        var session = NewSession();
        session.MarkRunning(15432, Now);
        session.MarkExited(Now);

        var running = () => session.MarkRunning(999, Now);

        running.Should().Throw<DomainException>();
    }

    [Fact]
    public void Failing_KeepsTheReason_AndEndsTheSession()
    {
        var session = NewSession();

        session.MarkFailed("  O terminal não abriu.  ", Now);

        session.Status.Should().Be(AgentSessionStatus.Failed);
        session.FailureReason.Should().Be("O terminal não abriu.");
        session.EndedAt.Should().Be(Now);
        session.IsActive.Should().BeFalse();
    }

    // --- Acompanhamento pelos hooks (ADR-036) -------------------------------

    private static readonly string TokenHash = new('a', AgentSession.HookTokenHashLength);

    private static AgentSession Monitored()
    {
        var session = NewSession();
        session.EnableMonitoring(TokenHash);
        session.MarkRunning(15432, Now);
        return session;
    }

    [Fact]
    public void ANewSession_IsNotMonitored_AndKnowsNothingYet()
    {
        var session = NewSession();

        session.IsMonitored.Should().BeFalse();
        session.Activity.Should().Be(AgentActivity.Unknown);
        session.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void Monitoring_KeepsOnlyTheHash_InUpperCase()
    {
        var session = NewSession();

        session.EnableMonitoring(TokenHash);

        session.IsMonitored.Should().BeTrue();
        session.HookTokenHash.Should().Be(TokenHash.ToUpperInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AnInvalidHash_IsRefused(string hash)
    {
        var enable = () => NewSession().EnableMonitoring(hash);

        enable.Should().Throw<DomainException>();
    }

    /// <summary>O segredo vai no ambiente do processo: depois de aberto, é tarde.</summary>
    [Fact]
    public void Monitoring_IsOnlyEnabledBeforeTheAgentOpens()
    {
        var session = NewSession();
        session.MarkRunning(15432, Now);

        var enable = () => session.EnableMonitoring(TokenHash);

        enable.Should().Throw<DomainException>();
    }

    [Fact]
    public void AChangeOfActivity_IsRecorded_WithTheMessageAndTheTime()
    {
        var session = Monitored();

        var changed = session.RecordActivity(AgentActivity.WaitingForUser, "  Redis ou MemoryCache?  ", Now.AddMinutes(5));

        changed.Should().BeTrue();
        session.Activity.Should().Be(AgentActivity.WaitingForUser);
        session.ActivityMessage.Should().Be("Redis ou MemoryCache?");
        session.ActivityChangedAt.Should().Be(Now.AddMinutes(5));
        session.NeedsAttention.Should().BeTrue();
    }

    /// <summary>O mesmo aviso duas vezes não avisa o usuário duas vezes.</summary>
    [Fact]
    public void TheSameActivityAgain_IsNotAChange_ButKeepsTheNewText()
    {
        var session = Monitored();
        session.RecordActivity(AgentActivity.WaitingForUser, "Primeira pergunta", Now);

        var changed = session.RecordActivity(AgentActivity.WaitingForUser, "Segunda pergunta", Now.AddMinutes(1));

        changed.Should().BeFalse();
        session.ActivityMessage.Should().Be("Segunda pergunta");
        session.ActivityChangedAt.Should().Be(Now);
    }

    [Fact]
    public void BackToWork_ClearsTheQuestion()
    {
        var session = Monitored();
        session.RecordActivity(AgentActivity.WaitingForUser, "Pergunta", Now);

        session.RecordActivity(AgentActivity.Working, null, Now.AddMinutes(1)).Should().BeTrue();

        session.ActivityMessage.Should().BeNull();
        session.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void ALongMessage_IsCut()
    {
        var session = Monitored();

        session.RecordActivity(AgentActivity.WaitingReview, new string('x', 2000), Now);

        session.ActivityMessage.Should().HaveLength(AgentSession.MaxActivityMessageLength);
    }

    /// <summary>O aviso atrasado de um processo que já saiu não reacende nada.</summary>
    [Fact]
    public void AnEndedSession_IgnoresActivity_AndNeedsNoAttention()
    {
        var session = Monitored();
        session.RecordActivity(AgentActivity.WaitingReview, null, Now);
        session.MarkExited(Now.AddMinutes(1));

        session.RecordActivity(AgentActivity.WaitingForUser, "tarde demais", Now.AddMinutes(2)).Should().BeFalse();

        session.Activity.Should().Be(AgentActivity.WaitingReview);
        session.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void TheAgentsOwnSessionId_IsKept_AndReplacedAfterAClear()
    {
        var session = Monitored();

        session.RecordExternalSession("abc-1");
        session.RecordExternalSession("  ");
        session.ExternalSessionId.Should().Be("abc-1");

        session.RecordExternalSession("abc-2");
        session.ExternalSessionId.Should().Be("abc-2");
    }

    [Fact]
    public void ARunningSession_CannotFailToOpen()
    {
        var session = NewSession();
        session.MarkRunning(15432, Now);

        var failing = () => session.MarkFailed("x", Now);

        failing.Should().Throw<DomainException>();
    }
}
