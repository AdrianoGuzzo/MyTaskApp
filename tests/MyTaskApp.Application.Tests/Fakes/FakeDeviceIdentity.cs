using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeDeviceIdentity(Guid? deviceId = null) : IDeviceIdentity
{
    public Guid DeviceId { get; private set; } = deviceId ?? Guid.CreateVersion7();

    public string DefaultNickname => "PC-TESTE";

    public void Rotate() => DeviceId = Guid.CreateVersion7();
}

/// <summary>O que o assentamento acharia no banco: devolve o que o teste mandou.</summary>
internal sealed class FakePendingPlacementQuery : IPendingPlacementQuery
{
    public PendingPlacements Pending { get; set; } = new([], [], [], [], []);

    public Task<PendingPlacements> FindAsync(Guid here, IReadOnlyCollection<Guid> hereIds, CancellationToken cancellationToken) =>
        Task.FromResult(Pending);
}

internal static class Placements
{
    /// <summary>Um estado de "onde aparece" já conectado, com estes outros computadores na conta.</summary>
    public static DevicePlacementState Connected(Guid here, params (Guid Id, string Nickname)[] others)
    {
        var directory = new SyncDeviceDirectory();
        directory.Update(others.Select(other => new MyTaskApp.Sync.Contracts.DeviceInfo(
            other.Id, other.Nickname, "Windows", "1.0.0", DateTimeOffset.UnixEpoch, null, null, false)));

        var state = new DevicePlacementState(new FakeDeviceIdentity(here), directory);
        state.Update(here, []);
        return state;
    }
}
