using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.SignIn;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Sync.Contracts;
using NSubstitute;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// Entrar com a conta pelo navegador do sistema e a aba Conta. O login de
/// ponta a ponta contra o servidor de verdade está nos testes de integração do
/// servidor; aqui, o que a tela faz com cada resultado.
/// </summary>
public sealed class ConnectAndAccountTests : IAsyncLifetime
{
    private readonly ISyncAccount _account = Substitute.For<ISyncAccount>();

    private readonly AppLinkAuthorizationRedirect _redirect = new(SyncSchema.MobileClientId, new Uri(SyncSchema.MobileRedirectUri));

    private readonly FakeBrowser _browser = new();

    private readonly RecordingNavigator _navigator = new();

    private readonly ScriptedDialogs _dialogs = new();

    private MobileHarness _app = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await MobileHarness.CreateAsync();
        _account.IsAvailable.Returns(true);
        _account.Server.Returns(new Uri("https://sync.exemplo.com/"));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task SigningIn_OpensTheBrowser_DeliversTheReturn_AndGoesToTheTasks()
    {
        IReadOnlyDictionary<string, string>? delivered = null;
        AuthorizeThenWait(code => delivered = code);
        var connect = Create();

        await connect.SignInAsync();

        _browser.Opened!.AbsolutePath.Should().Be(ApiRoutes.Authorize);
        _browser.Callback.Should().Be(new Uri(SyncSchema.MobileRedirectUri));
        delivered!["code"].Should().Be("codigo");
        connect.State.Should().Be(ConnectState.Done);
        _navigator.Last.Should().Be("tasks");
        _dialogs.Toasts.Should().Contain("Conta conectada.");
    }

    [Fact]
    public async Task ClosingTheBrowser_SaysTheLoginWasCancelled()
    {
        AuthorizeThenWait();
        _browser.Answer = _ => null;
        var connect = Create();

        await connect.SignInAsync();

        connect.State.Should().Be(ConnectState.Failed);
        connect.Message.Should().Contain("cancelado");
        _navigator.Visits.Should().BeEmpty();
    }

    [Fact]
    public async Task ABrowserThatWillNotOpen_IsExplained()
    {
        AuthorizeThenWait();
        _browser.Failure = new InvalidOperationException("sem navegador");
        var connect = Create();

        await connect.SignInAsync();

        connect.State.Should().Be(ConnectState.Failed);
        connect.Message.Should().Be("Não foi possível abrir o navegador para entrar.");
    }

    [Fact]
    public async Task ARevokedPhone_IsToldWhatHappens()
    {
        _account.BeginConnectAsync(Arg.Any<CancellationToken>()).Returns(new SyncAuthorization(
            new Uri("https://sync.exemplo.com/connect/authorize?state=s"),
            Task.FromException(new SyncApiException(SyncFailureKind.DeviceRevoked, "Este celular foi revogado."))));
        var connect = Create();

        await connect.SignInAsync();

        connect.Message.Should().Be("Este celular foi revogado.");
    }

    [Fact]
    public async Task WithoutAServer_ThereIsNothingToSignInTo()
    {
        _account.IsAvailable.Returns(false);
        var connect = Create();

        connect.CanSignIn.Should().BeFalse();
        await connect.SignInAsync();

        connect.Message.Should().Be(ConnectViewModel.NoServer);
        await _account.DidNotReceive().BeginConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkippingStaysLocal()
    {
        await Create().SkipAsync();

        _navigator.Last.Should().Be("tasks");
    }

    [Fact]
    public async Task TheAccountTab_WithoutAnAccount_OffersToConnect()
    {
        var account = _app.Get<AccountViewModel>();

        await account.LoadAsync();

        account.IsLinked.Should().BeFalse();
        account.CanConnect.Should().BeTrue();
        account.Badge!.Kind.Should().Be(SyncIndicatorKind.LocalOnly);
        account.Server.Should().Be("https://sync.exemplo.com");
        account.LastSync.Should().Be("Ainda não sincronizou.");
        account.Conflicts.Should().BeEmpty();
        account.Failures.Should().BeEmpty();
        account.Devices.Should().BeEmpty();

        await account.ConnectAsync();
        _app.Navigator.Last.Should().Be("connect");
    }

    [Fact]
    public void TheAccountTab_OffersTheDesktopThemes_AndAppliesOnTouch()
    {
        var account = _app.Get<AccountViewModel>();

        account.ThemeOptions.Select(option => option.Label)
            .Should().Equal("Automático", "Papel", "Sépia", "Carvão", "Nórdico", "Ameixa", "Verde Pecuária", "Alto contraste");
        account.ThemeOptions.Single(option => option.IsSelected).Value.Should().Be(MobileThemeCatalog.SystemId);

        account.UseTheme(account.ThemeOptions.Single(option => option.Value == "nordic"));

        _app.Themes.Used.Should().Equal("nordic");
        account.ThemeOptions.Single(option => option.IsSelected).Label.Should().Be("Nórdico");
        account.ThemeOptions.Single(option => option.Value == "nordic").Accent.Should().Be(MobileThemeCatalog.Nordic.Palette.Accent);
    }

    [Fact]
    public async Task SigningOut_AsksFirst_AndKeepsTheTasks()
    {
        await _app.CreateTaskAsync("Fica aqui");
        var account = _app.Get<AccountViewModel>();
        await account.LoadAsync();

        await account.SignOutAsync();

        _app.Dialogs.Questions.Should().Contain("Sair da conta");
        account.IsLinked.Should().BeFalse();
        (await _app.Runner.RunAsync<MyTaskApp.Application.Tasks.SearchTasksHandler, MyTaskApp.Application.Tasks.TaskSearchResult>(
            (handler, token) => handler.HandleAsync(new MyTaskApp.Application.Tasks.SearchTasks(), token), TestContext.Current.CancellationToken)).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task TheBrowserReturn_ReachesTheWaitingLogin()
    {
        var signIn = new AppLinkSignIn(_browser, _redirect);
        var session = _redirect.Open();
        var code = session.WaitForCodeAsync("s1", TestContext.Current.CancellationToken);

        await signIn.RunAsync(new Uri("https://sync.exemplo.com/connect/authorize?state=s1"), TestContext.Current.CancellationToken);

        (await code).Should().Be("codigo");
    }

    [Fact]
    public async Task ABrowserFailure_AbandonsTheWaitingLogin()
    {
        _browser.Failure = new InvalidOperationException();
        var signIn = new AppLinkSignIn(_browser, _redirect);
        var session = _redirect.Open();
        var code = session.WaitForCodeAsync("s1", TestContext.Current.CancellationToken);

        var run = () => signIn.RunAsync(new Uri("https://sync.exemplo.com/connect/authorize?state=s1"), TestContext.Current.CancellationToken);

        await run.Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => code)).Should().ThrowAsync<DomainException>();
    }

    /// <summary>O login de verdade espera a volta pelo redirect; aqui, a espera é a mesma, e o código só é registrado.</summary>
    private void AuthorizeThenWait(Action<IReadOnlyDictionary<string, string>>? onCode = null)
    {
        _account.BeginConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var session = _redirect.Open();
            var completion = WaitAsync(session);
            return new SyncAuthorization(new Uri("https://sync.exemplo.com/connect/authorize?state=estado-1&client_id=mytaskapp-mobile"), completion);
        });

        async Task WaitAsync(ISyncAuthorizationSession session)
        {
            var code = await session.WaitForCodeAsync("estado-1", CancellationToken.None);
            onCode?.Invoke(new Dictionary<string, string> { ["code"] = code });
        }
    }

    private ConnectViewModel Create() => new(
        _account,
        new AppLinkSignIn(_browser, _redirect),
        _navigator,
        _dialogs,
        NullLogger<ConnectViewModel>.Instance);
}
