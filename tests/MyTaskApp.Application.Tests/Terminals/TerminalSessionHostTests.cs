using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Tests.Terminals;

/// <summary>
/// A sessão de terminal hospedada (ADR-098, ADR-099): todos veem a mesma tela,
/// a vez de digitar é de um só, quem perde saída recebe o retrato, e as ações
/// estruturadas só valem no estado certo.
/// </summary>
public class TerminalSessionHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));

    private readonly FakePseudoTerminal _pty = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TerminalSessionHost NewHost(bool monitored = true, bool bypassed = false) =>
        new(
            new TerminalSessionInfo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), @"C:\wt", monitored, bypassed),
            _pty,
            _time,
            NullLogger<TerminalSessionHost>.Instance);

    [Fact]
    public async Task ProviderWithoutStructuredPermissionChoicesStillAcceptsRemoteKeyboardInput()
    {
        await using var host = new TerminalSessionHost(
            new TerminalSessionInfo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), @"C:\wt", true, false,
                "codex", SupportsPermissionChoices: false), _pty, _time, NullLogger<TerminalSessionHost>.Instance);
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        host.Observe(Event(AgentEventType.NeedsUserInput, "PermissionRequest", "O Codex precisa de você."));
        host.Status.ProviderId.Should().Be("codex");
        host.Status.Dialog!.AnswerableFromTheApp.Should().BeFalse();
        await remote.SendAsync(new TerminalTakeControl(), Ct);
        await remote.SendAsync(Keys("y\r"), Ct);
        _pty.Input.Should().Be("y\r");
    }

    [Fact]
    public async Task EveryClient_StartsWithTheSnapshot_AndThenFollowsTheSameOutput()
    {
        await using var host = NewHost();
        _pty.Print("primeira linha\r\n");
        await WaitUntil(() => host.Status.LastOutputAt is not null);

        using var late = host.Attach(TerminalClientKind.Remote, "Notebook");
        var view = new ClientView(late);

        await view.WaitForText("primeira linha");
        view.Resets.Should().Be(1, "quem chega no meio começa pelo retrato");

        _pty.Print("segunda\r\n");
        await view.WaitForText("segunda");
        view.Resets.Should().Be(1, "depois do retrato, só a saída nova");
    }

    [Fact]
    public async Task DisconnectingAClient_LeavesTheSessionRunning()
    {
        await using var host = NewHost();
        var client = host.Attach(TerminalClientKind.Remote, "Notebook");

        client.Dispose();

        host.IsRunning.Should().BeTrue();
        _pty.Closed.Should().BeFalse();
        _pty.Killed.Should().BeFalse();
        host.Status.Clients.Should().BeEmpty();
        (await client.ReadAsync(Ct)).Should().BeNull("o cliente desligado não lê mais nada");
    }

    [Fact]
    public async Task ARemoteClient_TypesOnlyWithControl()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");

        var typing = () => remote.SendAsync(Keys("ls\r"), Ct);

        await typing.Should().ThrowAsync<DomainException>().WithMessage("Assuma o controle*");
        await remote.SendAsync(new TerminalTakeControl(), Ct);
        await typing();

        _pty.Input.Should().Be("ls\r");
        host.Status.Lease!.Label.Should().Be("Notebook");
    }

    [Fact]
    public async Task TypingOnTheMachine_TakesControlBack_AndTellsTheRemote()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        using var window = host.Attach(TerminalClientKind.NativeWindow, "Janela");
        var remoteView = new ClientView(remote);
        await remote.SendAsync(new TerminalTakeControl(), Ct);

        await window.SendAsync(Keys("x"), Ct);

        host.Status.Lease.Should().BeNull();
        await remoteView.WaitForNotice("o controle voltou");
        var typing = () => remote.SendAsync(Keys("y"), Ct);
        await typing.Should().ThrowAsync<DomainException>();
        _pty.Input.Should().Be("x");
    }

    [Theory]
    [InlineData("\u001b[?1;2c")]
    [InlineData("\u001b[12;40R")]
    [InlineData("\u001b[I")]
    [InlineData("\u001b[<35;10;5M")]
    [InlineData("\u001b]11;rgb:0000/0000/0000\u001b\\")]
    [InlineData("\u001bP>|xterm(390)\u001b\\\u001b[O")]
    public async Task TheWindowAnsweringTheProgram_DoesNotTakeControl(string reply)
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        using var window = host.Attach(TerminalClientKind.NativeWindow, "Janela");
        await remote.SendAsync(new TerminalTakeControl(), Ct);

        await window.SendAsync(Keys(reply), Ct);

        host.Status.Lease!.Kind.Should().Be(TerminalClientKind.Remote);
        _pty.Input.Should().Be(reply, "a resposta chega ao programa mesmo assim");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("\u001b")]
    [InlineData("\u001b[A")]
    [InlineData("\u001b[?1;2cq")]
    [InlineData("\r")]
    public void RealKeys_AreNotPassive(string keys) =>
        TerminalReplies.IsPassive(Encoding.UTF8.GetBytes(keys)).Should().BeFalse();

    [Fact]
    public async Task RemoteControl_ExpiresWhenUnused()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        await remote.SendAsync(new TerminalTakeControl(), Ct);

        _time.Advance(TerminalSessionHost.RemoteLeaseIdle + TimeSpan.FromSeconds(1));

        host.Status.Lease.Should().BeNull();
        var typing = () => remote.SendAsync(Keys("y"), Ct);
        await typing.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task TakingControl_FromAnotherRemote_TellsTheOneWhoHadIt()
    {
        await using var host = NewHost();
        using var notebook = host.Attach(TerminalClientKind.Remote, "Notebook");
        using var tablet = host.Attach(TerminalClientKind.Remote, "Tablet");
        var notebookView = new ClientView(notebook);
        await notebook.SendAsync(new TerminalTakeControl(), Ct);

        await tablet.SendAsync(new TerminalTakeControl(), Ct);

        await notebookView.WaitForNotice("Tablet assumiu");
        host.Status.Lease!.Label.Should().Be("Tablet");
    }

    [Fact]
    public async Task TheNativeWindow_DecidesTheSize_AndTheOthersFollow()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        await remote.SendAsync(new TerminalTakeControl(), Ct);
        await remote.SendAsync(new TerminalResize(new TerminalSize(100, 40)), Ct);
        _pty.Size.Should().Be(new TerminalSize(100, 40), "sem janela, quem tem a vez decide");

        using var window = host.Attach(TerminalClientKind.NativeWindow, "Janela");
        await window.SendAsync(new TerminalResize(new TerminalSize(132, 43)), Ct);
        await remote.SendAsync(new TerminalResize(new TerminalSize(90, 20)), Ct);

        _pty.Size.Should().Be(new TerminalSize(132, 43));
        host.Status.Size.Should().Be(new TerminalSize(132, 43));

        window.Dispose();
        _pty.Size.Should().Be(new TerminalSize(90, 20), "a janela fechou: volta o tamanho de quem tem a vez");
    }

    [Fact]
    public async Task AnAbsurdSize_IsRefused()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");

        var resize = () => remote.SendAsync(new TerminalResize(new TerminalSize(1, 1)), Ct);

        await resize.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task AClientThatDoesNotRead_LosesTheMiddle_AndGetsTheSnapshot()
    {
        await using var host = NewHost();
        using var slow = host.Attach(TerminalClientKind.Remote, "Lento");
        var first = await slow.ReadAsync(Ct);
        first.Should().BeOfType<TerminalOutput>().Which.Reset.Should().BeTrue();

        var line = new string('x', 1000) + "\r\n";
        var lines = (TerminalSessionHost.MaxPendingBytes / line.Length) + 50;

        for (var i = 0; i < lines; i++)
        {
            _pty.Print(line);
        }

        _pty.Print("fim\r\n");
        await WaitUntil(() => host.Status.LastOutputAt is not null && OutputSeen(host) >= (lines * line.Length) + 5);

        var view = new ClientView(slow, resumeAt: ((TerminalOutput)first!).End);
        await view.WaitForText("fim");
        view.Resets.Should().Be(1, "o buraco virou um retrato, e a fila nunca passou do limite");
        view.Gaps.Should().Be(0, "a saída nunca chega com buraco: ou é contínua, ou é retrato");
    }

    [Fact]
    public async Task AskingForAResync_SendsTheSnapshotAgain()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        var view = new ClientView(remote);
        _pty.Print("olá\r\n");
        await view.WaitForText("olá");

        await remote.SendAsync(new TerminalResync(), Ct);

        await WaitUntil(() => view.Resets == 2);
    }

    [Fact]
    public async Task APrompt_IsPasted_AndConfirmed_OnlyWhileClaudeWaits()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        _pty.Print("\u001b[?2004h");
        await WaitUntil(() => host.Status.LastOutputAt is not null);
        host.Observe(Event(AgentEventType.ResponseCompleted, "Stop"));

        await remote.SendAsync(new TerminalPrompt("rode os testes\ne conte"), Ct);

        _pty.Input.Should().Be("\u001b[200~rode os testes\ne conte\u001b[201~\r");
        var again = () => remote.SendAsync(new TerminalPrompt("mais"), Ct);
        await again.Should().ThrowAsync<DomainException>().WithMessage("*trabalhando*");
    }

    [Theory]
    [InlineData("texto\u001b[201~malicioso")]
    [InlineData("\u0003")]
    [InlineData("   ")]
    public async Task APrompt_WithControlCharacters_OrEmpty_IsRefused(string text)
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");

        var prompt = () => remote.SendAsync(new TerminalPrompt(text), Ct);

        await prompt.Should().ThrowAsync<DomainException>();
        _pty.Input.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutMonitoring_OnlyKeysWork()
    {
        await using var host = NewHost(monitored: false);
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");

        var prompt = () => remote.SendAsync(new TerminalPrompt("oi"), Ct);
        var interrupt = () => remote.SendAsync(new TerminalInterrupt(), Ct);

        await prompt.Should().ThrowAsync<DomainException>().WithMessage("*sem acompanhamento*");
        await interrupt.Should().ThrowAsync<DomainException>().WithMessage("*sem acompanhamento*");
        await remote.SendAsync(new TerminalTakeControl(), Ct);
        await remote.SendAsync(Keys("oi\r"), Ct);
        _pty.Input.Should().Be("oi\r");
    }

    [Fact]
    public async Task APermissionRequest_OpensADialog_ThatCanBeAnsweredOnce()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        TerminalAttentionEventArgs? attention = null;
        host.AttentionNeeded += (_, args) => attention = args;

        host.Observe(Event(AgentEventType.NeedsUserInput, "PermissionRequest", "Bash: npm test", ("tool_name", "Bash")));
        host.Observe(Event(AgentEventType.NeedsUserInput, "Notification", "Claude needs your permission to use Bash", ("notification_type", "permission_prompt")));

        var dialog = host.Status.Dialog!;
        dialog.Kind.Should().Be(TerminalDialogKind.Permission);
        dialog.Message.Should().Be("Bash: npm test", "o aviso genérico que vem depois não apaga o contexto");
        attention!.Dialog.Should().Be(dialog);

        await remote.SendAsync(new TerminalChoice(dialog.Id, Allow: true), Ct);

        _pty.Input.Should().Be("\r");
        host.Status.Dialog.Should().BeNull();
        var twice = () => remote.SendAsync(new TerminalChoice(dialog.Id, Allow: true), Ct);
        await twice.Should().ThrowAsync<DomainException>().WithMessage("*já foi respondido*");
    }

    [Fact]
    public async Task DenyingAPermission_IsEscape()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        host.Observe(Event(AgentEventType.NeedsUserInput, "PermissionRequest", "Bash: rm -rf build", ("tool_name", "Bash")));

        await remote.SendAsync(new TerminalChoice(host.Status.Dialog!.Id, Allow: false), Ct);

        _pty.Input.Should().Be("\u001b");
    }

    [Fact]
    public async Task AnAnswerToAnOldDialog_NeverFallsIntoTheNextOne()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        host.Observe(Event(AgentEventType.NeedsUserInput, "PermissionRequest", "Bash: a", ("tool_name", "Bash")));
        var old = host.Status.Dialog!.Id;
        host.Observe(Event(AgentEventType.Working, "PostToolUse"));
        host.Observe(Event(AgentEventType.NeedsUserInput, "PermissionRequest", "Bash: b", ("tool_name", "Bash")));

        var stale = () => remote.SendAsync(new TerminalChoice(old, Allow: true), Ct);

        await stale.Should().ThrowAsync<DomainException>();
        _pty.Input.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterSomeoneTypedInTheDialog_AllowingFromTheAppIsRefused()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        using var window = host.Attach(TerminalClientKind.NativeWindow, "Janela");
        host.Observe(Event(AgentEventType.NeedsUserInput, "PermissionRequest", "Bash: a", ("tool_name", "Bash")));
        await window.SendAsync(Keys("\u001b[B"), Ct);

        var allow = () => remote.SendAsync(new TerminalChoice(host.Status.Dialog!.Id, Allow: true), Ct);

        await allow.Should().ThrowAsync<DomainException>().WithMessage("*mexeu*");
    }

    [Fact]
    public async Task AQuestion_IsShown_ButAnsweredInTheTerminal()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        host.Observe(Event(AgentEventType.NeedsUserInput, "PreToolUse", "Qual cache?", ("tool_name", "AskUserQuestion")));

        var dialog = host.Status.Dialog!;
        var choose = () => remote.SendAsync(new TerminalChoice(dialog.Id, Allow: true), Ct);

        dialog.Kind.Should().Be(TerminalDialogKind.Question);
        dialog.AnswerableFromTheApp.Should().BeFalse();
        await choose.Should().ThrowAsync<DomainException>().WithMessage("*no terminal*");
    }

    [Fact]
    public async Task Interrupt_IsEscape_OnlyWhileWorking()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        var idle = () => remote.SendAsync(new TerminalInterrupt(), Ct);
        await idle.Should().ThrowAsync<DomainException>();

        host.Observe(Event(AgentEventType.Working, "UserPromptSubmit"));
        await remote.SendAsync(new TerminalInterrupt(), Ct);

        _pty.Input.Should().Be("\u001b");
    }

    [Fact]
    public async Task StructuredActions_DoNotOverrideWhoHasControl()
    {
        await using var host = NewHost();
        using var notebook = host.Attach(TerminalClientKind.Remote, "Notebook");
        using var tablet = host.Attach(TerminalClientKind.Remote, "Tablet");
        host.Observe(Event(AgentEventType.Working, "UserPromptSubmit"));
        await notebook.SendAsync(new TerminalTakeControl(), Ct);

        var interrupt = () => tablet.SendAsync(new TerminalInterrupt(), Ct);

        await interrupt.Should().ThrowAsync<DomainException>().WithMessage("Notebook está com o controle*");
    }

    [Fact]
    public async Task Stop_IsGracefulOrForced_AndTheEndIsReported()
    {
        await using (var host = NewHost())
        {
            host.Stop(force: false);
            await host.Completion.WaitAsync(Patience, Ct);
            _pty.Closed.Should().BeTrue();
            host.Status.Process.Should().Be(TerminalProcessState.Exited);
        }

        var forced = new FakePseudoTerminal();
        await using var other = new TerminalSessionHost(
            new TerminalSessionInfo(Guid.NewGuid(), Guid.NewGuid(), null, @"C:\wt", true, false), forced, _time, NullLogger<TerminalSessionHost>.Instance);

        other.Stop(force: true);
        await other.Completion.WaitAsync(Patience, Ct);
        forced.Killed.Should().BeTrue();
        other.Status.Process.Should().Be(TerminalProcessState.Crashed);
        other.Status.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task AfterTheProcessEnds_InputIsRefused_AndTheScreenStays()
    {
        await using var host = NewHost();
        using var remote = host.Attach(TerminalClientKind.Remote, "Notebook");
        var view = new ClientView(remote);
        _pty.Print("tchau\r\n");
        await view.WaitForText("tchau");

        _pty.Exit(3);
        await host.Completion.WaitAsync(Patience, Ct);

        host.Status.Process.Should().Be(TerminalProcessState.Crashed);
        host.Status.ExitCode.Should().Be(3);
        var typing = () => remote.SendAsync(new TerminalTakeControl(), Ct);
        await typing.Should().ThrowAsync<DomainException>().WithMessage("*já saiu*");
        await WaitUntil(() => view.LastState?.Process == TerminalProcessState.Crashed);
        view.Text.Should().Contain("tchau");
    }

    [Fact]
    public async Task Hooks_ShowTheBypass_AndThePermissionMode()
    {
        await using var host = NewHost();

        host.Observe(Event(AgentEventType.Working, "UserPromptSubmit", null, ("permission_mode", "bypassPermissions")));

        host.Status.PermissionsBypassed.Should().BeTrue();
        host.Status.PermissionMode.Should().Be("bypassPermissions");
    }

    [Fact]
    public async Task WithoutTheNativeWindow_TheHostAnswersTheProgramsQueries()
    {
        await using var host = NewHost();
        _pty.Print("\u001b[c");
        await WaitUntil(() => _pty.Input.Length > 0);
        _pty.Input.Should().Be("\u001b[?1;2c");

        _pty.ClearInput();
        using var window = host.Attach(TerminalClientKind.NativeWindow, "Janela");
        _pty.Print("\u001b[c");
        _pty.Print("x");
        await WaitUntil(() => host.Status.LastOutputAt is not null && OutputSeen(host) >= 7);
        _pty.Input.Should().BeEmpty("com a janela ligada, é ela quem responde");
    }

    [Fact]
    public async Task ResponseCompleted_CallsForAttention()
    {
        await using var host = NewHost();
        var calls = new List<TerminalAttentionEventArgs>();
        host.AttentionNeeded += (_, args) => calls.Add(args);

        host.Observe(Event(AgentEventType.Working, "UserPromptSubmit"));
        host.Observe(Event(AgentEventType.ResponseCompleted, "Stop", "pronto"));
        host.Observe(Event(AgentEventType.Notification, "Notification", "idle", ("notification_type", "idle_prompt")));

        calls.Should().ContainSingle().Which.Activity.Should().Be(AgentActivity.WaitingReview);
        host.Status.ActivityMessage.Should().Be("pronto");
    }

    private static TerminalKeys Keys(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>Até onde o host já leu a saída: a posição do retrato de um cliente novo.</summary>
    private static long OutputSeen(TerminalSessionHost host)
    {
        using var probe = host.Attach(TerminalClientKind.Embedded, "sonda");
        return host.SnapshotFor(probe).Offset;
    }

    private static AgentEvent Event(AgentEventType type, string source, string? message = null, params (string Key, string Value)[] metadata) =>
        new(
            type,
            Guid.NewGuid(),
            null,
            "conversa",
            null,
            message,
            DateTimeOffset.UnixEpoch,
            source,
            metadata.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;

        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("A condição não chegou a valer.");
            }

            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Um cliente que lê tudo numa tela própria e confere a continuidade da saída.</summary>
    private sealed class ClientView
    {
        private readonly VtScreen _screen = new(new TerminalSize(80, 24), scrollback: 10_000);

        private readonly List<string> _notices = [];

        private long _expected = -1;

        /// <param name="resumeAt">O cliente já leu o retrato inicial: a saída continua desta posição.</param>
        public ClientView(TerminalClient client, long? resumeAt = null)
        {
            _expected = resumeAt ?? -1;

            _ = Task.Run(async () =>
            {
                while (await client.ReadAsync() is { } message)
                {
                    lock (this)
                    {
                        Handle(message);
                    }
                }
            });
        }

        public int Resets { get; private set; }

        public int Gaps { get; private set; }

        public TerminalSessionStatus? LastState { get; private set; }

        public string Text
        {
            get
            {
                lock (this)
                {
                    return string.Join("\n", Enumerable.Range(0, _screen.ScrollbackCount).Select(_screen.ScrollbackText).Append(_screen.ScreenText()));
                }
            }
        }

        public Task WaitForText(string text) => WaitUntil(() => Text.Contains(text, StringComparison.Ordinal));

        public Task WaitForNotice(string text) => WaitUntil(() =>
        {
            lock (this)
            {
                return _notices.Any(notice => notice.Contains(text, StringComparison.Ordinal));
            }
        });

        private void Handle(TerminalMessage message)
        {
            switch (message)
            {
                case TerminalOutput { Reset: true } snapshot:
                    Resets++;
                    _screen.Feed("\u001bc");
                    _screen.Feed(snapshot.Data);
                    _expected = snapshot.End;
                    break;
                case TerminalOutput output:
                    if (_expected >= 0 && output.Offset != _expected)
                    {
                        Gaps++;
                    }

                    _screen.Feed(output.Data);
                    _expected = output.End;
                    break;
                case TerminalStateMessage state:
                    LastState = state.Status;
                    break;
                case TerminalNotice notice:
                    _notices.Add(notice.Text);
                    break;
            }
        }
    }
}
