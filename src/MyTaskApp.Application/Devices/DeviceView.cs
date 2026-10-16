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

/// <summary>
/// A origem exata de uma tarefa, para o filtro "Origem" do celular (ADR-065):
/// só as compartilhadas, ou só as que moram num destes dispositivos.
/// </summary>
/// <remarks>
/// Diferente de <see cref="PlacementFilter"/>, que responde "o que aparece num
/// dispositivo" e por isso inclui as compartilhadas: aqui "Notebook" é só o que
/// está no notebook. O legado sem lugar conta como compartilhada, porque
/// aparece em todos.
/// </remarks>
public sealed record PlacementMatch
{
    private PlacementMatch(bool sharedOnly, IReadOnlyCollection<Guid> deviceIds)
    {
        SharedOnly = sharedOnly;
        DeviceIds = deviceIds;
    }

    public static PlacementMatch Shared { get; } = new(true, []);

    public bool SharedOnly { get; }

    /// <summary>Os ids do dispositivo — o atual e os antigos, quando é este.</summary>
    public IReadOnlyCollection<Guid> DeviceIds { get; }

    public static PlacementMatch On(IReadOnlyCollection<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);

        if (deviceIds.Count == 0)
        {
            throw new ArgumentException("Informe ao menos um dispositivo.", nameof(deviceIds));
        }

        return new(false, deviceIds);
    }
}
