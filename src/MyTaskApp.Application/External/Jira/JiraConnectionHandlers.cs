namespace MyTaskApp.Application.External.Jira;

// Casos de uso da janela de Integrações (ADR-045). Finos de propósito: a regra
// mora na porta de autenticação, e aqui fica o que é do app — esquecer o cache
// da busca quando a conta muda, para não sugerir issue de outra conta.

public sealed record GetJiraConnection;

public sealed class GetJiraConnectionHandler(IJiraAuthenticationService jira)
{
    public Task<JiraConnection> HandleAsync(GetJiraConnection query, CancellationToken cancellationToken = default) =>
        jira.GetConnectionAsync(cancellationToken);
}

public sealed record BeginJiraAuthorization;

public sealed class BeginJiraAuthorizationHandler(IJiraAuthenticationService jira, ExternalTaskSearch search)
{
    public async Task<JiraAuthorization> HandleAsync(
        BeginJiraAuthorization command,
        CancellationToken cancellationToken = default)
    {
        var authorization = await jira.BeginAuthorizationAsync(cancellationToken);

        return authorization with { Completion = ForgetCacheWhenDone(authorization.Completion) };
    }

    private async Task<JiraConnection> ForgetCacheWhenDone(Task<JiraConnection> completion)
    {
        var connection = await completion;
        search.Invalidate();
        return connection;
    }
}

public sealed record ChooseJiraSite(string SiteId);

public sealed class ChooseJiraSiteHandler(IJiraAuthenticationService jira, ExternalTaskSearch search)
{
    public async Task<JiraConnection> HandleAsync(ChooseJiraSite command, CancellationToken cancellationToken = default)
    {
        var connection = await jira.ChooseSiteAsync(command.SiteId, cancellationToken);
        search.Invalidate();
        return connection;
    }
}

public sealed record ConnectJiraWithApiToken(string SiteUrl, string Email, string ApiToken);

public sealed class ConnectJiraWithApiTokenHandler(IJiraAuthenticationService jira, ExternalTaskSearch search)
{
    public async Task<JiraConnection> HandleAsync(
        ConnectJiraWithApiToken command,
        CancellationToken cancellationToken = default)
    {
        var connection = await jira.ConnectWithApiTokenAsync(
            command.SiteUrl,
            command.Email,
            command.ApiToken,
            cancellationToken);

        search.Invalidate();
        return connection;
    }
}

public sealed record TestJiraConnection;

public sealed class TestJiraConnectionHandler(IJiraAuthenticationService jira)
{
    public Task<JiraConnection> HandleAsync(TestJiraConnection command, CancellationToken cancellationToken = default) =>
        jira.TestAsync(cancellationToken);
}

public sealed record ListJiraProjects;

public sealed class ListJiraProjectsHandler(IJiraAuthenticationService jira)
{
    public Task<IReadOnlyList<JiraProject>> HandleAsync(
        ListJiraProjects query,
        CancellationToken cancellationToken = default) =>
        jira.ListProjectsAsync(cancellationToken);
}

public sealed record SetJiraDefaultProject(string? ProjectKey);

public sealed class SetJiraDefaultProjectHandler(IJiraAuthenticationService jira, ExternalTaskSearch search)
{
    public async Task<JiraConnection> HandleAsync(
        SetJiraDefaultProject command,
        CancellationToken cancellationToken = default)
    {
        var connection = await jira.SetDefaultProjectAsync(command.ProjectKey, cancellationToken);

        // O projeto padrão restringe a busca: o que veio antes era de outro recorte.
        search.Invalidate();
        return connection;
    }
}

public sealed record DisconnectJira;

public sealed class DisconnectJiraHandler(IJiraAuthenticationService jira, ExternalTaskSearch search)
{
    public async Task HandleAsync(DisconnectJira command, CancellationToken cancellationToken = default)
    {
        await jira.DisconnectAsync(cancellationToken);
        search.Invalidate();
    }
}
