using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela de Integrações (ADR-045): o caminho de um clique, o avançado, e o
/// que a tela mostra em cada estado da conexão.
/// </summary>
public class IntegrationsViewModelTests
{
    private static readonly JiraConnection Disconnected = JiraConnection.Disconnected(isOAuthAvailable: true);

    private static readonly JiraConnection Connected = new(
        JiraConnectionState.Connected,
        JiraAuthMethod.OAuth,
        "empresa",
        "https://empresa.atlassian.net",
        "Ana Dev",
        "ana@empresa.com",
        "GAECO")
    {
        IsOAuthAvailable = true,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeShellLauncher _shell = new();

    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };

    public IntegrationsViewModelTests()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = Disconnected;
        _runner.ResultsByHandler[typeof(GetBranchConventionsHandler)] = BranchConventions.Default.ToText();
        _runner.ResultsByHandler[typeof(ListJiraProjectsHandler)] =
            (IReadOnlyList<JiraProject>)[new JiraProject("GAECO", "Gaeco"), new JiraProject("ECO", "Ecossistema")];
    }

    private IntegrationsViewModel ViewModel() =>
        new(_runner, _shell, _confirmation, NullLogger<IntegrationsViewModel>.Instance);

    private async Task<IntegrationsViewModel> LoadedAsync()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        return viewModel;
    }

    [Fact]
    public async Task Disconnected_ShowsTheOneClickButton_AndHidesTheTokenForm()
    {
        var viewModel = await LoadedAsync();

        viewModel.IsDisconnected.Should().BeTrue();
        viewModel.ShowConnectButton.Should().BeTrue();
        viewModel.IsApiTokenFormOpen.Should().BeFalse("o API token é o caminho avançado, e não o principal");
    }

    [Fact]
    public async Task ABuildWithoutOAuth_OpensTheTokenFormDirectly()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = JiraConnection.Disconnected(isOAuthAvailable: false);

        var viewModel = await LoadedAsync();

        viewModel.ShowConnectButton.Should().BeFalse();
        viewModel.IsApiTokenFormOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Connect_OpensTheBrowser_WaitsAndShowsTheAccount()
    {
        var completion = new TaskCompletionSource<JiraConnection>();
        var url = new Uri("https://auth.atlassian.com/authorize?client_id=x");
        _runner.ResultsByHandler[typeof(BeginJiraAuthorizationHandler)] = new JiraAuthorization(url, completion.Task);
        var viewModel = await LoadedAsync();
        var changed = 0;
        viewModel.ConnectionChanged += () => changed++;

        var connecting = viewModel.ConnectAsync();

        _shell.OpenedUris.Should().Equal(url);
        viewModel.IsAuthorizing.Should().BeTrue();
        viewModel.ShowConnectButton.Should().BeFalse();

        completion.SetResult(Connected);
        await connecting;

        viewModel.IsAuthorizing.Should().BeFalse();
        viewModel.IsConnected.Should().BeTrue();
        viewModel.AccountLabel.Should().Be("Ana Dev · ana@empresa.com");
        viewModel.SiteLabel.Should().Be("empresa.atlassian.net");
        viewModel.SelectedProject!.Key.Should().Be("GAECO");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Connect_DeniedInTheBrowser_ShowsTheReason()
    {
        var completion = new TaskCompletionSource<JiraConnection>();
        _runner.ResultsByHandler[typeof(BeginJiraAuthorizationHandler)] =
            new JiraAuthorization(new Uri("https://auth.atlassian.com/authorize"), completion.Task);
        var viewModel = await LoadedAsync();

        var connecting = viewModel.ConnectAsync();
        completion.SetException(new DomainException("A autorização foi recusada no Jira."));
        await connecting;

        viewModel.ErrorMessage.Should().Be("A autorização foi recusada no Jira.");
        viewModel.IsAuthorizing.Should().BeFalse();
        viewModel.ShowConnectButton.Should().BeTrue();
    }

    [Fact]
    public async Task Connect_CanBeCancelled()
    {
        var completion = new TaskCompletionSource<JiraConnection>();
        _runner.ResultsByHandler[typeof(BeginJiraAuthorizationHandler)] =
            new JiraAuthorization(new Uri("https://auth.atlassian.com/authorize"), completion.Task);
        var viewModel = await LoadedAsync();

        var connecting = viewModel.ConnectAsync();
        viewModel.CancelAuthorization();
        await connecting;

        viewModel.IsAuthorizing.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task SeveralSites_AreOffered_AndChoosingOneConnects()
    {
        var site = new JiraSite("b", "cliente", "https://cliente.atlassian.net");
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] =
            new JiraConnection(JiraConnectionState.ChoosingSite, JiraAuthMethod.OAuth, Sites: [site]) { IsOAuthAvailable = true };
        _runner.ResultsByHandler[typeof(ChooseJiraSiteHandler)] = Connected;
        var viewModel = await LoadedAsync();

        viewModel.IsChoosingSite.Should().BeTrue();
        viewModel.Sites.Should().Equal(site);

        await viewModel.ChooseSiteAsync(site);

        viewModel.IsConnected.Should().BeTrue();
        _runner.Invoked.Should().Contain(typeof(ChooseJiraSiteHandler));
    }

    [Fact]
    public async Task ApiToken_Connects_AndTheTokenLeavesTheScreen()
    {
        _runner.ResultsByHandler[typeof(ConnectJiraWithApiTokenHandler)] = Connected with { Method = JiraAuthMethod.ApiToken };
        var viewModel = await LoadedAsync();
        viewModel.ToggleApiTokenForm();
        viewModel.SiteUrl = "empresa";
        viewModel.Email = "ana@empresa.com";
        viewModel.ApiToken = "ATATT-secreto";

        await viewModel.ConnectWithApiTokenAsync();

        viewModel.IsConnected.Should().BeTrue();
        viewModel.ApiToken.Should().BeEmpty();
        viewModel.IsApiTokenFormOpen.Should().BeFalse();
        viewModel.MethodLabel.Should().Contain("API token");
    }

    [Fact]
    public async Task ApiToken_Refused_KeepsTheFormToTryAgain()
    {
        _runner.FailuresByHandler[typeof(ConnectJiraWithApiTokenHandler)] =
            new DomainException("O Jira não aceitou esse e-mail e token. Confira e tente de novo.");
        var viewModel = await LoadedAsync();
        viewModel.ToggleApiTokenForm();
        viewModel.ApiToken = "errado";

        await viewModel.ConnectWithApiTokenAsync();

        viewModel.ErrorMessage.Should().Contain("e-mail e token");
        viewModel.IsApiTokenFormOpen.Should().BeTrue();
    }

    [Fact]
    public async Task ATechnicalFailure_NeverReachesTheScreen()
    {
        _runner.FailuresByHandler[typeof(TestJiraConnectionHandler)] =
            new HttpRequestException("No such host is known. (api.atlassian.com:443)");
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = Connected;
        var viewModel = await LoadedAsync();

        await viewModel.TestAsync();

        viewModel.ErrorMessage.Should().Be("Não foi possível testar a conexão.");
    }

    [Fact]
    public async Task Disconnect_AsksFirst_AndExplainsThatLinkedTasksStay()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = Connected;
        var viewModel = await LoadedAsync();
        _confirmation.Answer = false;

        await viewModel.DisconnectAsync();

        _confirmation.LastAsked!.Message.Should().Contain("continuam");
        _runner.Invoked.Should().NotContain(typeof(DisconnectJiraHandler));
    }

    [Fact]
    public async Task Disconnect_Confirmed_GoesBackToTheButton()
    {
        _runner.Enqueue<GetJiraConnectionHandler>(Connected, Disconnected);
        var viewModel = await LoadedAsync();

        await viewModel.DisconnectAsync();

        _runner.Invoked.Should().Contain(typeof(DisconnectJiraHandler));
        viewModel.IsDisconnected.Should().BeTrue();
    }

    [Fact]
    public async Task ChoosingTheDefaultProject_SavesIt()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = Connected;
        _runner.ResultsByHandler[typeof(SetJiraDefaultProjectHandler)] = Connected with { DefaultProject = "ECO" };
        var viewModel = await LoadedAsync();
        _runner.Invoked.Clear();

        viewModel.SelectedProject = viewModel.Projects.Single(option => option.Key == "ECO");
        await Task.Yield();

        _runner.Invoked.Should().Contain(typeof(SetJiraDefaultProjectHandler));
        viewModel.Connection!.DefaultProject.Should().Be("ECO");
    }

    [Fact]
    public async Task LoadingTheProjects_DoesNotSaveTheProjectBack()
    {
        _runner.ResultsByHandler[typeof(GetJiraConnectionHandler)] = Connected;

        await LoadedAsync();

        _runner.Invoked.Should().NotContain(typeof(SetJiraDefaultProjectHandler));
    }

    [Fact]
    public async Task TheConventionsPreview_FollowsWhatIsTyped()
    {
        var viewModel = await LoadedAsync();

        viewModel.ConventionsPreview.Should().Contain("Bug PROJ-123 → bug/PROJ-123");

        viewModel.ConventionsText = "Bug = fix/{id}";

        viewModel.ConventionsPreview.Should().Contain("Bug PROJ-123 → fix/PROJ-123");
    }

    [Fact]
    public async Task AnInvalidConvention_StaysOnScreenWithTheReason()
    {
        _runner.FailuresByHandler[typeof(UpdateBranchConventionsHandler)] =
            new DomainException("Convenção de branch, linha 1: o molde precisa de {id}, que liga a branch à issue.");
        var viewModel = await LoadedAsync();
        viewModel.ConventionsText = "Bug = fix/";

        await viewModel.SaveConventionsAsync();

        viewModel.ConventionsError.Should().Contain("{id}");
        viewModel.ConventionsText.Should().Be("Bug = fix/");
    }

    [AvaloniaFact]
    public void TheWindow_DrawsEveryState_WithoutBrokenBindings()
    {
        var viewModel = ViewModel();
        var window = new IntegrationsWindow(viewModel);
        window.Show();

        viewModel.Connection = Disconnected;
        window.FindControl<Button>("ConnectButton")!.IsEffectivelyVisible.Should().BeTrue();

        viewModel.Connection = Connected;
        viewModel.Projects.Add(new JiraProjectOption("GAECO", "GAECO — Gaeco"));
        window.FindControl<Button>("ConnectButton")!.IsEffectivelyVisible.Should().BeFalse();
        window.FindControl<ComboBox>("ProjectBox")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<ComboBox>("ProjectBox")!.ItemCount.Should().Be(2);

        window.Close();
        window.IsVisible.Should().BeFalse("o X esconde: a janela é singleton");
    }
}
