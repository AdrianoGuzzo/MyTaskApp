namespace MyTaskApp.Application.Remote;

/// <summary>O que mudou, segundo o hub (ADR-094). O aviso não traz dado: quem lê é o REST.</summary>
public enum RemoteNoticeKind
{
    /// <summary>A operação mudou de estado; o id é o da operação.</summary>
    Operation,

    /// <summary>Chegaram eventos da operação; o id é o da operação.</summary>
    Events,

    /// <summary>Uma aprovação foi pedida, respondida ou venceu; o id é o da operação dela.</summary>
    Approval,

    /// <summary>Uma conversa do Claude mudou (ADR-091); o id é o da conversa.</summary>
    Session,

    /// <summary>A conexão voltou: os avisos do intervalo se perderam, e tudo é relido.</summary>
    Reconnected,
}

public sealed record RemoteNotice(RemoteNoticeKind Kind, Guid Id);

/// <summary>
/// Os avisos da conta para quem controla (ADR-094): o desktop e o celular,
/// enquanto uma tela remota está aberta. Cada tela liga com
/// <see cref="StartAsync"/> e desliga com <see cref="StopAsync"/>; a conexão
/// fica de pé enquanto houver uma.
/// </summary>
public interface IRemoteWatch
{
    /// <summary>Pode vir de fora da thread da tela.</summary>
    event Action<RemoteNotice>? Noticed;

    bool IsConnected { get; }

    /// <summary>Conecta, se ainda não; nunca lança: sem hub, as telas leem por tempo.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Sem hub (testes, o agente): as telas só leem por tempo.</summary>
public sealed class NoRemoteWatch : IRemoteWatch
{
    public event Action<RemoteNotice>? Noticed
    {
        add { }
        remove { }
    }

    public bool IsConnected => false;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// O ritmo de leitura de uma tela remota (ADR-094). Com o hub ligado, o aviso
/// que interessa à tela acorda a leitura na hora, e a leitura por tempo vira
/// reserva, a cada <see cref="LiveFallback"/>. Sem o hub, a tela lê no
/// intervalo dela, como antes. O aviso só diz "leia": a leitura parte do
/// cursor da tela, então um aviso perdido não perde dado.
/// </summary>
public sealed class RemotePace(IRemoteWatch watch, TimeProvider timeProvider, Func<RemoteNotice, bool>? relevant = null) : IAsyncDisposable
{
    /// <summary>Com o hub ligado, a leitura por tempo só cobre o aviso que se perdeu.</summary>
    public static readonly TimeSpan LiveFallback = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlySet<RemoteNoticeKind> None = new HashSet<RemoteNoticeKind>();

    private readonly Lock _gate = new();

    private TaskCompletionSource _noticed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HashSet<RemoteNoticeKind> _pending = [];

    private bool _started;

    /// <summary>O hub está ligado: a leitura por tempo pode ir devagar.</summary>
    public bool IsListening => watch.IsConnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        watch.Noticed += OnNoticed;
        await watch.StartAsync(cancellationToken);
    }

    /// <summary>Os tipos de aviso chegados desde a última leitura (vazio: nenhum). Responder os esquece.</summary>
    public IReadOnlySet<RemoteNoticeKind> TakeNotices()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return None;
            }

            var taken = _pending;
            _pending = [];
            _noticed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return taken;
        }
    }

    /// <summary>
    /// Espera a próxima leitura: o aviso que interessa, ou o tempo
    /// (<paramref name="interval"/> sem hub; <see cref="LiveFallback"/> com ele).
    /// </summary>
    public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        Task noticed;

        lock (_gate)
        {
            noticed = _noticed.Task;
        }

        if (!noticed.IsCompleted)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(IsListening && interval < LiveFallback ? LiveFallback : interval, timeProvider, timer.Token);

            await Task.WhenAny(noticed, delay);
            await timer.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        TakeNotices();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        watch.Noticed -= OnNoticed;
        await watch.StopAsync(CancellationToken.None);
    }

    private void OnNoticed(RemoteNotice notice)
    {
        if (notice.Kind != RemoteNoticeKind.Reconnected && relevant is not null && !relevant(notice))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add(notice.Kind);
            _noticed.TrySetResult();
        }
    }
}
