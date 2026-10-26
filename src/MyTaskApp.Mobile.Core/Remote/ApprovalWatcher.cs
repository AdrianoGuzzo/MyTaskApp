using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>
/// As aprovações em aberto da conta, com o app na frente (E7): a lista é
/// relida a cada <see cref="Interval"/>, e uma nova aparece em até 2 s. A
/// vencida sai da lista na hora, mesmo sem o servidor dizer: lá, ela vale
/// negar. Em segundo plano não lê nada; não há push na v1.
/// </summary>
public sealed class ApprovalWatcher(
    IUseCaseRunner runner,
    ISyncAccount account,
    TimeProvider timeProvider,
    ILogger<ApprovalWatcher> logger) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    private readonly Lock _gate = new();

    private IReadOnlyList<ApprovalView> _all = [];

    private CancellationTokenSource? _loop;

    /// <summary>As que ainda dá para responder, das que vencem antes para as que vencem depois.</summary>
    public IReadOnlyList<ApprovalView> Pending { get; private set; } = [];

    /// <summary>A lista mudou; pode vir de fora da thread da tela.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = new CancellationTokenSource();
            _ = RunAsync(_loop.Token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
        }
    }

    /// <summary>Uma leitura agora: ao abrir a aba, e depois de responder.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!account.IsAvailable || !(await runner.RunAsync<GetRemoteSettingsHandler, RemoteSettings>(
                (handler, token) => handler.HandleAsync(new GetRemoteSettings(), token),
                cancellationToken)).Enabled)
        {
            Publish([]);
            return;
        }

        try
        {
            Publish(await runner.RunAsync<GetPendingApprovalsHandler, IReadOnlyList<ApprovalView>>(
                (handler, token) => handler.HandleAsync(new GetPendingApprovals(), token),
                cancellationToken));
        }
        catch (SyncApiException exception)
        {
            // Sem servidor ou sem login: a lista fica como estava, e a aba diz o motivo.
            logger.LogDebug("RemoteApprovalsUnavailable {Kind}", exception.Kind);
            Publish(_all);
        }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken);
                await Task.Delay(Interval, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "RemoteApprovalWatchFailed");
                await Task.Delay(Interval, timeProvider, CancellationToken.None);
            }
        }
    }

    private void Publish(IReadOnlyList<ApprovalView> all)
    {
        var now = timeProvider.GetUtcNow();
        var pending = all
            .Where(view => view.State == ApprovalState.Pending && view.Request.ExpiresAt > now)
            .OrderBy(view => view.Request.ExpiresAt)
            .ToList();
        bool changed;

        lock (_gate)
        {
            _all = all;
            changed = !pending.Select(view => view.Request.ApprovalId).SequenceEqual(Pending.Select(view => view.Request.ApprovalId));
            Pending = pending;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
