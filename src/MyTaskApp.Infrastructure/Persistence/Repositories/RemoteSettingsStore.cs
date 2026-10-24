using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Remote;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class RemoteSettingsStore(MyTaskAppDbContext context) : IRemoteSettingsStore
{
    public async Task<RemoteSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.RemoteSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(settings => settings.Id == RemoteSettingsRow.SingletonId, cancellationToken);

        // Sem linha (instalação nova, ou anterior ao remoto): desligado, sem semear (ADR-014).
        // Aceitar controle sem o remoto ligado não existe; uma linha assim conta como desligada.
        return row is null
            ? RemoteSettings.Off
            : new RemoteSettings(row.Enabled, row.Enabled && row.AcceptControl);
    }

    public async Task SaveAsync(RemoteSettings settings, CancellationToken cancellationToken = default)
    {
        var row = await context.RemoteSettings.FirstOrDefaultAsync(stored => stored.Id == RemoteSettingsRow.SingletonId, cancellationToken);

        if (row is null)
        {
            row = new RemoteSettingsRow();
            await context.RemoteSettings.AddAsync(row, cancellationToken);
        }

        row.Enabled = settings.Enabled;
        row.AcceptControl = settings.AcceptControl;
    }
}
