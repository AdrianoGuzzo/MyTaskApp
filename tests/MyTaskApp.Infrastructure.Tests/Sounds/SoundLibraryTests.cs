using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Sounds;

namespace MyTaskApp.Infrastructure.Tests.Sounds;

/// <summary>Os sons em disco: os do app e os que o usuário adiciona (ADR-042).</summary>
public sealed class SoundLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mytaskapp-sounds-{Guid.NewGuid():N}");

    private string Custom => Path.Combine(_root, "custom");

    private string BuiltIn => Path.Combine(_root, "builtin");

    private string Downloads => Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;

    private SoundLibrary Library() => new(Custom, BuiltIn, NullLogger<SoundLibrary>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Wav(string name) => Write(name, [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVEfmt "u8]);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(Downloads, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void EveryBuiltInSound_HasARecipe()
    {
        BuiltInSounds.All.Should().OnlyContain(sound => SoundSynthesizer.Knows(sound.Id));
    }

    [Fact]
    public void ABuiltInSound_IsAPlayableWav()
    {
        foreach (var sound in BuiltInSounds.All)
        {
            var wav = File.ReadAllBytes(Library().Resolve(sound.Id)!);

            wav.AsSpan(0, 4).SequenceEqual("RIFF"u8).Should().BeTrue();
            wav.AsSpan(8, 4).SequenceEqual("WAVE"u8).Should().BeTrue();
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)).Should().Be(SoundSynthesizer.SampleRate);
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)).Should().Be(wav.Length - 44);

            // Nem mudo nem longo: um aviso, e não uma música.
            var seconds = (wav.Length - 44) / 2.0 / SoundSynthesizer.SampleRate;
            seconds.Should().BeInRange(0.2, 2.5);
            wav.Skip(44).Should().Contain(value => value != 0);
        }
    }

    /// <summary>Mesmo id, mesmos bytes: reabrir o app não reescreve o que está tocando.</summary>
    [Fact]
    public void ABuiltInSound_IsTheSameEveryTime()
    {
        var file = Library().Resolve(BuiltInSounds.Bell)!;
        var written = File.GetLastWriteTimeUtc(file);

        Library().Resolve(BuiltInSounds.Bell).Should().Be(file);

        File.GetLastWriteTimeUtc(file).Should().Be(written);
        SoundSynthesizer.Render(BuiltInSounds.Bell).Should().Equal(File.ReadAllBytes(file));
    }

    [Fact]
    public void TheList_StartsWithTheAppsSounds_ThenTheUsersInOrder()
    {
        var library = Library();
        library.Import(Wav("zabumba.wav"));
        library.Import(Write("apito.mp3", [.. "ID3"u8, 3, 0]));

        library.List().Select(sound => sound.Name).Should().Equal(
            [.. BuiltInSounds.All.Select(sound => sound.Name), "apito", "zabumba"]);
    }

    [Fact]
    public void Importing_CopiesTheFile_SoTheOriginalCanGo()
    {
        var original = Wav("Meu som.wav");

        var sound = Library().Import(original);
        File.Delete(original);

        sound.Should().Be(new SoundOption("custom:Meu som.wav", "Meu som", IsCustom: true));
        File.Exists(Library().Resolve(sound.Id)).Should().BeTrue();
    }

    [Fact]
    public void AnMp3WithoutTags_StartingOnAFrame_IsAccepted()
    {
        Library().Import(Write("quadro.mp3", [0xFF, 0xFB, 0x90, 0x64])).Name.Should().Be("quadro");
    }

    [Fact]
    public void TheSameNameTwice_GetsANumber_EvenWithAnotherExtension()
    {
        var library = Library();
        library.Import(Wav("gongo.wav"));

        library.Import(Wav("gongo.wav")).Name.Should().Be("gongo (2)");
        library.Import(Write("gongo.mp3", [.. "ID3"u8, 3, 0])).Name.Should().Be("gongo (3)");
    }

    [Theory]
    [InlineData("musica.ogg")]
    [InlineData("musica.txt")]
    public void AFormatThePlayerDoesNotPlay_IsRefused(string name)
    {
        var act = () => Library().Import(Write(name, [1, 2, 3]));

        act.Should().Throw<DomainException>().WithMessage("*WAV ou MP3*");
    }

    /// <summary>Renomeado para .wav, mas não é: seria aceito e ficaria mudo na hora do aviso.</summary>
    [Fact]
    public void AFileThatIsNotReallyAudio_IsRefused()
    {
        var act = () => Library().Import(Write("falso.wav", "não sou um som"u8.ToArray()));

        act.Should().Throw<DomainException>().WithMessage("*WAV válido*");
    }

    [Fact]
    public void AnEmptyOrMissingFile_IsRefused()
    {
        var empty = () => Library().Import(Write("vazio.wav", []));
        var missing = () => Library().Import(Path.Combine(Downloads, "nao-existe.wav"));

        empty.Should().Throw<DomainException>().WithMessage("*vazio*");
        missing.Should().Throw<DomainException>().WithMessage("*não encontrado*");
    }

    [Fact]
    public void AFileTooBig_IsRefused()
    {
        var path = Wav("longo.wav");

        using (var stream = File.OpenWrite(path))
        {
            stream.SetLength(SoundLibrary.MaxFileBytes + 1);
        }

        var act = () => Library().Import(path);

        act.Should().Throw<DomainException>().WithMessage("*10 MB*");
    }

    [Fact]
    public void Deleting_TakesTheSoundOffTheList()
    {
        var library = Library();
        var sound = library.Import(Wav("gongo.wav"));

        library.Delete(sound.Id);

        library.List().Should().NotContain(sound);
        library.Resolve(sound.Id).Should().BeNull();
    }

    [Fact]
    public void TheAppsSounds_CannotBeDeleted()
    {
        var act = () => Library().Delete(BuiltInSounds.Bell);

        act.Should().Throw<DomainException>();
    }

    /// <summary>O id vem do banco: não pode apontar para fora da pasta dos sons.</summary>
    [Theory]
    [InlineData(@"custom:..\..\segredo.wav")]
    [InlineData("custom:../segredo.wav")]
    [InlineData(@"custom:C:\Windows\Media\chimes.wav")]
    [InlineData("custom:")]
    [InlineData("builtin:nao-existe")]
    [InlineData("qualquer-coisa")]
    public void AnIdOutsideTheLibrary_ResolvesToNothing(string soundId)
    {
        Library().Resolve(soundId).Should().BeNull();
    }
}
