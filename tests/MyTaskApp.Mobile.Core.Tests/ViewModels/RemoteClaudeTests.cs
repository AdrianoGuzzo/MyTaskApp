using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Remote;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// O Claude de outra máquina no celular (ADR-103): a linha da tarefa diz onde
/// roda e o que faz, o detalhe oferece seguir quando a máquina aceita o
/// terminal remoto, e o pedido novo vira aviso com o app aberto — uma vez só,
/// sem a verificação em segundo plano repetir.
/// </summary>
public sealed class RemoteClaudeTests : IAsyncLifetime
{
    private static readonly Guid Notebook = Guid.CreateVersion7();

    private MobileHarness _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeRemoteServer Server => _app.Get<FakeRemoteServer>();

    private RemoteClaudeBoard Board => _app.Get<RemoteClaudeBoard>();

    public async ValueTask InitializeAsync()
    {
        _app = await MobileHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton(provider => new FakeRemoteServer(provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IRemoteApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteOperationsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteSessionsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteTerminalsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
        });

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var device = scope.ServiceProvider.GetRequiredService<IDeviceIdentity>().DeviceId;
            await scope.ServiceProvider.GetRequiredService<ISyncLocalStore>().LinkAsync(
                new SyncAccountLink(Guid.CreateVersion7(), "Ana", null, Guid.CreateVersion7(), device, new Uri("https://sync.exemplo.com/")),
                Ct);
        }

        await _app.Get<RemoteViewModel>().EnableAsync();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private void NotebookOnline(bool offersTerminal)
    {
        var device = FakeRemoteServer.Executor(Notebook, "Notebook", MobileHarness.Now);
        Server.Devices.Add(device with
        {
            Capabilities = device.Capabilities! with
            {
                Operations = offersTerminal ? [RemoteOperationKinds.TerminalAttach] : [],
            },
        });
    }

    private RemoteTerminalSessionReport Report(Guid taskId, RemoteAgentActivity activity, bool hasDialog = false) => new(
        Guid.CreateVersion7(), taskId, Guid.CreateVersion7(), RemoteTerminalProcess.Running, null,
        activity, null, hasDialog, true, false, false, MobileHarness.Now, MobileHarness.Now);

    [Fact]
    public async Task TheRow_ShowsWhereAndWhat_AndChangesInPlace()
    {
        NotebookOnline(offersTerminal: false);
        var task = await _app.CreateTaskAsync("Corrigir o login");
        var report = Report(task.TaskId, RemoteAgentActivity.Working);
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, report, MobileHarness.Now));
        await Board.RefreshAsync(Ct);
        var list = _app.Get<TaskListViewModel>();

        await list.LoadAsync();

        var row = list.Tasks.Should().ContainSingle().Which;
        row.ClaudeLabel.Should().Be("● Claude em Notebook · trabalhando");
        row.ClaudeNeedsAttention.Should().BeFalse();
        row.AccessibleDescription.Should().Contain("Claude em Notebook");

        Server.Terminals[0] = Server.Terminals[0] with { Session = report with { HasDialog = true } };
        await Board.RefreshAsync(Ct);

        list.Tasks.Single().ClaudeLabel.Should().Be("⚠ Claude em Notebook · aguardando você", "a linha troca sem recarregar a lista");
        list.Tasks.Single().ClaudeNeedsAttention.Should().BeTrue();

        Server.Terminals.Clear();
        await Board.RefreshAsync(Ct);

        list.Tasks.Single().HasClaude.Should().BeFalse();
    }

    [Fact]
    public async Task TheDetail_FollowsTheTerminal_WhenTheMachineAcceptsIt()
    {
        NotebookOnline(offersTerminal: true);
        var task = await _app.CreateTaskAsync("Corrigir o login");
        var report = Report(task.TaskId, RemoteAgentActivity.WaitingReview);
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, report, MobileHarness.Now));
        await Board.RefreshAsync(Ct);
        var detail = _app.Get<TaskDetailViewModel>();

        await detail.LoadAsync(task.TaskId);

        var line = detail.ClaudeLines.Should().ContainSingle().Which;
        line.Label.Should().Be("✓ revisar");
        line.Where.Should().Be("Notebook");
        line.OpenLabel.Should().Be("Seguir");

        await detail.FollowClaudeCommand.ExecuteAsync(line);

        _app.Navigator.Last.Should().Be($"terminal/{report.SessionId}");
    }

    [Fact]
    public async Task TheDetail_WithoutTheRemoteTerminal_ShowsTheState_WithoutAButton()
    {
        NotebookOnline(offersTerminal: false);
        var task = await _app.CreateTaskAsync("Corrigir o login");
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, Report(task.TaskId, RemoteAgentActivity.WaitingForUser), MobileHarness.Now));
        await Board.RefreshAsync(Ct);
        var detail = _app.Get<TaskDetailViewModel>();

        await detail.LoadAsync(task.TaskId);

        var line = detail.ClaudeLines.Should().ContainSingle().Which;
        line.CanOpen.Should().BeFalse();
        line.Hint.Should().Be("Notebook mostra o estado, mas não aceita o terminal remoto.");
    }

    [Fact]
    public async Task TheDetail_StopsListening_WhenItLeaves()
    {
        NotebookOnline(offersTerminal: true);
        var task = await _app.CreateTaskAsync("Corrigir o login");
        var detail = _app.Get<TaskDetailViewModel>();
        await detail.LoadAsync(task.TaskId);
        detail.Unload();

        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, Report(task.TaskId, RemoteAgentActivity.Working), MobileHarness.Now));
        await Board.RefreshAsync(Ct);

        detail.HasClaude.Should().BeFalse("fora da tela, o detalhe não redesenha");
    }

    [Fact]
    public async Task ANewRequest_IsNotifiedOnce_AndTheBackgroundCheckDoesNotRepeatIt()
    {
        NotebookOnline(offersTerminal: true);
        var task = await _app.CreateTaskAsync("Corrigir o login");
        var report = Report(task.TaskId, RemoteAgentActivity.Working);
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, report, MobileHarness.Now));
        var check = _app.Get<RemoteBackgroundCheck>();
        await check.RunAsync(notify: true, Ct);
        var notices = _app.Get<RemoteClaudeNotices>();
        notices.Start();
        Board.Start();
        await Board.RefreshAsync(Ct);

        Server.Terminals[0] = Server.Terminals[0] with { Session = report with { HasDialog = true } };
        await Board.RefreshAsync(Ct);
        await WaitUntil(() => notices.Shown == 1);

        var shown = _app.Notifier.Shown.Should().ContainSingle().Subject;
        shown.Tag.Should().Be($"terminal:{report.SessionId:N}");
        shown.Title.Should().Be("Corrigir o login");
        shown.Text.Should().Be("O Claude em Notebook espera você.");

        await Board.RefreshAsync(Ct);
        (await check.RunAsync(notify: true, Ct)).Should().Be(0, "o que a tela avisou fica anotado para o segundo plano");
        _app.Notifier.Shown.Should().ContainSingle();

        await Board.StopAsync();
    }

    [Fact]
    public void TheComposition_ResolvesTheBoardOnce()
    {
        _app.Get<RemoteClaudeBoard>().Should().BeSameAs(_app.Get<RemoteClaudeBoard>());
        _app.Get<RemoteClaudeNotices>().Should().NotBeNull();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }
}
