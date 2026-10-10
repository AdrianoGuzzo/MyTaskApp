using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Mcp;

/// <summary>
/// As configurações do servidor MCP em linha única no banco — mesmo desenho do
/// <c>IDataRetentionSettingsStore</c> (ADR-014).
/// </summary>
public interface IMcpServerSettingsStore
{
    Task<McpServerSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(McpServerSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// O token que o cliente MCP apresenta em <c>Authorization: Bearer</c>. Fica no
/// cofre do sistema (DPAPI), como o token do Jira: HTTP local não é
/// autenticação, e qualquer processo ou página da máquina alcança o loopback.
/// </summary>
public interface IMcpAccessTokenStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(string token, CancellationToken cancellationToken = default);
}

public sealed record GetMcpServerSettings;

public sealed class GetMcpServerSettingsHandler(IMcpServerSettingsStore settings)
{
    public Task<McpServerSettings> HandleAsync(
        GetMcpServerSettings query,
        CancellationToken cancellationToken = default) =>
        settings.GetAsync(cancellationToken);
}

public sealed record UpdateMcpServerSettings(bool Enabled, int Port, bool StartWithApp, bool ReadOnly);

public sealed class UpdateMcpServerSettingsHandler(
    IMcpServerSettingsStore settings,
    IUnitOfWork unitOfWork,
    ILogger<UpdateMcpServerSettingsHandler> logger)
{
    public async Task<McpServerSettings> HandleAsync(
        UpdateMcpServerSettings command,
        CancellationToken cancellationToken = default)
    {
        var updated = new McpServerSettings(
            command.Enabled,
            command.Port,
            command.StartWithApp,
            command.ReadOnly);

        await settings.SaveAsync(updated, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Salvar não sobe nem derruba o servidor: quem faz isso é o gerente,
        // que a tela chama depois. A configuração é o desejo; o estado é dele.
        logger.LogInformation(
            "McpServerSettingsUpdated {Enabled} {Port} {StartWithApp} {ReadOnly}",
            updated.Enabled,
            updated.Port,
            updated.StartWithApp,
            updated.ReadOnly);

        return updated;
    }
}

/// <summary>O token atual; gera um na primeira vez que alguém pergunta.</summary>
public sealed record GetMcpAccessToken;

public sealed class GetMcpAccessTokenHandler(
    IMcpAccessTokenStore tokens,
    ILogger<GetMcpAccessTokenHandler> logger)
{
    public async Task<string> HandleAsync(
        GetMcpAccessToken query,
        CancellationToken cancellationToken = default)
    {
        if (await tokens.ReadAsync(cancellationToken) is { Length: > 0 } stored)
        {
            return stored;
        }

        var token = McpAccessToken.Generate();
        await tokens.WriteAsync(token, cancellationToken);

        // O valor nunca vai para o log: só o fato.
        logger.LogInformation("McpAccessTokenCreated");

        return token;
    }
}

/// <summary>Troca o token: quem tinha o antigo perde o acesso na hora.</summary>
public sealed record RegenerateMcpAccessToken;

public sealed class RegenerateMcpAccessTokenHandler(
    IMcpAccessTokenStore tokens,
    ILogger<RegenerateMcpAccessTokenHandler> logger)
{
    public async Task<string> HandleAsync(
        RegenerateMcpAccessToken command,
        CancellationToken cancellationToken = default)
    {
        var token = McpAccessToken.Generate();
        await tokens.WriteAsync(token, cancellationToken);

        logger.LogInformation("McpAccessTokenRegenerated");

        return token;
    }
}

public static class McpAccessToken
{
    /// <summary>256 bits de aleatoriedade em base64url: cabe num header sem escapar nada.</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
