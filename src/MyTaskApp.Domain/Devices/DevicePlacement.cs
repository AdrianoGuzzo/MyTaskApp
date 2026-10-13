namespace MyTaskApp.Domain.Devices;

/// <summary>Onde uma tarefa ou etiqueta aparece, entre os computadores da conta (ADR-062).</summary>
public enum PlacementKind
{
    /// <summary>Só no computador de <see cref="DevicePlacement.DeviceId"/>.</summary>
    Device = 0,

    /// <summary>Em todos os computadores da conta: compartilhada.</summary>
    Everywhere = 1,
}

/// <summary>
/// O "onde aparece" de uma tarefa ou etiqueta (ADR-062). Tudo continua
/// sincronizando com todos os computadores; isto só decide em qual lista o item
/// aparece. Escondido não é privado.
/// </summary>
/// <remarks>
/// O domínio não sabe qual é "este computador": quem decide se o item aparece
/// aqui recebe os ids daqui. Sem valor (<c>null</c> na entidade) é o legado,
/// de antes do ADR-062 ou de uma instalação nunca conectada, e aparece em todos.
/// </remarks>
public sealed record DevicePlacement
{
    private DevicePlacement(PlacementKind kind, Guid? deviceId)
    {
        Kind = kind;
        DeviceId = deviceId;
    }

    public static DevicePlacement Everywhere { get; } = new(PlacementKind.Everywhere, null);

    public PlacementKind Kind { get; }

    /// <summary>O computador, quando <see cref="Kind"/> é <see cref="PlacementKind.Device"/>.</summary>
    public Guid? DeviceId { get; }

    public bool IsEverywhere => Kind == PlacementKind.Everywhere;

    public static DevicePlacement On(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
        {
            throw new DomainException("Escolha o computador.");
        }

        return new(PlacementKind.Device, deviceId);
    }

    /// <summary>
    /// Remonta das duas colunas. Um "só no computador" sem computador (que o
    /// contrato não tem como proibir) vale como sem valor: aparece em todos, em
    /// vez de sumir de todos.
    /// </summary>
    public static DevicePlacement? From(PlacementKind? kind, Guid? deviceId) => kind switch
    {
        PlacementKind.Everywhere => Everywhere,
        PlacementKind.Device when deviceId is { } id && id != Guid.Empty => On(id),
        _ => null,
    };

    /// <summary>Se aparece num computador que tem estes ids (o atual e os antigos).</summary>
    public static bool IsVisibleTo(DevicePlacement? placement, IReadOnlyCollection<Guid> deviceIds) =>
        placement is null || placement.IsEverywhere || deviceIds.Contains(placement.DeviceId!.Value);
}
