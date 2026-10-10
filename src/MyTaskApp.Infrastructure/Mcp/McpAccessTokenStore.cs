using MyTaskApp.Application.Mcp;
using MyTaskApp.Infrastructure.Secrets;

namespace MyTaskApp.Infrastructure.Mcp;

/// <summary>
/// O token do servidor MCP no cofre do sistema (ADR-059), ao lado do token do
/// Jira e das senhas de banco. Nunca no banco, nunca no log.
/// </summary>
internal sealed class McpAccessTokenStore(ISecretStore secrets) : IMcpAccessTokenStore
{
    internal const string SecretName = "mcp-access-token";

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
        secrets.ReadAsync(SecretName, cancellationToken);

    public Task WriteAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return secrets.WriteAsync(SecretName, token, cancellationToken);
    }
}
