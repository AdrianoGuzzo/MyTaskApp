using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Mobile.Core.Composition;

/// <summary>
/// O app no celular (ADR-067): o banco nasce pelas migrations fora da thread
/// da tela, e a sincronização roda só em primeiro plano — o sistema congela o
/// processo em segundo plano de qualquer jeito, e um ciclo cortado no meio é
/// reenviado com os mesmos ids na volta.
/// </summary>
/// <remarks>
/// Lembretes, a varredura do arquivo e os monitores de agente do desktop não
/// sobem aqui: avisos no celular pediriam notificações do sistema, fora do
/// escopo desta fase.
/// </remarks>
public sealed class MobileLifecycle(IServiceProvider services, SyncScheduler scheduler, ILogger<MobileLifecycle> logger)
{
    private readonly Lock _gate = new();

    private Task? _ready;

    /// <summary>
    /// O banco pronto e a sincronização ligada. Chamar de novo devolve a mesma
    /// tarefa; depois de uma falha, tenta outra vez ("Tentar de novo" da tela).
    /// </summary>
    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_ready is { IsFaulted: true } or { IsCanceled: true })
            {
                _ready = null;
            }

            return _ready ??= Task.Run(InitializeAsync);
        }
    }

    /// <summary>Foi para segundo plano: para o laço.</summary>
    public async Task SleepAsync()
    {
        if (IsReady)
        {
            await scheduler.StopAsync();
            logger.LogInformation("MobileSyncPaused");
        }
    }

    /// <summary>Voltou: liga o laço e sincroniza já.</summary>
    public void Resume()
    {
        if (IsReady)
        {
            scheduler.Start();
            scheduler.RequestNow();
        }
    }

    private bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready is { IsCompletedSuccessfully: true };
            }
        }
    }

    private async Task InitializeAsync()
    {
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        logger.LogInformation("MobileDatabaseReady");

        scheduler.Start();
        scheduler.RequestNow();
    }
}
