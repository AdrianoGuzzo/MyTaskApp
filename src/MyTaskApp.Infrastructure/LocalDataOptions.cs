namespace MyTaskApp.Infrastructure;

/// <summary>
/// O que um app que não é o desktop diz a <see cref="DependencyInjection.AddLocalData"/>
/// (ADR-067): onde guardar o id do dispositivo e como o aparelho se chama.
/// </summary>
/// <remarks>
/// O banco continua vindo da seção <c>Database</c> da configuração; isto é só o
/// que não tem seção. Sem valores, vale o do desktop: a pasta local do usuário
/// e o nome da máquina.
/// </remarks>
public sealed class LocalDataOptions
{
    /// <summary>A pasta do <c>device.json</c>; fora do backup do sistema.</summary>
    public string? DeviceIdentityFolder { get; init; }

    /// <summary>O apelido sugerido ao servidor no login — o nome do celular.</summary>
    public string? DeviceNickname { get; init; }
}
