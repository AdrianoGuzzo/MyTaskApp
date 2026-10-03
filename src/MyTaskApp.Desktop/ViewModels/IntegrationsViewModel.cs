using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha do "Projeto padrão". <c>Key</c> nulo é "todos os projetos".</summary>
public sealed record JiraProjectOption(string? Key, string Label)
{
    public static readonly JiraProjectOption All = new(null, "Todos os projetos");

    public override string ToString() => Label;
}

/// <summary>
/// A janela "Integrações…" (ADR-045): conectar o Jira, escolher o projeto
/// padrão e as convenções de branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero fricção.</b> O caminho principal é um botão: o navegador abre na
/// Atlassian, o usuário autoriza, o app volta conectado. O API token existe,
/// mas atrás de "Conectar com API token (avançado)" — e só vira o caminho
/// principal numa build sem o app OAuth.
/// </para>
/// <para>
/// O token digitado não fica no ViewModel depois de conectar: o campo se
/// esvazia, e quem guarda é o cofre da Infrastructure.
/// </para>
/// </remarks>
public sealed partial class IntegrationsViewModel(
    IUseCaseRunner runner,
    IShellLauncher shell,
    IConfirmationDialog confirmation,
    ILogger<IntegrationsViewModel> logger) : ObservableObject
{
    /// <summary>Onde a conta Atlassian cria API tokens. O link do caminho avançado.</summary>
    public static readonly Uri ApiTokenPage = new("https://id.atlassian.com/manage-profile/security/api-tokens");

    private static readonly ExternalTask[] PreviewIssues =
    [
        new() { Id = "PROJ-123", Title = "Corrigir erro de sincronização", IssueType = "Bug" },
        new() { Id = "PROJ-124", Title = "Novo relatório mensal", IssueType = "Story" },
        new() { Id = "PROJ-125", Title = "Atualizar dependências", IssueType = "Task" },
    ];

    private CancellationTokenSource? _authorization;

    /// <summary>A troca de projeto vinda da carga, que não deve gravar de volta.</summary>
    private bool _showingProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsConnected), nameof(IsDisconnected), nameof(IsChoosingSite), nameof(IsOAuthAvailable),
        nameof(ShowConnectButton), nameof(AccountLabel), nameof(SiteLabel), nameof(MethodLabel))]
    private JiraConnection? _connection;

    /// <summary>O navegador está aberto na Atlassian, e o app espera a volta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    private bool _isAuthorizing;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>O formulário do API token aberto. Fechado por padrão quando há OAuth.</summary>
    [ObservableProperty]
    private bool _isApiTokenFormOpen;

    [ObservableProperty]
    private string _siteUrl = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _apiToken = string.Empty;

    [ObservableProperty]
    private JiraProjectOption? _selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConventionsPreview))]
    private string _conventionsText = string.Empty;

    [ObservableProperty]
    private string? _conventionsError;

    public ObservableCollection<JiraSite> Sites { get; } = [];

    public ObservableCollection<JiraProjectOption> Projects { get; } = [JiraProjectOption.All];

    public bool IsConnected => Connection?.IsConnected == true;

    public bool IsDisconnected => Connection is null or { State: JiraConnectionState.Disconnected };

    public bool IsChoosingSite => Connection?.State == JiraConnectionState.ChoosingSite;

    public bool IsOAuthAvailable => Connection?.IsOAuthAvailable == true;

    /// <summary>O botão grande: desconectado, com o app OAuth, e sem uma autorização em curso.</summary>
    public bool ShowConnectButton => IsDisconnected && IsOAuthAvailable && !IsAuthorizing;

    public string AccountLabel => Connection switch
    {
        { AccountName: { } name, AccountEmail: { } email } => $"{name} · {email}",
        { AccountName: { } name } => name,
        { AccountEmail: { } email } => email,
        _ => string.Empty,
    };

    public string SiteLabel => Connection?.SiteUrl is { } url ? new Uri(url).Host : string.Empty;

    public string MethodLabel => Connection?.Method switch
    {
        JiraAuthMethod.OAuth => "Conectado pela conta Atlassian",
        JiraAuthMethod.ApiToken => "Conectado com API token",
        _ => string.Empty,
    };

    /// <summary>"Bug → bug/PROJ-123": o efeito das convenções enquanto se digita.</summary>
    public string ConventionsPreview
    {
        get
        {
            try
            {
                var conventions = string.IsNullOrWhiteSpace(ConventionsText)
                    ? BranchConventions.Default
                    : BranchConventions.Parse(ConventionsText);
                var strategy = new ConventionBranchNameStrategy(conventions);

                return string.Join(
                    Environment.NewLine,
                    PreviewIssues.Select(issue => $"{issue.IssueType} {issue.Id} → {strategy.GenerateBranchName(issue)}"));
            }
            catch (DomainException exception)
            {
                return exception.Message;
            }
        }
    }

    /// <summary>A conexão mudou: a captura volta a perguntar se há Jira para sugerir.</summary>
    public event Action? ConnectionChanged;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        JiraConnection? connection = null;
        string? conventions = null;

        var loaded = await TryAsync(
            async () =>
            {
                connection = await runner.RunAsync<GetJiraConnectionHandler, JiraConnection>(
                    (handler, token) => handler.HandleAsync(new GetJiraConnection(), token),
                    cancellationToken);

                conventions = await runner.RunAsync<GetBranchConventionsHandler, string>(
                    (handler, token) => handler.HandleAsync(new GetBranchConventions(), token),
                    cancellationToken);
            },
            "Não foi possível carregar as integrações.");

        if (!loaded)
        {
            return;
        }

        Show(connection!);
        ConventionsText = conventions!;
        ConventionsError = null;
        StatusMessage = null;

        if (IsConnected)
        {
            await LoadProjectsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// O botão "Conectar ao Jira": abre o navegador na Atlassian e espera a
    /// volta. Cancelar (ou fechar a janela) desiste e solta a porta.
    /// </summary>
    [RelayCommand]
    public async Task ConnectAsync()
    {
        _authorization?.Cancel();
        _authorization = new CancellationTokenSource();
        var cancellation = _authorization.Token;

        JiraAuthorization? authorization = null;

        var begun = await TryAsync(
            async () => authorization = await runner.RunAsync<BeginJiraAuthorizationHandler, JiraAuthorization>(
                (handler, token) => handler.HandleAsync(new BeginJiraAuthorization(), token),
                cancellation),
            "Não foi possível iniciar a conexão com o Jira.");

        if (!begun)
        {
            return;
        }

        IsAuthorizing = true;
        StatusMessage = "Conclua a autorização no navegador. O MyTaskApp espera aqui.";

        if (!await shell.OpenUriAsync(authorization!.AuthorizeUrl))
        {
            StatusMessage = null;
            ErrorMessage = "Não foi possível abrir o navegador. Tente de novo.";
        }

        try
        {
            var connection = await authorization.Completion.WaitAsync(cancellation);

            Show(connection);
            StatusMessage = IsConnected ? "Jira conectado." : null;
            ConnectionChanged?.Invoke();

            if (IsConnected)
            {
                await LoadProjectsAsync(CancellationToken.None);
            }
        }
        catch (DomainException exception)
        {
            StatusMessage = null;
            ErrorMessage = exception.Message;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "JiraAuthorizationFailed");
            StatusMessage = null;
            ErrorMessage = "Não foi possível concluir a conexão com o Jira. Tente de novo.";
        }
        finally
        {
            IsAuthorizing = false;
        }
    }

    [RelayCommand]
    public void CancelAuthorization()
    {
        _authorization?.Cancel();
        IsAuthorizing = false;
    }

    [RelayCommand]
    public async Task ChooseSiteAsync(JiraSite site)
    {
        JiraConnection? connection = null;

        var chosen = await TryAsync(
            async () => connection = await runner.RunAsync<ChooseJiraSiteHandler, JiraConnection>(
                (handler, token) => handler.HandleAsync(new ChooseJiraSite(site.Id), token)),
            "Não foi possível usar esse site.");

        if (chosen)
        {
            Show(connection!);
            StatusMessage = "Jira conectado.";
            ConnectionChanged?.Invoke();
            await LoadProjectsAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    public void ToggleApiTokenForm() => IsApiTokenFormOpen = !IsApiTokenFormOpen;

    [RelayCommand]
    public Task OpenApiTokenPageAsync() => shell.OpenUriAsync(ApiTokenPage);

    [RelayCommand]
    public async Task ConnectWithApiTokenAsync()
    {
        JiraConnection? connection = null;
        var command = new ConnectJiraWithApiToken(SiteUrl, Email, ApiToken);

        var connected = await TryAsync(
            async () => connection = await runner.RunAsync<ConnectJiraWithApiTokenHandler, JiraConnection>(
                (handler, token) => handler.HandleAsync(command, token)),
            "Não foi possível conectar com o API token.");

        if (!connected)
        {
            return;
        }

        // O token não fica na memória da tela: quem guarda é o cofre.
        ApiToken = string.Empty;
        IsApiTokenFormOpen = false;
        Show(connection!);
        StatusMessage = "Jira conectado.";
        ConnectionChanged?.Invoke();
        await LoadProjectsAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task TestAsync()
    {
        JiraConnection? connection = null;

        var tested = await TryAsync(
            async () => connection = await runner.RunAsync<TestJiraConnectionHandler, JiraConnection>(
                (handler, token) => handler.HandleAsync(new TestJiraConnection(), token)),
            "Não foi possível testar a conexão.");

        if (tested)
        {
            Show(connection!);
            StatusMessage = $"Conexão funcionando. Conta: {AccountLabel}.";
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            "Desconectar o Jira?",
            "As tarefas já vinculadas continuam com a chave, o título e o link. "
            + "Só a busca e o \"Atualizar do Jira\" ficam indisponíveis até você conectar de novo.",
            "Desconectar"));

        if (!confirmed)
        {
            return;
        }

        var disconnected = await TryAsync(
            () => runner.RunAsync<DisconnectJiraHandler>(
                (handler, token) => handler.HandleAsync(new DisconnectJira(), token)),
            "Não foi possível desconectar o Jira.");

        if (disconnected)
        {
            await LoadAsync(CancellationToken.None);
            StatusMessage = "Jira desconectado.";
            ConnectionChanged?.Invoke();
        }
    }

    [RelayCommand]
    public async Task SaveConventionsAsync()
    {
        ConventionsError = null;
        var text = ConventionsText;

        try
        {
            await runner.RunAsync<UpdateBranchConventionsHandler>(
                (handler, token) => handler.HandleAsync(new UpdateBranchConventions(text), token));

            if (string.IsNullOrWhiteSpace(text))
            {
                ConventionsText = BranchConventions.Default.ToText();
            }

            StatusMessage = "Convenções de branch salvas.";
        }
        catch (DomainException exception)
        {
            ConventionsError = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "BranchConventionsSaveFailed");
            ConventionsError = "Não foi possível salvar as convenções.";
        }
    }

    [RelayCommand]
    public void RestoreDefaultConventions()
    {
        ConventionsText = BranchConventions.Default.ToText();
        ConventionsError = null;
    }

    async partial void OnSelectedProjectChanged(JiraProjectOption? value)
    {
        if (_showingProject || value is null || !IsConnected || value.Key == Connection?.DefaultProject)
        {
            return;
        }

        JiraConnection? connection = null;

        var saved = await TryAsync(
            async () => connection = await runner.RunAsync<SetJiraDefaultProjectHandler, JiraConnection>(
                (handler, token) => handler.HandleAsync(new SetJiraDefaultProject(value.Key), token)),
            "Não foi possível salvar o projeto padrão.");

        if (saved)
        {
            Connection = connection;
            StatusMessage = value.Key is null
                ? "A busca procura em todos os projetos."
                : $"A busca procura primeiro em {value.Key}. Digite só o número para achar {value.Key}-123.";
        }
    }

    private async Task LoadProjectsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var projects = await runner.RunAsync<ListJiraProjectsHandler, IReadOnlyList<JiraProject>>(
                (handler, token) => handler.HandleAsync(new ListJiraProjects(), token),
                cancellationToken);

            ShowProjects(projects);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem a lista, o projeto padrão gravado continua valendo; só não dá
            // para trocar agora. Não vale uma faixa de erro.
            logger.LogWarning(exception, "JiraProjectsLoadFailed");
            ShowProjects([]);
        }
    }

    private void ShowProjects(IReadOnlyList<JiraProject> projects)
    {
        _showingProject = true;

        try
        {
            Projects.Clear();
            Projects.Add(JiraProjectOption.All);

            foreach (var project in projects)
            {
                Projects.Add(new JiraProjectOption(project.Key, $"{project.Key} — {project.Name}"));
            }

            // O padrão gravado aparece mesmo se a lista não veio.
            if (Connection?.DefaultProject is { } current && Projects.All(option => option.Key != current))
            {
                Projects.Add(new JiraProjectOption(current, current));
            }

            SelectedProject = Projects.FirstOrDefault(option => option.Key == Connection?.DefaultProject)
                ?? JiraProjectOption.All;
        }
        finally
        {
            _showingProject = false;
        }
    }

    private void Show(JiraConnection connection)
    {
        Connection = connection;

        Sites.Clear();

        foreach (var site in connection.Sites ?? [])
        {
            Sites.Add(site);
        }

        if (!connection.IsOAuthAvailable && IsDisconnected)
        {
            IsApiTokenFormOpen = true;
        }

        if (connection.SiteUrl is { } connectedSite && SiteUrl.Length == 0)
        {
            SiteUrl = new Uri(connectedSite).Host;
        }
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "IntegrationsOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
