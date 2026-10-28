using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>
/// Manda um pedido daqui e abre a tela da operação: assina (com o
/// destravamento), põe na fila, lembra na lista "Pedidos" e sobe o sync, para a
/// tarefa do pedido chegar lá antes de a outra máquina começar. Da tela de
/// uma conversa, o pedido fica nela (<c>open: false</c>): a linha do tempo já
/// mostra a operação nova.
/// </summary>
public sealed class RemoteSender(
    IUseCaseRunner runner,
    ISyncAccount account,
    SentOperations sent,
    INavigator navigator,
    TimeProvider timeProvider)
{
    public async Task<RemoteOperationView> SendAsync(SendRemoteOperation request, string title, bool open = true)
    {
        account.SyncNow();

        var operation = await runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
            (handler, token) => handler.HandleAsync(request, token));

        sent.Add(new SentOperation(operation.OperationId, request.TargetDeviceId, title, timeProvider.GetUtcNow()));
        if (open)
        {
            await navigator.OpenOperationAsync(operation.OperationId, request.TargetDeviceId);
        }

        return operation;
    }
}
