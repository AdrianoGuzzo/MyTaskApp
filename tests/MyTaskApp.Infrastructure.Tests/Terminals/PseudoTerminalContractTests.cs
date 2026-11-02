using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Terminals.Linux;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>
/// O pseudoterminal de cada sistema (ADR-098) com um Claude de mentira de
/// verdade dentro: o programa lê o teclado cru, desenha, muda de tamanho, sai
/// com o código dele, e o encerramento forçado leva a árvore da sessão e nada
/// mais. Os mesmos testes valem para o ConPTY e para o <c>openpty</c>.
/// </summary>
public abstract class PseudoTerminalContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary><c>null</c> fora do sistema da implementação: o teste é pulado.</summary>
    protected abstract IPseudoTerminalFactory? CreateFactory();

    private IPseudoTerminalFactory Factory()
    {
        var factory = CreateFactory();
        Assert.SkipWhen(factory is null, "Este pseudoterminal é de outro sistema.");
        return factory!;
    }

    private PtyHarness Start(params string[] arguments) => Start(PtyHarness.StartOf(arguments));

    private PtyHarness Start(PseudoTerminalStart start) => new(Factory().Start(start));

    [Fact]
    public void Support_IsReported()
    {
        Factory().Support.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task Program_SeesItsArguments_AndAnswersTypedText()
    {
        await using var pty = Start("--session-id", "abc");

        await pty.WaitForAsync("READY session=abc size=100x30");
        await pty.SendAsync("olá mundo\r");

        await pty.WaitForAsync("ECHO: olá mundo");
    }

    [Fact]
    public async Task BracketedPaste_ArrivesAsOneBlock()
    {
        await using var pty = Start();
        await pty.WaitForAsync("READY");

        await pty.SendAsync("\u001b[200~texto colado; com \"aspas\"\u001b[201~\r");

        await pty.WaitForAsync("ECHO: texto colado; com \"aspas\"");
    }

    [Fact]
    public async Task Resize_ChangesWhatTheProgramSees()
    {
        await using var pty = Start();
        await pty.WaitForAsync("READY");

        pty.Terminal.Resize(new TerminalSize(120, 40));

        await pty.WaitForAsync("RESIZED 120x40");
        pty.Terminal.Size.Should().Be(new TerminalSize(120, 40));
    }

    [Fact]
    public async Task Escape_InterruptsTheWork()
    {
        await using var pty = Start();
        await pty.WaitForAsync("READY");
        await pty.SendAsync("work\r");
        await pty.WaitForAsync("working");

        await pty.SendAsync("\u001b");

        await pty.WaitForAsync("INTERRUPTED");
    }

    [Fact]
    public async Task Exit_ReportsTheExitCode_AndEndsTheOutput()
    {
        await using var pty = Start();
        await pty.WaitForAsync("READY");

        await pty.SendAsync("crash\r");

        (await pty.Terminal.Exited.WaitAsync(PtyHarness.Patience, Ct)).Should().Be(3);
        await pty.Reading.WaitAsync(PtyHarness.Patience, Ct);
    }

    [Fact]
    public async Task Environment_AddsAndRemovesVariables()
    {
        var start = PtyHarness.StartOf() with
        {
            Environment = new Dictionary<string, string> { ["MYTASKAPP_PTY_TEST"] = "sim" },
            RemovedVariables = ["PATH"],
        };
        await using var pty = Start(start);
        await pty.WaitForAsync("READY");

        await pty.SendAsync("env MYTASKAPP_PTY_TEST\r");
        await pty.WaitForAsync("ENV MYTASKAPP_PTY_TEST=sim");
        await pty.SendAsync("env PATH\r");
        await pty.WaitForAsync("ENV PATH=(unset)");
    }

    [Fact]
    public async Task Kill_EndsTheSessionTree_AndNothingElse()
    {
        var factory = Factory();
        using var outsider = Process.Start(new ProcessStartInfo(PtyHarness.FakeClaude, "--child") { UseShellExecute = false, CreateNoWindow = true })!;

        try
        {
            await using var pty = Start();
            await pty.WaitForAsync("READY");
            await pty.SendAsync("spawn\r");
            var screen = await pty.WaitForAsync("CHILD ");
            var childId = int.Parse(screen[(screen.IndexOf("CHILD ", StringComparison.Ordinal) + 6)..].Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(childId);

            pty.Terminal.Kill();

            await pty.Terminal.Exited.WaitAsync(PtyHarness.Patience, Ct);
            child.WaitForExit(PtyHarness.Patience).Should().BeTrue("o neto da sessão morre junto");
            outsider.HasExited.Should().BeFalse("o encerramento forçado não alcança processo de fora da sessão");
        }
        finally
        {
            outsider.Kill();
        }
    }

    [Fact]
    public async Task Close_EndsTheProgram_WithoutLosingTheEndOfTheOutput()
    {
        await using var pty = Start();
        await pty.WaitForAsync("READY");

        pty.Terminal.Close();

        await pty.Terminal.Exited.WaitAsync(PtyHarness.Patience, Ct);
        await pty.Reading.WaitAsync(PtyHarness.Patience, Ct);
    }

    [Fact]
    public async Task Write_AfterTheEnd_IsAnExpectedFailure()
    {
        await using var pty = Start();
        await pty.WaitForAsync("READY");
        await pty.SendAsync("crash\r");
        await pty.Terminal.Exited.WaitAsync(PtyHarness.Patience, Ct);
        await pty.Reading.WaitAsync(PtyHarness.Patience, Ct);

        var write = async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await pty.SendAsync(new string('x', 4096));
            }
        };

        await write.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public void Start_WithAMissingProgram_SaysSo()
    {
        var factory = Factory();

        var start = () => factory.Start(PtyHarness.StartOf() with { Executable = @"C:\nao\existe\claude.exe" });

        start.Should().Throw<DomainException>().WithMessage("O executável não foi encontrado*");
    }

    [Fact]
    public void Start_WithAnAbsurdSize_IsRefused()
    {
        var factory = Factory();

        var start = () => factory.Start(PtyHarness.StartOf() with { Size = new TerminalSize(5000, 2) });

        start.Should().Throw<DomainException>().WithMessage("Tamanho de terminal fora dos limites*");
    }

    [SupportedOSPlatform("windows")]
    public sealed class ConPty : PseudoTerminalContractTests
    {
        protected override IPseudoTerminalFactory? CreateFactory() =>
            OperatingSystem.IsWindows() ? new ConPtyTerminalFactory(NullLogger<ConPtyTerminal>.Instance) : null;
    }

    public sealed class UnixPty : PseudoTerminalContractTests
    {
        protected override IPseudoTerminalFactory? CreateFactory() =>
            OperatingSystem.IsLinux() ? new UnixPtyTerminalFactory(NullLogger<UnixPtyTerminal>.Instance) : null;
    }
}

/// <summary>A linha de comando do <c>CreateProcess</c>: as regras do <c>ArgumentList</c>.</summary>
public class WindowsCommandLineTests
{
    [Theory]
    [InlineData(new[] { "simples" }, "app simples")]
    [InlineData(new[] { "com espaço" }, "app \"com espaço\"")]
    [InlineData(new[] { "" }, "app \"\"")]
    [InlineData(new[] { "diz \"oi\"" }, "app \"diz \\\"oi\\\"\"")]
    [InlineData(new[] { @"C:\pasta com espaço\" }, "app \"C:\\pasta com espaço\\\\\"")]
    [InlineData(new[] { @"a\\b" }, @"app a\\b")]
    [InlineData(new[] { "--model", "opus" }, "app --model opus")]
    public void CommandLine_FollowsTheRulesOfArgumentList(string[] arguments, string expected)
    {
        WindowsCommandLine.Build("app", arguments).Should().Be(expected);
    }
}
