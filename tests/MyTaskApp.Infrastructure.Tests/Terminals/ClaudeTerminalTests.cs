using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Tests.Agents;
using MyTaskApp.Infrastructure.Terminals.Linux;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>
/// O Claude Code de verdade no pseudoterminal do agente (ADR-098): o
/// executável que o app acha roda no ConPTY (ou no <c>openpty</c>), a TUI
/// desenha na mesma tela que as vistas montam, e fechar o terminal termina a
/// sessão sem matar.
/// </summary>
/// <remarks>
/// Opcional: só com <c>MYTASKAPP_TEST_CLAUDE=1</c> e o Claude instalado. Não
/// chama o modelo: vale com o login vencido, quando a TUI abre na tela de login
/// ou na de confiar na pasta. O que depende de resposta do modelo (as teclas de
/// cada diálogo, o Esc que interrompe) fica fora daqui.
/// </remarks>
public sealed class ClaudeTerminalTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mytaskapp-claude-pty-").FullName;

    private readonly string _state = Path.Combine(Path.GetTempPath(), "mytaskapp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);

            if (Directory.Exists(_state))
            {
                Directory.Delete(_state, recursive: true);
            }
        }
        catch (IOException)
        {
            // O Claude pode ainda segurar um arquivo da pasta por um instante depois de sair.
        }
    }

    [Fact]
    public async Task TheVersion_ComesThroughThePseudoTerminal_AndEndsWithZero()
    {
        var (factory, claude) = Installed();

        await using var harness = new PtyHarness(factory.Start(new PseudoTerminalStart(claude, ["--version"], _folder, null, new TerminalSize(100, 30))));

        var text = await harness.WaitForAsync("Claude Code");
        text.Should().MatchRegex(@"\d+\.\d+\.\d+");
        (await harness.Terminal.Exited.WaitAsync(PtyHarness.Patience, TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task TheInteractiveClaude_DrawsItsScreen_AndClosingTheTerminalEndsIt_WithoutKilling()
    {
        var (factory, claude) = Installed();
        var start = new PseudoTerminalStart(claude, ["--session-id", Guid.NewGuid().ToString()], _folder, null, new TerminalSize(120, 36));

        await using var harness = new PtyHarness(factory.Start(start));

        // Com ou sem login, a TUI se desenha: a tela de login, a de confiar na pasta ou o prompt.
        await WaitUntilAsync(() => harness.Text.Count(character => !char.IsWhiteSpace(character)) > 20);
        harness.Terminal.Exited.IsCompleted.Should().BeFalse("o Claude interativo fica esperando a pessoa");

        harness.Terminal.Close();

        var exited = await Task.WhenAny(harness.Terminal.Exited, Task.Delay(PtyHarness.Patience, TestContext.Current.CancellationToken));
        exited.Should().BeSameAs(harness.Terminal.Exited, "fechar o terminal é o fim para o Claude, como fechar a janela");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow + PtyHarness.Patience;

        while (!condition())
        {
            DateTimeOffset.UtcNow.Should().BeBefore(until, "a TUI do Claude devia ter aparecido");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private (IPseudoTerminalFactory Factory, string Claude) Installed()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(ClaudeHeadlessTests.Variable) == "1",
            $"Defina {ClaudeHeadlessTests.Variable}=1 para rodar o Claude Code de verdade no pseudoterminal.");

        IPseudoTerminalFactory? factory = OperatingSystem.IsWindows()
            ? new ConPtyTerminalFactory(NullLogger<ConPtyTerminal>.Instance)
            : OperatingSystem.IsLinux() ? new UnixPtyTerminalFactory(NullLogger<UnixPtyTerminal>.Instance) : null;
        Assert.SkipWhen(factory is not { Support.IsSupported: true }, "Este sistema não tem o pseudoterminal do agente.");

        var provider = new ClaudeCodeCliProvider(
            ExecutableLocator.ForCurrentSystem(),
            new ProcessRunner(),
            new ClaudeCodeHooks(_state, Path.Combine(_state, "home"), [], NullLogger<ClaudeCodeHooks>.Instance),
            NullLogger<ClaudeCodeCliProvider>.Instance);
        var claude = provider.Locate();
        Assert.SkipWhen(claude is null, "O Claude Code não está instalado nesta máquina.");

        return (factory!, claude!);
    }
}
