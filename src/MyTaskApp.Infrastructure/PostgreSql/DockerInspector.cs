using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Acha o <c>docker</c> e pergunta três coisas (ADR-083): a versão, se o
/// engine responde e o estado e a porta do container do banco local. Só
/// leitura, sem shell e com os argumentos um a um (ADR-027): nunca cria, liga
/// ou para nada. E escolhe a porta livre para um container novo.
/// </summary>
/// <remarks>
/// O Docker Desktop parado deixa o <c>docker.exe</c> no lugar, e o
/// <c>docker info</c> sai com 1 dizendo que não achou o pipe — por isso
/// "instalado" e "rodando" são perguntas separadas.
/// </remarks>
internal sealed class DockerInspector(
    Func<string, EnvironmentVariableTarget, string?> environment,
    Func<string, bool> fileExists,
    Func<int, bool> portInUse,
    bool isWindows,
    bool isMacOs,
    IProcessRunner runner,
    DatabaseOperationsOptions options,
    ILogger<DockerInspector> logger) : IDockerInspector
{
    /// <summary>O estado e as portas publicadas, numa resposta só: <c>running|5433 </c>.</summary>
    internal const string InspectFormat =
        "{{.State.Status}}|{{range $p, $b := .HostConfig.PortBindings}}{{range $b}}{{.HostPort}} {{end}}{{end}}";

    private readonly ExecutableLocator _locator = new(environment, fileExists, isWindows);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DockerEnvironment? _cached;

    public static DockerInspector ForCurrentSystem(
        IProcessRunner runner,
        DatabaseOperationsOptions options,
        ILogger<DockerInspector> logger) =>
        new(Environment.GetEnvironmentVariable, File.Exists, PortInUse, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), runner, options, logger);

    /// <summary>
    /// Alguém já escuta nela, em qualquer endereço: um PostgreSQL instalado
    /// (<c>0.0.0.0:5432</c>) ou a porta de outro container. Só olha a tabela do
    /// sistema; não abre conexão nenhuma.
    /// </summary>
    internal static bool PortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    public async Task<DockerEnvironment> InspectAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!refresh && _cached is not null)
            {
                return _cached;
            }

            _cached = await InspectNowAsync(cancellationToken);
            logger.LogInformation(
                "DockerInspected {Installed} {Running} {Container}",
                _cached.Installed,
                _cached.EngineRunning,
                _cached.Container);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DockerEnvironment> InspectNowAsync(CancellationToken cancellationToken)
    {
        var free = LocalPostgresContainer.CandidatePorts.FirstOrDefault(port => !portInUse(port));
        var docker = await InspectDockerAsync(cancellationToken);

        return docker with { FreePort = free == 0 ? LocalPostgresContainer.PreferredPort : free };
    }

    private async Task<DockerEnvironment> InspectDockerAsync(CancellationToken cancellationToken)
    {
        var system = isWindows ? DockerHost.Windows : isMacOs ? DockerHost.MacOs : DockerHost.Linux;
        var path = _locator.Locate([isWindows ? "docker.exe" : "docker"], KnownDirectories());

        if (path is null)
        {
            return new DockerEnvironment(false, null, null, false, null, null, LocalContainerState.Unknown, null,
                DockerInstallGuides.For(system, installed: false));
        }

        var version = await RunAsync(path, ["--version"], cancellationToken);

        if (version is null)
        {
            // Existe mas não roda: para o usuário, é o mesmo que um Docker parado.
            return new DockerEnvironment(true, path, null, false, null, "O docker existe, mas não abriu.", LocalContainerState.Unknown, null,
                DockerInstallGuides.For(system, installed: true));
        }

        var versionText = version.ExitCode == 0 && version.StandardOutput.Trim() is { Length: > 0 } text ? text : null;
        var info = await RunAsync(path, ["info", "--format", "{{.ServerVersion}}"], cancellationToken);

        if (info is not { ExitCode: 0, TimedOut: false })
        {
            return new DockerEnvironment(true, path, versionText, false, null, EngineProblem(info, system), LocalContainerState.Unknown, null,
                DockerInstallGuides.For(system, installed: true));
        }

        var engineVersion = info.StandardOutput.Trim();
        var (container, status, port) = ContainerOf(await RunAsync(
            path,
            ["container", "inspect", "--format", InspectFormat, LocalPostgresContainer.Name],
            cancellationToken));

        return new DockerEnvironment(true, path, versionText, true, engineVersion.Length > 0 ? engineVersion : null, null, container, status,
            DockerInstallGuides.Running(system))
        {
            ContainerPort = port,
        };
    }

    /// <summary><c>null</c> quando o processo nem abriu.</summary>
    private async Task<ProcessResult?> RunAsync(string path, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(new ProcessRequest(path, arguments, options.VersionTimeout), cancellationToken);
        }
        catch (ProcessStartException)
        {
            return null;
        }
    }

    internal static string EngineProblem(ProcessResult? info, DockerHost system)
    {
        if (info is { TimedOut: true })
        {
            return "O Docker não respondeu a tempo: talvez ainda esteja iniciando.";
        }

        if (info?.StandardError.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Sem permissão para falar com o Docker: entre no grupo docker (veja abaixo).";
        }

        return system == DockerHost.Linux
            ? "Parado: ligue o serviço do Docker (veja abaixo)."
            : "Parado: abra o Docker Desktop.";
    }

    /// <summary>
    /// A resposta do <see cref="InspectFormat"/>: <c>running|5433 </c> é rodando
    /// na 5433; <c>exited|5432 </c>, <c>created|…</c> e os outros, parado; "No such
    /// container", não existe. Qualquer outra falha é não saber.
    /// </summary>
    internal static (LocalContainerState State, string? Status, int? Port) ContainerOf(ProcessResult? inspect)
    {
        if (inspect is null or { TimedOut: true })
        {
            return (LocalContainerState.Unknown, null, null);
        }

        if (inspect.ExitCode != 0)
        {
            return inspect.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase)
                ? (LocalContainerState.Missing, null, null)
                : (LocalContainerState.Unknown, null, null);
        }

        var parts = inspect.StandardOutput.Trim().Split('|', 2);
        var state = parts[0].Trim();
        var port = parts.Length > 1
            ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : (int?)null)
                .FirstOrDefault(value => value is > 0)
            : null;

        var label = state switch
        {
            "running" => "rodando",
            "exited" => "parado",
            "created" => "criado, mas nunca ligado",
            "restarting" => "reiniciando sem parar: veja o log",
            "paused" => "pausado",
            _ => state,
        };

        return (state == "running" ? LocalContainerState.Running : LocalContainerState.Stopped, label, port);
    }

    private IEnumerable<string> KnownDirectories()
    {
        if (isWindows)
        {
            foreach (var variable in (string[])["ProgramFiles", "ProgramW6432"])
            {
                if (_locator.Variable(variable) is { } programFiles)
                {
                    yield return Path.Combine(programFiles, "Docker", "Docker", "resources", "bin");
                }
            }

            yield break;
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
        yield return "/Applications/Docker.app/Contents/Resources/bin";
    }
}

internal enum DockerHost
{
    Windows,

    MacOs,

    Linux,
}

/// <summary>
/// Como instalar e ligar o Docker, por sistema (ADR-083), até o banco local
/// ficar sempre ligado: no Windows e no macOS, o Docker Desktop abrindo com o
/// login; no Linux, o serviço habilitado. Só instruções (ADR-027).
/// </summary>
internal static class DockerInstallGuides
{
    public static readonly Uri OfficialSite = new("https://docs.docker.com/get-started/get-docker/");

    private static readonly string[] Checks = ["docker version", LocalPostgresContainer.StatusCommand];

    private static readonly InstallStep Verify =
        new("Confira: devem aparecer as versões do Client e do Server. Depois, clique em \"Verificar novamente\".", "docker version");

    private static readonly InstallStep StartWithLogin =
        new("Em Settings → General, deixe marcado \"Start Docker Desktop when you sign in to your computer\". "
            + "É isso que mantém o banco local sempre ligado.");

    private static readonly InstallStep LinuxService =
        new("Ligue o Docker e deixe ele subir junto com o computador — é o que mantém o banco local sempre ligado:", "sudo systemctl enable --now docker");

    private static readonly InstallStep LinuxGroup =
        new("Para usar o docker sem sudo (e o MyTaskApp conseguir falar com ele), entre no grupo docker e depois saia e entre de novo na sessão:",
            "sudo usermod -aG docker $USER");

    /// <summary>Os passos para o que falta: instalar, ou só ligar o que já está instalado.</summary>
    public static ToolInstallGuide For(DockerHost system, bool installed) => system switch
    {
        DockerHost.Windows => new ToolInstallGuide(
            "Windows",
            installed
                ?
                [
                    new("Abra o Docker Desktop pelo menu Iniciar e espere aparecer \"Engine running\" no canto da janela."),
                    StartWithLogin,
                    Verify,
                ]
                :
                [
                    new("Aperte a tecla Windows, digite PowerShell, clique com o botão direito e escolha \"Executar como administrador\". "
                        + "Cole o comando abaixo: ele liga o WSL 2, que o Docker usa.", "wsl --install --no-distribution"),
                    new("Reinicie o computador."),
                    new("Num PowerShell comum, instale o Docker Desktop. Aceite se o winget pedir confirmação:", "winget install -e --id Docker.DockerDesktop"),
                    new("Abra o Docker Desktop pelo menu Iniciar e aceite os termos de uso (é grátis para uso pessoal e para empresas pequenas). "
                        + "O login na conta Docker é opcional: pode pular."),
                    StartWithLogin,
                    Verify,
                ],
            Checks,
            OfficialSite),
        DockerHost.MacOs => new ToolInstallGuide(
            "macOS",
            installed
                ?
                [
                    new("Abra o Docker pelo Launchpad (ou pela pasta Aplicativos) e espere o ícone da baleia parar de se mexer."),
                    StartWithLogin,
                    Verify,
                ]
                :
                [
                    new("No Terminal, instale o Docker Desktop pelo Homebrew (ou baixe na página oficial):", "brew install --cask docker-desktop"),
                    new("Abra o Docker pelo Launchpad e aceite os termos de uso. O login na conta Docker é opcional."),
                    StartWithLogin,
                    Verify,
                ],
            Checks,
            OfficialSite),
        _ => new ToolInstallGuide(
            "Linux",
            installed
                ? [LinuxService, LinuxGroup, Verify]
                : [new("Num terminal, instale o Docker Engine pelo script oficial:", "curl -fsSL https://get.docker.com | sudo sh"), LinuxService, LinuxGroup, Verify],
            Checks,
            OfficialSite),
    };

    /// <summary>Com o Docker rodando, não há o que instalar: os passos são os do banco, que a tela monta.</summary>
    public static ToolInstallGuide Running(DockerHost system) =>
        new(system switch { DockerHost.Windows => "Windows", DockerHost.MacOs => "macOS", _ => "Linux" }, [], Checks, OfficialSite);
}
