using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// As conversas do Claude no servidor (ADR-091), dos dois lados: o agente
/// relata só o que mudou (e o lote vazio como sinal de vida), e as telas de
/// controle leem a lista e a linha do tempo pelos mesmos handlers, no desktop
/// e no celular.
/// </summary>
public sealed class RemoteSessionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-validacao";

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeSessionsApi _server = new();
    private readonly IRemoteOperationsApi _operations = Substitute.For<IRemoteOperationsApi>();
    private readonly TaskItem _task = TaskItem.Create("Implementar validação de antimicrobianos", Now);

    public RemoteSessionsTests()
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/validacao", Worktree, Now);
        _tasks.Seed(_task);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid DevelopmentId => _task.Developments[0].Id;

    [Fact]
    public async Task ThePublisher_ReportsOnlyWhatChanged_AndAnEmptyBatchIsTheHeartbeat()
    {
        var publisher = Publisher();
        var session = Headless();
        session.MarkRunning(4242, Now);
        _sessions.Seed(session);
        _sessions.Seed(Interactive());

        await publisher.PublishAsync(Ct);
        await publisher.PublishAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        session.EndHeadlessPrompt(AgentActivity.WaitingReview, null, _time.GetUtcNow());
        await publisher.PublishAsync(Ct);

        _server.Batches.Should().HaveCount(3);
        var first = _server.Batches[0].Sessions.Should().ContainSingle("a do terminal não sai daqui").Subject;
        (first.SessionId, first.Status, first.TaskId, first.DevelopmentId, first.ProfileId).Should().Be(
            (session.Id, RemoteAgentSessionStatus.Running, _task.Id, DevelopmentId, "edit"));
        _server.Batches[1].Sessions.Should().BeEmpty("nada mudou: só o sinal de vida");
        var second = _server.Batches[2].Sessions.Should().ContainSingle().Subject;
        second.Status.Should().Be(RemoteAgentSessionStatus.Idle);
        second.Version.Should().BeGreaterThan(first.Version);
    }

    /// <summary>Nenhum caminho, comando ou segredo sai no relato.</summary>
    [Fact]
    public async Task TheReport_CarriesNoPathCommandOrSecret()
    {
        var session = Headless();
        session.MarkRunning(4242, Now);
        session.RecordActivity(AgentActivity.WaitingForUser, "Aprovar Bash? password=hunter2", Now);
        _sessions.Seed(session);

        await Publisher().PublishAsync(Ct);

        var json = JsonSerializer.Serialize(_server.Batches.Single(), RemoteJson.Options);
        json.Should().NotContain("ecossistema-core").And.NotContain("claude.exe").And.NotContain("hunter2");
        _server.Batches.Single().Sessions.Single().ActivityMessage.Should().Contain("***");
    }

    /// <summary>O servidor tem versão maior (o relógio voltou): o próximo relato vai além dela.</summary>
    [Fact]
    public async Task WhenTheServerHasANewerVersion_TheNextReportJumpsAhead()
    {
        var publisher = Publisher();
        var session = Headless();
        _sessions.Seed(session);
        var ahead = Now.AddDays(1).UtcTicks;
        _server.Override = (id, _) => ahead;

        await publisher.PublishAsync(Ct);
        _server.Override = null;
        await publisher.PublishAsync(Ct);

        _server.Batches[1].Sessions.Single().Version.Should().BeGreaterThan(ahead, "não confirmada, ela vai de novo, maior");
    }

    [Fact]
    public async Task AnOldServer_IsLeftAloneForAnHour()
    {
        var publisher = Publisher();
        _sessions.Seed(Headless());
        _server.Failure = new SyncApiException(SyncFailureKind.NotFound, "Não encontrado.");

        await publisher.PublishAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        await publisher.PublishAsync(Ct);

        _server.Calls.Should().Be(1);
        _server.Failure = null;
        _time.Advance(RemoteSessionPublisher.OldServerBackoff);
        await publisher.PublishAsync(Ct);
        _server.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task TheList_NamesByTheSyncedTask_AndAnAgentWithoutNews_IsUnknownState()
    {
        var device = Guid.CreateVersion7();
        var known = View(Guid.CreateVersion7(), device, _task.Id, RemoteAgentSessionStatus.Running);
        var stranger = View(Guid.CreateVersion7(), device, Guid.CreateVersion7(), RemoteAgentSessionStatus.Idle);
        _server.List = new RemoteAgentSessionList([new RemoteAgentPresence(device, Now.AddMinutes(-10))], [known, stranger]);

        var list = await new ListRemoteSessionsHandler(_server, _tasks, _time).HandleAsync(new ListRemoteSessions(device), Ct);

        list.Unavailable.Should().BeNull();
        list.Items.Select(item => (item.Title, item.Branch)).Should().Equal(
            ("Implementar validação de antimicrobianos", "feature/validacao"),
            ("Tarefa que ainda não chegou aqui", null));
        list.Items[0].State.Should().StartWith("Sem notícias do agente desde").And.EndWith("o estado é desconhecido");
        list.Items[1].State.Should().Be("Esperando o próximo prompt");
        list.Items[1].CanPrompt.Should().BeTrue();
        list.Items[0].CanPrompt.Should().BeTrue("com o Claude respondendo, o próximo espera a vez (E6)");
        RemoteSessionTexts.CanPrompt(known with { Status = RemoteAgentSessionStatus.Stopped }).Should().BeFalse();
        RemoteSessionTexts.CanPrompt(known with { Status = RemoteAgentSessionStatus.Failed }).Should().BeFalse();
        _server.ListedDevice.Should().Be(device);
    }

    [Fact]
    public async Task AnOldServer_SaysToUpdateIt()
    {
        _server.Failure = new SyncApiException(SyncFailureKind.NotFound, "Não encontrado.");

        var list = await new ListRemoteSessionsHandler(_server, _tasks, _time).HandleAsync(new ListRemoteSessions(), Ct);

        list.Items.Should().BeEmpty();
        list.Unavailable.Should().Be(RemoteSessionTexts.ServerTooOld);
    }

    [Theory]
    [InlineData(RemoteAgentSessionStatus.Starting, RemoteAgentActivity.Working, "Abrindo o Claude")]
    [InlineData(RemoteAgentSessionStatus.Running, RemoteAgentActivity.Working, "Respondendo")]
    [InlineData(RemoteAgentSessionStatus.Running, RemoteAgentActivity.WaitingForUser, "Esperando aprovação")]
    [InlineData(RemoteAgentSessionStatus.Idle, RemoteAgentActivity.WaitingReview, "Esperando o próximo prompt")]
    [InlineData(RemoteAgentSessionStatus.Idle, RemoteAgentActivity.Failed, "O último prompt falhou; espera o próximo")]
    [InlineData(RemoteAgentSessionStatus.Stopped, RemoteAgentActivity.WaitingReview, "Parada")]
    [InlineData(RemoteAgentSessionStatus.Failed, RemoteAgentActivity.Failed, "Não abriu")]
    public void TheState_WithTheAgentOnline(RemoteAgentSessionStatus status, RemoteAgentActivity activity, string expected)
    {
        var view = View(Guid.CreateVersion7(), Guid.CreateVersion7(), _task.Id, status) with { Activity = activity };

        RemoteSessionTexts.State(view, new RemoteAgentPresence(view.DeviceId, Now), _time).Should().Be(expected);
        RemoteSessionTexts.State(view, new RemoteAgentPresence(view.DeviceId, null), _time).Should().Be(
            status is RemoteAgentSessionStatus.Starting or RemoteAgentSessionStatus.Running ? "Sem notícias do agente; o estado é desconhecido" : expected,
            "parada ou esperando, o último relato basta; rodando, não");
    }

    /// <summary>
    /// A linha do tempo atravessa os prompts em ordem, com "Você: …" de cada
    /// um, e a operação terminada só fecha quando não sobra página.
    /// </summary>
    [Fact]
    public async Task TheTimeline_CrossesThePrompts_AndAFinishedOneClosesOnlyWhenDrained()
    {
        var device = Guid.CreateVersion7();
        var session = View(Guid.CreateVersion7(), device, _task.Id, RemoteAgentSessionStatus.Running);
        var first = Operation(RemoteOperationStatus.Succeeded, 2, "Respondeu em 1 turno.");
        var second = Operation(RemoteOperationStatus.Succeeded, 3, "Respondeu em 2 turnos.");
        _server.Detail = new RemoteAgentSessionDetail(session, new RemoteAgentPresence(device, Now), [first, second]);
        Events(first.OperationId, hasMoreAfterFirstPage: false, Prompt(first.OperationId, 1, "Leia o README"), Message(first.OperationId, 2, "Li."));
        Events(second.OperationId, hasMoreAfterFirstPage: true, Prompt(second.OperationId, 1, "E os testes?"), Message(second.OperationId, 2, "Rodando."), Message(second.OperationId, 3, "Passaram."));
        var handler = new GetRemoteSessionTimelineHandler(_server, _operations, _tasks, _time);

        var timeline = await handler.HandleAsync(new GetRemoteSessionTimeline(session.SessionId, RemoteSessionCursor.Start), Ct);

        timeline.Lines.Should().Equal(
            "Você: Leia o README", "Claude: Li.", "— Respondeu em 1 turno.",
            "Você: E os testes?", "Claude: Rodando.", "Claude: Passaram.", "— Respondeu em 2 turnos.");
        timeline.IsLive.Should().BeFalse();
        timeline.Session.Title.Should().Be("Implementar validação de antimicrobianos");

        var again = await handler.HandleAsync(new GetRemoteSessionTimeline(session.SessionId, timeline.Cursor), Ct);
        again.Lines.Should().BeEmpty("o que já foi lido até o fim não é lido de novo");
    }

    /// <summary>
    /// Os cartões do celular (E7): um por prompt, texto, ferramenta, saída e
    /// aprovação, e a resposta da aprovação completa o cartão do pedido.
    /// </summary>
    [Fact]
    public async Task TheTimeline_AlsoComesAsCards_AndTheAnswerCompletesTheApprovalCard()
    {
        var device = Guid.CreateVersion7();
        var session = View(Guid.CreateVersion7(), device, _task.Id, RemoteAgentSessionStatus.Idle);
        var prompt = Operation(RemoteOperationStatus.Succeeded, 6, "Respondeu em 1 turno.");
        var approvalId = Guid.CreateVersion7().ToString("D");
        var at = Now;
        _server.Detail = new RemoteAgentSessionDetail(session, new RemoteAgentPresence(device, Now), [prompt]);
        Events(
            prompt.OperationId,
            hasMoreAfterFirstPage: false,
            Prompt(prompt.OperationId, 1, "Rode os testes"),
            new RemoteOperationEvent(prompt.OperationId, 2, at, RemoteEventKind.ToolUse, "agent", Text: "Bash: dotnet test",
                Data: JsonSerializer.SerializeToElement(new { tool = "Bash", toolUseId = "t1" })),
            new RemoteOperationEvent(prompt.OperationId, 3, at, RemoteEventKind.ApprovalRequested, Text: "O Claude quer usar Bash: dotnet test",
                Data: JsonSerializer.SerializeToElement(new { approvalId, value = "ToolUse" })),
            new RemoteOperationEvent(prompt.OperationId, 4, at, RemoteEventKind.ApprovalResolved, Text: "Permitido por Pixel 8.",
                Data: JsonSerializer.SerializeToElement(new { approvalId, value = "Allowed" })),
            new RemoteOperationEvent(prompt.OperationId, 5, at, RemoteEventKind.StepCompleted, "agent", Text: "Aprovados: 12.",
                Data: JsonSerializer.SerializeToElement(new { tool = "Bash", toolUseId = "t1" })),
            Message(prompt.OperationId, 6, "Passaram os 12."));

        var timeline = await new GetRemoteSessionTimelineHandler(_server, _operations, _tasks, _time)
            .HandleAsync(new GetRemoteSessionTimeline(session.SessionId, RemoteSessionCursor.Start), Ct);

        timeline.Entries.Should().Equal(
            new RemoteTimelineEntry(RemoteTimelineKind.Prompt, "Você", "Rode os testes"),
            new RemoteTimelineEntry(RemoteTimelineKind.Tool, "Usou Bash", "dotnet test"),
            new RemoteTimelineEntry(RemoteTimelineKind.Approval, "Pediu aprovação", "O Claude quer usar Bash: dotnet test", "Permitido por Pixel 8.", approvalId),
            new RemoteTimelineEntry(RemoteTimelineKind.Output, "Saída de Bash", "Aprovados: 12."),
            new RemoteTimelineEntry(RemoteTimelineKind.Claude, "Claude", "Passaram os 12."),
            new RemoteTimelineEntry(RemoteTimelineKind.Outcome, "Terminou bem", "Respondeu em 1 turno."));
        timeline.Lines.Should().NotContain(line => line.Contains("Aprovados", StringComparison.Ordinal), "a janela do desktop segue em linhas, como antes");
    }

    private RemoteSessionPublisher Publisher()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAgentSessionRepository>(_sessions)
            .BuildServiceProvider();

        return new RemoteSessionPublisher(
            _server,
            new ScopedUseCaseRunner(services.GetRequiredService<IServiceScopeFactory>()),
            _time,
            NullLogger<RemoteSessionPublisher>.Instance);
    }

    private AgentSession Headless() => AgentSession.CreateHeadless(
        _task.Id, DevelopmentId, "claude-code", @"C:\Tools\claude.exe", Worktree, Guid.CreateVersion7(), Guid.CreateVersion7(), "edit", null, null, Now);

    private AgentSession Interactive()
    {
        var session = AgentSession.Create(_task.Id, DevelopmentId, "claude-code", @"C:\Tools\claude.exe", Worktree, Now);
        session.MarkRunning(5151, Now);
        return session;
    }

    private RemoteAgentSessionView View(Guid sessionId, Guid deviceId, Guid taskId, RemoteAgentSessionStatus status) => new(
        sessionId, deviceId, 1, taskId, DevelopmentId, "edit", null, status, RemoteAgentActivity.Working, null, null, Now, null, Now, Now);

    private static RemoteOperationView Operation(RemoteOperationStatus status, long lastSeq, string summary) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), RemoteOperationKinds.AgentPrompt,
        null, status, Now, Now.AddMinutes(10), Now, null, null, summary, lastSeq);

    /// <summary>Uma página por evento quando <paramref name="hasMoreAfterFirstPage"/>: força a leitura em várias voltas.</summary>
    private void Events(Guid operationId, bool hasMoreAfterFirstPage, params RemoteOperationEvent[] events) =>
        _operations.ListEventsAsync(operationId, Arg.Any<long>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var after = call.ArgAt<long>(1);
                var newer = events.Where(item => item.Seq > after).ToList();
                var page = hasMoreAfterFirstPage ? newer.Take(1).ToList() : newer;
                return new RemoteEventPage(page, events[^1].Seq, newer.Count > page.Count);
            });

    private static RemoteOperationEvent Prompt(Guid operationId, long seq, string text) => new(
        operationId, seq, Now, RemoteEventKind.StepStarted, "agent", null, "Claude em feature/validacao · perfil Editar.",
        JsonSerializer.SerializeToElement(new { agentSessionId = Guid.CreateVersion7(), canContinue = true, prompt = text }));

    private static RemoteOperationEvent Message(Guid operationId, long seq, string text) => new(
        operationId, seq, Now, RemoteEventKind.AgentMessage, "agent", null, text, null);

    private sealed class FakeSessionsApi : IRemoteSessionsApi
    {
        public List<RemoteAgentSessionBatch> Batches { get; } = [];

        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        /// <summary>A versão que o servidor diz ter, no lugar da enviada.</summary>
        public Func<Guid, long, long>? Override { get; set; }

        public RemoteAgentSessionList List { get; set; } = new([], []);

        public RemoteAgentSessionDetail? Detail { get; set; }

        public Guid? ListedDevice { get; private set; }

        public Task<RemoteAgentSessionAck> ReportSessionsAsync(RemoteAgentSessionBatch batch, CancellationToken cancellationToken)
        {
            Calls++;

            if (Failure is not null)
            {
                return Task.FromException<RemoteAgentSessionAck>(Failure);
            }

            Batches.Add(batch);
            return Task.FromResult(new RemoteAgentSessionAck(
                [.. batch.Sessions.Select(report => new RemoteAgentSessionVersion(report.SessionId, Override?.Invoke(report.SessionId, report.Version) ?? report.Version))]));
        }

        public Task<RemoteAgentSessionList> ListSessionsAsync(Guid? deviceId, bool openOnly, int? limit, CancellationToken cancellationToken)
        {
            ListedDevice = deviceId;
            return Failure is not null ? Task.FromException<RemoteAgentSessionList>(Failure) : Task.FromResult(List);
        }

        public Task<RemoteAgentSessionDetail> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Detail ?? throw new InvalidOperationException("Sem conversa no fake."));
    }
}
