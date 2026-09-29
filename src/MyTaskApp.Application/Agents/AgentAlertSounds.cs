using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>O som de um estado do agente que avisa (ADR-042).</summary>
/// <param name="SoundId">Um <see cref="SoundOption.Id"/>.</param>
public sealed record AgentAlertSound(AgentActivity Activity, bool IsEnabled, string SoundId);

/// <summary>
/// Os estados que tocam som, e o som de fábrica de cada um. São os mesmos que
/// põem o aviso na tela (<see cref="AgentSession.NeedsAttention"/>): "voltou a
/// trabalhar" não interrompe ninguém.
/// </summary>
public static class AgentAlertSounds
{
    /// <summary>Na ordem da tela: do mais urgente ao menos.</summary>
    public static IReadOnlyList<AgentActivity> Activities { get; } =
    [
        AgentActivity.WaitingForUser,
        AgentActivity.WaitingReview,
        AgentActivity.Failed,
    ];

    public static bool Alerts(AgentActivity activity) => Activities.Contains(activity);

    /// <summary>
    /// Nasce ligado, e com um som diferente por estado: dá para saber, sem
    /// olhar, se o Claude perguntou algo ou só terminou.
    /// </summary>
    public static AgentAlertSound Default(AgentActivity activity) => activity switch
    {
        AgentActivity.WaitingForUser => new(activity, true, BuiltInSounds.Call),
        AgentActivity.WaitingReview => new(activity, true, BuiltInSounds.Done),
        AgentActivity.Failed => new(activity, true, BuiltInSounds.Warning),
        _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, "Este estado não avisa."),
    };

    /// <summary>O salvo de cada estado, ou o de fábrica quando nada foi salvo.</summary>
    public static IReadOnlyList<AgentAlertSound> Merge(IEnumerable<AgentAlertSound> stored)
    {
        var byActivity = stored.ToDictionary(sound => sound.Activity);

        return
        [
            .. Activities.Select(activity =>
                byActivity.TryGetValue(activity, out var sound) ? sound : Default(activity)),
        ];
    }
}

/// <summary>
/// O som de cada estado, no banco — mesmo desenho do
/// <see cref="IAgentSettingsStore"/> (ADR-014): é dado do usuário. Uma linha
/// por estado; sem linha, vale o de fábrica.
/// </summary>
public interface IAgentAlertSoundStore
{
    /// <summary>Só o que foi salvo.</summary>
    Task<IReadOnlyList<AgentAlertSound>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Só prepara a gravação: quem chama confirma com o <c>IUnitOfWork</c>.</summary>
    Task SaveAsync(AgentAlertSound sound, CancellationToken cancellationToken = default);
}

/// <summary>
/// Toca o som do estado em que o agente acabou de entrar (ADR-042). Nunca
/// lança: o som é um extra do aviso, e falhar nele não pode impedir o aviso
/// na tela nem a gravação da atividade.
/// </summary>
public sealed class AgentAlertSoundPlayer(
    IAgentAlertSoundStore store,
    ISoundLibrary library,
    IAudioPlayer player,
    ILogger<AgentAlertSoundPlayer> logger)
{
    public async Task PlayAsync(AgentActivity activity, CancellationToken cancellationToken = default)
    {
        if (!AgentAlertSounds.Alerts(activity))
        {
            return;
        }

        try
        {
            var sound = AgentAlertSounds
                .Merge(await store.GetAsync(cancellationToken))
                .Single(item => item.Activity == activity);

            if (!sound.IsEnabled)
            {
                return;
            }

            // O personalizado pode ter sumido da pasta: toca o de fábrica, em
            // vez de ficar em silêncio justo no aviso que o usuário quis ouvir.
            var file = library.Resolve(sound.SoundId)
                ?? library.Resolve(AgentAlertSounds.Default(activity).SoundId);

            if (file is null)
            {
                logger.LogWarning("AgentAlertSoundMissing {Activity} {SoundId}", activity, sound.SoundId);
                return;
            }

            player.Play(file);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "AgentAlertSoundFailed {Activity}", activity);
        }
    }
}
