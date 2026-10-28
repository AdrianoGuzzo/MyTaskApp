using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O Claude sem terminal de longe (E5, T-30), com o provider, o processo e o
/// MCP de aprovação falsos: a conversa abre e continua no mesmo worktree, a
/// ferramenta fora do perfil vira aprovação (sem resposta, negada), e o
/// <c>agent.stop</c> encerra o processo que outra operação abriu.
/// </summary>
public sealed class AgentSessionExecutorsTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-validacao";

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly FakeHeadlessProvider _claude = new();
    private readonly ScriptedHeadlessRunner _runner = new();
    private readonly FakeApprovalGateway _gateway = new();
    private readonly TaskItem _task = TaskItem.Create("Implementar validação de antimicrobianos", Now);
    private readonly List<RemoteOperationEvent> _events = [];
    private readonly List<RemoteApprovalAsk> _asks = [];
    private readonly ServiceProvider _services;

    public AgentSessionExecutorsTests()
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/validacao", Worktree, Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
        _tasks.Seed(_task);
        _disk.Existing.Add(Worktree);

        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_time)
            .AddSingleton<ITaskItemRepository>(_tasks)
            .AddSingleton<IAgentSessionRepository>(_sessions)
            .AddSingleton<IAgentProcessTracker>(_processes)
            .AddSingleton<IDirectoryProbe>(_disk)
            .AddSingleton(Substitute.For<IUnitOfWork>())
            .AddSingleton<IHeadlessAgentProvider>(_claude)
            .AddSingleton<IHeadlessAgentRunner>(_runner)
            .AddSingleton<IHeadlessApprovalGateway>(_gateway)
            .AddSingleton<ILogger<HeadlessPromptRunner>>(NullLogger<HeadlessPromptRunner>.Instance)
            .AddSingleton(HeadlessAgentLimits.Default)
            .AddSingleton<HeadlessConversations>()
            .AddSingleton<HeadlessPromptRunner>()
            .AddScoped<HeadlessAgentPlanner>()
            .AddScoped<HeadlessSessionStore>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .AddSingleton<AgentStartExecutor>()
            .AddSingleton<AgentPromptExecutor>()
            .AddSingleton<AgentStopExecutor>()
            .AddSingleton<ILogger<HeadlessSessionRecovery>>(NullLogger<HeadlessSessionRecovery>.Instance)
            .AddSingleton<HeadlessSessionRecovery>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid DevelopmentId => _task.Developments[0].Id;

    private AgentStartExecutor Start => _services.GetRequiredService<AgentStartExecutor>();

    private AgentPromptExecutor Prompt => _services.GetRequiredService<AgentPromptExecutor>();

    private AgentStopExecutor Stop => _services.GetRequiredService<AgentStopExecutor>();

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task TheFirstPrompt_OpensAConversationInTheWorktree_AndTheAnswerComesAsEvents()
    {
        _runner.Script(
            FakeHeadlessProvider.Text("Vou olhar os testes."),
            FakeHeadlessProvider.Tool("Read", "src/Validacao.cs"),
            FakeHeadlessProvider.Text("Os testes cobrem o caso."),
            FakeHeadlessProvider.Done(turns: 3, cost: 0.12m));
        var request = StartRequest("edit-test", "Revise a validação", maxTurns: 500, model: "haiku");

        (await Start.CheckAsync(request, Ct)).Should().BeNull();
        var result = await Start.ExecuteAsync(Execution(request), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        result.Summary.Should().Be("Respondeu em 3 turnos, US$ 0.12.");

        var session = _sessions.Sessions.Should().ContainSingle().Subject;
        session.IsHeadless.Should().BeTrue();
        (session.HeadlessProfileId, session.Model, session.RemoteOperationId).Should().Be(("edit-test", "haiku", request.OperationId));
        session.Status.Should().Be(AgentSessionStatus.Exited);
        session.Activity.Should().Be(AgentActivity.WaitingReview);
        session.ProcessId.Should().Be(ScriptedHeadlessRunner.FirstProcessId);
        result.Data!.Value.GetProperty("agentSessionId").GetGuid().Should().Be(session.Id, "é por ele que vem o próximo prompt");
        result.Data!.Value.GetProperty("canContinue").GetBoolean().Should().BeTrue();
        _events[0].Data!.Value.GetProperty("agentSessionId").GetGuid().Should().Be(session.Id, "para parar o primeiro prompt no meio");
        _events[0].Data!.Value.GetProperty("prompt").GetString().Should().Be("Revise a validação", "a linha do tempo da conversa mostra quem pediu o quê");

        var asked = _claude.Requests.Should().ContainSingle().Subject;
        asked.WorkingDirectory.Should().Be(Worktree);
        asked.ConversationId.ToString().Should().Be(session.ExternalSessionId);
        asked.Resume.Should().BeFalse();
        asked.Profile.Should().Be(AgentToolProfiles.Test);
        asked.MaxTurns.Should().Be(HeadlessAgentLimits.Default.MaxTurns, "o pedido pode pedir menos, nunca mais");
        asked.MaxBudgetUsd.Should().Be(HeadlessAgentLimits.Default.MaxBudgetUsd);
        asked.Approval.Should().Be(_gateway.Access);
        _runner.Prompts.Should().Equal("Revise a validação");

        _events.Where(item => item.Kind is RemoteEventKind.AgentMessage or RemoteEventKind.ToolUse)
            .Select(item => (item.Kind, item.Text))
            .Should().Equal(
                (RemoteEventKind.AgentMessage, "Vou olhar os testes."),
                (RemoteEventKind.ToolUse, "Read: src/Validacao.cs"),
                (RemoteEventKind.AgentMessage, "Os testes cobrem o caso."));
        _gateway.Open.Should().Be(0, "o canal de aprovação fecha com o prompt");
    }

    [Fact]
    public async Task TheNextPrompt_ContinuesTheSameConversation_WithItsProfile()
    {
        _runner.Script(FakeHeadlessProvider.Done());
        await Start.ExecuteAsync(Execution(StartRequest("read", "Leia o README", model: "opus")), Ct);
        var session = _sessions.Sessions.Single();

        _runner.Script(FakeHeadlessProvider.Text("Feito."), FakeHeadlessProvider.Done(turns: 1));
        var next = PromptRequest(session.Id, "E agora os testes?");
        (await Prompt.CheckAsync(next, Ct)).Should().BeNull();
        var result = await Prompt.ExecuteAsync(Execution(next), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        result.Summary.Should().Be("Respondeu em 1 turno.");
        _sessions.Sessions.Should().ContainSingle();
        session.RemoteOperationId.Should().Be(next.OperationId);
        session.Status.Should().Be(AgentSessionStatus.Exited);

        var second = _claude.Requests[1];
        second.Resume.Should().BeTrue();
        second.ConversationId.Should().Be(_claude.Requests[0].ConversationId);
        (second.Profile, second.Model).Should().Be((AgentToolProfiles.Read, "opus"), "o perfil é da conversa, não do prompt");
    }

    [Fact]
    public async Task AToolOutsideTheProfile_AsksWhoSentIt_AndDenyingTellsClaudeToGoOn()
    {
        ToolPermissionDecision? decision = null;
        _runner.Script(async () => decision = await _gateway.AskAsync("Bash", """{"command":"rm -rf bin"}"""), FakeHeadlessProvider.Done(denied: ["Bash"]));

        var result = await Start.ExecuteAsync(
            Execution(StartRequest("edit", "Limpe a pasta"), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Denied, "adria")),
            Ct);

        var ask = _asks.Should().ContainSingle().Subject;
        ask.Kind.Should().Be(ApprovalKind.ToolUse);
        ask.Summary.Should().Be("O Claude quer usar Bash: rm -rf bin");
        ask.Details.GetProperty("tool").GetString().Should().Be("Bash");
        decision.Should().Be(ToolPermissionDecision.Deny("Negado por adria. Siga sem essa ferramenta."));
        result.Summary.Should().Be("Respondeu em 1 turno; negado: Bash.");
    }

    [Fact]
    public async Task NoAnswerInTime_IsADenial()
    {
        ToolPermissionDecision? decision = null;
        _runner.Script(async () => decision = await _gateway.AskAsync("WebFetch", """{"url":"https://example.com"}"""), FakeHeadlessProvider.Done());

        await Start.ExecuteAsync(Execution(StartRequest("edit", "Pesquise"), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Expired)), Ct);

        decision!.IsAllowed.Should().BeFalse();
        decision.Message.Should().StartWith("Ninguém respondeu a tempo");
    }

    /// <summary>
    /// A pergunta do Claude (ADR-093) vai a quem pediu com as opções, e a
    /// resposta assinada volta ao Claude na entrada, pelo texto da pergunta.
    /// </summary>
    [Fact]
    public async Task TheQuestionOfClaude_GoesWithItsOptions_AndTheAnswerGoesBackInTheInput()
    {
        ToolPermissionDecision? decision = null;
        _runner.Script(async () => decision = await _gateway.AskAsync("AskUserQuestion", """{"questions":[{"question":"Qual banco de teste?","header":"Banco","options":[{"label":"Local","description":"O SQLite da pasta"},{"label":"Docker"}]}]}"""), FakeHeadlessProvider.Done());

        await Start.ExecuteAsync(
            Execution(StartRequest("read", "Rode os testes"), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Allowed, "adria", Answers: """["Docker"]""")),
            Ct);

        var ask = _asks.Should().ContainSingle().Subject;
        ask.Summary.Should().Be("O Claude pergunta: Qual banco de teste?");
        ask.Details.GetProperty("kind").GetString().Should().Be("question");
        ask.Details.GetProperty("canRemember").GetBoolean().Should().BeFalse("a pergunta é respondida a cada vez");
        HeadlessQuestions.FromDetails(ask.Details)!.Single().Options.Select(option => option.Label).Should().Equal("Local", "Docker");
        decision!.IsAllowed.Should().BeTrue();
        decision.UpdatedInput!.Value.GetProperty("answers").GetProperty("Qual banco de teste?").GetString().Should().Be("Docker");
        decision.UpdatedInput.Value.GetProperty("questions").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData(RemoteApprovalOutcome.Denied, null, "adria preferiu não responder")]
    [InlineData(RemoteApprovalOutcome.Expired, null, "Ninguém respondeu a tempo")]
    [InlineData(RemoteApprovalOutcome.Allowed, null, "A pergunta foi aceita sem escolher")]
    [InlineData(RemoteApprovalOutcome.Allowed, """["Docker","Local"]""", "A pergunta foi aceita sem escolher")]
    public async Task AQuestionWithoutAnAnswer_LetsClaudeDecide_AndSayWhat(RemoteApprovalOutcome outcome, string? answers, string message)
    {
        ToolPermissionDecision? decision = null;
        _runner.Script(async () => decision = await _gateway.AskAsync("AskUserQuestion", """{"questions":[{"question":"Qual banco de teste?","header":"Banco","options":[{"label":"Local","description":"O SQLite da pasta"},{"label":"Docker"}]}]}"""), FakeHeadlessProvider.Done());

        await Start.ExecuteAsync(
            Execution(StartRequest("read", "Rode os testes"), _ => new RemoteApprovalResult(outcome, "adria", Answers: answers)),
            Ct);

        decision!.IsAllowed.Should().BeFalse();
        decision.Message.Should().StartWith(message).And.EndWith("Siga pelo seu julgamento e diga o que escolheu.");
    }

    [Fact]
    public async Task AQuestionOutOfShape_IsNotAskedToAnyone()
    {
        ToolPermissionDecision? decision = null;
        _runner.Script(async () => decision = await _gateway.AskAsync("AskUserQuestion", """{"questions":"qual?"}"""), FakeHeadlessProvider.Done());

        await Start.ExecuteAsync(Execution(StartRequest("read", "Rode"), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Allowed)), Ct);

        _asks.Should().BeEmpty();
        decision!.IsAllowed.Should().BeFalse();
    }

    /// <summary>
    /// "Permitir nesta conversa" lembra o comando exato: o mesmo passa sem
    /// perguntar, outro (mesmo parecido) pergunta, e o composto nem oferece.
    /// </summary>
    [Fact]
    public async Task AllowingForThisConversation_RemembersTheExactCommand_NotTheTool()
    {
        var decisions = new List<ToolPermissionDecision>();
        _runner.Script(
            async () => decisions.Add(await _gateway.AskAsync("Bash", """{"command":"git push"}""")),
            async () => decisions.Add(await _gateway.AskAsync("Bash", """{"command":"git  push"}""")),
            async () => decisions.Add(await _gateway.AskAsync("Bash", """{"command":"git push --force"}""")),
            async () => decisions.Add(await _gateway.AskAsync("Bash", """{"command":"git push && rm -rf ."}""")),
            FakeHeadlessProvider.Done());

        await Start.ExecuteAsync(
            Execution(StartRequest("edit", "Publique"), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Allowed, "adria", RememberForThisSession: true)),
            Ct);

        decisions.Should().HaveCount(4).And.AllSatisfy(item => item.IsAllowed.Should().BeTrue());
        _asks.Select(ask => ask.Summary).Should().Equal(
            "O Claude quer usar Bash: git push",
            "O Claude quer usar Bash: git push --force",
            "O Claude quer usar Bash: git push && rm -rf .");
        _asks[0].Details.GetProperty("canRemember").GetBoolean().Should().BeTrue();
        _asks[0].Details.GetProperty("rememberAs").GetString().Should().Be("Bash: git push");
        _asks[2].Details.GetProperty("canRemember").GetBoolean().Should().BeFalse("o composto é sempre perguntado");
    }

    [Fact]
    public async Task AgentStop_EndsTheProcessAnotherOperationOpened_AndTheConversationTakesNoMorePrompts()
    {
        var running = new TaskCompletionSource();
        _runner.Script(async () =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, _runner.Token);
        });
        var request = StartRequest("edit", "Refatore tudo");
        var prompt = Start.ExecuteAsync(Execution(request), Ct);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var session = _sessions.Sessions.Single();
        session.Status.Should().Be(AgentSessionStatus.Running);

        var stop = StopRequest(session.Id);
        (await Stop.CheckAsync(stop, Ct)).Should().BeNull();
        var stopped = await Stop.ExecuteAsync(Execution(stop), Ct);
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        stopped.Summary.Should().Be("O Claude foi parado no meio da resposta.");
        result.Succeeded.Should().BeFalse();
        result.Summary.Should().Be("O Claude foi parado.");
        result.Data!.Value.GetProperty("canContinue").GetBoolean().Should().BeFalse();
        session.Status.Should().Be(AgentSessionStatus.Stopped, "o fim do processo depois do stop não reabre a conversa");
        (await Prompt.CheckAsync(PromptRequest(session.Id, "Mais uma"), Ct))!.Summary.Should().Be("A conversa foi parada. Abra outra.");
    }

    [Fact]
    public async Task ClaudeLoggedOut_EndsWithWhatToDoOnThatMachine()
    {
        _runner.Script(FakeHeadlessProvider.LoggedOut());

        var result = await Start.ExecuteAsync(Execution(StartRequest("read", "oi")), Ct);

        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be(RemoteErrorCodes.ExecutionFailed);
        result.Summary.Should().Be("O Claude desta máquina não está logado. Nela, rode \"claude auth login\".");
        _sessions.Sessions.Single().Activity.Should().Be(AgentActivity.Failed);
    }

    [Fact]
    public async Task WhatThisMachineCannotRun_IsRefusedBeforeOpeningAnything()
    {
        (await Start.CheckAsync(StartRequest("tudo", "oi"), Ct))!.Summary.Should().Be("Perfil desconhecido nesta máquina: tudo.");

        _disk.Existing.Remove(Worktree);
        (await Start.CheckAsync(StartRequest("read", "oi"), Ct))!.Summary.Should().Be("A pasta do worktree não existe mais nesta máquina.");
        _disk.Existing.Add(Worktree);

        _claude.Unavailable = "O Claude Code 2.0.1 desta máquina é antigo.";
        (await Start.CheckAsync(StartRequest("read", "oi"), Ct))!.Summary.Should().Be("O Claude Code 2.0.1 desta máquina é antigo.");

        (await Prompt.CheckAsync(PromptRequest(Guid.CreateVersion7(), "oi"), Ct))!.Summary.Should().Be("Essa conversa não existe nesta máquina.");
        _runner.Prompts.Should().BeEmpty();
        _sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherConversationOpenInTheWorktree_RefusesANewOne()
    {
        var open = AgentSession.Create(_task.Id, DevelopmentId, "claude-code", @"C:\claude.exe", Worktree, Now);
        open.MarkRunning(4242, Now);
        _sessions.Seed(open);
        _processes.Run(4242, Now);

        (await Start.CheckAsync(StartRequest("read", "oi"), Ct))!.Summary.Should().StartWith("Já há um agente aberto neste worktree.");

        _processes.Exit(4242);
        (await Start.CheckAsync(StartRequest("read", "oi"), Ct)).Should().BeNull("o processo que sumiu não segura o worktree");
    }

    /// <summary>Dois prompts na mesma conversa ao mesmo tempo: um roda, o outro é recusado sem tocar nela.</summary>
    /// <summary>
    /// A fila da conversa (E6): o prompt que chega com o Claude respondendo
    /// espera a vez sem tocar na conversa, diz que está na fila, e roda logo
    /// depois do primeiro. Mais um, com a fila cheia, é recusado.
    /// </summary>
    [Fact]
    public async Task APromptWhileClaudeAnswers_WaitsItsTurn_AndRunsRightAfter()
    {
        _runner.Script(FakeHeadlessProvider.Done());
        await Start.ExecuteAsync(Execution(StartRequest("read", "Leia o README")), Ct);
        var session = _sessions.Sessions.Single();

        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _runner.Script(
            async () =>
            {
                running.SetResult();
                await release.Task;
            },
            FakeHeadlessProvider.Done());
        _runner.Script(FakeHeadlessProvider.Done());
        var first = PromptRequest(session.Id, "Rode os testes");
        var firstRun = Prompt.ExecuteAsync(Execution(first), Ct);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var second = PromptRequest(session.Id, "E o lint?");
        (await Prompt.CheckAsync(second, Ct)).Should().BeNull("na fila, não recusado");
        var secondRun = Prompt.ExecuteAsync(Execution(second), Ct);
        await WaitUntilAsync(IsQueued);

        session.RemoteOperationId.Should().Be(first.OperationId, "o segundo ainda não tocou na conversa");
        var third = PromptRequest(session.Id, "E a documentação?");
        (await Prompt.CheckAsync(third, Ct))!.Summary.Should().Be(HeadlessAgentPlanner.QueueFullMessage);
        var refused = await Prompt.ExecuteAsync(Execution(third), Ct);
        refused.Summary.Should().Be(HeadlessAgentPlanner.QueueFullMessage);

        release.SetResult();
        (await firstRun.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Succeeded.Should().BeTrue();
        (await secondRun.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Succeeded.Should().BeTrue();

        _runner.Prompts.Should().Equal("Leia o README", "Rode os testes", "E o lint?");
        session.RemoteOperationId.Should().Be(second.OperationId);
    }

    /// <summary>Parar a conversa encerra o prompt que roda e dispensa o que esperava a vez.</summary>
    [Fact]
    public async Task StoppingTheConversation_DismissesThePromptWaitingItsTurn()
    {
        _runner.Script(FakeHeadlessProvider.Done());
        await Start.ExecuteAsync(Execution(StartRequest("read", "Leia o README")), Ct);
        var session = _sessions.Sessions.Single();

        var running = new TaskCompletionSource();
        _runner.Script(async () =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, _runner.Token);
        });
        var firstRun = Prompt.ExecuteAsync(Execution(PromptRequest(session.Id, "Rode os testes")), Ct);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var secondRun = Prompt.ExecuteAsync(Execution(PromptRequest(session.Id, "E o lint?")), Ct);
        await WaitUntilAsync(IsQueued);

        await Stop.ExecuteAsync(Execution(StopRequest(session.Id)), Ct);

        var dismissed = await secondRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        dismissed.Succeeded.Should().BeFalse();
        dismissed.Summary.Should().Be(AgentPromptExecutor.StoppedWhileQueued);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _runner.Prompts.Should().Equal("Leia o README", "Rode os testes");
    }

    /// <summary>Quem desiste na fila (o pedido cancelado) sai dela, e a vez segue para os outros.</summary>
    [Fact]
    public async Task APromptCanceledInTheQueue_LeavesIt()
    {
        var conversations = new HeadlessConversations();
        var sessionId = Guid.CreateVersion7();
        var first = conversations.TryBegin(sessionId)!;
        using var cancel = new CancellationTokenSource();

        var waiting = conversations.WaitAsync(sessionId, () => Task.CompletedTask, cancel.Token);
        conversations.WaitingFor(sessionId).Should().Be(1);
        await cancel.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        conversations.WaitingFor(sessionId).Should().Be(0);
        first.Dispose();
        conversations.IsBusy(sessionId).Should().BeFalse("ninguém esperava mais");
        using var next = await conversations.WaitAsync(sessionId, () => Task.CompletedTask, Ct);
        next.Should().NotBeNull();
    }

    private bool IsQueued()
    {
        lock (_events)
        {
            return _events.Any(item => item.Text == AgentPromptExecutor.QueuedText);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A prévia do prompt no primeiro evento vai mascarada e com teto; o Claude recebe o texto inteiro.</summary>
    [Fact]
    public async Task ThePromptPreview_IsMaskedAndClipped_ButClaudeGetsTheWholePrompt()
    {
        _runner.Script(FakeHeadlessProvider.Done(turns: 1, cost: 0m));
        var text = "Conecte com password=hunter2 e " + new string('x', RemoteLimits.MaxPromptPreviewLength);

        await Start.ExecuteAsync(Execution(StartRequest("edit", text)), Ct);

        var preview = _events[0].Data!.Value.GetProperty("prompt").GetString();
        preview.Should().NotContain("hunter2").And.HaveLength(RemoteLimits.MaxPromptPreviewLength).And.EndWith("…");
        _runner.Prompts.Should().Equal(text);
    }

    /// <summary>O <c>agent.stop</c> que chega antes de o processo abrir: o processo nem abre.</summary>
    [Fact]
    public async Task AStopBeforeTheProcessOpens_DoesNotOpenIt()
    {
        var request = StartRequest("edit", "Refatore tudo");
        _services.GetRequiredService<IUnitOfWork>()
            .When(unit => unit.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                // A conversa acabou de ser gravada, e o agent.stop chega antes do processo.
                if (_sessions.Sessions.SingleOrDefault() is { Status: AgentSessionStatus.Starting } session)
                {
                    session.MarkStopped(Now);
                }
            });

        var result = await Start.ExecuteAsync(Execution(request), Ct);

        result.Succeeded.Should().BeFalse();
        result.Summary.Should().Be("O Claude foi parado.");
        _runner.Prompts.Should().BeEmpty();
        _sessions.Sessions.Single().Status.Should().Be(AgentSessionStatus.Stopped);
    }

    /// <summary>
    /// O agente caiu no meio de um prompt: a sessão ficou "rodando" com um
    /// processo que não existe mais. O próximo prompt confere, fecha e segue.
    /// </summary>
    [Fact]
    public async Task AConversationLeftRunningByACrashedAgent_TakesTheNextPrompt()
    {
        var session = AgentSession.CreateHeadless(
            _task.Id, DevelopmentId, "claude-code", @"C:\claude.exe", Worktree, Guid.CreateVersion7(), Guid.CreateVersion7(), "read", null, null, Now);
        session.MarkRunning(4242, Now);
        _sessions.Seed(session);
        _processes.Run(4242, Now);

        (await Prompt.CheckAsync(PromptRequest(session.Id, "oi"), Ct))!.Summary.Should().Be(
            HeadlessAgentPlanner.BusyMessage, "o processo ainda existe: é outro Claude nesta conversa");

        _processes.Exit(4242);
        _runner.Script(FakeHeadlessProvider.Done());
        var next = PromptRequest(session.Id, "Continue de onde parou");
        (await Prompt.CheckAsync(next, Ct)).Should().BeNull();
        var result = await Prompt.ExecuteAsync(Execution(next), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        session.RemoteOperationId.Should().Be(next.OperationId);
        session.Status.Should().Be(AgentSessionStatus.Exited);
        _claude.Requests.Single().Resume.Should().BeTrue();
    }

    /// <summary>Ao subir o agente, a conversa presa volta a receber prompts; a que nem abriu o Claude falha; a viva fica.</summary>
    [Fact]
    public async Task OnStartup_ConversationsLeftRunning_AreClosed()
    {
        var midPrompt = Headless(Now);
        midPrompt.MarkRunning(4242, Now);
        var neverOpened = Headless(Now);
        var alive = Headless(Now);
        alive.MarkRunning(5151, Now);
        _processes.Run(5151, Now);
        _sessions.Seed(midPrompt);
        _sessions.Seed(neverOpened);
        _sessions.Seed(alive);

        await _services.GetRequiredService<HeadlessSessionRecovery>().RecoverAsync(Ct);

        midPrompt.Status.Should().Be(AgentSessionStatus.Exited);
        midPrompt.Activity.Should().Be(AgentActivity.Failed);
        midPrompt.ActivityMessage.Should().Be("O agente parou no meio do prompt.");
        midPrompt.CanContinue.Should().BeTrue();
        neverOpened.Status.Should().Be(AgentSessionStatus.Failed);
        alive.Status.Should().Be(AgentSessionStatus.Running);

        AgentSession Headless(DateTimeOffset at) => AgentSession.CreateHeadless(
            _task.Id, DevelopmentId, "claude-code", @"C:\claude.exe", Worktree, Guid.CreateVersion7(), Guid.CreateVersion7(), "read", null, null, at);
    }

    /// <summary>O Claude do card aberto no terminal do mesmo worktree: o prompt é recusado com o que fazer, e não vira erro de banco.</summary>
    [Fact]
    public async Task AClaudeOpenInTheTerminalOfTheWorktree_RefusesThePrompt()
    {
        _runner.Script(FakeHeadlessProvider.Done());
        await Start.ExecuteAsync(Execution(StartRequest("read", "Leia o README")), Ct);
        var session = _sessions.Sessions.Single();
        var terminal = AgentSession.Create(_task.Id, DevelopmentId, "claude-code", @"C:\claude.exe", Worktree, Now.AddMinutes(5));
        terminal.MarkRunning(5555, Now.AddMinutes(5));
        _sessions.Seed(terminal);
        _processes.Run(5555, Now.AddMinutes(5));

        (await Prompt.CheckAsync(PromptRequest(session.Id, "E agora?"), Ct))!.Summary.Should().Be(HeadlessAgentPlanner.TerminalOpenMessage);
        var refused = await FluentActions.Awaiting(() => Prompt.ExecuteAsync(Execution(PromptRequest(session.Id, "E agora?")), Ct))
            .Should().ThrowAsync<DomainException>();
        refused.WithMessage(HeadlessAgentPlanner.TerminalOpenMessage);
        session.Status.Should().Be(AgentSessionStatus.Exited, "a conversa não foi tocada");

        _processes.Exit(5555);
        _runner.Script(FakeHeadlessProvider.Done());
        (await Prompt.ExecuteAsync(Execution(PromptRequest(session.Id, "E agora?")), Ct)).Succeeded.Should().BeTrue("o terminal fechou");
    }

    private RemoteOperationRequest StartRequest(string profile, string text, int? maxTurns = null, string? model = null) =>
        Request(RemoteOperationKinds.AgentStart, new ClaudeCodeSessionRequest(DevelopmentId, profile, model, null, new PromptRequest(null, text, maxTurns)));

    private RemoteOperationRequest PromptRequest(Guid sessionId, string text) =>
        Request(RemoteOperationKinds.AgentPrompt, new PromptRequest(sessionId, text));

    private RemoteOperationRequest StopRequest(Guid sessionId) =>
        Request(RemoteOperationKinds.AgentStop, new AgentStopRequest(sessionId));

    private RemoteOperationRequest Request<T>(string kind, T payload) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        kind,
        JsonSerializer.SerializeToElement(payload, RemoteJson.Options),
        _task.Id,
        [],
        Now,
        Now.AddMinutes(10));

    private RemoteExecution Execution(RemoteOperationRequest request, Func<RemoteApprovalAsk, RemoteApprovalResult>? approve = null) =>
        new(
            request,
            "Pixel 8",
            (item, _) =>
            {
                lock (_events)
                {
                    _events.Add(item);
                }

                return Task.CompletedTask;
            },
            _time,
            approve is null
                ? null
                : (ask, _) =>
                {
                    _asks.Add(ask);
                    return Task.FromResult(approve(ask));
                });
}
