using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Remote;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// A verificação periódica com o app fechado (ADR-096): avisa a aprovação nova
/// e a conversa em que o Claude respondeu, uma vez só, e nada do que a tela já
/// mostrou. O sistema que agenda e a notificação são de mentira.
/// </summary>
public sealed class RemoteBackgroundCheckTests : IAsyncLifetime
{
    private static readonly Guid Notebook = Guid.CreateVersion7();

    private MobileHarness _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeRemoteServer Server => _app.Get<FakeRemoteServer>();

    private RemoteBackgroundCheck Check => _app.Get<RemoteBackgroundCheck>();

    public async ValueTask InitializeAsync()
    {
        _app = await MobileHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton(provider => new FakeRemoteServer(provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IRemoteApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteOperationsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteSessionsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
        });

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var device = scope.ServiceProvider.GetRequiredService<IDeviceIdentity>().DeviceId;
            await scope.ServiceProvider.GetRequiredService<ISyncLocalStore>().LinkAsync(
                new SyncAccountLink(Guid.CreateVersion7(), "Ana", null, Guid.CreateVersion7(), device, new Uri("https://sync.exemplo.com/")),
                Ct);
        }

        Server.Devices.Add(FakeRemoteServer.Executor(Notebook, "Notebook", MobileHarness.Now));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task TurningTheRemoteOn_SchedulesTheCheck_AndAsksToNotify()
    {
        await _app.Get<RemoteViewModel>().EnableAsync();

        _app.BackgroundChecks.Applied.Should().Equal(true);
        _app.Notifier.PermissionRequests.Should().Be(1, "a permissão é pedida com a tela, ao abrir a aba pronta");
    }

    [Fact]
    public async Task WithTheRemoteOff_TheCheckIsOff_AndSaysNothing()
    {
        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "O Claude quer usar Bash: dotnet test");

        (await Check.RunAsync(notify: true, Ct)).Should().Be(0);
        await Check.ApplyAsync(Ct);

        _app.BackgroundChecks.Applied.Should().Equal(false);
        _app.Notifier.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task ANewApproval_IsNotifiedOnce_WithTheMachine_AndTheExpiredOneIsNot()
    {
        await _app.Get<RemoteViewModel>().EnableAsync();
        var asked = Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "O Claude quer usar Bash: dotnet test");
        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "Já venceu", lifetime: TimeSpan.FromMinutes(-1));

        (await Check.RunAsync(notify: true, Ct)).Should().Be(1);

        var shown = _app.Notifier.Shown.Should().ContainSingle().Subject;
        shown.Tag.Should().Be($"approval:{asked.Request.ApprovalId:N}");
        shown.Text.Should().Contain("dotnet test").And.Contain("Notebook");

        (await Check.RunAsync(notify: true, Ct)).Should().Be(0, "a mesma aprovação não avisa de novo");
    }

    [Fact]
    public async Task WithTheAppInFront_TheCheckOnlyTakesNote()
    {
        await _app.Get<RemoteViewModel>().EnableAsync();
        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "O Claude quer usar Bash: dotnet test");

        (await Check.RunAsync(notify: false, Ct)).Should().Be(0);
        (await Check.RunAsync(notify: true, Ct)).Should().Be(0, "a tela já mostrou essa aprovação");

        _app.Notifier.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task AConversationTheClaudeAnswered_IsNotified_ButNotOnTheFirstCheck()
    {
        await _app.Get<RemoteViewModel>().EnableAsync();
        var waiting = Session(RemoteAgentSessionStatus.Idle);
        var answering = Session(RemoteAgentSessionStatus.Running);

        (await Check.RunAsync(notify: true, Ct)).Should().Be(0, "na primeira volta, o que já tinha acabado não é novidade");

        Replace(answering, answering with { Version = 3, Status = RemoteAgentSessionStatus.Idle });
        Replace(waiting, waiting with { Version = 2, Status = RemoteAgentSessionStatus.Running });

        (await Check.RunAsync(notify: true, Ct)).Should().Be(1);
        var answered = _app.Notifier.Shown.Should().ContainSingle().Subject;
        answered.Tag.Should().Be($"session:{answering.SessionId:N}");
        answered.Text.Should().Be(RemoteBackgroundCheck.AnsweredText);

        Replace(waiting, waiting with { Version = 4, Status = RemoteAgentSessionStatus.Failed });

        (await Check.RunAsync(notify: true, Ct)).Should().Be(1);
        _app.Notifier.Shown[^1].Text.Should().Be(RemoteBackgroundCheck.FailedText);
        (await Check.RunAsync(notify: true, Ct)).Should().Be(0, "nada mudou desde a última volta");
    }

    /// <summary>Um prompt inteiro entre duas voltas: a conversa sai e volta para "esperando", e avisa.</summary>
    [Fact]
    public async Task APromptThatStartedAndEndedBetweenTwoChecks_IsNotified()
    {
        await _app.Get<RemoteViewModel>().EnableAsync();
        var session = Session(RemoteAgentSessionStatus.Idle);
        await Check.RunAsync(notify: true, Ct);

        Replace(session, session with { Version = 5 });

        (await Check.RunAsync(notify: true, Ct)).Should().Be(1);
    }

    private RemoteAgentSessionView Session(RemoteAgentSessionStatus status)
    {
        var session = new RemoteAgentSessionView(
            Guid.CreateVersion7(), Notebook, 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "padrao", null, status,
            RemoteAgentActivity.Working, null, null, MobileHarness.Now, null, MobileHarness.Now, MobileHarness.Now);
        Server.Sessions.Add(session);
        return session;
    }

    private void Replace(RemoteAgentSessionView before, RemoteAgentSessionView after) =>
        Server.Sessions[Server.Sessions.FindIndex(item => item.SessionId == before.SessionId)] = after;
}
