namespace MyTaskApp.Infrastructure.Storage;

/// <summary>
/// Onde moram os arquivos do usuário — e, por consequência, onde eles <b>não</b>
/// moram: nunca dentro do diretório de instalação (ADR-018). Um instalador
/// atualiza ou remove o que instalou; se o banco estivesse lá, uma atualização
/// apagaria as tarefas de alguém.
/// </summary>
/// <remarks>
/// Classe comum, sem interface: não há segunda implementação nem nada para
/// substituir em teste além da raiz, que já entra por parâmetro (ADR-005 —
/// interface só quando agrega valor).
/// <para>
/// <see cref="Environment.SpecialFolder.ApplicationData"/> resolve sozinho em
/// cada sistema (<c>%APPDATA%</c>, <c>~/.config</c>,
/// <c>~/Library/Application Support</c>), então isto continua multiplataforma
/// sem um único <c>if (Windows)</c>.
/// </para>
/// </remarks>
public sealed class UserDataLocation
{
    public const string ProductFolderName = "MyTaskApp";

    /// <summary>
    /// Move os três artefatos de uma vez — banco, estado e logs. Existe para
    /// instalação portátil e para o teste de fumaça do instalador conseguir
    /// apontar tudo para uma pasta descartável.
    /// </summary>
    public const string OverrideVariable = "MYTASKAPP_DATA_DIR";

    private const string LogsFolderName = "logs";

    private UserDataLocation(string root) => Root = root;

    /// <summary>
    /// A localização deste processo, resolvida uma vez. Quem precisa variar a
    /// raiz em teste usa <see cref="For"/> em vez de mexer em estado global.
    /// </summary>
    public static UserDataLocation Current { get; } =
        Resolve(Environment.GetEnvironmentVariable(OverrideVariable));

    /// <summary>Raiz explícita, sem consultar o ambiente.</summary>
    public static UserDataLocation For(string root) =>
        new(Path.GetFullPath(root));

    /// <summary>
    /// A raiz informada vence; vazia cai na pasta de dados do sistema. O último
    /// recurso existe porque <c>GetFolderPath</c> pode devolver vazio em
    /// ambientes sem perfil — e devolver vazio aqui faria o banco nascer no
    /// diretório de trabalho, que é justamente o que se quer evitar.
    /// </summary>
    public static UserDataLocation Resolve(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return For(overrideRoot);
        }

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(applicationData))
        {
            applicationData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return For(Path.Combine(applicationData, ProductFolderName));
    }

    /// <summary>A pasta que sobrevive a atualização e a desinstalação.</summary>
    public string Root { get; }

    /// <summary>
    /// Logs <b>da aplicação</b>. Os do instalador ficam em outra subárvore, de
    /// propósito: quem procura "por que não instalou" não pode esbarrar em
    /// "por que o lembrete não tocou".
    /// </summary>
    public string Logs => Path.Combine(Root, LogsFolderName);

    /// <summary>
    /// Estado da janela (<c>widget.json</c>) e a sobreposição opcional de
    /// configuração. Hoje é a própria raiz — continua sendo um nome, e não um
    /// <c>Path.Combine</c> repetido em três arquivos.
    /// </summary>
    public string State => Root;

    /// <summary>Descartável por definição: nada aqui precisa sobreviver a nada.</summary>
    public string Temp => Path.Combine(Path.GetTempPath(), ProductFolderName);

    public string DatabaseFile(string fileName) => Path.Combine(Root, fileName);

    /// <summary>
    /// Nome deliberado: não é <c>EnsureCreated</c>. Aquele nome pertence ao EF
    /// Core e é proibido no projeto (ADR-005), então nem como coincidência ele
    /// pode aparecer num grep.
    /// </summary>
    public void CreateDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
