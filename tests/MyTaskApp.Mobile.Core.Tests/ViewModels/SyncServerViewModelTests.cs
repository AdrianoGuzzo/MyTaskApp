using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using NSubstitute;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// O servidor de sincronização escolhido no celular (ADR-078): sem servidor o
/// app é inteiro; o salvo volta na próxima abertura; trocar ou tirar avisa,
/// não apaga tarefa e não leva nada para o servidor novo antes do login.
/// </summary>
public sealed class SyncServerViewModelTests : IAsyncDisposable
{
    private const string Server = "https://sync.exemplo.com/";

    private const string Other = "https://outro.example.invalid/";

    /// <summary>O nome do refresh token no cofre (<c>SyncOptions.SecretName</c>).</summary>
    private const string SecretName = "sync";

    private readonly List<MobileHarness> _apps = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ANewPhone_HasNoServer_AndSaysSyncIsOff()
    {
        var app = await AppAsync(serverUrl: null);
        var screen = app.Get<SyncServerViewModel>();

        await screen.LoadAsync();

        screen.HasServer.Should().BeFalse();
        screen.Url.Should().BeEmpty("nenhum servidor vem da build");
        screen.StateText.Should().Contain("desativada");
        screen.CanConnect.Should().BeFalse();
        app.Get<ISyncAccount>().IsAvailable.Should().BeFalse();
        app.Preferences.Values.Should().NotContainKey(SyncServerSettings.PreferenceKey);
    }

    [Fact]
    public async Task WithoutAServer_EverythingLocalWorks_AndNothingWaitsToBeSent()
    {
        var app = await AppAsync(serverUrl: null);

        var form = app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();
        form.Title = "Comprar pão";
        await form.SaveAsync();
        var taskId = (await app.Runner.RunAsync<SearchTasksHandler, TaskSearchResult>((handler, token) => handler.HandleAsync(new SearchTasks(), token), Ct)).Items.Single().Task.TaskId;

        var edit = app.Get<TaskFormViewModel>();
        await edit.InitializeEditAsync(taskId);
        edit.Title = "Comprar pão integral";
        await edit.SaveAsync();

        var detail = app.Get<TaskDetailViewModel>();
        await detail.LoadAsync(taskId);
        await detail.CompleteAsync();
        await detail.MoveToTrashAsync();

        detail.Item!.Task.Title.Should().Be("Comprar pão integral");
        detail.Item.Lifecycle.Should().Be(TaskLifecycle.Trashed);
        app.Dialogs.Alerts.Should().BeEmpty();
        (await StatusAsync(app)).Counts.Pending.Should().Be(0, "sem servidor e sem conta, nada é guardado para enviar");
    }

    [Fact]
    public async Task InDevelopment_TheLocalServerIsOnlySuggested()
    {
        var app = await AppAsync(serverUrl: null, suggestion: "http://localhost:5280/");
        var screen = app.Get<SyncServerViewModel>();

        await screen.LoadAsync();

        screen.Url.Should().Be("http://localhost:5280/");
        screen.HasServer.Should().BeFalse("a sugestão só preenche o campo");
        app.Get<ISyncAccount>().IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task SavingAValidAddress_KeepsIt_AndItComesBackAfterReopening()
    {
        var app = await AppAsync(serverUrl: null);
        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();

        screen.Url = "  https://sync.exemplo.com  ";
        screen.UrlError.Should().BeNull();
        screen.IsChanged.Should().BeTrue();
        await screen.SaveAsync();

        app.Dialogs.Questions.Should().BeEmpty("sem conta, não há o que avisar");
        app.Preferences.Values[SyncServerSettings.PreferenceKey].Should().Be(Server);
        screen.HasServer.Should().BeTrue();
        screen.CanConnect.Should().BeTrue();
        app.Get<ISyncAccount>().Server.Should().Be(new Uri(Server));

        var reopened = await ReopenAsync(app);

        reopened.Get<ISyncAccount>().IsAvailable.Should().BeTrue();
        reopened.Get<ISyncAccount>().Server.Should().Be(new Uri(Server));
        var again = reopened.Get<SyncServerViewModel>();
        await again.LoadAsync();
        again.Url.Should().Be(Server);
        again.IsChanged.Should().BeFalse();
    }

    [Theory]
    [InlineData("sync.exemplo.com")]
    [InlineData("ftp://sync.exemplo.com")]
    [InlineData("http://sync.exemplo.com")]
    [InlineData("http://localhost:5280")]
    [InlineData("https://ana:senha@sync.exemplo.com")]
    public async Task AnInvalidAddress_IsExplained_AndNotSaved(string url)
    {
        var app = await AppAsync(serverUrl: null);
        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();

        screen.Url = url;
        screen.UrlError.Should().NotBeNullOrWhiteSpace();
        await screen.SaveAsync();

        app.Dialogs.Alerts.Should().ContainSingle().Which.Title.Should().Be("Endereço inválido");
        app.Preferences.Values.Should().NotContainKey(SyncServerSettings.PreferenceKey);
        screen.HasServer.Should().BeFalse();
    }

    [Fact]
    public async Task AnHttpAddressSavedInDevelopment_IsNotUsedInARelease_ButShownToFix()
    {
        var preferences = new MemoryPreferences();
        preferences.Set(SyncServerSettings.PreferenceKey, "http://localhost:5280/");
        var app = await AppAsync(serverUrl: null, preferences: preferences);
        var screen = app.Get<SyncServerViewModel>();

        await screen.LoadAsync();

        app.Get<ISyncAccount>().IsAvailable.Should().BeFalse("a Release exige HTTPS");
        screen.Url.Should().Be("http://localhost:5280/");
        screen.UrlError.Should().Contain("https://");
    }

    [Fact]
    public async Task WithoutAnAccount_ChangingAndRemoving_AskNothing()
    {
        var app = await AppAsync();
        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();

        screen.Url = Other;
        await screen.SaveAsync();
        app.Get<ISyncAccount>().Server.Should().Be(new Uri(Other));

        await screen.RemoveAsync();

        app.Dialogs.Questions.Should().BeEmpty();
        app.Preferences.Values.Should().NotContainKey(SyncServerSettings.PreferenceKey);
        app.Get<ISyncAccount>().IsAvailable.Should().BeFalse();
        screen.HasServer.Should().BeFalse();
        screen.Url.Should().BeEmpty();
        app.Dialogs.Toasts.Should().Contain(toast => toast.Contains("desativada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingWithAnAccountAndPendingChanges_AsksFirst_ForgetsTheOldServer_AndKeepsTheTasks()
    {
        var app = await AppAsync();
        await LinkAsync(app);
        var created = await app.CreateTaskAsync("Escrita sem rede");
        (await StatusAsync(app)).Counts.Pending.Should().BePositive();

        var change = await app.Get<SyncServerSettings>().PreviewAsync(new Uri(Other), Ct);
        change.NeedsConfirmation.Should().BeTrue();
        change.LinkedServer.Should().Be(new Uri(Server));
        change.Account.Should().Be("ana@exemplo.com");
        change.PendingChanges.Should().BePositive();

        // O cofre fica com um token de outro servidor: o teste não fala com rede nenhuma.
        app.Secrets.Secrets[SecretName] = "https://antigo.example.invalid/ refresh-antigo";

        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();
        screen.Url = Other;
        await screen.SaveAsync();

        app.Dialogs.Questions.Should().Equal("Trocar de servidor");
        app.Preferences.Values[SyncServerSettings.PreferenceKey].Should().Be(Other);
        app.Secrets.Secrets.Should().BeEmpty("a credencial era do servidor anterior");

        var status = await StatusAsync(app);
        status.Server.Should().Be(new Uri(Other));
        status.Local.State.Should().Be(SyncLinkState.Unlinked);
        status.Local.Server.Should().BeNull();
        status.Local.UserId.Should().BeNull();
        status.Local.Cursor.Should().BeEmpty();
        status.Counts.Pending.Should().Be(0);
        (await app.DetailsAsync(created.TaskId)).Task.Title.Should().Be("Escrita sem rede");

        // Até alguém entrar no novo, o ciclo nem começa.
        (await app.Get<SyncEngine>().RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Idle);
        screen.CanConnect.Should().BeTrue();
        screen.StateText.Should().Contain("Entre com sua conta");
    }

    [Fact]
    public async Task CancellingTheSwitch_ChangesNothing()
    {
        var app = await AppAsync();
        await LinkAsync(app);
        await app.CreateTaskAsync("Fica como está");
        var pending = (await StatusAsync(app)).Counts.Pending;
        app.Dialogs.Confirms = false;

        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();
        screen.Url = Other;
        await screen.SaveAsync();

        app.Dialogs.Questions.Should().Equal("Trocar de servidor");
        app.Preferences.Values[SyncServerSettings.PreferenceKey].Should().Be(Server);
        var status = await StatusAsync(app);
        status.Server.Should().Be(new Uri(Server));
        status.Local.State.Should().Be(SyncLinkState.Linked);
        status.Counts.Pending.Should().Be(pending);
    }

    [Fact]
    public async Task RemovingTheServerWithAnAccount_AsksFirst_AndLeavesTheTasksHere()
    {
        var app = await AppAsync();
        await LinkAsync(app);
        var created = await app.CreateTaskAsync("Só aqui");

        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();
        await screen.RemoveAsync();

        app.Dialogs.Questions.Should().Equal("Tirar o servidor");
        app.Get<ISyncAccount>().IsAvailable.Should().BeFalse();
        (await StatusAsync(app)).Local.State.Should().Be(SyncLinkState.Unlinked);
        (await app.DetailsAsync(created.TaskId)).Task.Title.Should().Be("Só aqui");
    }

    /// <summary>As versões antigas traziam o servidor na build: quem tinha entrado nele continua nele.</summary>
    [Fact]
    public async Task AnUpdatedPhoneThatHadSignedIn_KeepsItsServer()
    {
        var app = await AppAsync(serverUrl: null);
        await LinkAsync(app);

        var updated = await ReopenAsync(app);

        updated.Preferences.Values[SyncServerSettings.PreferenceKey].Should().Be(Server);
        updated.Get<ISyncAccount>().Server.Should().Be(new Uri(Server));
        (await StatusAsync(updated)).Local.State.Should().Be(SyncLinkState.Linked);
    }

    [Fact]
    public async Task AfterRemovingTheServer_ItIsNotAdoptedBack()
    {
        var app = await AppAsync();
        await LinkAsync(app);
        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();
        await screen.RemoveAsync();

        var reopened = await ReopenAsync(app);

        reopened.Get<ISyncAccount>().IsAvailable.Should().BeFalse();
        reopened.Preferences.Values.Should().NotContainKey(SyncServerSettings.PreferenceKey);
    }

    [Fact]
    public void TheWarning_SaysWhatHappensToThePendingChanges()
    {
        var text = SyncServerSettings.Consequences(new SyncServerChange(true, new Uri(Server), "ana@exemplo.com", 3), new Uri(Other));

        text.Should().Contain("sync.exemplo.com").And.Contain("ana@exemplo.com").And.Contain("3 alterações")
            .And.Contain("outro.example.invalid").And.Contain("Nenhuma tarefa é apagada");
        SyncServerSettings.Consequences(new SyncServerChange(true, new Uri(Server), null, 0), null)
            .Should().Contain("desligada").And.Contain("Não há alterações");
    }

    [Fact]
    public async Task TestingTheConnection_ShowsTheResult_AndAnInvalidAddressIsNotTested()
    {
        var app = await AppAsync(serverUrl: null);
        var probe = Substitute.For<ISyncServerProbe>();
        probe.TestAsync(new Uri(Server), Arg.Any<CancellationToken>())
            .Returns(new SyncServerProbeResult(SyncServerProbeKind.Dns, "Endereço não encontrado."));
        var screen = new SyncServerViewModel(
            app.Get<SyncServerSettings>(),
            probe,
            app.Runner,
            app.Navigator,
            app.Get<MobileHostOptions>(),
            app.Clock,
            app.Dialogs,
            NullLogger<SyncServerViewModel>.Instance);
        await screen.LoadAsync();

        screen.Url = "http://sync.exemplo.com";
        await screen.TestAsync();
        screen.Probe!.Kind.Should().Be(SyncServerProbeKind.InvalidUrl);
        await probe.DidNotReceiveWithAnyArgs().TestAsync(default!, Ct);

        screen.Url = Server;
        await screen.TestAsync();
        screen.ProbeFailed.Should().BeTrue();
        screen.ProbeText.Should().Be("Endereço não encontrado.");
        screen.IsTesting.Should().BeFalse();
        app.Get<ISyncAccount>().IsAvailable.Should().BeFalse("testar não salva");
    }

    [Fact]
    public async Task TheAccountTab_FollowsTheServer()
    {
        var app = await AppAsync(serverUrl: null);
        var account = app.Get<AccountViewModel>();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)account).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        account.IsAvailable.Should().BeFalse();

        await account.OpenServerAsync();
        app.Navigator.Last.Should().Be("server");

        var screen = app.Get<SyncServerViewModel>();
        await screen.LoadAsync();
        screen.Url = Server;
        await screen.SaveAsync();

        changed.Should().Contain(nameof(AccountViewModel.IsAvailable));
        account.IsAvailable.Should().BeTrue();
        account.Server.Should().Be("https://sync.exemplo.com");
    }

    private async Task<MobileHarness> AppAsync(string? serverUrl = Server, string? suggestion = null, MemoryPreferences? preferences = null)
    {
        var app = await MobileHarness.CreateAsync(serverUrl, preferences: preferences, suggestedServerUrl: suggestion);
        _apps.Add(app);
        return app;
    }

    private async Task<MobileHarness> ReopenAsync(MobileHarness app)
    {
        _apps.Remove(app);
        var reopened = await app.ReopenAsync();
        _apps.Add(reopened);
        return reopened;
    }

    private static Task LinkAsync(MobileHarness app) =>
        app.Runner.RunAsync<ISyncLocalStore>((store, token) => store.LinkAsync(
            new SyncAccountLink(Guid.CreateVersion7(), "Ana", "ana@exemplo.com", Guid.CreateVersion7(), app.Get<IDeviceIdentity>().DeviceId, new Uri(Server)),
            token),
            Ct);

    private static Task<SyncStatusView> StatusAsync(MobileHarness app) =>
        app.Runner.RunAsync<GetSyncStatusHandler, SyncStatusView>((handler, token) => handler.HandleAsync(new GetSyncStatus(), token), Ct);
}
