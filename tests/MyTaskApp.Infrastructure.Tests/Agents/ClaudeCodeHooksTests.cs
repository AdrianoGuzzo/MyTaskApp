using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// Os hooks do Claude Code (ADR-037): o arquivo passado com <c>--settings</c>,
/// a leitura da configuração do usuário e a tradução de cada evento.
/// </summary>
public sealed class ClaudeCodeHooksTests : IDisposable
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:47831/api/claude/events");

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 22, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mytaskapp-tests", Guid.NewGuid().ToString("N"));

    private string State => Path.Combine(_root, "state");

    private string Home => Path.Combine(_root, "home");

    private string Worktree => Path.Combine(_root, "worktree");

    private string Managed => Path.Combine(_root, "managed-settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ClaudeCodeHooks Hooks() => new(State, Home, [Managed], NullLogger<ClaudeCodeHooks>.Instance);

    private void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    // --- O arquivo de hooks ------------------------------------------------

    [Fact]
    public void TheSettingsFile_HasOnlyHooks_EachAnHttpCallToTheLocalPort()
    {
        var settings = JsonNode.Parse(ClaudeCodeHooks.SettingsJson(Endpoint))!.AsObject();

        // --settings sobrepõe chave a chave: nada além de "hooks" aqui.
        settings.Select(pair => pair.Key).Should().Equal("hooks");

        var hooks = settings["hooks"]!.AsObject();
        hooks.Select(pair => pair.Key).Should().BeEquivalentTo(
            ClaudeCodeHooks.Registrations.Select(registration => registration.Event));

        foreach (var (_, groups) in hooks)
        {
            var hook = groups![0]!["hooks"]![0]!;

            hook["type"]!.GetValue<string>().Should().Be("http");
            hook["url"]!.GetValue<string>().Should().Be(Endpoint.ToString());
            hook["headers"]!["Authorization"]!.GetValue<string>().Should().Be("Bearer $MYTASKAPP_HOOK_TOKEN");
            hook["headers"]!["X-MyTaskApp-Session"]!.GetValue<string>().Should().Be("$MYTASKAPP_AGENT_SESSION_ID");
            hook["allowedEnvVars"]!.AsArray().Select(name => name!.GetValue<string>())
                .Should().BeEquivalentTo("MYTASKAPP_HOOK_TOKEN", "MYTASKAPP_AGENT_SESSION_ID", "MYTASKAPP_TASK_ID");
            hook["timeout"]!.GetValue<int>().Should().Be(ClaudeCodeHooks.TimeoutSeconds);
        }

        hooks["PreToolUse"]![0]!["matcher"]!.GetValue<string>().Should().Be("AskUserQuestion|ExitPlanMode");
    }

    /// <summary>O arquivo não guarda segredo: vale para todas as sessões.</summary>
    [Fact]
    public void TheSettingsFile_CarriesNoSecret()
    {
        ClaudeCodeHooks.SettingsJson(Endpoint).Should().NotContainAny("Bearer 0", "Bearer A");
    }

    [Fact]
    public void EnsuringTheFile_WritesItOnce_AndRewritesWhenThePortChanges()
    {
        var hooks = Hooks();

        var path = hooks.EnsureSettingsFile(Endpoint);
        var firstWrite = File.GetLastWriteTimeUtc(path);

        hooks.EnsureSettingsFile(Endpoint).Should().Be(path);
        File.GetLastWriteTimeUtc(path).Should().Be(firstWrite);

        var other = new Uri("http://127.0.0.1:50001/api/claude/events");
        hooks.EnsureSettingsFile(other);

        File.ReadAllText(path).Should().Contain("50001");
        Path.GetDirectoryName(path).Should().Be(State);
    }

    // --- A configuração do usuário é lida, nunca escrita -------------------

    [Fact]
    public void WithoutAnyConfiguration_NothingBlocks()
    {
        Hooks().UnavailableReason(Worktree, Endpoint).Should().BeNull();
    }

    [Fact]
    public void TheUsersOwnHooks_DoNotBlock_AndAreNeverTouched()
    {
        var user = Path.Combine(Home, ".claude", "settings.json");
        const string Json = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "beep" } ] } ] } }""";
        Write(user, Json);

        Hooks().UnavailableReason(Worktree, Endpoint).Should().BeNull();
        Hooks().EnsureSettingsFile(Endpoint);

        File.ReadAllText(user).Should().Be(Json);
    }

    [Fact]
    public void DisableAllHooks_InTheUserSettings_Blocks()
    {
        Write(Path.Combine(Home, ".claude", "settings.json"), """{ "disableAllHooks": true }""");

        Hooks().UnavailableReason(Worktree, Endpoint).Should().Contain("disableAllHooks");
    }

    [Fact]
    public void DisableAllHooks_InTheProjectLocalSettings_Blocks()
    {
        Write(Path.Combine(Worktree, ".claude", "settings.local.json"), """{ "disableAllHooks": true }""");

        Hooks().UnavailableReason(Worktree, Endpoint).Should().Contain("settings.local.json");
    }

    [Fact]
    public void AManagedPolicy_OnlyForManagedHooks_Blocks()
    {
        Write(Managed, """{ "allowManagedHooksOnly": true }""");

        Hooks().UnavailableReason(Worktree, Endpoint).Should().Contain("política gerenciada");
    }

    [Theory]
    [InlineData("""{ "allowedHttpHookUrls": ["https://hooks.example.com/*"] }""", true)]
    [InlineData("""{ "allowedHttpHookUrls": ["http://127.0.0.1:*"] }""", false)]
    [InlineData("""{ "allowedHttpHookUrls": ["http://127.0.0.1:47831/api/claude/events"] }""", false)]
    public void AnHttpAllowList_BlocksOnlyWhenItLeavesTheLocalPortOut(string json, bool blocks)
    {
        Write(Path.Combine(Home, ".claude", "settings.json"), json);

        (Hooks().UnavailableReason(Worktree, Endpoint) is not null).Should().Be(blocks);
    }

    [Fact]
    public void AnUnreadableFile_DoesNotBlock()
    {
        Write(Path.Combine(Home, ".claude", "settings.json"), "{ isto não é json");

        Hooks().UnavailableReason(Worktree, Endpoint).Should().BeNull();
    }

    // --- Tradução: estado dos campos, nunca do texto -----------------------

    private static AgentEvent? Translate(string json) =>
        ClaudeCodeHookEvents.Translate(
            JsonDocument.Parse(json).RootElement,
            Guid.Parse("0199a0c0-0000-7000-8000-000000000001"),
            null,
            Now);

    [Theory]
    [InlineData("""{"hook_event_name":"SessionStart","source":"startup"}""", AgentEventType.SessionStarted)]
    [InlineData("""{"hook_event_name":"UserPromptSubmit","prompt":"faça"}""", AgentEventType.Working)]
    [InlineData("""{"hook_event_name":"PostToolUse","tool_name":"Bash"}""", AgentEventType.Working)]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"ExitPlanMode"}""", AgentEventType.NeedsUserInput)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Permitir?"}""", AgentEventType.NeedsUserInput)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"elicitation_dialog"}""", AgentEventType.NeedsUserInput)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"idle_prompt","message":"Esperando"}""", AgentEventType.Notification)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"auth_success"}""", AgentEventType.Notification)]
    [InlineData("""{"hook_event_name":"Stop","stop_hook_active":false}""", AgentEventType.ResponseCompleted)]
    [InlineData("""{"hook_event_name":"StopFailure","error_type":"rate_limit"}""", AgentEventType.SessionFailed)]
    [InlineData("""{"hook_event_name":"TaskCompleted"}""", AgentEventType.TaskCompleted)]
    [InlineData("""{"hook_event_name":"SessionEnd","reason":"prompt_input_exit"}""", AgentEventType.SessionStopped)]
    [InlineData("""{"hook_event_name":"UmEventoNovo"}""", AgentEventType.Notification)]
    public void EachHook_BecomesItsEvent(string json, AgentEventType expected)
    {
        Translate(json)!.Type.Should().Be(expected);
    }

    /// <summary>"Terminou" e um "?" no fim da resposta não mudam nada: só o nome do evento decide.</summary>
    [Fact]
    public void TheTextNeverDecidesTheState()
    {
        var stop = Translate("""{"hook_event_name":"Stop","last_assistant_message":"Devo usar Redis ou MemoryCache?"}""")!;
        var notification = Translate("""{"hook_event_name":"Notification","notification_type":"idle_prompt","message":"finished"}""")!;

        stop.Type.Should().Be(AgentEventType.ResponseCompleted);
        stop.Message.Should().Be("Devo usar Redis ou MemoryCache?");
        notification.Type.Should().Be(AgentEventType.Notification);
    }

    [Fact]
    public void AQuestion_BringsItsFirstQuestion()
    {
        var agentEvent = Translate("""
            {"hook_event_name":"PreToolUse","tool_name":"AskUserQuestion",
             "tool_input":{"questions":[{"question":"Qual cache?","options":[]}]}}
            """)!;

        agentEvent.Type.Should().Be(AgentEventType.NeedsUserInput);
        agentEvent.Message.Should().Be("Qual cache?");
    }

    [Fact]
    public void TheCommonFields_AreKept()
    {
        var agentEvent = Translate("""
            {"hook_event_name":"SessionEnd","session_id":"abc-123","cwd":"C:\\wt","reason":"clear"}
            """)!;

        agentEvent.ExternalSessionId.Should().Be("abc-123");
        agentEvent.WorkingDirectory.Should().Be(@"C:\wt");
        agentEvent.SourceEvent.Should().Be("SessionEnd");
        agentEvent.ReceivedAt.Should().Be(Now);
        agentEvent.Metadata!["reason"].Should().Be("clear");
    }

    [Fact]
    public void AFailure_IsDescribedInPortuguese()
    {
        Translate("""{"hook_event_name":"StopFailure","error_type":"overloaded"}""")!.Message
            .Should().Be("A API do Claude está sobrecarregada.");
    }

    /// <summary>
    /// Corpos como o Claude Code 2.x manda de verdade (capturados num
    /// <c>claude -p</c> com estes hooks, só com a pasta encurtada).
    /// </summary>
    [Theory]
    [InlineData(
        """{"session_id":"9f043fda-53f2-41ea-8d91-11483f7f0094","transcript_path":"C:\\t.jsonl","cwd":"C:\\wt","prompt_id":"c957e78e","permission_mode":"default","hook_event_name":"UserPromptSubmit","prompt":"Rode o comando"}""",
        AgentEventType.Working, null)]
    [InlineData(
        """{"session_id":"9f043fda-53f2-41ea-8d91-11483f7f0094","transcript_path":"C:\\t.jsonl","cwd":"C:\\wt","prompt_id":"c957e78e","permission_mode":"default","hook_event_name":"PostToolUse","tool_name":"Bash","tool_input":{"command":"echo oi","description":"Execute echo command"},"tool_response":{"stdout":"oi"},"tool_use_id":"toolu_019m","duration_ms":927}""",
        AgentEventType.Working, null)]
    [InlineData(
        """{"session_id":"9f043fda-53f2-41ea-8d91-11483f7f0094","transcript_path":"C:\\t.jsonl","cwd":"C:\\wt","prompt_id":"c957e78e","permission_mode":"default","hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"pronto","background_tasks":[],"session_crons":[]}""",
        AgentEventType.ResponseCompleted, "pronto")]
    public void RealPayloads_AreUnderstood(string json, AgentEventType type, string? message)
    {
        var agentEvent = Translate(json)!;

        agentEvent.Type.Should().Be(type);
        agentEvent.Message.Should().Be(message);
        agentEvent.ExternalSessionId.Should().Be("9f043fda-53f2-41ea-8d91-11483f7f0094");
        agentEvent.WorkingDirectory.Should().Be(@"C:\wt");
    }

    [Theory]
    [InlineData("""{"session_id":"x"}""")]
    [InlineData("""[1,2]""")]
    public void WhatIsNotAHook_IsNothing(string json)
    {
        Translate(json).Should().BeNull();
    }
}
