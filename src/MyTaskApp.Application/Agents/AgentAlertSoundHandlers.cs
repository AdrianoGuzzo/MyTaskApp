using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>A tela de sons dos avisos do agente (ADR-042).</summary>
public sealed record GetAgentAlertSounds;

/// <summary>O som de cada estado, e todos os sons que dá para escolher.</summary>
public sealed record AgentAlertSoundsView(
    IReadOnlyList<AgentAlertSound> Alerts,
    IReadOnlyList<SoundOption> Sounds);

public sealed class GetAgentAlertSoundsHandler(IAgentAlertSoundStore store, ISoundLibrary library)
{
    public async Task<AgentAlertSoundsView> HandleAsync(
        GetAgentAlertSounds command,
        CancellationToken cancellationToken = default)
    {
        var sounds = library.List();
        var alerts = AgentAlertSounds.Merge(await store.GetAsync(cancellationToken));

        // Um personalizado apagado por fora do app volta ao de fábrica na tela,
        // que é o que o aviso tocaria.
        return new AgentAlertSoundsView(
            [
                .. alerts.Select(alert => sounds.Any(sound => sound.Id == alert.SoundId)
                    ? alert
                    : alert with { SoundId = AgentAlertSounds.Default(alert.Activity).SoundId }),
            ],
            sounds);
    }
}

/// <summary>Liga, desliga ou troca o som de um estado. A tela grava a cada mudança.</summary>
public sealed record UpdateAgentAlertSound(AgentActivity Activity, bool IsEnabled, string SoundId);

public sealed class UpdateAgentAlertSoundHandler(
    IAgentAlertSoundStore store,
    ISoundLibrary library,
    IUnitOfWork unitOfWork,
    ILogger<UpdateAgentAlertSoundHandler> logger)
{
    public async Task HandleAsync(UpdateAgentAlertSound command, CancellationToken cancellationToken = default)
    {
        if (!AgentAlertSounds.Alerts(command.Activity))
        {
            throw new DomainException("Este estado do agente não toca som.");
        }

        if (library.List().All(sound => sound.Id != command.SoundId))
        {
            throw new DomainException("Esse som não existe mais. Escolha outro.");
        }

        await store.SaveAsync(
            new AgentAlertSound(command.Activity, command.IsEnabled, command.SoundId),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "AgentAlertSoundUpdated {Activity} {IsEnabled} {SoundId}",
            command.Activity,
            command.IsEnabled,
            command.SoundId);
    }
}

/// <summary>Adiciona um arquivo de som do usuário à lista.</summary>
public sealed record ImportSound(string SourcePath);

public sealed class ImportSoundHandler(ISoundLibrary library, ILogger<ImportSoundHandler> logger)
{
    public Task<SoundOption> HandleAsync(ImportSound command, CancellationToken cancellationToken = default)
    {
        var sound = library.Import(command.SourcePath);

        logger.LogInformation("SoundImported {SoundId}", sound.Id);

        return Task.FromResult(sound);
    }
}

/// <summary>Apaga um som do usuário.</summary>
public sealed record DeleteSound(string SoundId);

/// <summary>
/// O estado que usava o som apagado volta ao de fábrica — no banco, e não só na
/// tela: a linha não fica apontando para um arquivo que não existe.
/// </summary>
public sealed class DeleteSoundHandler(
    IAgentAlertSoundStore store,
    ISoundLibrary library,
    IUnitOfWork unitOfWork,
    ILogger<DeleteSoundHandler> logger)
{
    public async Task HandleAsync(DeleteSound command, CancellationToken cancellationToken = default)
    {
        if (BuiltInSounds.IsBuiltIn(command.SoundId))
        {
            throw new DomainException("Os sons que vêm com o app não podem ser excluídos.");
        }

        library.Delete(command.SoundId);

        foreach (var alert in await store.GetAsync(cancellationToken))
        {
            if (alert.SoundId == command.SoundId)
            {
                await store.SaveAsync(
                    alert with { SoundId = AgentAlertSounds.Default(alert.Activity).SoundId },
                    cancellationToken);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("SoundDeleted {SoundId}", command.SoundId);
    }
}

/// <summary>O botão ▶ da tela: toca o som para o usuário ouvir antes de escolher.</summary>
public sealed record PreviewSound(string SoundId);

public sealed class PreviewSoundHandler(ISoundLibrary library, IAudioPlayer player)
{
    public Task HandleAsync(PreviewSound command, CancellationToken cancellationToken = default)
    {
        var file = library.Resolve(command.SoundId)
            ?? throw new DomainException("Esse som não existe mais. Escolha outro.");

        player.Play(file);

        return Task.CompletedTask;
    }
}
