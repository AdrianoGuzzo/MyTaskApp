using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O Claude Code como agente (ADR-029), sem Claude nenhum: onde ele é
/// procurado, como a versão é lida e o que o terminal vai executar.
/// </summary>
public class ClaudeCodeCliProviderTests
{
    private const string Home = @"C:\Users\dev";
    private const string Native = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Npm = @"C:\Users\dev\AppData\Roaming\npm\claude.cmd";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingProcessRunner _runner = new();

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

    /// <summary>Nenhum argumento e nenhum shell: o executável, na pasta do worktree.</summary>
    [Fact]
    public void TheLaunch_IsTheExecutableAlone_InsideTheWorktree()
    {
        var launch = Provider(_ => false).CreateLaunch(
            new AgentCliStartContext(Guid.NewGuid(), @"C:\Projects\eco core-feature-123"),
            new CliDetectionResult { IsInstalled = true, ExecutablePath = Native });

        launch.Executable.Should().Be(Native);
        launch.Arguments.Should().BeEmpty();
        launch.WorkingDirectory.Should().Be(@"C:\Projects\eco core-feature-123");
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
