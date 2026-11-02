using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Infrastructure.Terminals;
using MyTaskApp.Infrastructure.Terminals.Linux;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>
/// O laço inteiro da janela nativa (ADR-098), com processos de verdade: o
/// Claude de mentira num pseudoterminal do agente, o canal local, e o
/// <c>MyTaskApp.TerminalAttach</c> dentro de um segundo pseudoterminal, que
/// faz o papel da janela que a pessoa vê.
/// </summary>
public sealed class TerminalAttachLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TerminalAttach =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MyTaskApp.TerminalAttach.exe" : "MyTaskApp.TerminalAttach");

    private static IPseudoTerminalFactory Factory()
    {
        IPseudoTerminalFactory? factory = OperatingSystem.IsWindows()
            ? new ConPtyTerminalFactory(NullLogger<ConPtyTerminal>.Instance)
            : OperatingSystem.IsLinux() ? new UnixPtyTerminalFactory(NullLogger<UnixPtyTerminal>.Instance) : null;

        Assert.SkipWhen(factory is null, "Sem pseudoterminal neste sistema.");
        return factory!;
    }

    [Fact]
    public async Task TheWindow_ShowsTheSession_TypesIntoIt_AndClosingItLeavesItRunning()
    {
        var factory = Factory();
        await using var host = new TerminalSessionHost(
            new TerminalSessionInfo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AppContext.BaseDirectory, Monitored: true, PermissionsBypassed: false),
            factory.Start(PtyHarness.StartOf()),
            TimeProvider.System,
            NullLogger<TerminalSessionHost>.Instance);

        var pipeName = $"MyTaskApp.Terminal.{Guid.NewGuid():N}";
        await using var listener = new TerminalPipeListener(
            pipeName,
            new LocalTerminalServer(new OneSession(host), new NoUseCases(), NullLogger<LocalTerminalServer>.Instance),
            NullLogger<TerminalPipeListener>.Instance);
        listener.Start();

        var window = new PtyHarness(factory.Start(new PseudoTerminalStart(
            TerminalAttach,
            ["--pipe", pipeName, "--session", host.SessionId.ToString(), "--title", "Teste"],
            AppContext.BaseDirectory,
            null,
            new TerminalSize(90, 28))));

        await using (window)
        {
            await window.WaitForAsync("READY");
            await WaitUntil(() => host.Status.Clients.Any(client => client.Kind == TerminalClientKind.NativeWindow));
            host.Status.Size.Should().Be(new TerminalSize(90, 28), "a janela nativa dita o tamanho");

            await window.SendAsync("pela janela\r");
            await window.WaitForAsync("ECHO: pela janela");

            window.Terminal.Resize(new TerminalSize(110, 32));
            await window.WaitForAsync("RESIZED 110x32");
        }

        await WaitUntil(() => host.Status.Clients.Count == 0);
        host.IsRunning.Should().BeTrue("fechar a janela só desliga a vista");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + PtyHarness.Patience;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A condição não aconteceu a tempo.");
            }

            await Task.Delay(50, Ct);
        }
    }

    private sealed class OneSession(TerminalSessionHost host) : ITerminalSessions
    {
        public bool IsHost => true;

        public PseudoTerminalSupport Support => new(true, "teste", null);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler<Guid>? ActivityChanged
        {
            add { }
            remove { }
        }

        public TerminalSessionHost? Find(Guid sessionId) => sessionId == host.SessionId ? host : null;

        public IReadOnlyList<TerminalSessionHost> List() => [host];

        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Observe(AgentEvent agentEvent)
        {
        }

        public void NotifyActivity(Guid sessionId)
        {
        }
    }

    private sealed class NoUseCases : IUseCaseRunner
    {
        public Task<TResult> RunAsync<THandler, TResult>(Func<THandler, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            where THandler : notnull =>
            throw new InvalidOperationException("Nenhum caso de uso neste teste.");

        public Task RunAsync<THandler>(Func<THandler, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            where THandler : notnull =>
            throw new InvalidOperationException("Nenhum caso de uso neste teste.");
    }
}
