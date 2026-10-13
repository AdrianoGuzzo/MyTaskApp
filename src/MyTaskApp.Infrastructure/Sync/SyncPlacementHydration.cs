using System.Text.Json;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync;

/// <summary>
/// Leva o que a linha de ligação sabe dos computadores para a memória (ADR-062):
/// quem é "daqui" e os apelidos. Quem lê a ligação chama, como faz com o
/// interruptor da captura.
/// </summary>
internal static class SyncPlacementHydration
{
    public static void Apply(SyncStateRow? row, DevicePlacementState placement, SyncDeviceDirectory directory)
    {
        // Os apelidos gravados só servem até o primeiro ciclo trazer os de agora.
        if (directory.Devices.Count == 0 && ReadDevices(row?.KnownDevices) is { Count: > 0 } known)
        {
            directory.Update(known);
        }

        placement.Update(row?.LinkedDeviceId, ReadIds(row?.FormerDeviceIds));
    }

    public static IReadOnlyList<Guid> ReadIds(string? json) => Read<List<Guid>>(json) ?? [];

    public static string? WriteIds(IReadOnlyCollection<Guid> ids) => ids.Count == 0 ? null : JsonSerializer.Serialize(ids);

    public static IReadOnlyList<DeviceInfo> ReadDevices(string? json) => Read<List<DeviceInfo>>(json) ?? [];

    public static string WriteDevices(IReadOnlyList<DeviceInfo> devices) => JsonSerializer.Serialize(devices);

    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            // É cache: uma linha ilegível só começa vazia.
            return null;
        }
    }
}
