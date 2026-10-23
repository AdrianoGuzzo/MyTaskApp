using System.Security.Cryptography;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>O container do banco local, como o <c>docker ps</c> o viu.</summary>
public enum LocalContainerState
{
    /// <summary>Não deu para perguntar: sem Docker, ou com o Docker parado.</summary>
    Unknown = 0,

    Missing = 1,

    Running = 2,

    Stopped = 3,
}

/// <summary>
/// O Docker desta máquina (ADR-083): instalado, rodando e o container do banco
/// local. É opcional — serve para quem não tem um PostgreSQL para receber a
/// cópia —, por isso nada aqui é falha: no máximo, aviso.
/// <see cref="Guide"/> são os passos para o estado atual: instalar, ligar ou nenhum.
/// </summary>
public sealed record DockerEnvironment(
    bool Installed,
    string? Path,
    string? Version,
    bool EngineRunning,
    string? EngineVersion,
    string? EngineProblem,
    LocalContainerState Container,
    string? ContainerStatus,
    ToolInstallGuide Guide)
{
    public const string Category = "Docker";

    /// <summary>A porta publicada do container que já existe; <c>null</c> sem container.</summary>
    public int? ContainerPort { get; init; }

    /// <summary>A primeira porta livre desta máquina para um container novo (ADR-083).</summary>
    public int FreePort { get; init; } = LocalPostgresContainer.PreferredPort;

    /// <summary>A porta do banco local: a do container, se ele existe; senão, a livre.</summary>
    public int LocalPort => ContainerPort ?? FreePort;

    public IReadOnlyList<CheckResult> Checks
    {
        get
        {
            if (!Installed)
            {
                return [new CheckResult(Category, "Docker", CheckOutcome.Warning, "Não instalado. É opcional: serve para ter um banco PostgreSQL neste computador.")];
            }

            List<CheckResult> checks =
            [
                new(Category, "Docker", Version is null ? CheckOutcome.Warning : CheckOutcome.Pass,
                    Version is null ? $"Versão ilegível em {Path}" : $"{Version} — {Path}"),
                EngineRunning
                    ? new(Category, "Docker Engine", CheckOutcome.Pass, EngineVersion is { Length: > 0 } engine ? $"Rodando — {engine}" : "Rodando.")
                    : new(Category, "Docker Engine", CheckOutcome.Warning, EngineProblem ?? "Parado."),
            ];

            if (EngineRunning)
            {
                var address = $"{LocalPostgresContainer.Name} em {LocalPostgresContainer.Host}:{LocalPort}";

                checks.Add(Container switch
                {
                    LocalContainerState.Running => new(Category, "Banco local", CheckOutcome.Pass, $"{address} — {ContainerStatus}"),
                    LocalContainerState.Stopped => new(Category, "Banco local", CheckOutcome.Warning, $"{address} — {ContainerStatus}"),
                    LocalContainerState.Missing => new(Category, "Banco local", CheckOutcome.Warning, "Ainda não criado. Os passos estão abaixo."),
                    _ => new(Category, "Banco local", CheckOutcome.Warning, "Não foi possível perguntar ao Docker."),
                });
            }

            return checks;
        }
    }
}

/// <summary>
/// O banco local no Docker (ADR-083): um PostgreSQL 17 que fica sempre ligado,
/// só para este computador. O app monta os comandos; quem roda é o usuário.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>--restart unless-stopped</c>: volta sozinho toda vez que o Docker inicia.</item>
/// <item>O volume guarda os dados mesmo se o container for apagado e criado de novo.</item>
/// <item><c>127.0.0.1</c>: a porta não fica aberta para a rede.</item>
/// <item>A imagem 17 casa com as ferramentas 17 que o Diagnóstico ensina a instalar:
/// um <c>pg_restore</c> 17+ não restaura num servidor 16 ou mais antigo.</item>
/// </list>
/// </remarks>
public static class LocalPostgresContainer
{
    public const string Name = "mytaskapp-postgres";

    public const string Volume = "mytaskapp-postgres-data";

    public const string Image = "postgres:17";

    /// <summary>O endereço da porta publicada: <c>localhost</c> pode virar <c>::1</c>, que o container não escuta.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>
    /// A porta de sempre, quando está livre. Ocupada — por um PostgreSQL
    /// instalado ou por outro container —, o <c>docker run</c> falharia com
    /// "port is already allocated"; por isso a porta é a primeira livre de
    /// <see cref="CandidatePorts"/>, e o "Cadastrar este banco" a preenche.
    /// </summary>
    public const int PreferredPort = 5432;

    public static IReadOnlyList<int> CandidatePorts { get; } = Enumerable.Range(PreferredPort, 11).ToList();

    public const string User = "postgres";

    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>
    /// Só letras e números: o comando é colado no PowerShell, no cmd ou no bash,
    /// e nenhum deles precisa de aspas nem de escape para ela.
    /// </summary>
    public static string NewPassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 20);

    public static string RunCommand(string password, int port)
    {
        if (string.IsNullOrEmpty(password) || !password.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("A senha do banco local tem só letras e números.", nameof(password));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return $"docker run -d --name {Name} --restart unless-stopped -e POSTGRES_PASSWORD={password} "
            + $"-p {Host}:{port}:5432 -v {Volume}:/var/lib/postgresql/data {Image}";
    }

    public static string StartCommand => $"docker start {Name}";

    public static string StopCommand => $"docker stop {Name}";

    public static string StatusCommand => $"docker ps -a --filter name={Name}";

    public static string LogsCommand => $"docker logs --tail 50 {Name}";

    /// <summary>Os passos do banco para o estado do container. Sem container (ou sem saber), os de criar.</summary>
    public static IReadOnlyList<InstallStep> Steps(LocalContainerState state, string password, int port) => state switch
    {
        LocalContainerState.Running =>
        [
            new($"O banco está rodando em {Host}:{port}, usuário {User}. Se ainda não cadastrou a conexão, clique em \"Cadastrar este banco\"."),
            new("Para parar o banco:", StopCommand),
            new("Para ligar de novo:", StartCommand),
            new("Se algo der errado, as últimas linhas do log:", LogsCommand),
        ],
        LocalContainerState.Stopped =>
        [
            new("O banco existe, mas está parado. Para ligar:", StartCommand),
            new("Ligado, ele volta sozinho junto com o Docker — a menos que seja parado com docker stop."),
            new("Se ele não ficar ligado, as últimas linhas do log:", LogsCommand),
        ],
        _ =>
        [
            new("Num terminal (PowerShell, cmd ou Terminal), crie o banco com o comando abaixo. "
                + "Na primeira vez ele baixa o PostgreSQL 17, uns 150 MB.", RunCommand(password, port)),
            new($"Ele fica sempre ligado: volta sozinho toda vez que o Docker iniciar. Os dados ficam no volume {Volume}, "
                + $"e a porta {port} — a primeira livre neste computador — só aceita conexões dele."),
            new("Confira: a coluna STATUS deve começar com \"Up\".", StatusCommand),
            new("Volte aqui, clique em \"Verificar novamente\" e depois em \"Cadastrar este banco\": "
                + "a conexão vem preenchida, com a senha que está no comando."),
        ],
    };
}
