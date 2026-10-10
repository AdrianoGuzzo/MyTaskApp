using MyTaskApp.Application.Mcp;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>O estado real do servidor — não o que a configuração deseja.</summary>
public enum McpServerState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Error = 3,
    Stopping = 4,
}

/// <param name="Endpoint">O endereço em que escuta agora; <c>null</c> se não escuta.</param>
/// <param name="Error">Por que não subiu, em português, para a tela.</param>
public sealed record McpServerStatus(
    McpServerState State,
    Uri? Endpoint,
    int? Port,
    string? Error,
    DateTimeOffset Since)
{
    public bool IsRunning => State is McpServerState.Running;

    public static McpServerStatus Stopped(DateTimeOffset at) => new(McpServerState.Stopped, null, null, null, at);
}

/// <summary>"A porta está livre?" — e, se não, por quê.</summary>
public sealed record PortCheck(int Port, bool IsAvailable, string Message);

/// <summary>Uma ferramenta como a tela a lista.</summary>
public sealed record McpToolDescriptor(string Name, string Title, string Description, bool ReadOnly, bool Destructive, string Area);

/// <summary>
/// O dono do servidor MCP (ADR-059): sobe, derruba e diz em que estado está.
/// Um só por app, como os agendadores; a tela só pede e observa.
/// </summary>
public interface IMcpServerManager
{
    McpServerStatus Status { get; }

    /// <summary>Dispara na thread de quem mudou o estado; a tela leva para a dela.</summary>
    event Action<McpServerStatus>? StatusChanged;

    McpActivityLog Activity { get; }

    IReadOnlyList<McpToolDescriptor> Tools { get; }

    /// <summary>Sobe com a configuração gravada. Desabilitado, recusa.</summary>
    Task<McpServerStatus> StartAsync(CancellationToken cancellationToken = default);

    Task<McpServerStatus> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A configuração acabou de ser gravada: somente leitura vale na hora; porta
    /// nova reinicia quem está no ar; desabilitar derruba.
    /// </summary>
    Task<McpServerStatus> ApplySettingsAsync(McpServerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Na abertura do app: sobe só se habilitado e com "iniciar com o app".</summary>
    Task<McpServerStatus> StartIfConfiguredAsync(CancellationToken cancellationToken = default);

    Task<PortCheck> CheckPortAsync(int port, CancellationToken cancellationToken = default);

    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Troca o token na hora: o cliente com o antigo passa a receber 401.</summary>
    Task<string> RegenerateAccessTokenAsync(CancellationToken cancellationToken = default);
}
