using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Infrastructure.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// As conversas do Claude de ponta a ponta (ADR-091), de computador para
/// computador: o notebook (o agente) relata a conversa sem terminal dele, e
/// outro desktop da conta lista e lê a linha do tempo pelos mesmos handlers do
/// celular, contra o servidor e o PostgreSQL de verdade. O título vem da tarefa
/// sincronizada no desktop que lê; o servidor só guarda ids e estado.
/// </summary>
public sealed partial class RemoteOperationsEndToEndTests
{
    [Fact]
    public async Task AnotherDesktop_ListsTheConversationOfTheNotebook_AndReadsItsTimeline()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook);
        var conversation = await HeadlessConversationAsync(notebook, taskId, developmentId);

        // Um prompt na conversa, pedido do celular; quem responde é o agente (aqui, o teste no lugar do Claude).
        var prompt = await RequestAsync(
            notebook, phone, phoneKey, RemoteOperationKinds.AgentPrompt,
            JsonSerializer.SerializeToElement(new PromptRequest(conversation, "E os testes?"), RemoteJson.Options), taskId);
        await AnswerAsync(notebook, prompt.OperationId, conversation, "E os testes?", "Passaram os 12.");

        await notebook.Services.GetRequiredService<RemoteSessionPublisher>().PublishAsync(Ct);
        await notebook.SyncedAsync(Ct);

        var office = await SyncedDesktop.CreateAsync(_api, "Escritório", Ct);
        _desktops.Add(office);
        await office.UseCaseAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
            (handler, cancel) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: false), cancel));
        await office.ConnectAsync("ana", Ct);
        await office.SyncedAsync(Ct);

        var list = await office.UseCaseAsync<ListRemoteSessionsHandler, RemoteSessionList>(
            (handler, cancel) => handler.HandleAsync(new ListRemoteSessions(notebook.Device.DeviceId), cancel));

        list.Unavailable.Should().BeNull();
        list.AgentOf(notebook.Device.DeviceId).Should().Be("agente ligado");
        var item = list.Items.Should().ContainSingle().Subject;
        (item.Title, item.Branch, item.State).Should().Be(("Validar antimicrobianos", "feature/validacao", "Esperando o próximo prompt"));
        item.CanPrompt.Should().BeTrue();

        var timeline = await office.UseCaseAsync<GetRemoteSessionTimelineHandler, RemoteSessionTimeline>(
            (handler, cancel) => handler.HandleAsync(new GetRemoteSessionTimeline(conversation, RemoteSessionCursor.Start), cancel));

        timeline.Operations.Select(operation => operation.OperationId).Should().Equal(prompt.OperationId);
        timeline.Lines.Should().Equal("Você: E os testes?", "Claude: Passaram os 12.", "— Respondeu.");
        timeline.IsLive.Should().BeFalse();
    }

    /// <summary>
    /// O outro desktop ouve o hub (ADR-094): a saída do prompt e a mudança da
    /// conversa chegam como aviso, pelo mesmo cliente do agente, e a tela lê
    /// pelo REST do cursor dela.
    /// </summary>
    [Fact]
    public async Task AnotherDesktop_HearsTheOutputAndTheConversationChange_ThroughTheHub()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook);
        var conversation = await HeadlessConversationAsync(notebook, taskId, developmentId);

        var office = await SyncedDesktop.CreateAsync(_api, "Escritório", Ct);
        _desktops.Add(office);
        await office.UseCaseAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
            (handler, cancel) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: false), cancel));
        await office.ConnectAsync("ana", Ct);

        var notices = new System.Collections.Concurrent.ConcurrentQueue<RemoteNotice>();
        await using var watch = new SignalRRemoteWatch(
            office.Services.GetRequiredService<ISyncAuthenticator>(),
            NullLogger<SignalRRemoteWatch>.Instance,
            new RemoteHubTransport(() => _api.Server.CreateHandler(), HttpTransportType.LongPolling));
        watch.Noticed += notices.Enqueue;
        await watch.StartAsync(Ct);
        watch.IsConnected.Should().BeTrue();

        var prompt = await RequestAsync(
            notebook, phone, phoneKey, RemoteOperationKinds.AgentPrompt,
            JsonSerializer.SerializeToElement(new PromptRequest(conversation, "E os testes?"), RemoteJson.Options), taskId);
        await AnswerAsync(notebook, prompt.OperationId, conversation, "E os testes?", "Passaram os 12.");
        await notebook.Services.GetRequiredService<RemoteSessionPublisher>().PublishAsync(Ct);

        await WaitForAsync(() => notices.Contains(new RemoteNotice(RemoteNoticeKind.Session, conversation)));
        notices.Should().Contain(new RemoteNotice(RemoteNoticeKind.Events, prompt.OperationId));
        notices.Should().Contain(new RemoteNotice(RemoteNoticeKind.Operation, prompt.OperationId));

        await watch.StopAsync(Ct);
        watch.IsConnected.Should().BeFalse("sem tela aberta, a conexão cai");
    }

    /// <summary>
    /// A sessão gerenciada (ADR-092), do computador para o celular: o desktop
    /// do notebook pede ao próprio agente, que admite pela chave da máquina;
    /// o celular vê a conversa e manda o próximo prompt, que o agente também
    /// admite.
    /// </summary>
    [Fact]
    public async Task AConversationStartedOnTheDesktop_IsContinuedFromThePhone()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook);
        var conversation = await HeadlessConversationAsync(notebook, taskId, developmentId);

        var mine = await notebook.UseCaseAsync<SendRemoteOperationHandler, RemoteOperationView>(
            (handler, cancel) => handler.HandleAsync(
                new SendRemoteOperation(
                    notebook.Device.DeviceId,
                    RemoteOperationKinds.AgentPrompt,
                    JsonSerializer.SerializeToElement(new PromptRequest(conversation, "Rode os testes."), RemoteJson.Options),
                    taskId),
                cancel));

        var admitted = (await AdmitAsync(notebook, mine.OperationId)).Should().BeOfType<RemoteAdmission.Admitted>().Subject;
        admitted.Entry.ControllerNickname.Should().Be(RemoteTrust.OwnNickname);
        await AnswerAsync(notebook, mine.OperationId, conversation, "Rode os testes.", "Passaram os 12.");
        await notebook.Services.GetRequiredService<RemoteSessionPublisher>().PublishAsync(Ct);

        // O celular: a conversa da máquina, com o prompt que o desktop mandou.
        var list = await SendAsync<RemoteAgentSessionList>(phone, HttpMethod.Get, RemoteRoutes.SessionsQuery(notebook.Device.DeviceId, false, null), null);
        list.Sessions.Should().ContainSingle(session => session.SessionId == conversation);
        var detail = await SendAsync<RemoteAgentSessionDetail>(phone, HttpMethod.Get, RemoteRoutes.Session(conversation), null);
        detail.Operations.Select(operation => operation.OperationId).Should().Equal(mine.OperationId);

        var next = await RequestAsync(
            notebook, phone, phoneKey, RemoteOperationKinds.AgentPrompt,
            JsonSerializer.SerializeToElement(new PromptRequest(conversation, "E o build?"), RemoteJson.Options), taskId);

        (await AdmitAsync(notebook, next.OperationId)).Should().BeOfType<RemoteAdmission.Admitted>();
    }

    /// <summary>O agente do notebook confere o pedido que está na fila dele, como faz a cada busca.</summary>
    private static async Task<RemoteAdmission> AdmitAsync(SyncedDesktop notebook, Guid operationId)
    {
        var queued = await notebook.Services.GetRequiredService<IRemoteOperationsApi>().ListAgentOperationsAsync(Ct);
        var envelope = queued.Single(operation => RemoteJws.TryParse(operation.Request, out var parts)
            && JsonSerializer.Deserialize<RemoteOperationRequest>(parts.Payload, RemoteJson.Options)!.OperationId == operationId).Request;

        return await notebook.UseCaseAsync<AdmitRemoteOperationHandler, RemoteAdmission>(
            (handler, cancel) => handler.HandleAsync(new AdmitRemoteOperation(envelope, 0), cancel));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (!condition())
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "o aviso não chegou");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Uma conversa sem terminal do notebook, esperando o próximo prompt.</summary>
    private static async Task<Guid> HeadlessConversationAsync(SyncedDesktop notebook, Guid taskId, Guid developmentId)
    {
        var now = notebook.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var session = AgentSession.CreateHeadless(
            taskId, developmentId, "claude-code", "claude", notebook.Folder, Guid.CreateVersion7(), Guid.CreateVersion7(), "read", null, null, now);
        session.MarkRunning(4242, now);
        session.EndHeadlessPrompt(AgentActivity.WaitingReview, null, now);

        return await notebook.RunAsync(async services =>
        {
            await services.GetRequiredService<IAgentSessionRepository>().AddAsync(session, Ct);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
            return session.Id;
        });
    }

    /// <summary>O agente pega o prompt e conta, como o executor do Claude: o prompt no primeiro evento e a resposta.</summary>
    private static async Task AnswerAsync(SyncedDesktop notebook, Guid operationId, Guid conversation, string prompt, string answer)
    {
        var api = notebook.Services.GetRequiredService<IRemoteOperationsApi>();
        await api.ListAgentOperationsAsync(Ct);
        await api.UpdateStatusAsync(operationId, new OperationStatusUpdate(RemoteOperationStatus.Accepted), Ct);
        await api.UpdateStatusAsync(operationId, new OperationStatusUpdate(RemoteOperationStatus.Running), Ct);
        var at = DateTimeOffset.UtcNow;
        await api.AppendEventsAsync(
            new EventBatch(
                operationId,
                [
                    new RemoteOperationEvent(operationId, 1, at, RemoteEventKind.StepStarted, "agent", Text: "Claude em feature/validacao · perfil Ler.",
                        Data: JsonSerializer.SerializeToElement(new { agentSessionId = conversation, canContinue = true, prompt }, RemoteJson.Options)),
                    new RemoteOperationEvent(operationId, 2, at, RemoteEventKind.AgentMessage, "agent", Text: answer),
                ]),
            Ct);
        await api.UpdateStatusAsync(operationId, new OperationStatusUpdate(RemoteOperationStatus.Succeeded, null, "Respondeu."), Ct);
    }
}
