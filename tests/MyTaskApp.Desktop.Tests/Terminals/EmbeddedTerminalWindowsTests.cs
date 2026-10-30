using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.Terminals;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.Terminals;

/// <summary>
/// As vistas do terminal do agente dentro do app (ADR-098): uma por sessão,
/// pedir de novo traz a aberta, e fechar só desliga a vista.
/// </summary>
public sealed class EmbeddedTerminalWindowsTests
{
    private readonly IAgentTerminalClient _client = Substitute.For<IAgentTerminalClient>();

    private readonly AgentTerminalWindows _windows;

    private readonly EmbeddedTerminalWindows _views;

    private readonly List<TerminalSessionViewModel> _opened = [];

    public EmbeddedTerminalWindowsTests()
    {
        _client.Support.Returns(new PseudoTerminalSupport(true, "teste", null));
        _client.AttachAsync(Arg.Any<Guid>(), Arg.Any<TerminalClientKind>(), Arg.Any<string>(), Arg.Any<TerminalSize?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ITerminalChannel>(new FakeTerminalChannel()));

        _windows = new AgentTerminalWindows(
            _client,
            Substitute.For<ITerminalLauncher>(),
            Substitute.For<ITerminalWindowManager>(),
            Substitute.For<IAgentProcessTracker>(),
            NullLogger<AgentTerminalWindows>.Instance);

        _views = new EmbeddedTerminalWindows(_client, _windows, new FakeConfirmationDialog(), NullLogger<TerminalSessionViewModel>.Instance)
        {
            Post = action => action(),
        };
    }

    [Fact]
    public async Task Showing_OpensOneViewPerSession_AndAgainBringsItToTheFront()
    {
        _views.Opened += (_, view) => _opened.Add(view);
        var sessionId = Guid.NewGuid();

        _views.Show(sessionId, "Claude Code · eco").Should().BeTrue();
        var activations = 0;
        _opened.Single().ActivationRequested += (_, _) => activations++;
        _views.Show(sessionId, "Claude Code · eco").Should().BeTrue();

        _opened.Should().ContainSingle("a segunda vez traz a mesma vista");
        activations.Should().Be(1);
        await _client.Received(1).AttachAsync(sessionId, TerminalClientKind.Embedded, EmbeddedTerminalWindows.Label, Arg.Any<TerminalSize?>(), Arg.Any<CancellationToken>());
        _windows.WindowProcessFor(sessionId).Should().Be(Environment.ProcessId, "o aviso sabe que a pessoa já vê a sessão no app");
    }

    [Fact]
    public async Task Closing_OnlyDisconnectsTheView()
    {
        _views.Opened += (_, view) => _opened.Add(view);
        var sessionId = Guid.NewGuid();
        _views.Show(sessionId, "Claude Code").Should().BeTrue();

        await _views.ClosedAsync(_opened[0]);

        _windows.WindowProcessFor(sessionId).Should().BeNull();
        _views.Show(sessionId, "Claude Code").Should().BeTrue();
        _opened.Should().HaveCount(2, "fechada, a próxima vez abre outra vista");
    }

    [Fact]
    public void WithoutTheAgentsTerminal_OrWithoutAScreen_NothingIsShown()
    {
        _views.Show(Guid.NewGuid(), "Claude Code").Should().BeFalse("sem quem crie a janela, não há onde mostrar");

        _views.Opened += (_, view) => _opened.Add(view);
        _client.Support.Returns(PseudoTerminalSupport.Unsupported("teste", "sem pseudoterminal"));

        _views.Show(Guid.NewGuid(), "Claude Code").Should().BeFalse();
        _opened.Should().BeEmpty();
    }
}
