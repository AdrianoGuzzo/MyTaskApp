namespace MyTaskApp.Application.Agents;

/// <summary>
/// Os parâmetros com que cada agente abre, no banco — mesmo desenho do
/// <c>IDataRetentionSettingsStore</c> (ADR-014): é dado do usuário, e o
/// <c>appsettings.json</c> não é gravável.
/// </summary>
public interface IAgentSettingsStore
{
    /// <summary>
    /// O texto salvo; <c>null</c> se nunca foi salvo — aí vale o
    /// <see cref="IAgentCliProvider.DefaultArguments"/>. Texto vazio é escolha
    /// do usuário (abrir sem parâmetro nenhum), e não "sem configuração".
    /// </summary>
    Task<string?> GetArgumentsAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Só prepara a gravação: quem chama confirma com o <c>IUnitOfWork</c>.</summary>
    Task SaveArgumentsAsync(string providerId, string arguments, CancellationToken cancellationToken = default);
}

internal static class AgentSettingsStoreExtensions
{
    /// <summary>O salvo, ou o padrão do agente quando nada foi salvo.</summary>
    public static async Task<string> ArgumentsForAsync(
        this IAgentSettingsStore settings,
        IAgentCliProvider provider,
        CancellationToken cancellationToken) =>
        await settings.GetArgumentsAsync(provider.Id, cancellationToken) ?? provider.DefaultArguments;
}
