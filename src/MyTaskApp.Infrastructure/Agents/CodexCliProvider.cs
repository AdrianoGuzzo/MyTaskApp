using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Agents.Codex;

internal sealed partial class CodexCliProvider(ExecutableLocator locator, IProcessRunner runner) : IAgentCliProvider, IConsultationLaunchBuilder, IHeadlessAgentProvider
{
    private volatile bool _supportsHooks;
    public string Id => "codex";
    public string Name => "Codex";
    public string Command => "codex";
    public string DefaultArguments => "--dangerously-bypass-approvals-and-sandbox";

    public bool SupportsTerminalPermissionChoices => false;

    public IReadOnlyList<AgentCliOption> Efforts { get; } =
    [
        new("low", "Baixo", ["-c", "model_reasoning_effort=\"low\""]),
        new("medium", "Médio", ["-c", "model_reasoning_effort=\"medium\""]),
        new("high", "Alto", ["-c", "model_reasoning_effort=\"high\""]),
        new("xhigh", "Muito alto", ["-c", "model_reasoning_effort=\"xhigh\""]),
    ];

    public string? HeadlessUnavailableReason(CliDetectionResult detection) => !detection.IsInstalled
        ? "Codex não encontrado."
        : "O Codex não oferece um teto de custo em dólares equivalente ao das conversas gerenciadas. Use o terminal do agente, que também permite acesso remoto.";

    public HeadlessAgentLaunch CreateHeadlessLaunch(HeadlessAgentRequest request, CliDetectionResult detection) =>
        throw new DomainException(HeadlessUnavailableReason(detection)!);

    public IReadOnlyList<HeadlessAgentEvent> ReadEvents(string line) => [];

    public string Summarize(string tool, JsonElement input) => tool;

    public TerminalLaunchOptions CreateConsultationLaunch(ConsultationLaunchRequest request, CliDetectionResult detection)
    {
        List<string> arguments = [.. request.ExtraArguments.Where(argument => argument != DefaultArguments), "--sandbox", "read-only"];
        arguments.AddRange(["-c", "developer_instructions=" + JsonSerializer.Serialize(request.Instructions)]);
        foreach (var directory in request.Directories.Skip(1))
            arguments.AddRange(["--add-dir", directory]);
        if (request.Tools is { } tools)
        {
            arguments.AddRange(["-c", "mcp_servers.mytaskapp-consulta.url=" + JsonSerializer.Serialize(tools.Endpoint.ToString()),
                "-c", "mcp_servers.mytaskapp-consulta.bearer_token_env_var=" + JsonSerializer.Serialize(tools.TokenVariable),
                "-c", "mcp_servers.mytaskapp-consulta.enabled_tools=" + JsonSerializer.Serialize(tools.Tools)]);
        }
        var launch = CreateLaunch(new AgentCliStartContext(Guid.Empty, request.Directories[0], arguments,
            request.Question, RunDirectly: true, request.Monitoring,
            request.Resume ? new AgentCliTerminal(request.ConversationId, true) : null), detection);
        return launch with { Environment = request.Environment };
    }

    public async Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var npm = Path.Combine(locator.Variable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var executable = locator.Locate(locator.IsWindows ? ["codex.exe", "codex.cmd"] : ["codex"], locator.IsWindows ? [npm] : []);
        if (locator.IsWindows && executable?.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) == true)
        {
            var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var target = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            var native = Path.Combine(Path.GetDirectoryName(executable)!, "node_modules", "@openai", "codex", "node_modules",
                "@openai", $"codex-win32-{architecture}", "vendor", target, "bin");
            executable = locator.Locate(["codex.exe"], [native]) ?? executable;
        }
        if (executable is null)
            return CliDetectionResult.NotInstalled("Codex não encontrado.");
        string? version = null;
        try
        {
            var result = await runner.RunAsync(new ProcessRequest(executable, ["--version"], TimeSpan.FromSeconds(10)), cancellationToken);
            if (result is { ExitCode: 0, TimedOut: false })
                version = VersionPattern().Match(result.StandardOutput) is { Success: true } match ? match.Value : null;
        }
        catch (ProcessStartException)
        {
            // Encontrar o executável basta para oferecer a abertura interativa.
        }
        _supportsHooks = Version.TryParse(version?.Split(['-', '+'])[0], out var parsed) && parsed >= new Version(0, 169, 0);
        return new CliDetectionResult { IsInstalled = true, ExecutablePath = executable, Version = version };
    }

    public AgentCliInstallGuide? InstallGuideFor(OSPlatform platform) => new(
        "Instalar Codex",
        [new AgentCliInstallStep("Instale o Codex CLI e autentique sua conta.", "npm install -g @openai/codex"),
         new AgentCliInstallStep("Entre na sua conta.", "codex login")],
        new Uri("https://developers.openai.com/codex/cli"));

    public string? MonitoringUnavailableReason(string workingDirectory, Uri endpoint) =>
        !_supportsHooks ? "O acompanhamento precisa do Codex 0.169.0 ou mais recente, com versão identificável."
        : Curl() is null ? "O acompanhamento do Codex precisa do curl instalado." : null;

    public bool BypassesPermissions(IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (arguments.Any(argument => argument == DefaultArguments || argument.Contains("danger-full-access", StringComparison.Ordinal)))
            return true;
        var home = locator.Variable("CODEX_HOME") ?? Path.Combine(locator.Variable("USERPROFILE") ?? locator.Variable("HOME") ?? string.Empty, ".codex");
        try
        {
            var path = Path.Combine(home, "config.toml");
            return File.Exists(path) && (new FileInfo(path).Length > 1024 * 1024
                || File.ReadAllText(path).Contains("danger-full-access", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Na dúvida, o aviso antes de assumir o controle continua necessário.
            return true;
        }
    }

    public TerminalLaunchOptions CreateLaunch(AgentCliStartContext context, CliDetectionResult detection)
    {
        var executable = detection.ExecutablePath ?? throw new DomainException("Codex não encontrado.");
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && context.Monitoring is not null)
            throw new DomainException("O acompanhamento precisa do executável nativo do Codex. Reinstale o pacote oficial ou abra sem acompanhamento.");
        List<string> arguments = [.. context.Arguments];
        if (context.Terminal is { Resume: true } terminal)
            arguments.InsertRange(0, ["resume", terminal.ConversationId]);
        if (context.Monitoring is { } monitoring)
            arguments.AddRange(HookArguments(monitoring.Endpoint));
        if (!context.RunDirectly && !string.IsNullOrWhiteSpace(context.Prompt))
        {
            arguments.RemoveAll(argument => argument == DefaultArguments);
            arguments.AddRange(["--sandbox", "read-only", "-c",
                "developer_instructions=" + JsonSerializer.Serialize("Somente planeje a implementação. Não altere arquivos nem execute ações com efeitos colaterais. Aguarde a aprovação do usuário.")]);
        }
        if (!string.IsNullOrWhiteSpace(context.Prompt))
            arguments.Add(context.Prompt);
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            && arguments.Any(argument => argument.IndexOfAny(['\r', '\n', '"', '%', '&', '|', '<', '>', '^']) >= 0))
            throw new DomainException("Este texto ou configuração precisa do executável nativo do Codex. Reinstale o pacote oficial ou cole o texto no terminal.");
        return new TerminalLaunchOptions(executable, arguments, context.WorkingDirectory, context.Monitoring?.Environment,
            RemovedVariables: ["CODEX_THREAD_ID", "CODEX_TURN_ID"]);
    }

    private string? Curl() => locator.Locate(locator.IsWindows ? ["curl.exe"] : ["curl"],
        locator.IsWindows ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")] : ["/usr/bin"]);

    private IReadOnlyList<string> HookArguments(Uri endpoint)
    {
        var curl = Curl() ?? throw new DomainException("O acompanhamento do Codex precisa do curl instalado.");
        string Variable(string name) => locator.IsWindows ? $"%{name}%" : $"${name}";
        var command = $"\"{curl}\" --silent --max-time 5 -X POST -H \"Content-Type: application/json\" "
            + $"-H \"Authorization: Bearer {Variable(AgentMonitoringEnvironment.HookToken)}\" "
            + $"-H \"X-MyTaskApp-Session: {Variable(AgentMonitoringEnvironment.AgentSessionId)}\" "
            + $"-H \"X-MyTaskApp-Task: {Variable(AgentMonitoringEnvironment.TaskId)}\" "
            + $"-H \"X-MyTaskApp-Provider: codex\" --data-binary @- \"{endpoint}\"";
        List<string> result = [];
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop", "SessionEnd" })
        {
            result.Add("-c");
            result.Add($"hooks.{name}=[{{hooks=[{{type=\"command\",command={JsonSerializer.Serialize(command)},timeout=5}}]}}]");
        }
        return result;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+[0-9A-Za-z.\-+]*", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
