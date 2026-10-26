using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// O Claude Code CLI (<c>claude</c>) como agente de desenvolvimento (ADR-030).
/// É o único lugar do app que sabe o nome do executável, onde ele costuma
/// morar e como pedir a versão.
/// </summary>
/// <remarks>
/// <para>
/// No Windows há duas instalações comuns: o instalador nativo, que deixa um
/// <c>claude.exe</c> em <c>%USERPROFILE%\.local\bin</c>, e o npm, que deixa um
/// <c>claude.cmd</c> em <c>%APPDATA%\npm</c>. As duas pastas entram como
/// conhecidas, porque o PATH de quem acabou de instalar pode ainda não tê-las.
/// </para>
/// <para>
/// A versão vem de <c>claude --version</c>, que responde e sai — nunca abre
/// sessão interativa. Se ela não vier, o Claude continua "instalado": achar o
/// executável é o que importa para abrir; a versão é informação.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeCliProvider(
    ExecutableLocator locator,
    IProcessRunner runner,
    ClaudeCodeHooks hooks,
    ILogger<ClaudeCodeCliProvider> logger) : IAgentCliProvider, IConsultationLaunchBuilder, IHeadlessAgentProvider
{
    public const string ProviderId = "claude-code";

    /// <summary>O nome do servidor MCP do app na configuração da consulta: as ferramentas viram <c>mcp__mytaskapp__…</c>.</summary>
    public const string ConsultationServerName = "mytaskapp";

    /// <summary>As únicas ferramentas próprias do Claude numa consulta: ler e procurar arquivos.</summary>
    public const string ConsultationBuiltInTools = "Read,Grep,Glob";

    /// <summary>Os arquivos de consulta mais velhos que isso são apagados ao gravar um novo.</summary>
    private static readonly TimeSpan ConsultationFilesLifetime = TimeSpan.FromDays(30);

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public string Id => ProviderId;

    public string Name => "Claude Code";

    public string Command => "claude";

    /// <summary>
    /// O worktree é um lugar descartável e isolado, feito para o agente
    /// trabalhar sem parar a cada arquivo — daí abrir sem pedir permissões.
    /// O usuário troca no card do agente.
    /// </summary>
    public string DefaultArguments => "--dangerously-skip-permissions";

    /// <summary>
    /// Os apelidos do <c>--model</c>: cada um aponta para a versão mais nova
    /// daquela família, então a lista não envelhece a cada modelo novo
    /// (ADR-040).
    /// </summary>
    public IReadOnlyList<AgentCliOption> Models { get; } =
    [
        Model("fable", "Fable"),
        Model("opus", "Opus"),
        Model("sonnet", "Sonnet"),
        Model("haiku", "Haiku"),
    ];

    /// <summary>Os níveis do <c>--effort</c>, do mais rápido ao mais caprichado.</summary>
    public IReadOnlyList<AgentCliOption> Efforts { get; } =
    [
        Effort("low", "Baixo"),
        Effort("medium", "Médio"),
        Effort("high", "Alto"),
        Effort("xhigh", "Muito alto"),
        Effort("max", "Máximo"),
    ];

    public async Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var executable = Locate();

        if (executable is null)
        {
            return CliDetectionResult.NotInstalled("Claude Code não encontrado.");
        }

        return new CliDetectionResult
        {
            IsInstalled = true,
            ExecutablePath = executable,
            Version = await ReadVersionAsync(executable, cancellationToken),
        };
    }

    public AgentCliInstallGuide? InstallGuideFor(OSPlatform platform) =>
        platform == OSPlatform.Windows ? WindowsInstallationGuide.Guide
        : platform == OSPlatform.Linux ? LinuxInstallationGuide.Guide
        : null;

    /// <summary>
    /// O Claude abre a sessão interativa na pasta em que nasceu — o worktree.
    /// Primeiro os parâmetros escolhidos no card. Com texto, ele vai depois, como
    /// a primeira mensagem: <c>claude [parâmetros] "texto"</c> executa direto;
    /// <c>claude [parâmetros] --permission-mode plan "texto"</c> só monta o plano
    /// e espera aprovação antes de alterar arquivos.
    /// </summary>
    /// <remarks>
    /// O texto é <b>um</b> argumento, nunca uma linha de shell. A exceção é o
    /// <c>claude.cmd</c> do npm: o Windows o roda pelo <c>cmd.exe</c>, que corta
    /// o texto na quebra de linha e interpreta <c>%</c> e <c>"</c>. Ali as quebras
    /// viram espaço, e o que o <c>cmd.exe</c> estragaria é recusado com o motivo.
    /// <para>
    /// Com acompanhamento (ADR-037), <c>--settings &lt;arquivo de hooks&gt;</c> vem
    /// antes de tudo, e o ambiente leva a sessão e o segredo. Antes dos
    /// parâmetros do usuário de propósito: se ele passar o próprio
    /// <c>--settings</c>, o dele vale — perde-se o acompanhamento, e não a
    /// configuração dele.
    /// </para>
    /// </remarks>
    public TerminalLaunchOptions CreateLaunch(AgentCliStartContext context, CliDetectionResult detection)
    {
        var executable = detection.ExecutablePath
            ?? throw new InvalidOperationException("O Claude Code não foi encontrado.");

        IReadOnlyList<string> monitoring = context.Monitoring is { } settings
            ? ["--settings", hooks.EnsureSettingsFile(settings.Endpoint)]
            : [];

        if (string.IsNullOrWhiteSpace(context.Prompt))
        {
            return new TerminalLaunchOptions(
                executable,
                [.. monitoring, .. context.Arguments],
                context.WorkingDirectory,
                context.Monitoring?.Environment);
        }

        var prompt = RunsThroughCmd(executable) ? ForCmd(context.Prompt) : context.Prompt;

        string[] arguments = context.RunDirectly
            ? [.. monitoring, .. context.Arguments, prompt]
            : [.. monitoring, .. context.Arguments, "--permission-mode", "plan", prompt];

        return new TerminalLaunchOptions(executable, arguments, context.WorkingDirectory, context.Monitoring?.Environment);
    }

    public string? MonitoringUnavailableReason(string workingDirectory, Uri endpoint) =>
        hooks.UnavailableReason(workingDirectory, endpoint);

    /// <summary>
    /// A Consulta Inteligente (ADR-081): o Claude aberto só para ler.
    /// <list type="bullet">
    /// <item><c>--restricted</c> tira tudo que executa comando ou código (Bash, PowerShell…) e o WebFetch, prende as ferramentas de arquivo às pastas da consulta e recusa <c>bypassPermissions</c>;</item>
    /// <item><c>--tools Read,Grep,Glob</c> deixa, das ferramentas do Claude, só ler e procurar — sem Edit nem Write;</item>
    /// <item><c>--strict-mcp-config</c> ignora os servidores MCP do usuário; com banco, entra só o do app, e só as ferramentas de consulta são pré-aprovadas;</item>
    /// <item>as instruções vão num arquivo (<c>--append-system-prompt-file</c>), e o segredo do MCP vai no ambiente — o arquivo do MCP só cita a variável.</item>
    /// </list>
    /// Os "Parâmetros" do card de Desenvolvimento não entram: o
    /// <c>--dangerously-skip-permissions</c> de lá nunca chega aqui.
    /// </summary>
    /// <remarks>
    /// <para>
    /// As opções que aceitam vários valores (<c>--mcp-config</c>,
    /// <c>--allowedTools</c>, <c>--add-dir</c>) vêm antes do
    /// <c>--append-system-prompt-file</c>, que aceita um só: a pergunta, no
    /// fim, nunca é lida como mais um valor delas.
    /// </para>
    /// <para>
    /// Pelo <c>claude.cmd</c> do npm, a pergunta com aspas ou <c>%</c> vai para
    /// o fim das instruções, e a primeira mensagem só aponta para ela: o
    /// <c>cmd.exe</c> estragaria o texto.
    /// </para>
    /// </remarks>
    public TerminalLaunchOptions CreateConsultationLaunch(ConsultationLaunchRequest request, CliDetectionResult detection)
    {
        var executable = detection.ExecutablePath
            ?? throw new InvalidOperationException("O Claude Code não foi encontrado.");

        if (request.Directories.Count == 0)
        {
            throw new InvalidOperationException("A consulta precisa de pelo menos uma pasta.");
        }

        var instructions = request.Instructions;
        var question = request.Question;

        if (question is not null && RunsThroughCmd(executable))
        {
            if (question.Contains('"', StringComparison.Ordinal) || question.Contains('%', StringComparison.Ordinal))
            {
                instructions = $"{instructions.TrimEnd()}\n\n## Pergunta inicial\n\n{question.Trim()}\n";
                question = "Responda à pergunta inicial que está nas suas instruções, na seção Pergunta inicial.";
            }
            else
            {
                question = ForCmd(question);
            }
        }

        var directory = Path.Combine(hooks.StateDirectory, "consultations");
        Directory.CreateDirectory(directory);
        PruneConsultationFiles(directory);

        var instructionsPath = Path.Combine(directory, $"{request.SessionId:N}.md");
        Write(instructionsPath, instructions);

        var arguments = new List<string>();

        if (request.Monitoring is { } monitoring)
        {
            arguments.AddRange(["--settings", hooks.EnsureSettingsFile(monitoring.Endpoint)]);
        }

        arguments.AddRange(request.Resume
            ? ["--resume", request.ConversationId]
            : ["--session-id", request.ConversationId]);

        arguments.AddRange(["--name", Title(request.Title), "--restricted", "--tools", ConsultationBuiltInTools, "--strict-mcp-config"]);

        if (request.Tools is { } tools)
        {
            var mcpPath = Path.Combine(directory, $"{request.SessionId:N}.mcp.json");
            Write(mcpPath, McpConfigJson(tools));

            arguments.AddRange([
                "--mcp-config", mcpPath,
                "--allowedTools", string.Join(',', tools.Tools.Select(tool => $"mcp__{ConsultationServerName}__{tool}")),
            ]);
        }

        foreach (var extra in request.Directories.Skip(1))
        {
            arguments.AddRange(["--add-dir", extra]);
        }

        arguments.AddRange(request.ExtraArguments);
        arguments.AddRange(["--append-system-prompt-file", instructionsPath]);

        if (!string.IsNullOrWhiteSpace(question))
        {
            arguments.Add(question);
        }

        var environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal);

        if (request.Monitoring is { } watched)
        {
            foreach (var (name, value) in watched.Environment)
            {
                environment[name] = value;
            }
        }

        return new TerminalLaunchOptions(executable, arguments, request.Directories[0], environment);
    }

    /// <summary>
    /// O servidor MCP do app para a consulta. O cabeçalho cita a variável de
    /// ambiente, e o Claude a expande ao conectar: o arquivo não guarda segredo.
    /// </summary>
    public static string McpConfigJson(ConsultationToolsAccess tools)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ConsultationServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = tools.Endpoint.ToString(),
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = "Bearer ${" + tools.TokenVariable + "}",
                    },
                },
            },
        };

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>O nome da sessão no Claude: uma linha, sem o que o <c>cmd.exe</c> interpretaria.</summary>
    private static string Title(string title)
    {
        var clean = new string(title.Where(c => !char.IsControl(c) && c is not ('"' or '%')).ToArray()).Trim();
        return clean.Length <= 80 ? clean : clean[..80].TrimEnd();
    }

    private static void Write(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>As consultas antigas já não precisam dos arquivos — o <c>--resume</c> grava os seus de novo.</summary>
    private void PruneConsultationFiles(string directory)
    {
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > ConsultationFilesLifetime)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "ConsultationFilesPruneFailed");
        }
    }

    private static AgentCliOption Model(string alias, string label) => new(alias, label, ["--model", alias]);

    private static AgentCliOption Effort(string level, string label) => new(level, label, ["--effort", level]);

    private static bool RunsThroughCmd(string executable) =>
        Path.GetExtension(executable).ToUpperInvariant() is ".CMD" or ".BAT";

    private static string ForCmd(string prompt)
    {
        if (prompt.Contains('"', StringComparison.Ordinal) || prompt.Contains('%', StringComparison.Ordinal))
        {
            throw new DomainException(
                "O Claude Code instalado pelo npm (claude.cmd) não recebe texto com aspas (\") ou %. "
                + "Tire esses caracteres do texto ou use o instalador nativo do Claude Code.");
        }

        return string.Join(' ', prompt.Split(["\r\n", "\n", "\r"], StringSplitOptions.None));
    }

    /// <summary>O caminho absoluto do <c>claude</c>, ou <c>null</c>.</summary>
    public string? Locate() => locator.Locate(FileNames(), KnownDirectories());

    public IEnumerable<string> Candidates() => locator.Candidates(FileNames(), KnownDirectories());

    /// <summary>A primeira versão <c>x.y.z</c> da saída de <c>claude --version</c>.</summary>
    public static string? ParseVersion(string output) =>
        VersionPattern().Match(output) is { Success: true } match ? match.Value : null;

    private string[] FileNames() =>
        locator.IsWindows ? ["claude.exe", "claude.cmd"] : ["claude"];

    private IEnumerable<string> KnownDirectories()
    {
        var home = locator.IsWindows ? locator.Variable("USERPROFILE") : locator.Variable("HOME");

        if (home is not null)
        {
            yield return Path.Combine(home, ".local", "bin");
        }

        if (locator.IsWindows)
        {
            if (locator.Variable("APPDATA") is { } roaming)
            {
                yield return Path.Combine(roaming, "npm");
            }

            yield break;
        }

        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
    }

    private async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(
                new ProcessRequest(executable, ["--version"], VersionTimeout),
                cancellationToken);

            if (result is { TimedOut: false, ExitCode: 0 } && ParseVersion(result.StandardOutput) is { } version)
            {
                return version;
            }

            logger.LogWarning(
                "ClaudeCodeVersionUnavailable {ExitCode} {TimedOut}",
                result.ExitCode,
                result.TimedOut);
        }
        catch (ProcessStartException exception)
        {
            logger.LogWarning(exception, "ClaudeCodeVersionUnavailable");
        }

        return null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+[0-9A-Za-z.\-+]*", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
