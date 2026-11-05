using System.Text.Json;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.Terminals;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Os terminais da máquina controlada na tela "Controlar" (ADR-099): só com a
/// máquina aceitando, "Seguir" abre uma vista por sessão, e o "Abrir no
/// terminal de lá" leva só os ids.
/// </summary>
public sealed class RemoteTerminalsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly MachineView Notebook = new(
        Guid.Parse("0199a000-0000-7000-8000-000000000002"), "Notebook", "windows", "1.25.0", false, true, Now, true, null, [], OffersTerminal: true);

    private static readonly RemoteEnvironmentView Environment = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Validar antimicrobianos", "feature/ECO-12", []);

    private readonly FakeUseCaseRunner _runner = new();

    private readonly IRemoteTerminalWires _wires = Substitute.For<IRemoteTerminalWires>();

    private readonly List<TerminalSessionViewModel> _opened = [];

    private readonly RemoteTerminalWindows _windows;

    private readonly RemoteTerminalSessionReport _session = new(
        Guid.CreateVersion7(), Environment.TaskId, Environment.DevelopmentId, RemoteTerminalProcess.Running, null,
        RemoteAgentActivity.Working, null, false, true, true, false, Now, Now);

    public RemoteTerminalsViewModelTests()
    {
        _runner.ResultsByHandler[typeof(GetRemoteControlHandler)] = new RemoteControlView(new AgentCatalog("h", [], [], [], []), [Environment]);
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[];
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([]);
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = new RemoteOperationView(
            Guid.CreateVersion7(), Guid.Empty, Notebook.DeviceId, Guid.Empty, RemoteOperationKinds.TerminalStart, Environment.TaskId,
            RemoteOperationStatus.Queued, Now, Now, Now, null, null, null, 0);
        _runner.ResultsByHandler[typeof(ListRemoteTerminalsHandler)] = new RemoteTerminalList(
            [new RemoteTerminalItem(new RemoteTerminalSessionView(Notebook.DeviceId, _session, Now), "Validar antimicrobianos", "● Trabalhando")],
            null);

        // Sem servidor no teste: a vista abre e mostra por que não ligou.
        _wires.OpenAsync(Arg.Any<CancellationToken>()).Returns<IRemoteTerminalWire>(_ => throw new DomainException("Conecte a sincronização para seguir o terminal de outra máquina."));
        _windows = new RemoteTerminalWindows(
            new RemoteTerminalConnector(_runner, _wires, TimeProvider.System, NullLogger<RemoteTerminalChannel>.Instance),
            new FakeConfirmationDialog(),
            NullLogger<TerminalSessionViewModel>.Instance)
        {
            Post = action => action(),
        };
        _windows.Opened += (_, view) => _opened.Add(view);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task AMachineAcceptingTheTerminal_ListsItsSessions()
    {
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Terminals.IsOffered.Should().BeTrue();
        var item = viewModel.Terminals.Items.Should().ContainSingle().Which;
        item.Title.Should().Be("Validar antimicrobianos");
        item.BypassWarning.Should().NotBeNull();
    }

    [AvaloniaFact]
    public async Task AMachineNotAccepting_HidesTheList_AndDoesNotAskTheServer()
    {
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook with { OffersTerminal = false }, Ct);

        viewModel.Terminals.IsOffered.Should().BeFalse();
        _runner.Invoked.Should().NotContain(typeof(ListRemoteTerminalsHandler));
    }

    [AvaloniaFact]
    public async Task WithoutTheViews_TheListDoesNotShowEither()
    {
        var viewModel = new RemoteControlViewModel(_runner, NullLogger<RemoteControlViewModel>.Instance);

        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Terminals.IsOffered.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Following_OpensOneRemoteViewPerSession()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        var item = viewModel.Terminals.Items[0];

        viewModel.Terminals.FollowCommand.Execute(item);
        viewModel.Terminals.FollowCommand.Execute(item);

        var view = _opened.Should().ContainSingle("a segunda vez traz a mesma vista").Which;
        view.SessionId.Should().Be(_session.SessionId);
        view.IsRemote.Should().BeTrue();
        view.Title.Should().Contain("Validar antimicrobianos");
        await WaitUntil(() => view.IsDisconnected);
        view.Message.Should().Contain("Conecte a sincronização");
    }

    [AvaloniaFact]
    public async Task ASessionOutsideAnEnvironment_CannotBeFollowed()
    {
        _runner.ResultsByHandler[typeof(ListRemoteTerminalsHandler)] = new RemoteTerminalList(
            [new RemoteTerminalItem(new RemoteTerminalSessionView(Notebook.DeviceId, _session with { DevelopmentId = null }, Now), "Tarefa", "● Rodando")],
            null);
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Terminals.FollowCommand.Execute(viewModel.Terminals.Items[0]);

        _opened.Should().BeEmpty();
        viewModel.Terminals.Message.Should().Contain("não é de um ambiente");
    }

    [AvaloniaFact]
    public async Task ANoticeFromTheMachine_ReadsTheListAgain()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        _runner.Invoked.Clear();

        await viewModel.Terminals.TickAsync(new HashSet<RemoteNoticeKind>(), listening: true, Ct);
        _runner.Invoked.Should().NotContain(typeof(ListRemoteTerminalsHandler), "com o hub, o relógio espera o aviso");

        await viewModel.Terminals.TickAsync(new HashSet<RemoteNoticeKind> { RemoteNoticeKind.Terminals }, listening: true, Ct);
        _runner.Invoked.Should().Contain(typeof(ListRemoteTerminalsHandler));
    }

    [AvaloniaFact]
    public async Task StartingInTheTerminalThere_SendsOnlyTheIds()
    {
        var sent = new List<SendRemoteOperation>();
        _runner.Handlers[typeof(SendRemoteOperationHandler)] = Recording(sent);
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        await viewModel.StartTerminalCommand.ExecuteAsync(null);

        var request = sent.Should().ContainSingle().Which;
        request.Kind.Should().Be(RemoteOperationKinds.TerminalStart);
        request.TaskId.Should().Be(Environment.TaskId);
        request.Payload.Deserialize<RemoteTerminalStartRequest>(RemoteJson.Options).Should().Be(new RemoteTerminalStartRequest(Environment.DevelopmentId));
        viewModel.OperationTitle.Should().Be("Abrir o Claude no terminal de Notebook · Validar antimicrobianos");
    }

    private static SendRemoteOperationHandler Recording(List<SendRemoteOperation> sent)
    {
        var remote = Substitute.For<IRemoteOperationsApi>();
        remote.CreateAsync(Arg.Any<SignedEnvelope>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            RemoteJws.TryParse(call.Arg<SignedEnvelope>(), out var parts).Should().BeTrue();
            var request = JsonSerializer.Deserialize<RemoteOperationRequest>(parts.Payload, RemoteJson.Options)!;
            sent.Add(new SendRemoteOperation(request.TargetDeviceId, request.Kind, request.Payload, request.TaskId, request.RequiredEntities));
            return new RemoteOperationView(
                request.OperationId, request.WorkspaceId, request.TargetDeviceId, request.RequestedByDeviceId, request.Kind, request.TaskId,
                RemoteOperationStatus.Queued, Now, Now, Now, null, null, null, 0);
        });

        var sync = Substitute.For<MyTaskApp.Application.Sync.ISyncLocalStore>();
        sync.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => MyTaskApp.Application.Sync.SyncLocalState.Unlinked with { WorkspaceId = Guid.CreateVersion7() });

        var signer = Substitute.For<IDeviceSigner>();
        signer.GetPublicKeyAsync(Arg.Any<CancellationToken>()).Returns(new DevicePublicKey("ES256", "AAAA", "fingerprint"));
        signer.SignAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns("c2lnbmF0dXJl");

        var identity = Substitute.For<MyTaskApp.Application.Sync.IDeviceIdentity>();
        identity.DeviceId.Returns(Guid.CreateVersion7());

        return new SendRemoteOperationHandler(remote, signer, identity, sync, TimeProvider.System);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }

    /// <summary>
    /// Com só "Aceitar controle", a máquina relata o estado (ADR-103): a lista
    /// de todas mostra a sessão, mas não oferece seguir o que o agente recusaria.
    /// </summary>
    [AvaloniaFact]
    public async Task InTheListOfAll_AMachineWithoutTheRemoteTerminal_ShowsTheState_ButCannotBeFollowed()
    {
        var terminals = new RemoteTerminalsViewModel(_runner, NullLogger<RemoteTerminalsViewModelTests>.Instance, _windows);

        await terminals.LoadAllAsync(new Dictionary<Guid, string> { [Notebook.DeviceId] = "Notebook" }, null, Ct, offering: new HashSet<Guid>());

        var item = terminals.Items.Should().ContainSingle().Which;
        item.CanFollow.Should().BeFalse();

        terminals.FollowCommand.Execute(item);

        _opened.Should().BeEmpty();
        terminals.Message.Should().Be("Notebook mostra o estado, mas não aceita o terminal remoto.");
    }

    private RemoteControlViewModel ViewModel() => new(_runner, NullLogger<RemoteControlViewModel>.Instance, terminals: _windows);
}
