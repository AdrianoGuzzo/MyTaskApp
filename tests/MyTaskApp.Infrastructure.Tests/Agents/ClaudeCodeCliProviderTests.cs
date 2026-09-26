using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O Claude Code como agente (ADR-030), sem Claude nenhum: onde ele é
/// procurado, como a versão é lida e o que o terminal vai executar.
/// </summary>
public class ClaudeCodeCliProviderTests : IDisposable
{
    private const string Home = @"C:\Users\dev";
    private const string Native = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Npm = @"C:\Users\dev\AppData\Roaming\npm\claude.cmd";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingProcessRunner _runner = new();

    /// <summary>Onde o arquivo de hooks é gravado (ADR-037): uma pasta por teste.</summary>
    private readonly string _state = Path.Combine(Path.GetTempPath(), "mytaskapp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ClaudeCodeHooks Hooks() =>
        new(_state, Path.Combine(_state, "home"), [], NullLogger<ClaudeCodeHooks>.Instance);

    private ClaudeCodeCliProvider Provider(
        Func<string, bool> fileExists,
        string? path = @"C:\Windows",
        bool isWindows = true) =>
        new(
            new ExecutableLocator(
                (name, target) => (name, target) switch
                {
                    ("PATH", EnvironmentVariableTarget.Process) => path,
                    ("USERPROFILE", _) => Home,
                    ("HOME", _) => "/home/dev",
                    ("APPDATA", _) => @"C:\Users\dev\AppData\Roaming",
                    _ => null,
                },
                fileExists,
                isWindows),
            _runner,
            Hooks(),
            NullLogger<ClaudeCodeCliProvider>.Instance);

    [Fact]
    public void ItIsTheClaudeCommand()
    {
        var provider = Provider(_ => false);

        provider.Id.Should().Be("claude-code");
        provider.Name.Should().Be("Claude Code");
        provider.Command.Should().Be("claude");
    }

    [Fact]
    public async Task Installed_ByTheNativeInstaller_IsFound_WithItsVersion()
    {
        _runner.Result = new ProcessResult(0, "2.1.4 (Claude Code)\n", "", false);

        var detection = await Provider(path => path == Native).DetectAsync(Ct);

        detection.IsInstalled.Should().BeTrue();
        detection.ExecutablePath.Should().Be(Native);
        detection.Version.Should().Be("2.1.4");
        detection.Error.Should().BeNull();

        var request = _runner.Requests.Should().ContainSingle().Subject;
        request.FileName.Should().Be(Native);
        request.Arguments.Should().Equal("--version");
    }

    /// <summary>Instalado pelo npm, o comando é um <c>.cmd</c>.</summary>
    [Fact]
    public async Task Installed_ByNpm_IsFound_AsACmdShim()
    {
        var detection = await Provider(path => path == Npm).DetectAsync(Ct);

        detection.IsInstalled.Should().BeTrue();
        detection.ExecutablePath.Should().Be(Npm);
    }

    [Fact]
    public async Task OnThePath_WinsOverTheKnownFolders()
    {
        const string OnPath = @"C:\tools\claude.exe";

        var detection = await Provider(path => path is OnPath or Native, path: @"C:\tools").DetectAsync(Ct);

        detection.ExecutablePath.Should().Be(OnPath);
    }

    [Fact]
    public void Windows_TriesTheExeBeforeTheCmd_InEachFolder()
    {
        Provider(_ => false, path: @"C:\tools").Candidates().Should().StartWith(
        [
            @"C:\tools\claude.exe",
            @"C:\tools\claude.cmd",
            Native,
            @"C:\Users\dev\.local\bin\claude.cmd",
        ]);
    }

    [Fact]
    public void Linux_LooksForThePlainName_InTheHomeBin()
    {
        // Caminho "/usr/bin" só é absoluto — e "/" só é separador — num Unix.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Caminhos Unix só se montam num Unix.");

        Provider(_ => false, path: "/usr/bin", isWindows: false).Candidates().Should().StartWith(
        [
            "/usr/bin/claude",
            "/home/dev/.local/bin/claude",
        ]);
    }

    [Fact]
    public async Task NotInstalled_SaysSo_WithoutRunningAnything()
    {
        var detection = await Provider(_ => false).DetectAsync(Ct);

        detection.IsInstalled.Should().BeFalse();
        detection.ExecutablePath.Should().BeNull();
        detection.Error.Should().Be("Claude Code não encontrado.");
        _runner.Requests.Should().BeEmpty();
    }

    /// <summary>Sem versão, mas com executável: dá para abrir, então está instalado.</summary>
    [Fact]
    public async Task AVersionThatCannotBeRead_StillCountsAsInstalled()
    {
        _runner.StartFailure = new ProcessStartException(Native, new System.ComponentModel.Win32Exception(5));

        var detection = await Provider(path => path == Native).DetectAsync(Ct);

        detection.IsInstalled.Should().BeTrue();
        detection.Version.Should().BeNull();
    }

    [Fact]
    public async Task AVersionCommandThatFails_StillCountsAsInstalled()
    {
        _runner.Result = new ProcessResult(1, "", "boom", false);

        var detection = await Provider(path => path == Native).DetectAsync(Ct);

        detection.IsInstalled.Should().BeTrue();
        detection.Version.Should().BeNull();
    }

    [Theory]
    [InlineData("2.1.4 (Claude Code)", "2.1.4")]
    [InlineData("1.0.119-beta.2 (Claude Code)\r\n", "1.0.119-beta.2")]
    [InlineData("Claude Code v0.2.9", "0.2.9")]
    [InlineData("sem versão", null)]
    public void TheVersion_IsReadFromTheOutput(string output, string? expected)
    {
        ClaudeCodeCliProvider.ParseVersion(output).Should().Be(expected);
    }

    /// <summary>Nenhum shell: o executável com os parâmetros escolhidos, na pasta do worktree.</summary>
    [Fact]
    public void TheLaunch_IsTheExecutableWithTheChosenArguments_InsideTheWorktree()
    {
        var launch = Provider(_ => false).CreateLaunch(
            new AgentCliStartContext(
                Guid.NewGuid(),
                @"C:\Projects\eco core-feature-123",
                ["--dangerously-skip-permissions", "--model", "opus"]),
            new CliDetectionResult { IsInstalled = true, ExecutablePath = Native });

        launch.Executable.Should().Be(Native);
        launch.Arguments.Should().Equal("--dangerously-skip-permissions", "--model", "opus");
        launch.WorkingDirectory.Should().Be(@"C:\Projects\eco core-feature-123");
    }

    private static readonly string Worktree = @"C:\Projects\eco core-feature-123";

    // --- Acompanhamento (ADR-037) ------------------------------------------

    private static readonly AgentMonitoring Monitoring = new(
        new Uri("http://127.0.0.1:47831/api/claude/events"),
        new Dictionary<string, string> { ["MYTASKAPP_AGENT_SESSION_ID"] = "s-1", ["MYTASKAPP_HOOK_TOKEN"] = "segredo" });

    /// <summary>
    /// <c>--settings</c> antes de tudo: se o usuário passar o próprio, o dele
    /// vale — perde-se o acompanhamento, não a configuração dele.
    /// </summary>
    [Theory]
    [InlineData(false, new[] { "--dangerously-skip-permissions", "--permission-mode", "plan", "Implemente" })]
    [InlineData(true, new[] { "--dangerously-skip-permissions", "Implemente" })]
    public void WithMonitoring_TheHookSettingsComeFirst_AndTheProcessGetsItsIdentity(bool runDirectly, string[] rest)
    {
        var launch = Provider(_ => false).CreateLaunch(
            new AgentCliStartContext(
                Guid.NewGuid(), Worktree, ["--dangerously-skip-permissions"], "Implemente", runDirectly, Monitoring),
            new CliDetectionResult { IsInstalled = true, ExecutablePath = Native });

        var settingsFile = Path.Combine(_state, ClaudeCodeHooks.SettingsFileName);

        launch.Arguments.Should().Equal(["--settings", settingsFile, .. rest]);
        launch.Environment.Should().BeSameAs(Monitoring.Environment);
        File.ReadAllText(settingsFile).Should().Contain("http://127.0.0.1:47831/api/claude/events");
    }

    [Fact]
    public void WithoutMonitoring_NoHooksAndNoEnvironment()
    {
        var launch = Launch(Native, prompt: null, runDirectly: false);

        launch.Arguments.Should().NotContain("--settings");
        launch.Environment.Should().BeNull();
        Directory.Exists(_state).Should().BeFalse();
    }

    private TerminalLaunchOptions Launch(string executable, string? prompt, bool runDirectly) =>
        Provider(_ => false).CreateLaunch(
            new AgentCliStartContext(Guid.NewGuid(), Worktree, [], prompt, runDirectly),
            new CliDetectionResult { IsInstalled = true, ExecutablePath = executable });

    /// <summary>Os parâmetros do card vêm antes; o texto, sempre por último.</summary>
    [Theory]
    [InlineData(false, new[] { "--dangerously-skip-permissions", "--permission-mode", "plan", "Implemente" })]
    [InlineData(true, new[] { "--dangerously-skip-permissions", "Implemente" })]
    public void WithArgumentsAndText_TheArgumentsComeFirst(bool runDirectly, string[] expected)
    {
        var launch = Provider(_ => false).CreateLaunch(
            new AgentCliStartContext(Guid.NewGuid(), Worktree, ["--dangerously-skip-permissions"], "Implemente", runDirectly),
            new CliDetectionResult { IsInstalled = true, ExecutablePath = Native });

        launch.Arguments.Should().Equal(expected);
    }

    /// <summary>Desmarcado é o padrão: o Claude planeja, e só altera arquivos com aprovação.</summary>
    [Fact]
    public void WithText_NotRunningDirectly_OpensInPlanMode_WithTheTextAsOneArgument()
    {
        var launch = Launch(Native, "Implemente \"x\" & teste\nem 100%", runDirectly: false);

        launch.Arguments.Should().Equal("--permission-mode", "plan", "Implemente \"x\" & teste\nem 100%");
        launch.WorkingDirectory.Should().Be(Worktree);
    }

    [Fact]
    public void WithText_RunningDirectly_SendsOnlyTheText()
    {
        Launch(Native, "Implemente a tarefa", runDirectly: true).Arguments.Should().Equal("Implemente a tarefa");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void WithoutText_TheCheckboxChangesNothing(string? prompt)
    {
        Launch(Native, prompt, runDirectly: true).Arguments.Should().BeEmpty();
    }

    /// <summary>O <c>cmd.exe</c> cortaria o texto na primeira quebra de linha.</summary>
    [Fact]
    public void ThroughTheNpmCmd_LineBreaksBecomeSpaces()
    {
        Launch(Npm, "Primeira linha\r\nsegunda\nterceira", runDirectly: true)
            .Arguments.Should().Equal("Primeira linha segunda terceira");
    }

    [Theory]
    [InlineData("Use \"aspas\"")]
    [InlineData("Cubra 100% dos casos")]
    public void ThroughTheNpmCmd_WhatCmdWouldMangle_IsRefused(string prompt)
    {
        FluentActions.Invoking(() => Launch(Npm, prompt, runDirectly: false))
            .Should().Throw<DomainException>().WithMessage("*claude.cmd*");
    }

    [Fact]
    public void ByDefault_TheClaude_OpensWithoutAskingForPermissions()
    {
        Provider(_ => false).DefaultArguments.Should().Be("--dangerously-skip-permissions");
    }

    [Fact]
    public void Windows_HasAnInstallGuide_WithTheOfficialInstaller()
    {
        var guide = Provider(_ => false).InstallGuideFor(OSPlatform.Windows);

        guide.Should().NotBeNull();
        guide!.Title.Should().Contain("Windows");
        guide.Steps.Select(step => step.Command).Should().Contain("irm https://claude.ai/install.ps1 | iex");
        guide.DocumentationUrl.Scheme.Should().Be("https");
    }

    [Fact]
    public void Linux_HasItsOwnInstallGuide()
    {
        var guide = Provider(_ => false).InstallGuideFor(OSPlatform.Linux);

        guide!.Title.Should().Contain("Linux");
        guide.Steps.Select(step => step.Command).Should().Contain("curl -fsSL https://claude.ai/install.sh | bash");
    }

    [Fact]
    public void AnUnknownSystem_HasNoGuide()
    {
        Provider(_ => false).InstallGuideFor(OSPlatform.FreeBSD).Should().BeNull();
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public ProcessResult Result { get; set; } = new(0, "", "", false);

        public ProcessStartException? StartFailure { get; set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return StartFailure is not null
                ? Task.FromException<ProcessResult>(StartFailure)
                : Task.FromResult(Result);
        }
    }
}
