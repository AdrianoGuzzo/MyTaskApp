using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Application.Devices;

/// <summary>
/// Quem é "este computador" para o "onde aparece" (ADR-062): o id atual e os
/// que ele já teve na conta. Singleton, renovado por quem lê a ligação com a
/// conta (a abertura do banco e o estado da sincronização).
/// </summary>
/// <remarks>
/// <para>
/// <b>Só vale depois da primeira conexão.</b> Uma instalação que nunca
/// conectou não tem outro computador: tudo aparece, nada nasce com lugar, e
/// o <c>device.json</c> nem é lido (lê-lo o criaria).
/// </para>
/// <para>
/// <b>Os ids antigos continuam sendo daqui.</b> Revogado e reconectado, o
/// computador volta com outro id; o que estava no antigo aparece até o
/// assentamento levar para o novo.
/// </para>
/// </remarks>
public sealed class DevicePlacementState(IDeviceIdentity device, SyncDeviceDirectory directory)
{
    private volatile Snapshot _current = Snapshot.Inactive;

    /// <summary>A identidade mudou (conectou, trocou de id): as listas recarregam.</summary>
    public event EventHandler? Changed;

    public bool IsActive => _current.Here is not null;

    /// <summary>O id atual deste computador; <c>null</c> antes da primeira conexão.</summary>
    public Guid? Here => _current.Here;

    /// <summary>O id atual e os antigos.</summary>
    public IReadOnlyCollection<Guid> HereIds => _current.HereIds;

    /// <summary>Renova a partir da ligação gravada: o dispositivo ligado e os ids que este computador já teve.</summary>
    public void Update(Guid? linkedDeviceId, IReadOnlyCollection<Guid> formerDeviceIds)
    {
        var next = linkedDeviceId is { } linked
            ? new Snapshot(device.DeviceId, [device.DeviceId, linked, .. formerDeviceIds])
            : Snapshot.Inactive;

        var previous = _current;
        _current = next;

        if (previous.Here != next.Here || !previous.HereIds.SetEquals(next.HereIds))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsHere(Guid? deviceId) => deviceId is { } id && _current.HereIds.Contains(id);

    /// <summary>O filtro de uma visão. Antes da primeira conexão, nenhuma visão filtra.</summary>
    public PlacementFilter Filter(DeviceView view) => (IsActive, view.Kind) switch
    {
        (false, _) or (_, DeviceViewKind.All) => PlacementFilter.All,
        (_, DeviceViewKind.Device) when view.DeviceId is { } other && !IsHere(other) => new PlacementFilter([other]),
        _ => new PlacementFilter([.. _current.HereIds]),
    };

    /// <summary>Lembretes, avisos de prazo e o padrão do MCP: sempre daqui, seja qual for o seletor.</summary>
    public PlacementFilter HereFilter => Filter(DeviceView.Here);

    /// <summary>Onde nasce o que é criado aqui: neste computador, depois da primeira conexão.</summary>
    public DevicePlacement? DefaultForNew() => Here is { } here ? DevicePlacement.On(here) : null;

    /// <summary>O lugar pedido na criação, ou o padrão; recusa um destino que a conta não tem.</summary>
    public DevicePlacement? ResolveForNew(DevicePlacement? requested) =>
        requested is null ? DefaultForNew() : EnsureCanPlace(requested);

    /// <summary>
    /// Confere um destino escolhido: só depois de conectar, e só num computador
    /// ativo da conta. Mandar para um revogado seria esconder de todos.
    /// </summary>
    public DevicePlacement EnsureCanPlace(DevicePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (!IsActive)
        {
            throw new DomainException("Para escolher o computador, conecte a sincronização em ☰ → Sincronização…");
        }

        if (placement.DeviceId is { } target && !IsKnownActiveDevice(target))
        {
            throw new DomainException("Esse computador não está mais na conta. Escolha outro.");
        }

        // Um id antigo daqui vale como o atual.
        return placement.DeviceId is { } id && IsHere(id) ? DevicePlacement.On(Here!.Value) : placement;
    }

    public bool IsVisibleHere(DevicePlacement? placement) =>
        !IsActive || DevicePlacement.IsVisibleTo(placement, _current.HereIds);

    /// <summary>Os outros computadores da conta, que não foram revogados: os destinos do "Enviar para".</summary>
    public IReadOnlyList<DeviceInfo> OtherDevices =>
        IsActive
            ? [.. directory.Devices.Values
                .Where(info => info.RevokedAt is null && !IsHere(info.DeviceId))
                .OrderBy(info => info.Nickname, StringComparer.CurrentCultureIgnoreCase)]
            : [];

    /// <summary>
    /// A conta já teve outro computador, ativo ou revogado: é o que faz o
    /// seletor aparecer. Com o único outro revogado, as tarefas que ficaram
    /// nele só se acham em "Todos".
    /// </summary>
    public bool HasOtherComputers => IsActive && directory.Devices.Keys.Any(id => !IsHere(id));

    public bool IsKnownActiveDevice(Guid deviceId) =>
        IsHere(deviceId) || OtherDevices.Any(info => info.DeviceId == deviceId);

    /// <summary>"este computador", o apelido, ou <c>null</c> para um id que a conta não conhece.</summary>
    public string? NicknameOf(Guid? deviceId) =>
        IsHere(deviceId) ? ThisComputer : directory.NicknameOf(deviceId);

    /// <summary>Como a tela escreve um lugar: "este computador", "Notebook", "todos os computadores".</summary>
    public string Describe(DevicePlacement? placement) => placement switch
    {
        null => AllComputers,
        { IsEverywhere: true } => AllComputers,
        { DeviceId: var id } => NicknameOf(id) ?? UnknownComputer,
    };

    public const string ThisComputer = "este computador";

    public const string AllComputers = "todos os computadores";

    public const string UnknownComputer = "um computador removido";

    private sealed record Snapshot(Guid? Here, HashSet<Guid> HereIds)
    {
        public static Snapshot Inactive { get; } = new(null, []);
    }
}

/// <summary>Para os casos de uso que recebem o estado como opcional (composições e testes sem sincronização).</summary>
public static class DevicePlacementStateExtensions
{
    /// <summary>Sem o estado, nada nasce com lugar, e um lugar pedido é recusado como numa instalação nunca conectada.</summary>
    public static DevicePlacement? ForNew(this DevicePlacementState? state, DevicePlacement? requested) =>
        state is not null
            ? state.ResolveForNew(requested)
            : requested is null
                ? null
                : throw new DomainException("Para escolher o computador, conecte a sincronização em ☰ → Sincronização…");
}
