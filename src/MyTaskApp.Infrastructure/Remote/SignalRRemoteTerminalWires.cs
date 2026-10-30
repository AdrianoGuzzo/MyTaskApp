using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

/// <summary>
/// As conexões do hub de quem segue um terminal de longe (ADR-099): uma por
/// vista, separada da dos avisos, para que fechar a vista seja o "saí" para o
/// agente sem derrubar as outras telas.
/// </summary>
internal sealed class SignalRRemoteTerminalWires(
    ISyncAuthenticator authenticator,
    ILogger<SignalRRemoteTerminalWires> logger,
    RemoteHubTransport? transport = null) : IRemoteTerminalWires
{
    public async Task<IRemoteTerminalWire> OpenAsync(CancellationToken cancellationToken)
    {
        if (authenticator.ServerBaseUrl is not { } server)
        {
            throw new DomainException("Conecte a sincronização para seguir o terminal de outra máquina.");
        }

        var connection = RemoteHubConnection.Build(new Uri(server, RemoteRoutes.Hub.TrimStart('/')), authenticator, transport);
        var wire = new Wire(connection, logger);

        try
        {
            await connection.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await wire.DisposeAsync();
            logger.LogInformation("RemoteTerminalHubUnavailable {Reason}", exception.GetType().Name);
            throw new DomainException("Não foi possível ligar ao servidor para seguir o terminal.");
        }

        return wire;
    }

    private sealed class Wire : IRemoteTerminalWire
    {
        private readonly HubConnection _connection;

        private readonly ILogger _logger;

        public Wire(HubConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
            connection.On<RemoteTerminalFrame>(RemoteHubMessages.TerminalFrame, frame => FrameReceived?.Invoke(frame));
            connection.On<Guid>(RemoteHubMessages.TerminalGap, attachId => GapReceived?.Invoke(attachId));
            connection.On<Guid, string>(RemoteHubMessages.TerminalClosed, (attachId, reason) => ClosedReceived?.Invoke(attachId, reason));

            // A assinatura é da conexão: religada, ela é outra, e a vista conecta de novo com outro attach.
            connection.Reconnecting += _ =>
            {
                Dropped?.Invoke();
                return Task.CompletedTask;
            };

            connection.Closed += _ =>
            {
                Dropped?.Invoke();
                return Task.CompletedTask;
            };
        }

        public event Action<RemoteTerminalFrame>? FrameReceived;

        public event Action<Guid>? GapReceived;

        public event Action<Guid, string>? ClosedReceived;

        public event Action? Dropped;

        public Task SubscribeAsync(Guid attachId, CancellationToken cancellationToken) =>
            Call(() => _connection.InvokeAsync(RemoteHubMethods.TerminalSubscribe, attachId, cancellationToken));

        public Task SendAsync(Guid attachId, SignedEnvelope input, CancellationToken cancellationToken) =>
            Call(() => _connection.InvokeAsync(RemoteHubMethods.TerminalSend, attachId, input, cancellationToken));

        public async ValueTask DisposeAsync()
        {
            FrameReceived = null;
            GapReceived = null;
            ClosedReceived = null;
            Dropped = null;

            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "RemoteTerminalHubDisposeFailed");
            }
        }

        /// <summary>A recusa do servidor chega como <see cref="HubException"/>, com a frase que a pessoa lê.</summary>
        private static async Task Call(Func<Task> call)
        {
            try
            {
                await call();
            }
            catch (HubException exception)
            {
                throw new DomainException(RemoteHubErrors.Text(exception.Message));
            }
        }
    }
}

/// <summary>A frase de uma <see cref="HubException"/>, sem o prefixo que o SignalR põe.</summary>
internal static class RemoteHubErrors
{
    private const string Prefix = "An unexpected error occurred invoking";

    public static string Text(string message)
    {
        // "HubException: <frase>" quando o servidor lançou de propósito.
        var start = message.IndexOf("HubException: ", StringComparison.Ordinal);

        if (start >= 0)
        {
            return message[(start + "HubException: ".Length)..];
        }

        return message.StartsWith(Prefix, StringComparison.Ordinal)
            ? "O servidor recusou o pedido do terminal."
            : message;
    }
}
