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

    // --- Acompanhamento pelos hooks (ADR-037) -------------------------------

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

    private static readonly Guid Conversation = Guid.Parse("3f9c2a7e-1b4d-4c8e-9a51-6d2e8f0b7c13");

    private static readonly Guid FirstOperation = Guid.CreateVersion7(Now);

    private static AgentSession NewHeadless() =>
        AgentSession.CreateHeadless(TaskId, DevelopmentId, "claude-code", Claude, Worktree, Conversation, FirstOperation, "edit", "haiku", null, Now);

    /// <summary>Sem terminal (ADR-089): a conversa nasce com o id e a operação que a pediu.</summary>
    [Fact]
    public void AHeadlessSession_IsTheConversation_AndStartsByTheOperation()
    {
        var session = NewHeadless();

        session.Mode.Should().Be(AgentSessionMode.Headless);
        session.ExternalSessionId.Should().Be(Conversation.ToString());
        session.RemoteOperationId.Should().Be(FirstOperation);
        session.PromptStartedAt.Should().Be(Now);
        session.Status.Should().Be(AgentSessionStatus.Starting);
        session.HeadlessProfileId.Should().Be("edit");
        session.Model.Should().Be("haiku");
        session.Effort.Should().BeNull();
        NewSession().Mode.Should().Be(AgentSessionMode.Interactive);
        NewSession().RemoteOperationId.Should().BeNull();
    }

    [Fact]
    public void AfterAPromptEnds_TheConversationContinues_ByAnotherOperation_InAnotherProcess()
    {
        var session = NewHeadless();
        session.MarkRunning(4242, Now);
        session.MarkExited(Now.AddMinutes(1));
        session.CanContinue.Should().BeTrue();

        var next = Guid.CreateVersion7();
        session.ContinueWith(next, Now.AddHours(2));

        session.Status.Should().Be(AgentSessionStatus.Starting);
        session.RemoteOperationId.Should().Be(next);
        session.PromptStartedAt.Should().Be(Now.AddHours(2), "a folga da reconciliação conta do prompt, não da conversa");
        session.ProcessId.Should().BeNull("o processo do prompt anterior não é este");
        session.EndedAt.Should().BeNull();
        session.Activity.Should().Be(AgentActivity.Working);
        session.ExternalSessionId.Should().Be(Conversation.ToString());
    }

    [Fact]
    public void APromptRunning_OrAStoppedConversation_OrTheTerminal_DoNotContinue()
    {
        var running = NewHeadless();
        FluentActions.Invoking(() => running.ContinueWith(Guid.CreateVersion7(), Now)).Should().Throw<DomainException>().WithMessage("*ainda está respondendo*");

        var stopped = NewHeadless();
        stopped.MarkStopped(Now);
        FluentActions.Invoking(() => stopped.ContinueWith(Guid.CreateVersion7(), Now)).Should().Throw<DomainException>().WithMessage("*foi parada*");

        var terminal = NewSession();
        terminal.MarkRunning(1, Now);
        terminal.MarkExited(Now);
        FluentActions.Invoking(() => terminal.ContinueWith(Guid.CreateVersion7(), Now)).Should().Throw<DomainException>();
        terminal.CanContinue.Should().BeFalse();
    }

    /// <summary><c>Stopped</c> só a pedido, e só sem terminal: no terminal, quem encerra é o usuário.</summary>
    [Fact]
    public void Stopping_EndsTheConversation_WithOrWithoutAPromptRunning()
    {
        var mid = NewHeadless();
        mid.MarkRunning(4242, Now);
        mid.MarkStopped(Now.AddMinutes(3));
        mid.Status.Should().Be(AgentSessionStatus.Stopped);
        mid.EndedAt.Should().Be(Now.AddMinutes(3));
        mid.IsActive.Should().BeFalse();

        var idle = NewHeadless();
        idle.MarkRunning(4242, Now);
        idle.MarkExited(Now.AddMinutes(1));
        idle.MarkStopped(Now.AddMinutes(9));
        idle.EndedAt.Should().Be(Now.AddMinutes(1), "o fim do último prompt fica");
        idle.MarkStopped(Now.AddMinutes(10));
        idle.Status.Should().Be(AgentSessionStatus.Stopped);

        var exitedLater = NewHeadless();
        exitedLater.MarkStopped(Now);
        exitedLater.MarkExited(Now.AddMinutes(1));
        exitedLater.Status.Should().Be(AgentSessionStatus.Stopped, "o fim do processo chega depois e não desfaz o pedido");

        FluentActions.Invoking(() => NewSession().MarkStopped(Now)).Should().Throw<DomainException>().WithMessage("*no próprio terminal*");
    }

    /// <summary>O fim do prompt grava como acabou, mesmo depois de a reconciliação ter fechado a sessão; a parada fica.</summary>
    [Fact]
    public void EndingAPrompt_RecordsHowItEnded_EvenIfTheSessionWasAlreadyClosed()
    {
        var running = NewHeadless();
        running.MarkRunning(4242, Now);
        running.EndHeadlessPrompt(AgentActivity.WaitingReview, "Pronto", Now.AddMinutes(2));
        running.Status.Should().Be(AgentSessionStatus.Exited);
        running.EndedAt.Should().Be(Now.AddMinutes(2));
        running.Activity.Should().Be(AgentActivity.WaitingReview);
        running.ActivityMessage.Should().Be("Pronto");
        running.CanContinue.Should().BeTrue();

        var reconciled = NewHeadless();
        reconciled.MarkRunning(4242, Now);
        reconciled.MarkExited(Now.AddMinutes(1));
        reconciled.EndHeadlessPrompt(AgentActivity.Failed, "Erro no Claude", Now.AddMinutes(2));
        reconciled.EndedAt.Should().Be(Now.AddMinutes(1), "o processo saiu quando a reconciliação viu");
        reconciled.Activity.Should().Be(AgentActivity.Failed, "só quem leu a saída sabe como acabou");
        reconciled.ActivityMessage.Should().Be("Erro no Claude");

        var stopped = NewHeadless();
        stopped.MarkRunning(4242, Now);
        stopped.MarkStopped(Now.AddMinutes(1));
        stopped.EndHeadlessPrompt(AgentActivity.WaitingReview, "Pronto", Now.AddMinutes(2));
        stopped.Status.Should().Be(AgentSessionStatus.Stopped);
        stopped.Activity.Should().NotBe(AgentActivity.WaitingReview);

        FluentActions.Invoking(() => NewSession().EndHeadlessPrompt(AgentActivity.WaitingReview, null, Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void AHeadlessSession_NeedsTheConversationAndTheOperation() =>
        FluentActions.Invoking(() => AgentSession.CreateHeadless(TaskId, DevelopmentId, "claude-code", Claude, Worktree, Guid.Empty, FirstOperation, "edit", null, null, Now))
            .Should().Throw<DomainException>();
}
