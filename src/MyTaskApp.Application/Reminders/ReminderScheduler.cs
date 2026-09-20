using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Configuration;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// O que faz o app "trabalhar ativamente": acorda de tempos em tempos e despacha
/// o que venceu.
/// </summary>
/// <remarks>
/// <para>
/// Sobre <c>TimeProvider.CreateTimer</c> e não <c>IHostedService</c> (ADR-015):
/// o <c>FakeTimeProvider</c> dirige este timer deterministicamente — um
/// <c>Advance</c> produz exatamente um tique, em processo. Um
/// <c>BackgroundService</c> espera internamente em <c>Task.Delay</c>, e
/// torná-lo testável exigiria reimplementar o laço sobre <c>TimeProvider</c> de
/// qualquer forma; além disso o app não tem Generic Host, e introduzir um só
/// para este laço traria dois ciclos de vida concorrentes para conciliar.
/// </para>
/// <para>
/// Mora na Application, e não no Desktop, porque não toca em nenhum tipo de UI:
/// assim é testável sem levantar o host headless da Avalonia.
/// </para>
/// </remarks>
public sealed class ReminderScheduler(
    IUseCaseRunner runner,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options,
    ILogger<ReminderScheduler> logger) : IAsyncDisposable, IDisposable
{
    /// <summary>Quanto se espera por um tique em voo antes de soltar o banco.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stopping = new();

    // Espera zero: um tique já em andamento faz o próximo ser pulado, não enfileirado.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITimer? _timer;
    private TimeSpan _period;
    private DateTimeOffset _lastTickAt;
    private bool _disposed;

    /// <summary>Tiques concluídos, para os testes saberem que o timer andou.</summary>
    public int CompletedTicks { get; private set; }

    /// <summary>
    /// Primeiro tique imediato de propósito: é ele que recupera tudo que venceu
    /// com o app fechado, sem nenhum caminho de código separado.
    /// </summary>
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _period = options.Value.ToReminderTickPeriod();
        _lastTickAt = timeProvider.GetUtcNow();

        _timer = timeProvider.CreateTimer(
            _ => _ = RunTickAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: _period);

        logger.LogInformation("ReminderSchedulerStarted {Period}", _period);
    }

    /// <summary>Público para que os testes rodem um tique sem depender do timer.</summary>
    public async Task<DispatchDueRemindersResult> TickAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            logger.LogDebug("ReminderTickSkippedBecauseOneWasStillRunning");
            return DispatchDueRemindersResult.Nothing;
        }

        try
        {
            NoteClockSkew();

            // O handler precisa de DbContext, que é scoped; o runner abre o
            // escopo (ADR-012). Sem isto este singleton capturaria um escopo.
            return await runner.RunAsync<DispatchDueRemindersHandler, DispatchDueRemindersResult>(
                (handler, token) => handler.HandleAsync(new DispatchDueReminders(), token),
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

        // Dá ao tique em voo a chance de terminar antes de soltar o banco.
        if (await _gate.WaitAsync(ShutdownGrace, CancellationToken.None))
        {
            _gate.Release();
        }

        Cleanup();
    }

    /// <summary>
    /// Também síncrono de propósito: um singleton que só é
    /// <see cref="IAsyncDisposable"/> faz o <c>Dispose()</c> do contêiner
    /// <b>lançar</b> — e o composition root descarta o contêiner com um
    /// <c>using</c> comum, no fim do <c>Main</c>.
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

    /// <summary>Um tique que lança nunca pode matar o timer.</summary>
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
            logger.LogError(exception, "ReminderTickFailed");
        }
        finally
        {
            CompletedTicks++;
        }
    }

    /// <summary>
    /// Nada especial a fazer com salto de relógio ou volta da suspensão: o
    /// disparo é instante absoluto e o tique é um poll de "o que venceu". Mas
    /// deixa o buraco visível no log, que é onde se procura depois.
    /// </summary>
    private void NoteClockSkew()
    {
        var now = timeProvider.GetUtcNow();
        var gap = now - _lastTickAt;

        if (_period > TimeSpan.Zero && gap > _period * 3)
        {
            logger.LogInformation("ReminderTickSkew {Gap}", gap);
        }

        _lastTickAt = now;
    }
}
