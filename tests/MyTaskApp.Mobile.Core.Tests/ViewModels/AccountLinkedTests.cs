using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;
using NSubstitute;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// A aba Conta com a conta ligada: o que só existe com um servidor do outro
/// lado (conflitos, recusas, dispositivos) vem de um runner roteirizado; o
/// caminho de verdade está nos testes de ponta a ponta do servidor.
/// </summary>
public sealed class AccountLinkedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);

    private readonly IUseCaseRunner _runner = Substitute.For<IUseCaseRunner>();

    private readonly ISyncAccount _account = Substitute.For<ISyncAccount>();

    private readonly ScriptedDialogs _dialogs = new();

    private readonly Guid _phone = Guid.CreateVersion7();

    private readonly Guid _conflict = Guid.CreateVersion7();

    private readonly Guid _failure = Guid.CreateVersion7();

    private SyncLocalState _state;

    public AccountLinkedTests()
    {
        _state = SyncLocalState.Unlinked with
        {
            State = SyncLinkState.Linked,
            IsEnabled = true,
            UserDisplayName = "Ana",
            UserEmail = "ana@exemplo.com",
            LastSuccessAt = Now,
        };

        _account.IsAvailable.Returns(true);
        _account.Server.Returns(new Uri("https://sync.exemplo.com/"));
        _account.DeviceId.Returns(_phone);
        _account.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new DeviceInfo(Guid.CreateVersion7(), "PC antigo", "Windows", "1.0.0", Now, null, Now, false),
            new DeviceInfo(Guid.CreateVersion7(), "Notebook", "Windows", "1.15.0", Now, Now, null, false),
            new DeviceInfo(_phone, "Pixel", "Android", "1.15.0", Now, Now, null, true),
        ]);

        _runner.RunAsync(Arg.Any<Func<GetSyncStatusHandler, CancellationToken, Task<SyncStatusView>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new SyncStatusView(true, null, _state, new SyncCounts(0, 1, 1, 0, 0), false, null));
        _runner.RunAsync(Arg.Any<Func<ListSyncConflictsHandler, CancellationToken, Task<IReadOnlyList<SyncConflictView>>>>(), Arg.Any<CancellationToken>())
            .Returns([new SyncConflictView(_conflict, SyncEntityTypes.Task, Guid.CreateVersion7(), "Revisar contrato", "title", "Meu título", null, "concurrent_change", Now)]);
        _runner.RunAsync(Arg.Any<Func<ListSyncFailuresHandler, CancellationToken, Task<SyncFailuresView>>>(), Arg.Any<CancellationToken>())
            .Returns(new SyncFailuresView([new SyncFailedOperationView(_failure, SyncEntityTypes.Task, Guid.CreateVersion7(), null, "too_long", null, Now)], []));
    }

    [Fact]
    public async Task Linked_ItShowsTheAccount_TheProblems_AndThisPhoneFirst()
    {
        var account = Create();

        await account.LoadAsync();

        account.IsLinked.Should().BeTrue();
        account.CanConnect.Should().BeFalse();
        account.UserName.Should().Be("Ana");
        account.UserEmail.Should().Be("ana@exemplo.com");
        account.LastSync.Should().StartWith("Última sincronização: ");
        account.Counts.Should().Contain("1 recusada(s)").And.Contain("1 conflito(s)");
        account.Badge!.Kind.Should().Be(SyncIndicatorKind.Attention, "uma alteração recusada pede a pessoa");
        account.IsRefreshing.Should().BeFalse();

        account.HasConflicts.Should().BeTrue();
        account.Conflicts.Single().Should().Be(new ConflictLine(_conflict, "Revisar contrato — title", "Meu título", "(vazio)"));
        account.HasFailures.Should().BeTrue();
        account.Failures.Single().Should().Be(new FailureLine(_failure, "Alteração", "too_long"));

        account.Devices.Select(device => device.Nickname).Should().Equal("Pixel", "Notebook", "PC antigo");
        account.Devices[0].Detail.Should().StartWith("Este celular · Android");
        account.Devices[2].IsRevoked.Should().BeTrue();
        account.Devices[2].Detail.Should().Be("Windows · removido");
    }

    [Fact]
    public async Task Conflicts_AreReappliedOrLetGo_ThroughTheUseCase()
    {
        var account = Create();
        await account.LoadAsync();

        await account.ReapplyConflictAsync(account.Conflicts.Single());
        await account.KeepServerAsync(account.Conflicts.Single());
        await account.ReapplyConflictAsync(null);

        await _runner.Received(2).RunAsync(Arg.Any<Func<ResolveSyncConflictHandler, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusedChange_IsRetried_OrDiscardedAfterAsking()
    {
        var account = Create();
        await account.LoadAsync();

        await account.RetryFailureAsync(account.Failures.Single());
        await _runner.Received(1).RunAsync(Arg.Any<Func<RetrySyncOperationHandler, CancellationToken, Task>>(), Arg.Any<CancellationToken>());

        _dialogs.Confirms = false;
        await account.DiscardFailureAsync(account.Failures.Single());
        await _runner.DidNotReceive().RunAsync(Arg.Any<Func<DiscardSyncOperationHandler, CancellationToken, Task>>(), Arg.Any<CancellationToken>());

        _dialogs.Confirms = true;
        await account.DiscardFailureAsync(account.Failures.Single());
        await account.DiscardFailureAsync(null);
        await _runner.Received(1).RunAsync(Arg.Any<Func<DiscardSyncOperationHandler, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        _dialogs.Questions.Should().Contain("Descartar alteração");
    }

    [Fact]
    public async Task PausingAndResuming_GoThroughTheAccount()
    {
        var account = Create();
        await account.LoadAsync();
        account.IsPaused.Should().BeFalse();

        await account.TogglePauseAsync();
        await _account.Received(1).SetEnabledAsync(false, Arg.Any<CancellationToken>());

        _state = _state with { IsEnabled = false };
        await account.LoadAsync();
        account.IsPaused.Should().BeTrue();

        await account.TogglePauseAsync();
        await _account.Received(1).SetEnabledAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenamingThisPhone_AsksForTheName_AndIgnoresACancel()
    {
        var account = Create();
        await account.LoadAsync();

        _dialogs.PromptAnswer = null;
        await account.RenameThisDeviceAsync();
        await _account.DidNotReceive().RenameDeviceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _dialogs.PromptAnswer = "  Pixel da Ana  ";
        await account.RenameThisDeviceAsync();
        await _account.Received(1).RenameDeviceAsync(_phone, "Pixel da Ana", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncNow_AsksTheScheduler()
    {
        var account = Create();

        await account.SyncNowAsync();

        _account.Received(1).SyncNow();
        _dialogs.Toasts.Should().Contain("Sincronizando…");
    }

    [Fact]
    public async Task WithoutNetwork_TheDeviceListIsSimplyLeftOut()
    {
        _account.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<DeviceInfo>>(new HttpRequestException()));
        var account = Create();

        await account.LoadAsync();

        account.IsLinked.Should().BeTrue();
        account.Devices.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedStatusRead_LeavesTheTabAsItWas()
    {
        _runner.RunAsync(Arg.Any<Func<GetSyncStatusHandler, CancellationToken, Task<SyncStatusView>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SyncStatusView>(new IOException("disco")));
        var account = Create();

        await account.LoadAsync();

        account.Status.Should().BeNull();
        account.IsRefreshing.Should().BeFalse();
        account.LastSync.Should().Be("Ainda não sincronizou.");
        account.Counts.Should().BeEmpty();
    }

    private AccountViewModel Create() => new(
        _runner,
        _account,
        new SyncScheduler(Substitute.For<ISyncCycleRunner>(), new SyncTrigger(), new FakeTimeProvider(Now), NullLogger<SyncScheduler>.Instance),
        new InlineDispatcher(),
        new RecordingNavigator(),
        new RecordingThemes(),
        Substitute.For<ISyncServerAddress>(),
        _dialogs,
        new FakeTimeProvider(Now),
        NullLogger<AccountViewModel>.Instance);
}
