using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Sounds;

/// <summary>
/// Os sons em disco (ADR-042). Os personalizados moram na pasta de dados do
/// usuário e sobrevivem a atualização e desinstalação, como o banco (ADR-018).
/// Os do app são renderizados numa pasta descartável na primeira vez que
/// tocam: o player toca arquivo, e assim os dois tipos seguem o mesmo caminho.
/// </summary>
/// <remarks>
/// A pasta é o catálogo: não há tabela de sons. O id de um personalizado é o
/// nome do arquivo, e o nome mostrado é esse nome sem a extensão.
/// </remarks>
internal sealed class SoundLibrary(
    string customFolder,
    string builtInFolder,
    ILogger<SoundLibrary> logger) : ISoundLibrary
{
    public const string CustomPrefix = "custom:";

    /// <summary>Um aviso não precisa de mais que isso; um álbum inteiro não deveria entrar por engano.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private const int MaxNameLength = 80;

    /// <summary>O que o player do Windows (MCI) toca sem codec extra.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".wav", ".mp3"];

    private readonly Lock _gate = new();

    public IReadOnlyList<SoundOption> List()
    {
        IEnumerable<SoundOption> custom = Directory.Exists(customFolder)
            ? Directory.EnumerateFiles(customFolder)
                .Where(IsSupported)
                .Select(file => ToOption(Path.GetFileName(file)))
                .OrderBy(sound => sound.Name, StringComparer.CurrentCultureIgnoreCase)
            : [];

        return [.. BuiltInSounds.All, .. custom];
    }

    public SoundOption Import(string sourcePath)
    {
        var source = new FileInfo(sourcePath);

        if (!source.Exists)
        {
            throw new DomainException("Arquivo não encontrado.");
        }

        var extension = source.Extension.ToLowerInvariant();

        if (!Extensions.Contains(extension))
        {
            throw new DomainException("Use um arquivo WAV ou MP3.");
        }

        if (source.Length == 0)
        {
            throw new DomainException("O arquivo está vazio.");
        }

        if (source.Length > MaxFileBytes)
        {
            throw new DomainException("O som pode ter até 10 MB.");
        }

        if (!LooksLikeAudio(source.FullName, extension))
        {
            throw new DomainException($"O arquivo não parece ser um {extension.TrimStart('.').ToUpperInvariant()} válido.");
        }

        lock (_gate)
        {
            Directory.CreateDirectory(customFolder);

            var target = Path.Combine(customFolder, FreeName(Sanitize(Path.GetFileNameWithoutExtension(source.Name)), extension));

            source.CopyTo(target);

            return ToOption(Path.GetFileName(target));
        }
    }

    public void Delete(string soundId)
    {
        var file = CustomFile(soundId)
            ?? throw new DomainException("Só dá para excluir os sons que você adicionou.");

        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "SoundDeleteFailed {SoundId}", soundId);
            throw new DomainException("Não foi possível excluir o som agora: ele pode estar tocando. Tente de novo em instantes.");
        }
    }

    public string? Resolve(string soundId)
    {
        if (SoundSynthesizer.Knows(soundId))
        {
            return BuiltInFile(soundId);
        }

        return CustomFile(soundId) is { } file && File.Exists(file) ? file : null;
    }

    /// <summary>
    /// Escreve só se faltar ou se a receita mudou numa versão nova: o arquivo
    /// pode estar aberto pelo player, e reescrever à toa falharia.
    /// </summary>
    private string BuiltInFile(string soundId)
    {
        var file = Path.Combine(builtInFolder, soundId[BuiltInSounds.Prefix.Length..] + ".wav");
        var wav = SoundSynthesizer.Render(soundId);

        lock (_gate)
        {
            if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(wav))
            {
                return file;
            }

            Directory.CreateDirectory(builtInFolder);
            File.WriteAllBytes(file, wav);

            return file;
        }
    }

    /// <summary>
    /// O caminho de um personalizado — só dentro da pasta. O id vem do banco,
    /// e um <c>custom:..\..\algo</c> não pode apontar para fora dela.
    /// </summary>
    private string? CustomFile(string soundId)
    {
        if (!soundId.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = soundId[CustomPrefix.Length..];

        if (name.Length == 0
            || name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !IsSupported(name))
        {
            return null;
        }

        return Path.Combine(customFolder, name);
    }

    private static SoundOption ToOption(string fileName) =>
        new(CustomPrefix + fileName, Path.GetFileNameWithoutExtension(fileName), IsCustom: true);

    private static bool IsSupported(string file) =>
        Extensions.Contains(Path.GetExtension(file).ToLowerInvariant());

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(character => invalid.Contains(character) ? '_' : character)]).Trim(' ', '.');

        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].TrimEnd(' ', '.');
        }

        return clean.Length == 0 ? "Som" : clean;
    }

    /// <summary>
    /// "meu som", "meu som (2)"… O nome mostrado não tem extensão, então um
    /// <c>.wav</c> e um <c>.mp3</c> com o mesmo nome também contam como repetidos.
    /// </summary>
    private string FreeName(string name, string extension)
    {
        var candidate = name;

        for (var copy = 2; Extensions.Any(other => File.Exists(Path.Combine(customFolder, candidate + other))); copy++)
        {
            candidate = $"{name} ({copy})";
        }

        return candidate + extension;
    }

    /// <summary>
    /// Confere a assinatura, e não só a extensão: um arquivo renomeado seria
    /// aceito aqui e ficaria mudo justo na hora do aviso.
    /// </summary>
    private static bool LooksLikeAudio(string file, string extension)
    {
        Span<byte> header = stackalloc byte[12];

        using (var stream = File.OpenRead(file))
        {
            header = header[..stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)];
        }

        return extension switch
        {
            ".wav" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8),

            // Com etiqueta ID3, ou começando direto num quadro MPEG (sincronia de 11 bits).
            ".mp3" => (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8))
                || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0),
            _ => false,
        };
    }
}
