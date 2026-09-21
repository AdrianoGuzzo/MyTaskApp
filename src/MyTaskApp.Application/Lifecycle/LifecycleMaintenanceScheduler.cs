using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Configuration;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// Acorda de tempos em tempos e roda a manutenção do ciclo de vida: arquiva o
/// que venceu e esvazia a lixeira vencida (§6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reusa o mecanismo que o app já tem</b> (§13.3): o mesmo
/// <c>TimeProvider.CreateTimer</c> do ADR-015, o mesmo <c>IUseCaseRunner</c>
/// para abrir escopo por operação (ADR-012) e os mesmos ganchos de início e
/// desligamento na <c>App</c>. Nada de Generic Host, pelas razões já registradas.
/// </para>
/// <para>
/// <b>Por que é uma classe separada do <c>ReminderScheduler</c>, e não um
/// segundo passo do tique dele:</b> as cadências são de ordens de grandeza
/// diferentes — lembrete é de 30 s, manutenção é de horas — e pendurar uma
/// varredura de banco no laço quente dos lembretes a faria rodar 720 vezes por
/// hora para não achar nada. O custo aceito é que a mecânica do timer aparece
/// duas vezes no código; extrair uma base comum é o próximo passo natural, e
/// fica para quando houver um terceiro laço, não antes.
/// </para>
/// <para>
/// <b>Primeiro tique imediato</b>, como no ADR-015: é ele que põe em dia o que
/// venceu enquanto o app estava fechado — um app de desktop passa mais tempo
/// desligado do que ligado, e sem isso a lixeira de quem abre o app uma vez por
/// semana nunca seria esvaziada.
/// </para>
/// </remarks>
public sealed class LifecycleMaintenanceScheduler(
    IUseCaseRunner runner,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options,
    ILogger<LifecycleMaintenanceScheduler> logger) : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stopping = new();

    // Espera zero: um tique já em andamento faz o próximo ser pulado, não
    // enfileirado. Dois lotes concorrentes disputariam as mesmas linhas.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Tiques concluídos, para os testes saberem que o timer andou.</summary>
    public int CompletedTicks { get; private set; }

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        var period = options.Value.ToLifecycleSweepPeriod();

        _timer = timeProvider.CreateTimer(
            _ => _ = RunTickAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: period);

        logger.LogInformation("LifecycleSchedulerStarted {Period}", period);
    }

    /// <summary>Público para que os testes rodem um tique sem depender do timer.</summary>
    public async Task<LifecycleMaintenanceResult> TickAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            logger.LogDebug("LifecycleTickSkippedBecauseOneWasStillRunning");
            return LifecycleMaintenanceResult.Nothing;
        }

        try
        {
            return await runner
                .RunAsync<RunLifecycleMaintenanceHandler, LifecycleMaintenanceResult>(
                    (handler, token) => handler.HandleAsync(new RunLifecycleMaintenance(), token),
                    cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _stopping.CancelAsync();

        if (_timer is not null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }

        if (await _gate.WaitAsync(ShutdownGrace, CancellationToken.None))
        {
            _gate.Release();
        }

        Cleanup();
    }

    /// <summary>
    /// Também síncrono, pela mesma razão do <c>ReminderScheduler</c>: um
    /// singleton que só fosse <see cref="IAsyncDisposable"/> faria o
    /// <c>Dispose()</c> do contêiner lançar, e o composition root o descarta com
    /// um <c>using</c> comum no fim do <c>Main</c>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopping.Cancel();
        _timer?.Dispose();
        _timer = null;

        if (_gate.Wait(ShutdownGrace))
        {
            _gate.Release();
        }

        Cleanup();
    }

    private void Cleanup()
    {
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Um tique que lança nunca pode matar o timer — aqui o silêncio custaria
    /// caro: a lixeira pararia de ser esvaziada sem ninguém perceber.
    /// </summary>
    private async Task RunTickAsync()
    {
        try
        {
            await TickAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Desligando: normal.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "LifecycleTickFailed");
        }
        finally
        {
            CompletedTicks++;
        }
    }
}
