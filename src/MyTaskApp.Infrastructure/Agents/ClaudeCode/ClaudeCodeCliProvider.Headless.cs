using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Storage;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// O Claude sem terminal (ADR-089): <c>claude -p</c> com a saída em
/// <c>stream-json</c>, para a sessão pedida de longe.
/// </summary>
/// <remarks>
/// <para>A linha de comando, sempre nesta forma:</para>
/// <code>
/// claude -p --restricted --output-format stream-json --verbose
///        --session-id &lt;id&gt; | --resume &lt;id&gt;
///        --tools &lt;do perfil&gt; [--allowedTools …] [--disallowedTools …]
///        --strict-mcp-config
///        --mcp-config &lt;só o de aprovação&gt; --permission-prompt-tool mcp__&lt;servidor&gt;__&lt;ferramenta&gt;
///          | --permission-prompts none
///        --settings &lt;permissions.deny&gt;
///        [--model …] [--effort …] --max-turns n --max-budget-usd x
///        &lt; prompt pelo stdin
/// </code>
/// <para>
/// Os <see cref="DefaultArguments"/> do card (o <c>--dangerously-skip-permissions</c>)
/// nunca entram aqui, e nada que pule as permissões passa, venha de onde vier:
/// <see cref="EnsureNoBypass"/> confere a linha pronta. O <c>--restricted</c>
/// é a segunda trava: o próprio Claude recusa os dois com ele.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeCliProvider
{
    /// <summary>
    /// A versão em que os flags do modo foram conferidos (arquitetura, seção 7).
    /// <c>--permission-prompt-tool</c> e <c>--max-turns</c> não aparecem na
    /// ajuda; antes dela, podem nem existir.
    /// </summary>
    public static readonly Version MinimumHeadlessVersion = new(2, 1, 309);

    /// <summary>
    /// Os segredos que nenhum perfil lê nem edita: as chaves do usuário, as
    /// credenciais de ferramentas e os <c>.env</c> do repositório.
    /// </summary>
    internal static readonly string[] DeniedHomePaths =
    [
        "~/.ssh/**",
        "~/.aws/**",
        "~/.azure/**",
        "~/.config/gh/**",
        "~/.config/gcloud/**",
        "~/.gnupg/**",
        "~/.kube/**",
        "~/.docker/config.json",
        "~/.git-credentials",
        "~/.netrc",
        "~/.pgpass",
        "~/.claude/**",
        "**/.env",
        "**/.env.*",
    ];

    public string? HeadlessUnavailableReason(CliDetectionResult detection)
    {
        if (!detection.IsInstalled || detection.ExecutablePath is null)
        {
            return "O Claude Code não está instalado nesta máquina.";
        }

        if (ParseComparableVersion(detection.Version) is not { } version)
        {
            return "Não foi possível ler a versão do Claude Code nesta máquina. Rode \"claude --version\" no terminal.";
        }

        return version < MinimumHeadlessVersion
            ? $"O Claude Code {detection.Version} desta máquina é antigo para rodar sem terminal. Atualize para a {MinimumHeadlessVersion} ou mais nova (\"claude update\")."
            : null;
    }

    public HeadlessAgentLaunch CreateHeadlessLaunch(HeadlessAgentRequest request, CliDetectionResult detection)
    {
        var executable = detection.ExecutablePath
            ?? throw new InvalidOperationException("O Claude Code não foi encontrado.");

        if (request.Profile.Tools.Count == 0)
        {
            throw new DomainException($"O perfil {request.Profile.Name} não libera nenhuma ferramenta.");
        }

        var model = request.Model is null ? null : AgentCliOption.Find(Models, request.Model)
            ?? throw new DomainException($"Modelo desconhecido: {request.Model}.");
        var effort = request.Effort is null ? null : AgentCliOption.Find(Efforts, request.Effort)
            ?? throw new DomainException($"Esforço desconhecido: {request.Effort}.");

        var directory = Path.Combine(hooks.StateDirectory, "headless");
        Directory.CreateDirectory(directory);
        var stamp = Guid.NewGuid().ToString("N");
        var files = new List<string>();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        var arguments = new List<string>
        {
            "-p",
            "--restricted",
            "--output-format", "stream-json",
            "--verbose",
            request.Resume ? "--resume" : "--session-id", request.ConversationId.ToString(),
            "--tools", string.Join(',', request.Profile.Tools),
        };

        if (request.Profile.AllowedTools.Count > 0)
        {
            arguments.AddRange(["--allowedTools", string.Join(',', request.Profile.AllowedTools)]);
        }

        if (request.Profile.DisallowedTools.Count > 0)
        {
            arguments.AddRange(["--disallowedTools", string.Join(',', request.Profile.DisallowedTools)]);
        }

        arguments.Add("--strict-mcp-config");

        if (request.Approval is { } approval)
        {
            var mcp = Path.Combine(directory, $"{stamp}.mcp.json");
            Write(mcp, ApprovalMcpConfigJson(approval));
            files.Add(mcp);
            environment[approval.TokenVariable] = approval.Token;

            arguments.AddRange([
                "--mcp-config", mcp,
                "--permission-prompt-tool", $"mcp__{approval.ServerName}__{approval.ToolName}",
            ]);
        }
        else
        {
            arguments.AddRange(["--permission-prompts", "none"]);
        }

        // Os dados do app (o banco, o cofre, os logs) entram sempre, além dos que o pedido trouxer.
        var settings = Path.Combine(directory, $"{stamp}.settings.json");
        Write(settings, HeadlessSettingsJson([.. request.DeniedPaths, .. AppDataPaths()]));
        files.Add(settings);
        arguments.AddRange(["--settings", settings]);

        arguments.AddRange(model?.Arguments ?? []);
        arguments.AddRange(effort?.Arguments ?? []);
        arguments.AddRange([
            "--max-turns", request.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--max-budget-usd", request.MaxBudgetUsd.ToString("0.00", CultureInfo.InvariantCulture),
        ]);

        EnsureNoBypass(arguments);

        return new HeadlessAgentLaunch(executable, arguments, request.WorkingDirectory, environment, files);
    }

    public IReadOnlyList<HeadlessAgentEvent> ReadEvents(string line) => ClaudeStreamJsonReader.Read(line);

    public string Summarize(string tool, JsonElement input) => ClaudeStreamJsonReader.Summarize(tool, input);

    /// <summary>
    /// A última trava daqui: nenhum argumento que pule as permissões ou troque o
    /// modo delas, mesmo dentro de um padrão de ferramenta.
    /// </summary>
    internal static void EnsureNoBypass(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Contains("dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("bypassPermissions", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--permission-mode", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--allow-dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("A sessão sem terminal nunca pula as permissões.");
            }
        }
    }

    /// <summary>
    /// O <c>--settings</c> da sessão: só o <c>permissions.deny</c>. O <c>-p</c>
    /// ignora em silêncio um arquivo inválido, então ele é gerado aqui, e um
    /// teste confere que é JSON.
    /// </summary>
    /// <remarks>
    /// Caminho absoluto em regra do Claude é <c>//caminho</c>; no Windows, na
    /// forma POSIX (<c>C:\Users</c> vira <c>//c/Users</c>).
    /// </remarks>
    internal static string HeadlessSettingsJson(IEnumerable<string> deniedPaths)
    {
        var patterns = DeniedHomePaths.Concat(deniedPaths.Select(path => AbsoluteRule(path) + "/**")).ToList();
        var deny = new JsonArray();

        foreach (var pattern in patterns)
        {
            deny.Add($"Read({pattern})");
            deny.Add($"Edit({pattern})");
        }

        var settings = new JsonObject
        {
            ["permissions"] = new JsonObject { ["deny"] = deny },
        };

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>O MCP de aprovação, e só ele: o segredo vai no ambiente, o arquivo só cita a variável.</summary>
    internal static string ApprovalMcpConfigJson(HeadlessApprovalAccess approval) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [approval.ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = approval.Endpoint.ToString(),
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = "Bearer ${" + approval.TokenVariable + "}",
                    },
                },
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    internal static string AbsoluteRule(string path)
    {
        var full = path.Replace('\\', '/').TrimEnd('/');

        if (full.Length >= 2 && full[1] == ':')
        {
            full = "/" + char.ToLowerInvariant(full[0]) + full[2..];
        }

        return "/" + (full.StartsWith('/') ? full : "/" + full);
    }

    private static IEnumerable<string> AppDataPaths() =>
        new[] { UserDataLocation.Current.Root, UserDataLocation.Current.LocalRoot }.Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>O <c>x.y.z</c> do começo da versão, para comparar; <c>null</c> se não houver.</summary>
    internal static Version? ParseComparableVersion(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var numeric = new string(version.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(numeric, out var parsed) ? parsed : null;
    }
}
