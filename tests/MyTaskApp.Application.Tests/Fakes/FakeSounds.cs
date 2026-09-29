using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeAgentAlertSoundStore : IAgentAlertSoundStore
{
    public List<AgentAlertSound> Stored { get; } = [];

    public Task<IReadOnlyList<AgentAlertSound>> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentAlertSound>>([.. Stored]);

    public Task SaveAsync(AgentAlertSound sound, CancellationToken cancellationToken = default)
    {
        Stored.RemoveAll(stored => stored.Activity == sound.Activity);
        Stored.Add(sound);
        return Task.CompletedTask;
    }
}

/// <summary>Os sons do app, mais os personalizados que o teste puser, sem disco.</summary>
internal sealed class FakeSoundLibrary : ISoundLibrary
{
    public List<SoundOption> Custom { get; } = [];

    public List<string> Deleted { get; } = [];

    public IReadOnlyList<SoundOption> List() => [.. BuiltInSounds.All, .. Custom];

    public SoundOption Add(string name)
    {
        var sound = new SoundOption($"custom:{name}.wav", name, IsCustom: true);
        Custom.Add(sound);
        return sound;
    }

    public SoundOption Import(string sourcePath) => Add(Path.GetFileNameWithoutExtension(sourcePath));

    public void Delete(string soundId)
    {
        if (Custom.RemoveAll(sound => sound.Id == soundId) == 0)
        {
            throw new DomainException("Só dá para excluir os sons que você adicionou.");
        }

        Deleted.Add(soundId);
    }

    public string? Resolve(string soundId) =>
        List().Any(sound => sound.Id == soundId) ? $@"C:\sons\{soundId}" : null;
}

internal sealed class RecordingAudioPlayer : IAudioPlayer
{
    public List<string> Played { get; } = [];

    public void Play(string filePath) => Played.Add(filePath);
}
