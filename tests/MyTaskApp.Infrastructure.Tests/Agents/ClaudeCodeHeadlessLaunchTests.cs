using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O Claude sem terminal (ADR-089), sem Claude nenhum: a linha de comando, as
/// travas, os arquivos gerados e a versão mínima.
/// </summary>
public sealed class ClaudeCodeHeadlessLaunchTests : IDisposable
{
    private const string Native = @"C:\Users\dev\.local\bin\claude.exe";

    private static readonly Guid Conversation = Guid.Parse("3f9c2a7e-1b4d-4c8e-9a51-6d2e8f0b7c13");

    private static readonly AgentToolProfile Edit = new(
        "edit", "Editar no worktree", "Lê e edita os arquivos do worktree.",
        ["Read", "Grep", "Glob", "Edit", "Write", "Bash"],
        ["Read", "Grep", "Glob", "Edit", "Write"],
        ["Bash(git push:*)"]);

    private static readonly HeadlessApprovalAccess Approval =
        new(new Uri("http://127.0.0.1:51234/mcp"), "mytaskapp-agent", "approval", "MYTASKAPP_AGENT_APPROVAL_TOKEN", new string('B', 64));

    private readonly string _state = Path.Combine(Path.GetTempPath(), "mytaskapp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private ClaudeCodeCliProvider Provider() =>
        new(
            new ExecutableLocator((_, _) => null, _ => false, isWindows: true),
            new NoProcessRunner(),
            new ClaudeCodeHooks(_state, Path.Combine(_state, "home"), [], NullLogger<ClaudeCodeHooks>.Instance),
            NullLogger<ClaudeCodeCliProvider>.Instance);

    private static CliDetectionResult Installed(string? version = "2.1.310") =>
        new() { IsInstalled = true, ExecutablePath = Native, Version = version };

    private static HeadlessAgentRequest Request(
        AgentToolProfile? profile = null,
        bool resume = false,
        HeadlessApprovalAccess? approval = null,
        string? model = "haiku") =>
        new(@"C:\Projetos\eco-feature-1", Conversation, resume, profile ?? Edit, model, "low", 20, 1.5m, approval, [@"C:\Users\dev\AppData\Local\MyTaskApp"]);

    [Fact]
    public void TheLine_IsPrintMode_Restricted_StreamJson_WithTheProfile_AndTheLocalCeilings()
    {
        var launch = Provider().CreateHeadlessLaunch(Request(approval: Approval), Installed());

        launch.Executable.Should().Be(Native);
        launch.WorkingDirectory.Should().Be(@"C:\Projetos\eco-feature-1");
        var arguments = launch.Arguments.ToList();
        arguments.Take(6).Should().Equal("-p", "--restricted", "--output-format", "stream-json", "--verbose", "--session-id");
        arguments[6].Should().Be(Conversation.ToString());
        Value(arguments, "--tools").Should().Be("Read,Grep,Glob,Edit,Write,Bash");
        Value(arguments, "--allowedTools").Should().Be("Read,Grep,Glob,Edit,Write");
        Value(arguments, "--disallowedTools").Should().Be("Bash(git push:*)");
        arguments.Should().Contain("--strict-mcp-config");
        Value(arguments, "--permission-prompt-tool").Should().Be("mcp__mytaskapp-agent__approval");
        Value(arguments, "--model").Should().Be("haiku");
        Value(arguments, "--effort").Should().Be("low");
        Value(arguments, "--max-turns").Should().Be("20");
        Value(arguments, "--max-budget-usd").Should().Be("1.50");
        arguments.Should().NotContain(argument => argument.Contains("dangerously", StringComparison.Ordinal), "os DefaultArguments do card nunca entram");
        arguments.Should().NotContain("--permission-prompts");
    }

    [Fact]
    public void TheNextPrompt_ResumesTheSameConversation()
    {
        var arguments = Provider().CreateHeadlessLaunch(Request(resume: true), Installed()).Arguments.ToList();

        Value(arguments, "--resume").Should().Be(Conversation.ToString());
        arguments.Should().NotContain("--session-id");
    }

    [Fact]
    public void WithoutAnApprovalChannel_WhatWouldAsk_IsDenied()
    {
        var launch = Provider().CreateHeadlessLaunch(Request(), Installed());

        Value(launch.Arguments.ToList(), "--permission-prompts").Should().Be("none");
        launch.Arguments.Should().NotContain("--permission-prompt-tool").And.NotContain("--mcp-config");
        launch.Environment.Should().BeEmpty();
        launch.TemporaryFiles.Should().ContainSingle("só o --settings");
    }

    /// <summary>O <c>-p</c> ignora em silêncio um arquivo inválido: os dois são conferidos como JSON aqui.</summary>
    [Fact]
    public void TheGeneratedFiles_AreValidJson_AndTheSecretStaysInTheEnvironment()
    {
        var launch = Provider().CreateHeadlessLaunch(Request(approval: Approval), Installed());
        var arguments = launch.Arguments.ToList();

        using var settings = JsonDocument.Parse(File.ReadAllText(Value(arguments, "--settings")));
        var deny = settings.RootElement.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(rule => rule.GetString()).ToList();
        deny.Should().Contain(["Read(~/.ssh/**)", "Edit(~/.ssh/**)", "Read(**/.env)", "Read(//c/Users/dev/AppData/Local/MyTaskApp/**)"]);

        var mcpText = File.ReadAllText(Value(arguments, "--mcp-config"));
        using var mcp = JsonDocument.Parse(mcpText);
        var server = mcp.RootElement.GetProperty("mcpServers").GetProperty("mytaskapp-agent");
        server.GetProperty("url").GetString().Should().Be("http://127.0.0.1:51234/mcp");
        server.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer ${MYTASKAPP_AGENT_APPROVAL_TOKEN}");
        mcpText.Should().NotContain(Approval.Token);
        launch.Environment.Should().Equal(new Dictionary<string, string> { ["MYTASKAPP_AGENT_APPROVAL_TOKEN"] = Approval.Token });
        launch.TemporaryFiles.Should().HaveCount(2).And.OnlyContain(file => File.Exists(file));
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("Bash(claude --permission-mode bypassPermissions:*)")]
    public void NothingThatSkipsThePermissions_GetsThrough(string pattern)
    {
        var sneaky = Edit with { AllowedTools = [.. Edit.AllowedTools, pattern] };

        FluentActions.Invoking(() => Provider().CreateHeadlessLaunch(Request(sneaky), Installed()))
            .Should().Throw<DomainException>().WithMessage("*nunca pula as permissões*");
    }

    [Fact]
    public void AnUnknownModel_OrAProfileWithoutTools_IsRefused()
    {
        FluentActions.Invoking(() => Provider().CreateHeadlessLaunch(Request(model: "gpt"), Installed()))
            .Should().Throw<DomainException>().WithMessage("Modelo desconhecido: gpt.");
        FluentActions.Invoking(() => Provider().CreateHeadlessLaunch(Request(Edit with { Tools = [] }), Installed()))
            .Should().Throw<DomainException>().WithMessage("*não libera nenhuma ferramenta*");
    }

    [Theory]
    [InlineData("2.1.309", true)]
    [InlineData("2.1.310", true)]
    [InlineData("3.0.0-beta.1", true)]
    [InlineData("2.1.308", false)]
    [InlineData("1.9.999", false)]
    [InlineData(null, false)]
    public void TheMinimumVersion_IsTheOneWhoseFlagsWereChecked(string? version, bool serves)
    {
        var reason = Provider().HeadlessUnavailableReason(Installed(version));

        (reason is null).Should().Be(serves, reason);
    }

    [Fact]
    public void TheForeignVariables_LeaveTheEnvironment_AndTheSessionOnesStay()
    {
        var session = new Dictionary<string, string> { ["MYTASKAPP_AGENT_APPROVAL_TOKEN"] = "x" };

        new[] { "PGPASSWORD", "PGHOST", "GH_TOKEN", "github_token", "MYTASKAPP_DATA_DIR", "MYTASKAPP_HOOK_SECRET" }
            .Should().OnlyContain(name => HeadlessEnvironment.IsRemoved(name, session));
        new[] { "PATH", "USERPROFILE", "ANTHROPIC_API_KEY", "MYTASKAPP_AGENT_APPROVAL_TOKEN" }
            .Should().OnlyContain(name => !HeadlessEnvironment.IsRemoved(name, session));
    }

    [Theory]
    [InlineData(@"C:\Users\dev\AppData", "//c/Users/dev/AppData")]
    [InlineData("/home/dev/.local/share/MyTaskApp/", "//home/dev/.local/share/MyTaskApp")]
    public void AnAbsolutePath_IsWrittenTheWayClaudeRulesRead(string path, string rule) =>
        ClaudeCodeCliProvider.AbsoluteRule(path).Should().Be(rule);

    private static string Value(List<string> arguments, string option)
    {
        var index = arguments.IndexOf(option);
        index.Should().BeGreaterThanOrEqualTo(0, $"{option} devia estar na linha");
        return arguments[index + 1];
    }

    private sealed class NoProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, "", "", false));
    }
}
