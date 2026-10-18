using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.OAuth;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// Por onde o login da sincronização volta ao app, e com qual cliente OAuth
/// (ADR-061, ADR-067). O desktop volta pelo loopback; o celular, pelo esquema
/// próprio do app, que o sistema entrega ao <see cref="AppLinkAuthorizationRedirect"/>.
/// </summary>
/// <remarks>
/// O resto do login — PKCE, troca do código, cofre do refresh, renovação — é o
/// mesmo nos dois, em <see cref="SyncAuthenticationService"/>.
/// </remarks>
public interface ISyncAuthorizationRedirect
{
    /// <summary>O cliente OAuth público deste app; o servidor só aceita a volta registrada para ele.</summary>
    string ClientId { get; }

    /// <summary>A volta registrada no servidor. No loopback, sem porta: a de cada login é a da sessão.</summary>
    Uri RedirectUri { get; }

    /// <summary>Começa a esperar uma volta. Abrir de novo desiste da anterior.</summary>
    ISyncAuthorizationSession Open();
}

/// <summary>Uma espera pela volta do login.</summary>
public interface ISyncAuthorizationSession : IAsyncDisposable
{
    /// <summary>O <c>redirect_uri</c> deste login, no pedido e na troca do código.</summary>
    Uri RedirectUri { get; }

    /// <summary>
    /// O código da primeira volta com o <paramref name="expectedState"/>. Recusa
    /// do usuário ou do servidor vem como <see cref="DomainException"/>.
    /// </summary>
    Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken);

    /// <summary>Para de esperar na hora: um login novo tomou o lugar deste.</summary>
    void Close();
}

/// <summary>
/// A volta pelo loopback do desktop: o <see cref="LoopbackCallbackListener"/> de
/// sempre, numa porta efêmera por login (ADR-074), a menos que
/// <see cref="SyncOptions.CallbackPort"/> fixe uma.
/// </summary>
internal sealed class LoopbackSyncAuthorizationRedirect(SyncOptions options, ILogger logger) : ISyncAuthorizationRedirect
{
    public string ClientId => SyncSchema.DesktopClientId;

    public Uri RedirectUri { get; } = new("http://127.0.0.1/callback");

    public ISyncAuthorizationSession Open() =>
        new Session(LoopbackCallbackListener.Start(options.CallbackPort, SyncAuthenticationService.CallbackMessages, logger));

    private sealed class Session(LoopbackCallbackListener listener) : ISyncAuthorizationSession
    {
        public Uri RedirectUri { get; } = new($"http://127.0.0.1:{listener.Port}/callback");

        public Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken) =>
            listener.WaitForCodeAsync(expectedState, cancellationToken);

        public void Close() => listener.StopListening();

        public ValueTask DisposeAsync() => listener.DisposeAsync();
    }
}

/// <summary>
/// A volta pelo esquema próprio do app (ADR-067). Quem recebe o endereço é o
/// sistema do celular — no MAUI, o <c>WebAuthenticator</c> —, e a casca do app
/// entrega os parâmetros aqui com <see cref="Deliver"/>. Nada daqui depende de
/// MAUI: o mesmo tipo serve aos testes.
/// </summary>
/// <remarks>
/// Quase as regras do loopback: uma volta com outro <c>state</c> não é a
/// autorização que este app pediu — mas aqui é recusada em vez de ignorada,
/// porque o navegador do sistema só devolve uma volta por login. Sem código, a
/// recusa vem com o código OAuth (o <c>device_revoked</c> da sincronização).
/// </remarks>
public sealed class AppLinkAuthorizationRedirect(string clientId, Uri redirectUri) : ISyncAuthorizationRedirect
{
    private readonly Lock _gate = new();

    private Session? _current;

    public string ClientId { get; } = clientId;

    public Uri RedirectUri { get; } = redirectUri;

    public ISyncAuthorizationSession Open()
    {
        var session = new Session(this);
        Session? previous;

        lock (_gate)
        {
            previous = _current;
            _current = session;
        }

        previous?.Close();
        return session;
    }

    /// <summary>
    /// A volta chegou, com os parâmetros da query. Sem login esperando, não faz
    /// nada: um endereço velho aberto de novo não vale como login.
    /// </summary>
    public void Deliver(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        CurrentSession()?.Arrive(parameters);
    }

    /// <summary>A pessoa fechou o navegador sem terminar o login.</summary>
    public void Abandon() =>
        CurrentSession()?.Fail(new DomainException("O login foi cancelado. Tente de novo quando quiser."));

    private Session? CurrentSession()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    private void Forget(Session session)
    {
        lock (_gate)
        {
            if (_current == session)
            {
                _current = null;
            }
        }
    }

    private sealed class Session(AppLinkAuthorizationRedirect owner) : ISyncAuthorizationSession
    {
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> _arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri RedirectUri => owner.RedirectUri;

        public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
        {
            var parameters = await _arrived.Task.WaitAsync(cancellationToken);

            if (!string.Equals(parameters.GetValueOrDefault("state"), expectedState, StringComparison.Ordinal))
            {
                throw new DomainException("A volta do login não é a deste aparelho. Tente de novo.");
            }

            if (parameters.TryGetValue("code", out var code) && code.Length > 0)
            {
                return code;
            }

            var error = parameters.GetValueOrDefault("error");

            throw new AuthorizationRefusedException(
                error,
                error == "access_denied"
                    ? SyncAuthenticationService.CallbackMessages.Denied
                    : SyncAuthenticationService.CallbackMessages.Failed);
        }

        public void Arrive(IReadOnlyDictionary<string, string> parameters) => _arrived.TrySetResult(parameters);

        public void Fail(Exception exception) => _arrived.TrySetException(exception);

        public void Close() => _arrived.TrySetCanceled();

        public ValueTask DisposeAsync()
        {
            owner.Forget(this);
            _arrived.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }
}
