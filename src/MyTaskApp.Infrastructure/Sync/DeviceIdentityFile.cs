using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync;

/// <summary>
/// O id do dispositivo em <c>device.json</c>, na pasta <b>local</b> do
/// usuário (<c>%LOCALAPPDATA%</c>), que não viaja com o perfil nem com o
/// backup do banco (ADR-061). Criado na primeira leitura.
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

    public string DefaultNickname => NicknameFor(Environment.MachineName, Environment.UserName);

    /// <summary>
    /// Computador e usuário do Windows: dois logins na mesma máquina são dois
    /// dispositivos, e só o nome da máquina os deixaria iguais na lista. O
    /// servidor descarta apelido acima do limite, então o corte é aqui, no usuário.
    /// </summary>
    internal static string NicknameFor(string machine, string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return machine;
        }

        var nickname = $"{machine} ({user.Trim()})";

        return nickname.Length <= SyncSchema.MaxNicknameLength
            ? nickname
            : nickname[..(SyncSchema.MaxNicknameLength - 1)] + ")";
    }

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
