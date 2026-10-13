namespace MyTaskApp.Application.Devices;

public enum DeviceViewKind
{
    /// <summary>Este computador e o que é compartilhado: o padrão.</summary>
    Here,

    /// <summary>O que outro computador vê: as dele e as compartilhadas.</summary>
    Device,

    /// <summary>Tudo, de todos os computadores.</summary>
    All,
}

/// <summary>De qual computador a lista mostra as tarefas (ADR-062): o seletor no topo da tela.</summary>
public readonly record struct DeviceView(DeviceViewKind Kind, Guid? DeviceId = null)
{
    public static DeviceView Here { get; } = new(DeviceViewKind.Here);

    public static DeviceView All { get; } = new(DeviceViewKind.All);

    public static DeviceView Of(Guid deviceId) => new(DeviceViewKind.Device, deviceId);
}

/// <summary>
/// O filtro que as consultas aplicam (ADR-062): aparece o que não tem lugar
/// (legado), o que é de todos e o que está num destes computadores.
/// <see cref="DeviceIds"/> nulo é "sem filtro".
/// </summary>
/// <remarks>
/// O filtro é explícito em cada consulta, e não um query filter global, pelo
/// mesmo motivo da lixeira: as buscas por id, a restauração e a varredura de
/// arquivamento precisam ver tudo.
/// </remarks>
public sealed record PlacementFilter(IReadOnlyCollection<Guid>? DeviceIds)
{
    public static PlacementFilter All { get; } = new((IReadOnlyCollection<Guid>?)null);

    public bool IsAll => DeviceIds is null;
}
