using System.Text.Json;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Cada linha da saída falsa já é o evento: o leitor do stream-json tem os testes dele.</summary>
internal sealed class FakeHeadlessProvider : IHeadlessAgentProvider
{
    private static readonly Dictionary<string, HeadlessAgentEvent> Lines = [];

    public string Id => "claude-code";

    public string Name => "Claude Code";

    public string? Unavailable { get; set; }

    public List<HeadlessAgentRequest> Requests { get; } = [];

    public static string Text(string text) => Line(new HeadlessText(text));

    public static string Tool(string name, string summary) => Line(new HeadlessToolUse(Guid.NewGuid().ToString(), name, summary));

    /// <summary>O uso de uma ferramenta e o resultado dele, com o mesmo id.</summary>
    public static (string Use, string Result) ToolWithResult(string name, string summary, string result, bool isError = false)
    {
        var id = Guid.NewGuid().ToString();
        return (Line(new HeadlessToolUse(id, name, summary)), Line(new HeadlessToolResult(id, isError, result)));
    }

    public static string Done(int turns = 1, decimal? cost = null, IReadOnlyList<string>? denied = null) =>
        Line(new HeadlessResult(false, "success", "ok", turns, cost, denied ?? [], "completed"));

    public static string LoggedOut() =>
        Line(new HeadlessResult(true, "success", "Failed to authenticate. API Error: 401", 1, 0, [], "api_error"));

    public Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe", Version = "2.1.310" });

    public string? HeadlessUnavailableReason(CliDetectionResult detection) => Unavailable;

    public HeadlessAgentLaunch CreateHeadlessLaunch(HeadlessAgentRequest request, CliDetectionResult detection)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return new HeadlessAgentLaunch(detection.ExecutablePath!, ["-p"], request.WorkingDirectory, new Dictionary<string, string>(), []);
    }

    public IReadOnlyList<HeadlessAgentEvent> ReadEvents(string line)
    {
        lock (Lines)
        {
            return Lines.TryGetValue(line, out var item) ? [item] : [];
        }
    }

    public string Summarize(string tool, JsonElement input) =>
        input.TryGetProperty("command", out var command) ? command.GetString()!
        : input.TryGetProperty("url", out var url) ? url.GetString()! : input.GetRawText();

    private static string Line(HeadlessAgentEvent item)
    {
        var line = Guid.NewGuid().ToString();

        lock (Lines)
        {
            Lines[line] = item;
        }

        return line;
    }
}

/// <summary>
/// O processo falso: cada prompt toca um roteiro, de linhas da saída e de
/// passos (a pergunta ao MCP de aprovação no meio da resposta).
/// </summary>
internal sealed class ScriptedHeadlessRunner : IHeadlessAgentRunner
{
    public const int FirstProcessId = 7100;

    public static readonly DateTimeOffset Started = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly Queue<object[]> _scripts = new();

    private int _nextProcess = FirstProcessId;

    public List<string> Prompts { get; } = [];

    public CancellationToken Token { get; private set; }

    public void Script(params object[] steps) => _scripts.Enqueue(steps);

    public async Task<HeadlessRunResult> RunAsync(
        HeadlessAgentLaunch launch,
        string prompt,
        Func<int, DateTimeOffset, Task> started,
        Func<string, Task> line,
        CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        Token = cancellationToken;
        await started(_nextProcess++, Started);

        try
        {
            foreach (var step in _scripts.Dequeue())
            {
                await (step switch
                {
                    string text => line(text),
                    Func<Task> action => action(),
                    _ => throw new InvalidOperationException($"Passo desconhecido: {step}."),
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HeadlessRunResult(-1, string.Empty, Canceled: true);
        }

        return new HeadlessRunResult(0, string.Empty, Canceled: false);
    }
}

internal sealed class FakeApprovalGateway : IHeadlessApprovalGateway
{
    private ToolPermissionDecider? _decider;

    public HeadlessApprovalAccess Access { get; } = new(new Uri("http://127.0.0.1:5999/mcp"), "mytaskapp-agent", "approval", "TOKEN", "segredo");

    public int Open { get; private set; }

    public Task<ToolPermissionDecision> AskAsync(string tool, string input) =>
        _decider!(new ToolPermissionRequest(tool, JsonDocument.Parse(input).RootElement, Guid.NewGuid().ToString()), CancellationToken.None);

    public Task<IHeadlessApprovalChannel> OpenAsync(ToolPermissionDecider decider, CancellationToken cancellationToken = default)
    {
        _decider = decider;
        Open++;
        return Task.FromResult<IHeadlessApprovalChannel>(new Channel(this));
    }

    private sealed class Channel(FakeApprovalGateway gateway) : IHeadlessApprovalChannel
    {
        public HeadlessApprovalAccess Access => gateway.Access;

        public ValueTask DisposeAsync()
        {
            gateway.Open--;
            return ValueTask.CompletedTask;
        }
    }
}
