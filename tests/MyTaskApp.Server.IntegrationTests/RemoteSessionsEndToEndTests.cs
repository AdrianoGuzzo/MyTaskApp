using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Domain.Agents;
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
