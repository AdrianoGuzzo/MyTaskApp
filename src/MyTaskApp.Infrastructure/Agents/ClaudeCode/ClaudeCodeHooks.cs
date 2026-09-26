using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// Os hooks com que o Claude Code avisa o app do que está fazendo (ADR-037).
/// </summary>
/// <remarks>
/// <para>
/// <b>Por execução, e não na configuração do usuário.</b> Os hooks moram num
/// arquivo do próprio MyTaskApp, passado com <c>claude --settings &lt;arquivo&gt;</c>
/// só para os Claude que o app abre. O Claude <b>soma</b> esses hooks aos do
/// usuário (<c>~/.claude/settings.json</c>, projeto e local) — nenhum arquivo
/// do usuário é lido para escrever, nenhum hook dele é tocado, e não há o que
/// desinstalar: desligar o acompanhamento é só não passar o arquivo. Um Claude
/// aberto à mão, fora do app, nem fica sabendo que o MyTaskApp existe.
/// </para>
/// <para>
/// <b>Hook HTTP, direto para a porta local.</b> Sem processo intermediário: o
/// Claude manda o JSON do evento para <c>127.0.0.1</c>. A URL é fixa no
/// arquivo; o que identifica a sessão vem nos cabeçalhos, a partir de
/// variáveis do ambiente do processo (<c>allowedEnvVars</c>) — o arquivo é um
/// só para todas as sessões e não guarda segredo nenhum.
/// </para>
/// <para>
/// A configuração do usuário é <b>lida</b>, só para dizer antes quando os hooks
/// não vão rodar (<c>disableAllHooks</c>, <c>allowManagedHooksOnly</c>,
/// <c>allowedHttpHookUrls</c> sem a porta do app).
/// </para>
/// </remarks>
internal sealed class ClaudeCodeHooks(
    string settingsDirectory,
    string userProfile,
    IReadOnlyList<string> managedSettingsFiles,
    ILogger<ClaudeCodeHooks> logger)
{
    /// <summary>Nome do arquivo — o MyTaskApp é o dono dele, e de nada além dele.</summary>
    public const string SettingsFileName = "claude-code-hooks.json";

    /// <summary>Bem acima do que a porta local leva para responder, e curto para nunca prender o Claude.</summary>
    public const int TimeoutSeconds = 5;

    /// <summary>
    /// Os eventos acompanhados e o filtro de cada um. <c>PostToolUse</c> em
    /// qualquer ferramenta é o "voltou a trabalhar" depois de uma permissão ou
    /// pergunta respondida. <c>SubagentStop</c> fica de fora: um subagente
    /// terminar não termina a resposta — o <c>Stop</c> do agente principal vem
    /// depois, e é ele que conta. <c>SessionStart</c> também: o Claude não
    /// dispara hook HTTP nele (conferido com o Claude real — o hook de comando
    /// disparou, o HTTP não), e o <c>session_id</c> vem em todo evento.
    /// </summary>
    public static readonly IReadOnlyList<(string Event, string? Matcher)> Registrations =
    [
        ("UserPromptSubmit", null),
        ("PreToolUse", "AskUserQuestion|ExitPlanMode"),
        ("PostToolUse", "*"),
        ("Notification", null),
        ("Stop", null),
        ("StopFailure", null),
        ("TaskCompleted", null),
        ("SessionEnd", null),
    ];

    private static readonly string[] AllowedEnvVars =
    [
        AgentMonitoringEnvironment.HookToken,
        AgentMonitoringEnvironment.AgentSessionId,
        AgentMonitoringEnvironment.TaskId,
    ];

    private readonly Lock _writeLock = new();

    public static ClaudeCodeHooks ForCurrentSystem(string settingsDirectory, ILogger<ClaudeCodeHooks> logger) =>
        new(
            settingsDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ManagedSettingsFiles(),
            logger);

    public string SettingsPath => Path.Combine(settingsDirectory, SettingsFileName);

    /// <summary>
    /// O conteúdo do arquivo para a porta <paramref name="endpoint"/>. Só os
    /// hooks: <c>--settings</c> sobrepõe chave por chave, então qualquer outra
    /// chave aqui passaria por cima da escolha do usuário.
    /// </summary>
    public static string SettingsJson(Uri endpoint)
    {
        var hooks = new JsonObject();

        foreach (var (eventName, matcher) in Registrations)
        {
            var group = new JsonObject();

            if (matcher is not null)
            {
                group["matcher"] = matcher;
            }

            group["hooks"] = new JsonArray(Hook(endpoint));
            hooks[eventName] = new JsonArray(group);
        }

        var settings = new JsonObject { ["hooks"] = hooks };

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Grava (ou confirma) o arquivo para a porta atual e devolve o caminho.
    /// Só reescreve se mudou, e troca o arquivo de uma vez: um Claude abrindo
    /// agora não pode ler um arquivo pela metade.
    /// </summary>
    public string EnsureSettingsFile(Uri endpoint)
    {
        var content = SettingsJson(endpoint);
        var path = SettingsPath;

        lock (_writeLock)
        {
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content)
            {
                return path;
            }

            Directory.CreateDirectory(settingsDirectory);

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }

        logger.LogInformation("ClaudeCodeHookSettingsWritten {Path} {Endpoint}", path, endpoint);
        return path;
    }

    /// <summary>
    /// Por que os hooks não rodariam num Claude aberto em
    /// <paramref name="workingDirectory"/>; <c>null</c> = nada impede. Lê os
    /// arquivos na ordem de precedência do Claude: gerenciado, usuário,
    /// projeto e local.
    /// </summary>
    public string? UnavailableReason(string workingDirectory, Uri endpoint)
    {
        foreach (var file in managedSettingsFiles)
        {
            if (Read(file) is not { } managed)
            {
                continue;
            }

            if (IsTrue(managed, "disableAllHooks") || IsTrue(managed, "allowManagedHooksOnly"))
            {
                return $"a política gerenciada do Claude Code não permite estes hooks ({file}).";
            }

            if (BlocksUrl(managed, endpoint))
            {
                return $"a política gerenciada do Claude Code não libera a URL {endpoint} (allowedHttpHookUrls em {file}).";
            }
        }

        string[] files =
        [
            Path.Combine(userProfile, ".claude", "settings.json"),
            Path.Combine(workingDirectory, ".claude", "settings.json"),
            Path.Combine(workingDirectory, ".claude", "settings.local.json"),
        ];

        foreach (var file in files)
        {
            if (Read(file) is not { } settings)
            {
                continue;
            }

            if (IsTrue(settings, "disableAllHooks"))
            {
                return $"os hooks do Claude Code estão desligados (disableAllHooks em {file}).";
            }

            if (BlocksUrl(settings, endpoint))
            {
                return $"a URL {endpoint} não está em allowedHttpHookUrls ({file}).";
            }
        }

        return null;
    }

    private static JsonObject Hook(Uri endpoint) =>
        new()
        {
            ["type"] = "http",
            ["url"] = endpoint.ToString(),
            ["headers"] = new JsonObject
            {
                ["Authorization"] = $"Bearer ${AgentMonitoringEnvironment.HookToken}",
                [AgentEventHeaders.Session] = $"${AgentMonitoringEnvironment.AgentSessionId}",
                [AgentEventHeaders.Task] = $"${AgentMonitoringEnvironment.TaskId}",
            },
            ["allowedEnvVars"] = new JsonArray([.. AllowedEnvVars.Select(name => JsonValue.Create(name))]),
            ["timeout"] = TimeoutSeconds,
        };

    /// <summary>
    /// <c>allowedHttpHookUrls</c> presente e sem nenhum padrão que case com a
    /// URL. O <c>*</c> vale qualquer trecho, como na documentação do Claude.
    /// </summary>
    private static bool BlocksUrl(JsonObject settings, Uri endpoint)
    {
        if (settings["allowedHttpHookUrls"] is not JsonArray patterns)
        {
            return false;
        }

        var url = endpoint.ToString();

        return !patterns
            .Select(pattern => pattern?.GetValueKind() == JsonValueKind.String ? pattern.GetValue<string>() : null)
            .OfType<string>()
            .Any(pattern => Regex.IsMatch(
                url,
                "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)));
    }

    private static bool IsTrue(JsonObject settings, string key) =>
        settings[key] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    /// <summary>
    /// Arquivo ausente ou ilegível não impede nada: o Claude também o
    /// ignoraria, ou reclamaria ele mesmo ao abrir.
    /// </summary>
    private JsonObject? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonNode.Parse(
                File.ReadAllText(file),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(exception, "ClaudeCodeSettingsUnreadable {File}", file);
            return null;
        }
    }

    /// <summary>Onde a TI da empresa põe a política do Claude, por sistema.</summary>
    private static IReadOnlyList<string> ManagedSettingsFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "ClaudeCode",
                    "managed-settings.json"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ClaudeCode",
                    "managed-settings.json"),
            ];
        }

        return OperatingSystem.IsMacOS()
            ? ["/Library/Application Support/ClaudeCode/managed-settings.json"]
            : ["/etc/claude-code/managed-settings.json"];
    }
}
