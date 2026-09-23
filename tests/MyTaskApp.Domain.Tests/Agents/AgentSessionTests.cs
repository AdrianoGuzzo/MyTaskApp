using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Domain.Tests.Agents;

/// <summary>A sessão de agente associada à tarefa (ADR-029).</summary>
public class AgentSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);

    private const string Claude = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private static readonly Guid TaskId = Guid.CreateVersion7(Now);

    private static AgentSession NewSession() =>
        AgentSession.Create(TaskId, "claude-code", Claude, Worktree, Now);

    [Fact]
    public void ANewSession_BelongsToTheTask_AndIsStarting()
    {
        var session = NewSession();

        session.Id.Should().NotBe(Guid.Empty);
        session.TaskItemId.Should().Be(TaskId);
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
        var create = () => AgentSession.Create(Guid.Empty, "claude-code", Claude, Worktree, Now);

        create.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("", Claude, Worktree)]
    [InlineData("claude-code", " ", Worktree)]
    [InlineData("claude-code", Claude, "")]
    public void EveryFieldIsRequired(string provider, string command, string directory)
    {
        var create = () => AgentSession.Create(TaskId, provider, command, directory, Now);

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

    [Fact]
    public void ARunningSession_CannotFailToOpen()
    {
        var session = NewSession();
        session.MarkRunning(15432, Now);

        var failing = () => session.MarkFailed("x", Now);

        failing.Should().Throw<DomainException>();
    }
}
