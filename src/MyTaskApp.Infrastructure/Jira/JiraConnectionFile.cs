using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.External.Jira;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>
/// O que se sabe da conexão e não é segredo: método, site, conta, projeto
/// padrão. Em <c>jira.json</c>, ao lado do <c>widget.json</c>, e não no banco
/// (ADR-045): a conexão vale para este usuário nesta máquina — o segredo que a
/// acompanha é do DPAPI e não decifra em outro lugar —, então não deve viajar
/// com um backup do banco.
/// </summary>
internal sealed record StoredJiraConnection
{
    public JiraAuthMethod Method { get; init; }

    /// <summary>O <c>cloudId</c> do site, no OAuth. A API mora em <c>api.atlassian.com/ex/jira/{id}</c>.</summary>
    public string? SiteId { get; init; }

    public string? SiteName { get; init; }

    /// <summary><c>https://empresa.atlassian.net</c> — de onde saem os links <c>/browse/</c>.</summary>
    public string? SiteUrl { get; init; }

    public string? AccountName { get; init; }

    public string? AccountEmail { get; init; }

    public string? DefaultProject { get; init; }

    /// <summary>Autorizado, com mais de um site: os sites à escolha. Vazio = conectado.</summary>
    public IReadOnlyList<JiraSite>? PendingSites { get; init; }

    public bool IsChoosingSite => PendingSites is { Count: > 0 };
}

internal sealed class JiraConnectionFile(string path, ILogger<JiraConnectionFile> logger)
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<StoredJiraConnection?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<StoredJiraConnection>(stream, Format, cancellationToken);
        }
        catch (JsonException exception)
        {
            // Editado à mão ou corrompido: desconectado, e o usuário conecta de novo.
            logger.LogWarning("JiraConnectionFileUnreadable {Error}", exception.Message);
            return null;
        }
    }

    public async Task SaveAsync(StoredJiraConnection state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Format), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete() => File.Delete(path);
}
