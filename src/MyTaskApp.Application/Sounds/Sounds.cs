namespace MyTaskApp.Application.Sounds;

/// <summary>Um som que dá para escolher: um dos que vêm com o app ou um que o usuário adicionou.</summary>
/// <param name="Id"><c>builtin:&lt;chave&gt;</c> ou <c>custom:&lt;arquivo&gt;</c> — é o que fica gravado.</param>
public sealed record SoundOption(string Id, string Name, bool IsCustom);

/// <summary>
/// Os sons que vêm com o app (ADR-042). Não são arquivos na instalação: são
/// sintetizados na primeira vez que tocam, então não há licença de áudio de
/// terceiros nem asset para esquecer no instalador.
/// </summary>
public static class BuiltInSounds
{
    public const string Prefix = "builtin:";

    public const string Call = Prefix + "chamada";

    public const string Done = Prefix + "concluido";

    public const string Warning = Prefix + "alerta";

    public const string Bell = Prefix + "sino";

    public const string TwoTones = Prefix + "dois-toques";

    public const string Soft = Prefix + "suave";

    public const string Pop = Prefix + "bolha";

    /// <summary>Na ordem em que aparecem na lista.</summary>
    public static IReadOnlyList<SoundOption> All { get; } =
    [
        new(Call, "Chamada", IsCustom: false),
        new(Done, "Concluído", IsCustom: false),
        new(Warning, "Alerta", IsCustom: false),
        new(Bell, "Sino", IsCustom: false),
        new(TwoTones, "Dois toques", IsCustom: false),
        new(Soft, "Suave", IsCustom: false),
        new(Pop, "Bolha", IsCustom: false),
    ];

    public static bool IsBuiltIn(string soundId) =>
        soundId.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// Os sons que o usuário pode escolher: os do app e os que ele adicionou
/// (ADR-042). Os personalizados são <b>copiados</b> para a pasta de dados do
/// app, e não referenciados onde estavam: mover ou apagar o original não
/// emudece o aviso.
/// </summary>
public interface ISoundLibrary
{
    /// <summary>Os do app primeiro, depois os personalizados em ordem alfabética.</summary>
    IReadOnlyList<SoundOption> List();

    /// <summary>
    /// Copia o arquivo para a biblioteca. Recusa com <c>DomainException</c> o
    /// que não dá para tocar: formato fora da lista, arquivo grande demais ou
    /// que não existe.
    /// </summary>
    SoundOption Import(string sourcePath);

    /// <summary>Apaga um som personalizado. Os do app não saem.</summary>
    void Delete(string soundId);

    /// <summary>O arquivo que toca este som; <c>null</c> se ele não existe mais.</summary>
    string? Resolve(string soundId);
}

/// <summary>
/// Toca um arquivo de som. Dispara e esquece, e <b>nunca lança</b>, como o
/// <c>ISoundPlayer</c> dos lembretes: falhar em tocar não pode derrubar o aviso.
/// Um som novo interrompe o que ainda estiver tocando.
/// </summary>
public interface IAudioPlayer
{
    void Play(string filePath);
}

/// <summary>Sem áudio (testes, fora do Windows): não toca nada.</summary>
public sealed class NoAudioPlayer : IAudioPlayer
{
    public void Play(string filePath)
    {
    }
}
