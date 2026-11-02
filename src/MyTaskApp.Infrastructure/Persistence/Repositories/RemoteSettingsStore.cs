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
        // Aceitar controle sem o remoto ligado não existe, nem o terminal sem aceitar controle; uma linha assim conta como desligada.
        if (row is null)
        {
            return RemoteSettings.Off;
        }

        var acceptControl = row.Enabled && row.AcceptControl;
        return new RemoteSettings(row.Enabled, acceptControl, acceptControl && row.AcceptTerminal);
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
        row.AcceptTerminal = settings.AcceptTerminal;
    }
}
