using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// Trocar de servidor (ADR-078) sobre SQLite de verdade: o que era do servidor
/// anterior é esquecido, as tarefas ficam, e nada sai para o novo antes de
/// alguém entrar nele.
/// </summary>
public sealed class SyncServerSwitchTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SyncDeviceHarness.Now;

    private static readonly Uri Old = new("https://sync.exemplo.com/");

    private static readonly Uri Next = new("https://outro.exemplo.com/");

    private readonly ScriptedSyncApi _api = new();

    private readonly RecordingAuthenticator _authenticator = new();

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _workspace = Guid.CreateVersion7();

    private SyncDeviceHarness _device = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISyncServerAddress Address => _device.Services.GetRequiredService<ISyncServerAddress>();

    public async ValueTask InitializeAsync()
    {
        _device = await SyncDeviceHarness.CreateAsync(_api, Ct, authenticator: _authenticator);
        _authenticator.Address = () => Address.Current;
        Address.Set(Old);
    }

    public async ValueTask DisposeAsync()
    {
        await _device.DisposeAsync();
        _device.DeleteFolder();
    }

    [Fact]
    public async Task AnAccountFromAnotherServer_BlocksTheCycle_BeforeAnyCall()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        // O endereço mudou por fora da tela (o appsettings.user.json do desktop, um backup restaurado).
        _authenticator.Server = Next;

        var result = await _device.Engine.RunCycleAsync(Ct);

        result.Status.Should().Be(SyncCycleStatus.Blocked);
        result.Message.Should().Be(SyncEngine.ServerChangedCode);
        _api.Calls.Should().BeEmpty("nada vai para o servidor novo sem alguém entrar nele");
        var state = await _device.StoreAsync(store => store.ReadAsync(Ct));
        state.State.Should().Be(SyncLinkState.NeedsReconnect);
        state.LastErrorCode.Should().Be(SyncEngine.ServerChangedCode);
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(2, "o que não subiu continua guardado");
    }

    [Fact]
    public async Task ConnectingTheSameAccountOnAnotherServer_ForgetsWhatWasSynced_ButKeepsTheTasks()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        await _device.StoreAsync(store => store.MarkSchemaCurrentAsync(5, Ct));

        // Os mesmos ids de conta, emitidos por outro servidor, não são a mesma conta.
        await _device.StoreAsync(store => store.LinkAsync(new SyncAccountLink(_user, "Ana", "ana@exemplo.com", _workspace, _device.Device.DeviceId, Next), Ct));

        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
        (await _device.ReadAsync(context => context.SyncEntityStates.CountAsync(Ct))).Should().Be(0);
        var state = await _device.StoreAsync(store => store.ReadAsync(Ct));
        state.Server.Should().Be(Next);
        state.Cursor.Should().BeEmpty();
        state.ClientSchemaVersion.Should().Be(0, "a primeira conexão no servidor novo refaz o pull e a varredura");
        (await _device.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1);
    }

    [Fact]
    public async Task SwitchingWithPendingChanges_SignsOutOnTheOldServer_ForgetsItsState_AndKeepsEveryTask()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().BePositive();

        await _device.Services.GetRequiredService<ISyncAccount>().SwitchServerAsync(Next, Ct);

        _authenticator.SignedOutOn.Should().Equal([Old], "a revogação vai para o servidor que emitiu o token, nunca para o novo");
        Address.Current.Should().Be(Next);

        var state = await _device.StoreAsync(store => store.ReadAsync(Ct));
        state.State.Should().Be(SyncLinkState.Unlinked);
        state.Server.Should().BeNull();
        state.UserId.Should().BeNull();
        state.WorkspaceId.Should().BeNull();
        state.Cursor.Should().BeEmpty();
        state.ClientSchemaVersion.Should().Be(0);
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
        (await _device.ReadAsync(context => context.SyncEntityStates.CountAsync(Ct))).Should().Be(0);
        (await _device.ReadAsync(context => context.SyncConflicts.CountAsync(Ct))).Should().Be(0);
        (await _device.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1, "trocar de servidor não apaga tarefa");
    }

    [Fact]
    public async Task AfterTheSwitch_NothingIsCapturedOrSent_UntilSomeoneSignsIn()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.Services.GetRequiredService<ISyncAccount>().SwitchServerAsync(Next, Ct);
        _authenticator.Server = Next;

        await _device.SaveAsync(context => context.Add(TaskItem.Create("Depois da troca", Now)), Ct);
        var result = await _device.Engine.RunCycleAsync(Ct);

        result.Status.Should().Be(SyncCycleStatus.Idle);
        _api.Calls.Should().BeEmpty();
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0, "sem conta, nada é capturado");
        (await _device.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1);
    }

    [Fact]
    public async Task RemovingTheServer_LeavesTheAppLocal_WithTheTasks()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        await _device.Services.GetRequiredService<ISyncAccount>().SwitchServerAsync(null, Ct);

        Address.Current.Should().BeNull();
        _authenticator.SignedOutOn.Should().Equal([Old]);
        (await _device.StoreAsync(store => store.ReadAsync(Ct))).State.Should().Be(SyncLinkState.Unlinked);
        (await _device.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1);
    }

    [Fact]
    public async Task SavingTheSameServerAgain_KeepsTheAccount()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        await _device.Services.GetRequiredService<ISyncAccount>().SwitchServerAsync(new Uri("https://SYNC.exemplo.com"), Ct);

        _authenticator.SignedOutOn.Should().BeEmpty();
        (await _device.StoreAsync(store => store.ReadAsync(Ct))).State.Should().Be(SyncLinkState.Linked);
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().BePositive();
    }

    [Fact]
    public async Task WithoutAnAccount_SwitchingOnlyChangesTheAddress()
    {
        _authenticator.HasCredentials = false;

        await _device.Services.GetRequiredService<ISyncAccount>().SwitchServerAsync(Next, Ct);

        Address.Current.Should().Be(Next);
        _authenticator.SignedOutOn.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInsecureAddress_IsRefused_AndNothingChanges()
    {
        await _device.LinkAsync(_user, _workspace, Ct);

        var switching = () => _device.Services.GetRequiredService<ISyncAccount>().SwitchServerAsync(new Uri("http://sync.exemplo.com/"), Ct);

        await switching.Should().ThrowAsync<MyTaskApp.Domain.DomainException>();
        Address.Current.Should().Be(Old);
        (await _device.StoreAsync(store => store.ReadAsync(Ct))).State.Should().Be(SyncLinkState.Linked);
    }

    /// <summary>Sempre com login, e anota em que servidor saiu da conta.</summary>
    private sealed class RecordingAuthenticator : ISyncAuthenticator
    {
        public Func<Uri?> Address { get; set; } = () => null;

        public Uri? Server { get; set; } = Old;

        public List<Uri?> SignedOutOn { get; } = [];

        public bool IsAvailable => Server is not null;

        public Uri? ServerBaseUrl => Server;

        public bool HasCredentials { get; set; } = true;

        public Task<SyncAuthorization> BeginAuthorizationAsync(bool includeRemote, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");

        public void ForgetAccessToken()
        {
        }

        public Task<bool> HasCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(HasCredentials);

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            SignedOutOn.Add(Address());
            HasCredentials = false;
            return Task.CompletedTask;
        }
    }
}
