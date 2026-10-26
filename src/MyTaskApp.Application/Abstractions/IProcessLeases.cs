namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Quem, entre os processos deste computador, faz um trabalho que não pode
/// rodar em dois ao mesmo tempo (ADR-085): o desktop e o agente abrem o mesmo
/// SQLite. O dono é o processo (PID e início), e não a thread nem o escopo:
/// dentro de um processo, quem coordena são as travas de sempre.
/// </summary>
/// <remarks>
/// Um lease vencido, ou de um processo que não existe mais, passa para quem
/// pedir. Assim um processo que morreu sem soltar não trava ninguém além do
/// tempo de conferir que ele morreu.
/// </remarks>
public interface IProcessLeases
{
    /// <summary>
    /// Toma o lease, ou renova, se já for deste processo, até <paramref name="duration"/>
    /// a partir de agora. <c>false</c>: outro processo vivo o tem.
    /// </summary>
    Task<bool> TryAcquireAsync(string name, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Solta o lease, se for deste processo. Sem efeito se não for.</summary>
    Task ReleaseAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Os leases que existem. Novo nome, novo laço com dono só.</summary>
public static class ProcessLeaseNames
{
    /// <summary>O ciclo da sincronização: fica com quem o pegou primeiro, enquanto ele sincronizar.</summary>
    public const string Sync = "sync";

    /// <summary>A renovação do token da sincronização: o refresh gira a cada uso, e dois girando revogam a cadeia.</summary>
    public const string SyncToken = "sync-token";

    /// <summary>Uma operação de banco por vez no computador, não só no processo (ADR-056).</summary>
    public const string DatabaseOperation = "database-operation";
}

/// <summary>
/// Um lease tomado, renovado sozinho a cada terço da validade enquanto o
/// trabalho dura. Se outro processo o tomar no meio (este ficou congelado além
/// da validade), <see cref="Lost"/> cancela, e o trabalho para.
/// </summary>
public sealed class ProcessLeaseHold : IAsyncDisposable
{
    /// <summary>De quanto em quanto tempo <see cref="TakeAsync"/> pergunta de novo.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProcessLeases _leases;
    private readonly string _name;
    private readonly TimeSpan _duration;
    private readonly bool _releaseOnDispose;
    private readonly CancellationTokenSource _lost = new();
    private readonly SemaphoreSlim _renewing = new(1, 1);
    private readonly ITimer _timer;

    private ProcessLeaseHold(IProcessLeases leases, string name, TimeSpan duration, bool releaseOnDispose, TimeProvider timeProvider)
    {
        _leases = leases;
        _name = name;
        _duration = duration;
        _releaseOnDispose = releaseOnDispose;

        var period = duration / 3;
        _timer = timeProvider.CreateTimer(_ => _ = RenewAsync(), state: null, dueTime: period, period: period);
    }

    /// <summary>Cancela quando outro processo tomou o lease.</summary>
    public CancellationToken Lost => _lost.Token;

    /// <summary>
    /// Toma o lease agora, ou devolve <c>null</c> se outro processo vivo o tem.
    /// <paramref name="releaseOnDispose"/> falso deixa o lease com este processo
    /// até vencer: quem o tinha tem a preferência na próxima vez.
    /// </summary>
    public static async Task<ProcessLeaseHold?> TryTakeAsync(
        IProcessLeases leases,
        string name,
        TimeSpan duration,
        TimeProvider timeProvider,
        bool releaseOnDispose,
        CancellationToken cancellationToken)
    {
        return await leases.TryAcquireAsync(name, duration, cancellationToken)
            ? new ProcessLeaseHold(leases, name, duration, releaseOnDispose, timeProvider)
            : null;
    }

    /// <summary>
    /// Espera o lease ficar livre, até <paramref name="wait"/>, e o solta ao
    /// descartar. Para trabalho curto que todos os processos fazem, um de cada vez.
    /// </summary>
    /// <exception cref="TimeoutException">Outro processo o segurou o tempo todo.</exception>
    public static async Task<ProcessLeaseHold> TakeAsync(
        IProcessLeases leases,
        string name,
        TimeSpan duration,
        TimeSpan wait,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + wait;

        while (true)
        {
            if (await TryTakeAsync(leases, name, duration, timeProvider, releaseOnDispose: true, cancellationToken) is { } hold)
            {
                return hold;
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Outro processo deste computador segura \"{name}\" há mais de {wait.TotalSeconds:0} s.");
            }

            await Task.Delay(RetryInterval, timeProvider, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync();

        // Uma renovação em andamento termina antes de soltar: senão ela tomaria de novo o que acabou de ser solto.
        await _renewing.WaitAsync(CancellationToken.None);

        try
        {
            if (_releaseOnDispose && !_lost.IsCancellationRequested)
            {
                await _leases.ReleaseAsync(_name, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Não soltou (banco ocupado demais): vence sozinho, ou passa adiante quando este processo sair.
        }
        finally
        {
            _renewing.Release();
            _renewing.Dispose();
            _lost.Dispose();
        }
    }

    private async Task RenewAsync()
    {
        if (!await _renewing.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!_lost.IsCancellationRequested && !await _leases.TryAcquireAsync(_name, _duration, CancellationToken.None))
            {
                await _lost.CancelAsync();
            }
        }
        catch (Exception)
        {
            // Uma renovação que falhou não é perda: a próxima tenta, e só outro dono a faz falhar de verdade.
        }
        finally
        {
            _renewing.Release();
        }
    }
}
