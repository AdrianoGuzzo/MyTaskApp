using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class AgentAlertSoundStore(MyTaskAppDbContext context) : IAgentAlertSoundStore
{
    public async Task<IReadOnlyList<AgentAlertSound>> GetAsync(CancellationToken cancellationToken = default) =>
        await context.AgentAlertSounds
            .AsNoTracking()
            .OrderBy(row => row.Activity)
            .Select(row => new AgentAlertSound(row.Activity, row.IsEnabled, row.SoundId))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Procura também entre as linhas adicionadas e ainda não gravadas, como o
    /// <see cref="AgentSettingsStore"/>: duas mudanças no mesmo <c>SaveChanges</c>
    /// não podem inserir a mesma chave duas vezes.
    /// </summary>
    public async Task SaveAsync(AgentAlertSound sound, CancellationToken cancellationToken = default)
    {
        var row = context.AgentAlertSounds.Local.FirstOrDefault(stored => stored.Activity == sound.Activity)
            ?? await context.AgentAlertSounds.FirstOrDefaultAsync(
                stored => stored.Activity == sound.Activity,
                cancellationToken);

        if (row is null)
        {
            row = new AgentAlertSoundRow { Activity = sound.Activity };
            await context.AgentAlertSounds.AddAsync(row, cancellationToken);
        }

        row.IsEnabled = sound.IsEnabled;
        row.SoundId = sound.SoundId;
    }
}
