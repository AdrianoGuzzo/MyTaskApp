using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.External;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>Uma issue como a API devolve, já com o link de abrir.</summary>
internal sealed record JiraIssue(string Key, string Summary, string? Type, string? Status, string? Project, Uri Url);

/// <summary>
/// A API REST v3 do Jira Cloud, vista pelos provedores (ADR-045). Só leitura,
/// e só o que o MyTaskApp usa: buscar e ler uma issue.
/// </summary>
/// <remarks>
/// Falha sobe como <see cref="ExternalTaskUnavailableException"/> com o motivo,
/// e nunca como HTTP: o provedor não precisa saber o que é 401.
/// </remarks>
internal interface IJiraClient
{
    /// <summary>Por texto do título, no projeto padrão quando houver. Número puro vira chave.</summary>
    Task<IReadOnlyList<JiraIssue>> SearchAsync(string text, int maxResults, CancellationToken cancellationToken);

    /// <summary><c>null</c> = não existe, ou a conta não enxerga.</summary>
    Task<JiraIssue?> GetIssueAsync(string key, CancellationToken cancellationToken);
}

internal sealed class JiraClient(IJiraAccess access, JiraHttp http, ILogger<JiraClient> logger) : IJiraClient
{
    public async Task<IReadOnlyList<JiraIssue>> SearchAsync(string text, int maxResults, CancellationToken cancellationToken)
    {
        var current = await access.GetAccessAsync(cancellationToken);

        if (JiraQuery.KeyFromNumber(text, current.DefaultProject) is { } key)
        {
            return await GetIssueAsync(key, cancellationToken) is { } issue ? [issue] : [];
        }

        if (JiraQuery.ForText(text, current.DefaultProject) is not { } jql)
        {
            return [];
        }

        var path = "rest/api/3/search/jql"
            + $"?jql={Uri.EscapeDataString(jql)}"
            + $"&maxResults={maxResults.ToString(CultureInfo.InvariantCulture)}"
            + $"&fields={JiraQuery.Fields}";

        using var document = await SendAsync(path, cancellationToken);

        if (document is null)
        {
            return [];
        }

        return document.RootElement.TryGetProperty("issues", out var issues)
            ? [.. issues.EnumerateArray().Select(issue => Read(issue, current.SiteUrl))]
            : [];
    }

    public async Task<JiraIssue?> GetIssueAsync(string key, CancellationToken cancellationToken)
    {
        var path = $"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={JiraQuery.Fields}";

        using var document = await SendAsync(path, cancellationToken);

        return document is null ? null : Read(document.RootElement, (await access.GetAccessAsync(cancellationToken)).SiteUrl);
    }

    /// <summary>
    /// Uma ida à API, com uma segunda tentativa quando o token foi recusado: o
    /// access do OAuth pode ter sido revogado antes de vencer, e renovar resolve.
    /// Não encontrada e consulta recusada (<c>400</c>) viram <c>null</c>.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var current = await access.GetAccessAsync(cancellationToken);

            try
            {
                return await http.GetAsync(new Uri(current.ApiBase, path), current.Authorization, cancellationToken);
            }
            catch (JiraHttpException exception) when (exception.Failure == JiraHttpFailure.Unauthorized && attempt == 1)
            {
                access.ForgetAccessToken();
            }
            catch (JiraHttpException exception) when (exception.Failure is JiraHttpFailure.NotFound or JiraHttpFailure.BadRequest)
            {
                logger.LogInformation("JiraNothingFound {Failure}", exception.Failure);
                return null;
            }
            catch (JiraHttpException exception)
            {
                throw new ExternalTaskUnavailableException(
                    exception.Failure is JiraHttpFailure.Unauthorized or JiraHttpFailure.Forbidden
                        ? ExternalTaskFailure.Unauthorized
                        : ExternalTaskFailure.Unavailable);
            }
        }
    }

    private static JiraIssue Read(JsonElement issue, Uri siteUrl)
    {
        var key = issue.GetProperty("key").GetString()!;
        var fields = issue.GetProperty("fields");

        return new JiraIssue(
            key,
            fields.TryGetProperty("summary", out var summary) ? summary.GetString() ?? key : key,
            Name(fields, "issuetype"),
            Name(fields, "status"),
            fields.TryGetProperty("project", out var project) && project.TryGetProperty("key", out var projectKey)
                ? projectKey.GetString()
                : null,
            new Uri(siteUrl, "browse/" + Uri.EscapeDataString(key)));
    }

    private static string? Name(JsonElement fields, string field) =>
        fields.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;
}
