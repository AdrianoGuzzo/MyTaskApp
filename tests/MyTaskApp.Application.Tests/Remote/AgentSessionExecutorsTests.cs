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

    [Fact]
    public async Task AllowingForThisSession_IsNotAskedAgainInTheConversation()
    {
        var decisions = new List<ToolPermissionDecision>();
        _runner.Script(
            async () => decisions.Add(await _gateway.AskAsync("Bash", """{"command":"git push"}""")),
            async () => decisions.Add(await _gateway.AskAsync("Bash", """{"command":"git push --tags"}""")),
            FakeHeadlessProvider.Done());

        await Start.ExecuteAsync(
            Execution(StartRequest("edit", "Publique"), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Allowed, "adria", RememberForThisSession: true)),
            Ct);

        decisions.Should().AllSatisfy(item => item.IsAllowed.Should().BeTrue());
        _asks.Should().ContainSingle();
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
