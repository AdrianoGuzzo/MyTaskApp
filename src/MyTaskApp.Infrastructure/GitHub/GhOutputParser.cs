using System.Text.Json;
using MyTaskApp.Application.Development;

namespace MyTaskApp.Infrastructure.GitHub;

/// <summary>
/// Lê o <c>--json number,title,url,isDraft</c> do <c>gh pr list</c> (ADR-047).
/// Puro, para os testes cobrirem o formato sem o <c>gh</c>.
/// </summary>
internal static class GhOutputParser
{
    /// <summary>A primeira PR da lista, ou <c>null</c> quando a lista é vazia ou não se entende.</summary>
    public static PullRequestInfo? ParseFirstPullRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("number", out var number)
                    && number.TryGetInt32(out var value)
                    && item.TryGetProperty("url", out var url)
                    && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps)
                {
                    var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                    var isDraft = item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True;

                    return new PullRequestInfo(value, title ?? string.Empty, uri, isDraft);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
