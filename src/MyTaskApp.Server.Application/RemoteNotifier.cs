namespace MyTaskApp.Server.Application;

/// <summary>
/// A campainha da orquestração (fase 2, T-13; ADR-086): avisa que algo mudou,
/// só com ids (<c>RemoteHubMessages</c>), e quem recebe busca o resto por REST.
/// Um aviso perdido não perde nada, e a falha de avisar nunca desfaz o que o
/// caso de uso já gravou: a implementação engole e registra.
/// </summary>
public interface IRemoteNotifier
{
    /// <summary>Para as conexões de uma máquina da conta.</summary>
    Task ToDeviceAsync(Guid userId, Guid deviceId, string message, params object?[] args);

    /// <summary>Para todas as conexões da conta.</summary>
    Task ToAccountAsync(Guid userId, string message, params object?[] args);
}

/// <summary>Sem hub (os casos de uso fora do servidor HTTP): ninguém é avisado.</summary>
internal sealed class NullRemoteNotifier : IRemoteNotifier
{
    public static readonly NullRemoteNotifier Instance = new();

    public Task ToDeviceAsync(Guid userId, Guid deviceId, string message, params object?[] args) => Task.CompletedTask;

    public Task ToAccountAsync(Guid userId, string message, params object?[] args) => Task.CompletedTask;
}
