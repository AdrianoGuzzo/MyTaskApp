using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.Tests.Terminals;

/// <summary>
/// A vista de uma sessão do terminal do agente (ADR-098): a tela segue o que
/// chega, um buraco pede o retrato, as ações saem só quando o estado deixa, e
/// cair a ligação não encerra nada.
/// </summary>
public sealed class TerminalSessionViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A "thread da tela" do teste: as mensagens esperam aqui até o teste rodar.</summary>
    private readonly ConcurrentQueue<Action> _ui = new();

    private readonly FakeConfirmationDialog _confirmations = new();

    private readonly List<FakeTerminalChannel> _channels = [];

    private readonly List<TerminalSize> _requestedSizes = [];

    private Exception? _connectFailure;

    private TerminalSessionViewModel? _viewModel;

    private FakeTerminalChannel Channel => _channels[^1];

    public async ValueTask DisposeAsync()
    {
        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheScreen_StartsFromTheSnapshot_AndFollowsTheOutput()
    {
        var viewModel = await ConnectedAsync();

        Channel.Snapshot("olá do Claude\r\n");
        Channel.Print("\x1b[32mpronto\x1b[0m");
        await WaitUntil(() => viewModel.Screen?.ScreenText().Contains("pronto", StringComparison.Ordinal) == true);

        viewModel.Screen!.ScreenText().Should().StartWith("olá do Claude");
        viewModel.Version.Should().Be(2, "cada pedaço redesenha uma vez");
        viewModel.IsConnected.Should().BeTrue();
        _requestedSizes.Should().Equal(TerminalSize.Default);
    }

    [Fact]
    public async Task AGapInTheOutput_AsksForTheSnapshot_OnceAndWaitsForIt()
    {
        var viewModel = await ConnectedAsync();
        Channel.Snapshot("início\r\n");
        await WaitUntil(() => viewModel.Screen is not null);

        Channel.PrintAfterAGap("perdido");
        Channel.PrintAfterAGap("perdido de novo");
        await WaitUntil(() => Sent<TerminalResync>().Count == 1);
        Drain();

        viewModel.Screen!.ScreenText().Should().NotContain("perdido", "o que vem depois de um buraco não entra na tela");
        Sent<TerminalResync>().Should().ContainSingle("um retrato basta até ele chegar");

        Channel.Snapshot("retrato novo\r\n");
        await WaitUntil(() => viewModel.Screen!.ScreenText().Contains("retrato novo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenAnotherViewSetsTheSize_TheScreenFollows_AndAsksForTheSnapshot()
    {
        var viewModel = await ConnectedAsync();
        Channel.Snapshot("x");
        await WaitUntil(() => viewModel.Screen is not null);

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(new TerminalSize(132, 40))));
        await WaitUntil(() => viewModel.Status is not null);

        viewModel.Screen!.Size.Should().Be(new TerminalSize(132, 40));
        viewModel.ProcessText.Should().Contain("132×40");
        Sent<TerminalResync>().Should().ContainSingle();
    }

    [Fact]
    public async Task Typing_AndTheSizeOfTheView_GoToTheSession()
    {
        var viewModel = await ConnectedAsync();

        await viewModel.TypeAsync("ls\r"u8.ToArray());
        await viewModel.ResizeAsync(new TerminalSize(100, 30));

        Sent<TerminalKeys>().Single().Data.Should().Equal("ls\r"u8.ToArray());
        Sent<TerminalResize>().Single().Size.Should().Be(new TerminalSize(100, 30));
    }

    [Fact]
    public async Task ThePermission_IsAnsweredHere_OtherDialogsInTheTerminal()
    {
        var viewModel = await ConnectedAsync();
        var permission = new TerminalDialog(Guid.NewGuid(), TerminalDialogKind.Permission, "Rodar npm test?", DateTimeOffset.UnixEpoch);

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(activity: AgentActivity.WaitingForUser, dialog: permission)));
        await WaitUntil(() => viewModel.Status is not null);

        viewModel.DialogText.Should().Be("Rodar npm test?");
        viewModel.StatusText.Should().Be("◐ Esperando você responder");
        viewModel.AllowCommand.CanExecute(null).Should().BeTrue();
        viewModel.InterruptCommand.CanExecute(null).Should().BeFalse("com um diálogo aberto, o Esc responderia a ele");

        await viewModel.AllowCommand.ExecuteAsync(null);
        Sent<TerminalChoice>().Single().Should().Be(new TerminalChoice(permission.Id, true));

        var question = permission with { Id = Guid.NewGuid(), Kind = TerminalDialogKind.Question };
        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(dialog: question)));
        await WaitUntil(() => viewModel.Status!.Dialog!.Id == question.Id);

        viewModel.CanAnswerDialog.Should().BeFalse("a pergunta tem opções que só o terminal mostra");
    }

    [Fact]
    public async Task Interrupting_IsOnlyWhileWorking()
    {
        var viewModel = await ConnectedAsync();
        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(activity: AgentActivity.WaitingReview)));
        await WaitUntil(() => viewModel.Status is not null);
        viewModel.InterruptCommand.CanExecute(null).Should().BeFalse();

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(activity: AgentActivity.Working)));
        await WaitUntil(() => viewModel.Status!.Activity == AgentActivity.Working);

        viewModel.StatusText.Should().Be("● Trabalhando");
        await viewModel.InterruptCommand.ExecuteAsync(null);
        Sent<TerminalInterrupt>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForcingTheEnd_AsksFirst(bool confirmed)
    {
        var viewModel = await ConnectedAsync();
        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status()));
        await WaitUntil(() => viewModel.Status is not null);
        _confirmations.Answer = confirmed;

        await viewModel.StopCommand.ExecuteAsync(null);
        await viewModel.ForceStopCommand.ExecuteAsync(null);

        _confirmations.LastAsked!.IsIrreversible.Should().BeTrue();
        Channel.Stops.Should().Equal(confirmed ? [false, true] : [false]);
    }

    [Fact]
    public async Task TheWarnings_ComeFromTheState()
    {
        var viewModel = await ConnectedAsync();
        var remote = new TerminalLease(Guid.NewGuid(), TerminalClientKind.Remote, "Notebook", DateTimeOffset.UnixEpoch);

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(
            bypassed: true,
            monitored: false,
            lease: remote,
            clients:
            [
                new TerminalClientView(Guid.NewGuid(), TerminalClientKind.Embedded, "MyTaskApp", DateTimeOffset.UnixEpoch),
                new TerminalClientView(Guid.NewGuid(), TerminalClientKind.NativeWindow, "Janela", DateTimeOffset.UnixEpoch),
                new TerminalClientView(remote.ClientId, TerminalClientKind.Remote, "Notebook", DateTimeOffset.UnixEpoch),
            ])));
        await WaitUntil(() => viewModel.Status is not null);

        viewModel.BypassWarning.Should().Contain("sem as aprovações");
        viewModel.MonitoringWarning.Should().Contain("Só o teclado vale");
        viewModel.LeaseText.Should().Contain("Notebook").And.Contain("toma o controle de volta");
        viewModel.OthersText.Should().Be("Também aberto em a janela do terminal, Notebook, de longe.", "esta vista não lista a si mesma");
    }

    [Fact]
    public async Task TheEnd_KeepsTheScreen_AndStopsTheKeys()
    {
        var viewModel = await ConnectedAsync();
        Channel.Snapshot("Tchau!");
        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(running: false)));
        await WaitUntil(() => viewModel.Status is not null);

        viewModel.StatusText.Should().Be("○ Encerrado (código 0)");
        viewModel.IsReadOnly.Should().BeTrue();
        viewModel.Screen!.ScreenText().Should().Contain("Tchau!");
        viewModel.StopCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task LosingTheConnection_OffersToReconnect_ToTheSameSession()
    {
        var viewModel = await ConnectedAsync();
        Channel.Close();
        await WaitUntil(() => viewModel.IsDisconnected);

        viewModel.Message.Should().Contain("fechou a ligação");
        viewModel.StatusText.Should().Contain("segue rodando no agente");
        viewModel.ReconnectCommand.CanExecute(null).Should().BeTrue();

        await viewModel.ReconnectCommand.ExecuteAsync(null);

        _channels.Should().HaveCount(2);
        _channels[0].Disposed.Should().BeTrue();
        viewModel.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ABrokenSend_Disconnects_WithoutThrowing()
    {
        var viewModel = await ConnectedAsync();
        Channel.BreakOnSend = true;

        await viewModel.TypeAsync("x"u8.ToArray());
        Drain();

        viewModel.IsDisconnected.Should().BeTrue();
        viewModel.Message.Should().Be("A ligação com o terminal caiu.");
    }

    [Fact]
    public async Task WhenTheAgentRefusesTheView_TheReasonIsShown()
    {
        _connectFailure = new DomainException("O MyTaskApp Agent, que hospeda o terminal, não respondeu nesta máquina.");
        var viewModel = Create();

        await viewModel.ConnectAsync();

        viewModel.IsDisconnected.Should().BeTrue();
        viewModel.Message.Should().StartWith("O MyTaskApp Agent");

        _connectFailure = null;
        await viewModel.ConnectAsync();
        Channel.Send(new TerminalRefusal("Esta sessão não está mais no agente."));
        await WaitUntil(() => viewModel.IsDisconnected);

        viewModel.Message.Should().Be("Esta sessão não está mais no agente.");
    }

    [Fact]
    public async Task OutputFromAReplacedConnection_IsIgnored()
    {
        var viewModel = await ConnectedAsync();
        var old = Channel;

        // O retrato velho já saiu da ligação, mas a tela ainda não o aplicou.
        old.Snapshot("velho");
        await WaitUntilQueued();
        await viewModel.ConnectAsync();
        Drain();

        viewModel.Screen.Should().BeNull("a mensagem era da ligação trocada");

        Channel.Snapshot("novo");
        await WaitUntil(() => viewModel.Screen is not null);
        viewModel.Screen!.ScreenText().Should().Be("novo");
    }

    private TerminalSessionViewModel Create()
    {
        _viewModel = new TerminalSessionViewModel(
            Guid.NewGuid(),
            "Claude Code · eco",
            TerminalClientKind.Embedded,
            (size, _) =>
            {
                if (_connectFailure is { } failure)
                {
                    throw failure;
                }

                _requestedSizes.Add(size);
                var channel = new FakeTerminalChannel();
                _channels.Add(channel);
                return Task.FromResult<ITerminalChannel>(channel);
            },
            _ui.Enqueue,
            _confirmations,
            NullLogger<TerminalSessionViewModel>.Instance);

        return _viewModel;
    }

    private async Task<TerminalSessionViewModel> ConnectedAsync()
    {
        var viewModel = Create();
        await viewModel.ConnectAsync();
        return viewModel;
    }

    private List<T> Sent<T>()
        where T : TerminalInput
    {
        lock (Channel.Sent)
        {
            return [.. Channel.Sent.OfType<T>()];
        }
    }

    private void Drain()
    {
        while (_ui.TryDequeue(out var action))
        {
            action();
        }
    }

    private async Task WaitUntilQueued()
    {
        var deadline = DateTime.UtcNow + Patience;

        while (_ui.IsEmpty)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Nada chegou da ligação.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (true)
        {
            Drain();

            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A vista não chegou lá a tempo.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
