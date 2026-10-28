using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.Remote;

namespace MyTaskApp.Mobile.Core.Composition;

/// <summary>
/// O app no celular (ADR-067): o banco nasce pelas migrations fora da thread
/// da tela, e a sincronização roda só em primeiro plano — o sistema congela o
/// processo em segundo plano de qualquer jeito, e um ciclo cortado no meio é
/// reenviado com os mesmos ids na volta.
/// </summary>
/// <remarks>
/// Lembretes, a varredura do arquivo e os monitores de agente do desktop não
/// sobem aqui. As aprovações do controle remoto (E7) são lidas ao vivo só em
/// primeiro plano, e a chave tranca ao sair; em segundo plano, o sistema roda
/// a <see cref="RemoteBackgroundCheck"/> de tempos em tempos (ADR-096), que só
/// prepara o banco, sem ligar a sincronização.
/// </remarks>
public sealed class MobileLifecycle(
    IServiceProvider services,
    SyncScheduler scheduler,
    ApprovalWatcher approvals,
    UnlockedDeviceSigner signer,
    ILogger<MobileLifecycle> logger)
{
    private readonly Lock _gate = new();

    private Task? _ready;

    private Task? _prepared;

    private volatile bool _awake;

    /// <summary>A tela está na frente: a verificação do sistema não avisa o que ela já mostra.</summary>
    public bool IsAwake => _awake;

    /// <summary>
    /// O banco pronto e a sincronização ligada. Chamar de novo devolve a mesma
    /// tarefa; depois de uma falha, tenta outra vez ("Tentar de novo" da tela).
    /// </summary>
    public Task StartAsync()
    {
        _awake = true;

        lock (_gate)
        {
            if (_ready is { IsFaulted: true } or { IsCanceled: true })
            {
                _ready = null;
            }

            return _ready ??= Task.Run(InitializeAsync);
        }
    }

    /// <summary>
    /// Só o banco migrado e o servidor salvo, sem laço nenhum: o que a
    /// verificação do sistema precisa com o processo aberto sem tela (ADR-096).
    /// </summary>
    public Task PrepareAsync()
    {
        lock (_gate)
        {
            if (_prepared is { IsFaulted: true } or { IsCanceled: true })
            {
                _prepared = null;
            }

            return _prepared ??= Task.Run(PrepareCoreAsync);
        }
    }

    /// <summary>Foi para segundo plano: para o laço, as aprovações e tranca a chave.</summary>
    public async Task SleepAsync()
    {
        _awake = false;
        signer.Lock();
        approvals.Stop();

        if (IsReady)
        {
            await scheduler.StopAsync();
            logger.LogInformation("MobileSyncPaused");
        }
    }

    /// <summary>Voltou: liga o laço e sincroniza já.</summary>
    public void Resume()
    {
        _awake = true;

        if (IsReady)
        {
            scheduler.Start();
            scheduler.RequestNow();
            approvals.Start();
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
        await PrepareAsync();

        scheduler.Start();
        scheduler.RequestNow();
        approvals.Start();

        try
        {
            // A verificação do sistema segue o remoto: ligado aqui ou em outra versão do app.
            await services.GetRequiredService<RemoteBackgroundCheck>().ApplyAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MobileRemoteBackgroundApplyFailed");
        }
    }

    private async Task PrepareCoreAsync()
    {
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        logger.LogInformation("MobileDatabaseReady");

        // O servidor salvo, antes do primeiro ciclo: sem ele, o laço só fica ocioso (ADR-078).
        await services.GetRequiredService<SyncServerSettings>().InitializeAsync(CancellationToken.None);
    }
}
