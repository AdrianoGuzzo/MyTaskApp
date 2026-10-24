using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// Os dispositivos na orquestração remota (fase 2, T-04; ADR-087): cada um
/// registra a chave pública, os papéis e o que sabe fazer, e a conta vê a lista
/// com quem está ligado. O dispositivo em si continua sendo o do sync
/// (<see cref="DeviceUseCases"/>): revogá-lo lá o tira daqui.
/// </summary>
public sealed class RemoteDeviceUseCases(
    IDeviceRegistry devices,
    IRemoteDeviceStore remote,
    TimeProvider timeProvider,
    ILogger<RemoteDeviceUseCases> logger)
{
    internal const int MaxOperations = 32;

    internal const int MaxOperationLength = 64;

    internal const int MaxShortTextLength = 40;

    internal const int MaxCatalogHashLength = 128;

    internal const int MaxConcurrentOperations = 16;

    /// <summary>
    /// O dispositivo do token se registra. A mesma chave de antes só renova o
    /// sinal de vida; uma chave nova revoga a anterior: a instalação mudou de
    /// identidade (cofre ilegível, <c>device.json</c> levado para outro PC).
    /// </summary>
    public async Task<RemoteDeviceInfo> RegisterAsync(SyncCaller caller, DeviceRegistration registration, CancellationToken cancellationToken)
    {
        if (registration.DeviceId != caller.DeviceId)
        {
            throw SyncServerException.Forbidden("O dispositivo do pedido não é o do token.");
        }

        Validate(registration);

        var outcome = await remote.RegisterAsync(caller, registration, timeProvider.GetUtcNow(), cancellationToken);

        if (outcome == RemoteRegistrationOutcome.KeyReplaced)
        {
            logger.LogWarning("RemoteDeviceKeyReplaced {UserId} {DeviceId}", caller.UserId, caller.DeviceId);
        }

        return (await ListAsync(caller, cancellationToken)).Single(device => device.IsCurrent);
    }

    /// <summary>Os dispositivos não revogados da conta, os ligados por último primeiro.</summary>
    public async Task<IReadOnlyList<RemoteDeviceInfo>> ListAsync(SyncCaller caller, CancellationToken cancellationToken)
    {
        var states = (await remote.ListAsync(caller.UserId, cancellationToken)).ToDictionary(state => state.DeviceId);

        return
        [
            .. (await devices.ListAsync(caller.UserId, cancellationToken))
                .Where(device => !device.IsRevoked)
                .Select(device => states.GetValueOrDefault(device.DeviceId) is { } state
                    ? new RemoteDeviceInfo(
                        device.DeviceId, device.Nickname, device.Platform, device.AppVersion, device.DeviceId == caller.DeviceId,
                        state.Roles, state.KeyFingerprint, state.Capabilities, state.LastHeartbeatAt)
                    : new RemoteDeviceInfo(
                        device.DeviceId, device.Nickname, device.Platform, device.AppVersion, device.DeviceId == caller.DeviceId,
                        DeviceRoles.None, null, null, null))
                .OrderByDescending(device => device.LastHeartbeatAt ?? DateTimeOffset.MinValue)
                .ThenBy(device => device.Nickname, StringComparer.CurrentCulture),
        ];
    }

    /// <summary>
    /// Mais estrito que o "olá" do sync: aqui nada é cortado nem limpo em
    /// silêncio, porque é com isto que o controlador decide o que pedir.
    /// </summary>
    internal static void Validate(DeviceRegistration registration)
    {
        if ((registration.Roles & ~(DeviceRoles.Controller | DeviceRoles.Executor)) != 0)
        {
            throw SyncServerException.BadRequest("Papel de dispositivo desconhecido.");
        }

        if (!registration.Key.IsWellFormedEs256())
        {
            throw SyncServerException.BadRequest("A chave do dispositivo precisa ser ES256 (P-256), com a impressão digital do SPKI.");
        }

        var capabilities = registration.Capabilities;

        if (capabilities.ProtocolVersion < 1)
        {
            throw SyncServerException.BadRequest("Versão de protocolo inválida.");
        }

        if (capabilities.Operations.Count > MaxOperations
            || capabilities.Operations.Distinct(StringComparer.Ordinal).Count() != capabilities.Operations.Count
            || capabilities.Operations.Any(operation => !IsShortText(operation, MaxOperationLength)))
        {
            throw SyncServerException.BadRequest(
                $"Até {MaxOperations} operações, sem repetir, de 1 a {MaxOperationLength} caracteres, sem caracteres de controle.");
        }

        string?[] texts =
        [
            capabilities.OperatingSystem,
            capabilities.Git.Version,
            capabilities.ClaudeCode.Version,
            capabilities.GitHubCli.Version,
            capabilities.PostgresTools.Version,
        ];

        if (texts.Any(text => text is not null && !IsShortText(text, MaxShortTextLength))
            || !IsShortText(capabilities.CatalogHash, MaxCatalogHashLength, allowEmpty: true))
        {
            throw SyncServerException.BadRequest(
                $"Sistema e versões de 1 a {MaxShortTextLength} caracteres, e o hash do catálogo até {MaxCatalogHashLength}, sem caracteres de controle.");
        }

        if (capabilities.MaxConcurrentOperations is < 1 or > MaxConcurrentOperations)
        {
            throw SyncServerException.BadRequest($"Operações simultâneas de 1 a {MaxConcurrentOperations}.");
        }
    }

    private static bool IsShortText(string value, int max, bool allowEmpty = false) =>
        (allowEmpty || value.Length > 0) && value.Length <= max && !ReceivedText.HasControlOrFormat(value);
}

/// <summary>A orquestração guardada de um dispositivo: o que o último registro trouxe.</summary>
public sealed record RemoteDeviceState(
    Guid DeviceId,
    DeviceRoles Roles,
    string KeyFingerprint,
    DeviceCapabilities Capabilities,
    DateTimeOffset LastHeartbeatAt);

public enum RemoteRegistrationOutcome
{
    /// <summary>Primeiro registro do dispositivo.</summary>
    Registered,

    /// <summary>A mesma chave de antes: só papéis, capacidades e sinal de vida mudam.</summary>
    Renewed,

    /// <summary>Chave nova: a anterior foi revogada.</summary>
    KeyReplaced,
}

/// <summary>Chaves e capacidades dos dispositivos na orquestração.</summary>
public interface IRemoteDeviceStore
{
    /// <summary>
    /// Grava chave, papéis, capacidades e sinal de vida, travando o dispositivo:
    /// o desktop e o agente da mesma instalação se registram juntos, com a
    /// mesma chave. Dispositivo que não existe dá 404; revogado, 403.
    /// </summary>
    Task<RemoteRegistrationOutcome> RegisterAsync(
        SyncCaller caller,
        DeviceRegistration registration,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Os dispositivos da conta que já se registraram, com a chave que vale.</summary>
    Task<IReadOnlyList<RemoteDeviceState>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>A chave que vale agora para o dispositivo; <c>null</c> se ele nunca registrou.</summary>
    Task<DevicePublicKey?> FindActiveKeyAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}
