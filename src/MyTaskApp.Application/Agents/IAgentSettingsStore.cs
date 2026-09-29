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

    /// <summary>
    /// Acompanhar o agente pelos hooks dele (ADR-037)? <c>null</c> se nunca foi
    /// escolhido — aí vale ligado.
    /// </summary>
    Task<bool?> GetMonitoringAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Só prepara a gravação, como <see cref="SaveArgumentsAsync"/>.</summary>
    Task SaveMonitoringAsync(string providerId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// O modelo escolhido (ADR-040): o <see cref="AgentCliOption.Value"/>, ou
    /// <c>null</c>/vazio para o padrão do agente.
    /// </summary>
    Task<string?> GetModelAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Só prepara a gravação, como <see cref="SaveArgumentsAsync"/>.</summary>
    Task SaveModelAsync(string providerId, string model, CancellationToken cancellationToken = default);

    /// <summary>O esforço escolhido, como <see cref="GetModelAsync"/>.</summary>
    Task<string?> GetEffortAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Só prepara a gravação, como <see cref="SaveArgumentsAsync"/>.</summary>
    Task SaveEffortAsync(string providerId, string effort, CancellationToken cancellationToken = default);
}

internal static class AgentSettingsStoreExtensions
{
    /// <summary>O salvo, ou o padrão do agente quando nada foi salvo.</summary>
    public static async Task<string> ArgumentsForAsync(
        this IAgentSettingsStore settings,
        IAgentCliProvider provider,
        CancellationToken cancellationToken) =>
        await settings.GetArgumentsAsync(provider.Id, cancellationToken) ?? provider.DefaultArguments;

    /// <summary>O escolhido, ou ligado quando nada foi escolhido.</summary>
    public static async Task<bool> MonitoringForAsync(
        this IAgentSettingsStore settings,
        IAgentCliProvider provider,
        CancellationToken cancellationToken) =>
        await settings.GetMonitoringAsync(provider.Id, cancellationToken) ?? true;

    /// <summary>
    /// O modelo salvo, se o agente ainda o oferece; senão o padrão (vazio). Um
    /// modelo que saiu da lista não trava a abertura.
    /// </summary>
    public static async Task<string> ModelForAsync(
        this IAgentSettingsStore settings,
        IAgentCliProvider provider,
        CancellationToken cancellationToken) =>
        AgentCliOption.Find(provider.Models, await settings.GetModelAsync(provider.Id, cancellationToken))?.Value
            ?? string.Empty;

    /// <summary>O esforço salvo, como <see cref="ModelForAsync"/>.</summary>
    public static async Task<string> EffortForAsync(
        this IAgentSettingsStore settings,
        IAgentCliProvider provider,
        CancellationToken cancellationToken) =>
        AgentCliOption.Find(provider.Efforts, await settings.GetEffortAsync(provider.Id, cancellationToken))?.Value
            ?? string.Empty;
}
