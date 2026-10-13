using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Infrastructure.Sync;

/// <summary>
/// O id do dispositivo em <c>device.json</c>, na pasta <b>local</b> do
/// usuário (<c>%LOCALAPPDATA%</c>), que não viaja com o perfil nem com o
/// backup do banco (ADR-060). Criado na primeira leitura.
/// </summary>
internal sealed class DeviceIdentityFile(string folder, ILogger<DeviceIdentityFile> logger) : IDeviceIdentity
{
    public const string FileName = "device.json";

    private readonly Lock _gate = new();

    private Guid? _deviceId;

    public Guid DeviceId
    {
        get
        {
            lock (_gate)
            {
                return _deviceId ??= LoadOrCreate();
            }
        }
    }

    public string DefaultNickname => Environment.MachineName;

    public void Rotate()
    {
        lock (_gate)
        {
            _deviceId = Write(Guid.CreateVersion7());
            logger.LogInformation("SyncDeviceIdRotated");
        }
    }

    private string Path => System.IO.Path.Combine(folder, FileName);

    private Guid LoadOrCreate()
    {
        try
        {
            if (File.Exists(Path)
                && JsonSerializer.Deserialize<DeviceFile>(File.ReadAllText(Path)) is { DeviceId: var stored }
                && stored != Guid.Empty)
            {
                return stored;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Arquivo ilegível: um id novo é um dispositivo novo, e o antigo pode ser revogado pela tela.
            logger.LogWarning(exception, "SyncDeviceFileUnreadable");
        }

        return Write(Guid.CreateVersion7());
    }

    private Guid Write(Guid deviceId)
    {
        Directory.CreateDirectory(folder);

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new DeviceFile(deviceId)));
        File.Move(temporary, Path, overwrite: true);

        return deviceId;
    }

    private sealed record DeviceFile(Guid DeviceId);
}
