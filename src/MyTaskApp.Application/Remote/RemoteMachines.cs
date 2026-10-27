using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O controle remoto nesta instalação (ADR-087, RS-10). Tudo desligado de
/// fábrica, e só se liga aqui: não sincroniza e não vem do servidor.
/// </summary>
/// <param name="Enabled">Usar o controle remoto: o login pede o escopo <c>remote</c> e a máquina aparece em "Máquinas".</param>
/// <param name="AcceptControl">Aceitar ser controlada: a máquina se registra como executora e recebe pedidos de pareamento.</param>
public sealed record RemoteSettings(bool Enabled, bool AcceptControl)
{
    public static readonly RemoteSettings Off = new(false, false);
}

/// <summary>Em linha única no banco, como as do MCP (ADR-014).</summary>
public interface IRemoteSettingsStore
{
    Task<RemoteSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RemoteSettings settings, CancellationToken cancellationToken = default);
}

public sealed record GetRemoteSettings;

public sealed class GetRemoteSettingsHandler(IRemoteSettingsStore settings)
{
    public Task<RemoteSettings> HandleAsync(GetRemoteSettings query, CancellationToken cancellationToken = default) =>
        settings.GetAsync(cancellationToken);
}

public sealed record UpdateRemoteSettings(bool Enabled, bool AcceptControl);

/// <summary>Aceitar controle exige o remoto ligado: desligar o remoto desliga os dois.</summary>
public sealed class UpdateRemoteSettingsHandler(
    IRemoteSettingsStore settings,
    IUnitOfWork unitOfWork,
    IAgentAutostart autostart,
    ILogger<UpdateRemoteSettingsHandler> logger)
{
    public async Task<RemoteSettings> HandleAsync(UpdateRemoteSettings command, CancellationToken cancellationToken = default)
    {
        var updated = new RemoteSettings(command.Enabled, command.Enabled && command.AcceptControl);

        await settings.SaveAsync(updated, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("RemoteSettingsUpdated {Enabled} {AcceptControl}", updated.Enabled, updated.AcceptControl);

        // Aceitar pedidos com o desktop fechado é o agente no logon (ADR-085).
        await autostart.ApplyAsync(updated.AcceptControl, cancellationToken);

        return updated;
    }
}

/// <summary>Em que pé está o controle remoto, visto desta máquina.</summary>
public enum RemoteAvailability
{
    /// <summary>Desligado aqui.</summary>
    Off,

    /// <summary>A sincronização não está conectada (ou o login venceu).</summary>
    NotConnected,

    /// <summary>Conectada, mas o login é de antes de ligar o remoto: falta o escopo <c>remote</c>.</summary>
    NeedsRemoteLogin,

    /// <summary>O servidor ainda não tem as rotas da orquestração.</summary>
    ServerWithoutRemote,

    /// <summary>Sem resposta do servidor agora; tenta de novo sozinho.</summary>
    Unreachable,

    Ready,
}

/// <summary>Uma máquina da conta, como a janela "Máquinas" mostra.</summary>
/// <param name="AcceptsControl">Tem o papel de executora: pode ser pareada e controlada.</param>
/// <param name="Tools">As ferramentas que ela disse ter, com a versão: "Git 2.51.0".</param>
public sealed record MachineView(
    Guid DeviceId,
    string Nickname,
    string Platform,
    string AppVersion,
    bool IsThis,
    bool IsOnline,
    DateTimeOffset? LastSeenAt,
    bool AcceptsControl,
    string? KeyFingerprint,
    IReadOnlyList<string> Tools);

public sealed record TrustedControllerView(Guid Id, Guid ControllerDeviceId, string Nickname, string Fingerprint, DateTimeOffset ApprovedAt);

/// <param name="Problem">A frase para a pessoa, quando <paramref name="Availability"/> não é <see cref="RemoteAvailability.Ready"/>.</param>
public sealed record MachinesView(
    RemoteSettings Settings,
    RemoteAvailability Availability,
    string? Problem,
    IReadOnlyList<MachineView> Machines,
    IReadOnlyList<OpenPairingView> OpenPairings,
    IReadOnlyList<TrustedControllerView> TrustedControllers);

public sealed record GetMachines;

/// <summary>
/// A janela "Máquinas" inteira de uma vez: as máquinas da conta, os pedidos
/// de pareamento para esta e os controladores em que ela confia. Os
/// confiáveis vêm do banco local e aparecem mesmo sem servidor.
/// </summary>
public sealed class GetMachinesHandler(
    IRemoteSettingsStore settings,
    IRemoteApi remote,
    ITrustedControllerRepository controllers,
    TimeProvider timeProvider)
{
    public async Task<MachinesView> HandleAsync(GetMachines query, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        var trusted = (await controllers.ListAllActiveAsync(cancellationToken))
            .OrderBy(controller => controller.Nickname, StringComparer.CurrentCulture)
            .Select(controller => new TrustedControllerView(controller.Id, controller.ControllerDeviceId, controller.Nickname, controller.Fingerprint, controller.ApprovedAt))
            .ToList();

        if (!current.Enabled)
        {
            return new MachinesView(current, RemoteAvailability.Off, null, [], [], trusted);
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var machines = (await remote.ListDevicesAsync(cancellationToken)).Select(device => Machine(device, now)).ToList();
            var pairings = current.AcceptControl
                ? (await remote.ListOpenPairingsAsync(cancellationToken))
                    .Where(pairing => pairing.Status == PairingStatus.Pending)
                    .Select(pairing => new OpenPairingView(pairing.PairingId, pairing.ControllerDeviceId, pairing.ControllerNickname, pairing.ExpiresAt))
                    .ToList()
                : [];

            return new MachinesView(current, RemoteAvailability.Ready, null, machines, pairings, trusted);
        }
        catch (SyncApiException exception)
        {
            var (availability, problem) = RemoteProblems.Of(exception);
            return new MachinesView(current, availability, problem, [], [], trusted);
        }
    }

    private static MachineView Machine(RemoteDeviceInfo device, DateTimeOffset now) =>
        new(
            device.DeviceId,
            device.Nickname,
            device.Platform,
            device.AppVersion,
            device.IsCurrent,
            device.IsOnlineAt(now),
            device.LastHeartbeatAt,
            device.Roles.HasFlag(DeviceRoles.Executor),
            device.KeyFingerprint,
            device.Capabilities is { } capabilities ? Tools(capabilities) : []);

    private static List<string> Tools(DeviceCapabilities capabilities) =>
        [
            .. new (string Name, ToolStatus Status)[]
                {
                    ("Git", capabilities.Git),
                    ("Claude Code", capabilities.ClaudeCode),
                    ("GitHub CLI", capabilities.GitHubCli),
                    ("PostgreSQL", capabilities.PostgresTools),
                }
                .Where(tool => tool.Status.Available)
                .Select(tool => tool.Status.Version is { } version ? $"{tool.Name} {version}" : tool.Name),
        ];
}

/// <summary>A recusa do servidor, dita como a janela precisa.</summary>
internal static class RemoteProblems
{
    public static (RemoteAvailability Availability, string Problem) Of(SyncApiException exception) => exception.Kind switch
    {
        SyncFailureKind.NeedsReconnect or SyncFailureKind.Unauthorized =>
            (RemoteAvailability.NotConnected, "Conecte a sincronização para usar o controle remoto."),
        SyncFailureKind.DeviceRevoked =>
            (RemoteAvailability.NotConnected, "Este computador foi revogado na sua conta. Conecte a sincronização de novo."),

        // 403 sem código é a política do servidor: o token é de antes de ligar o remoto.
        SyncFailureKind.Forbidden when exception.Code is null =>
            (RemoteAvailability.NeedsRemoteLogin, "Conecte de novo para o login incluir o controle remoto."),
        SyncFailureKind.NotFound when exception.Code is null =>
            (RemoteAvailability.ServerWithoutRemote, "O servidor de sincronização ainda não tem o controle remoto. Atualize o servidor."),
        _ => (RemoteAvailability.Unreachable, exception.Message),
    };
}

/// <summary>
/// As ferramentas desta máquina, para o registro. Detectar roda processos
/// (<c>git --version</c>, <c>claude --version</c>), então o resultado vale por
/// uma hora; o sinal de vida de cada minuto reaproveita.
/// </summary>
public sealed class MachineCapabilities(
    IGitClient git,
    IAgentCliProviders agents,
    IPostgresToolLocator postgres,
    SupportedRemoteOperations supported,
    RemoteDispatchLimits limits,
    TimeProvider timeProvider)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>O teto do servidor para sistema e versões (RemoteDeviceUseCases).</summary>
    private const int MaxTextLength = 40;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private (DeviceCapabilities Value, DateTimeOffset At)? _cached;

    /// <param name="refresh">Mede de novo: o <c>environment.inspect</c> responde com o que há agora.</param>
    public async Task<DeviceCapabilities> GetAsync(CancellationToken cancellationToken, bool refresh = false)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var now = timeProvider.GetUtcNow();

            if (!refresh && _cached is { } cached && now - cached.At < Lifetime)
            {
                return cached.Value;
            }

            var detected = await DetectAsync(cancellationToken);
            _cached = (detected, now);
            return detected;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeviceCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        var gitInstallation = await git.DetectAsync(cancellationToken);
        var claude = await agents.Default.DetectAsync(cancellationToken);
        var pgDump = (await postgres.DetectAsync(refresh: false, cancellationToken)).Find(PostgresTool.PgDump);

        return new DeviceCapabilities(
            RemoteLimits.ProtocolVersion,

            // As que esta versão roda. Quem atende é o agente; o desktop registra o mesmo, do mesmo código.
            supported.Kinds,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "outro",
            Tool(gitInstallation.IsInstalled, gitInstallation.Version),
            Tool(claude.IsInstalled, claude.Version),

            // Sem porta que diga a versão do gh ainda.
            ToolStatus.Missing,
            Tool(pgDump.Found, pgDump.VersionText),
            limits.MaxConcurrent,

            // Quem registra põe o do catálogo de agora (RegisterThisMachineHandler): este fica em cache.
            CatalogHash: string.Empty);
    }

    /// <summary>
    /// O servidor recusa o registro inteiro por um texto de controle ou longo
    /// demais; a saída de <c>--version</c> não é nossa, então passa por aqui.
    /// </summary>
    internal static ToolStatus Tool(bool available, string? version)
    {
        if (!available)
        {
            return ToolStatus.Missing;
        }

        var clean = version is null
            ? string.Empty
            : new string([.. version.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) is not (UnicodeCategory.Control or UnicodeCategory.Format))]).Trim();

        return new ToolStatus(true, clean.Length == 0 ? null : clean.Length > MaxTextLength ? clean[..MaxTextLength] : clean);
    }
}

public sealed record RegisterThisMachine;

/// <summary>
/// Esta máquina se apresenta ao servidor: chave, papéis e ferramentas. É o
/// sinal de vida que faz ela aparecer ligada nas outras. Com o remoto
/// desligado, não faz nada.
/// </summary>
public sealed class RegisterThisMachineHandler(
    IRemoteSettingsStore settings,
    IRemoteApi remote,
    IDeviceSigner signer,
    IDeviceIdentity identity,
    MachineCapabilities capabilities,
    RemoteCatalogPublisher catalogs,
    ITrustedControllerRepository trusted,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RegisterThisMachineHandler> logger)
{
    public async Task<RemoteAvailability> HandleAsync(RegisterThisMachine command, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);

        if (!current.Enabled)
        {
            return RemoteAvailability.Off;
        }

        // O registro grava as capacidades inteiras, inclusive o hash do catálogo: vai o de agora.
        var catalog = current.AcceptControl ? await catalogs.CurrentAsync(cancellationToken) : null;
        var registration = new DeviceRegistration(
            identity.DeviceId,
            current.AcceptControl ? DeviceRoles.Controller | DeviceRoles.Executor : DeviceRoles.Controller,
            await signer.GetPublicKeyAsync(cancellationToken),
            (await capabilities.GetAsync(cancellationToken)) with { CatalogHash = catalog?.Hash ?? string.Empty });

        try
        {
            await remote.RegisterAsync(registration, cancellationToken);

            // Executora registrada: o catálogo pode subir, se mudou.
            if (catalog is not null)
            {
                await catalogs.PublishAsync(cancellationToken);
                await ForgetGoneControllersAsync(cancellationToken);
            }

            return RemoteAvailability.Ready;
        }
        catch (SyncApiException exception)
        {
            var (availability, _) = RemoteProblems.Of(exception);
            logger.LogWarning("RemoteRegistrationFailed {Kind} {Code}", exception.Kind, exception.Code);
            return availability;
        }
    }

    /// <summary>
    /// RS-06: o controlador revogado na conta, ou que trocou de chave, deixa de
    /// valer aqui também. O servidor já recusa o token dele; isto vale para um
    /// servidor que mentisse, porque só se esquece, nunca se confia por ele.
    /// Uma chave nova só volta a valer com um pareamento novo.
    /// </summary>
    private async Task ForgetGoneControllersAsync(CancellationToken cancellationToken)
    {
        var active = await trusted.ListAllActiveAsync(cancellationToken);

        if (active.Count == 0)
        {
            return;
        }

        var keys = (await remote.ListDevicesAsync(cancellationToken)).ToDictionary(device => device.DeviceId, device => device.KeyFingerprint);
        var now = timeProvider.GetUtcNow();
        var forgotten = 0;

        foreach (var controller in active)
        {
            if (!keys.TryGetValue(controller.ControllerDeviceId, out var fingerprint) || fingerprint != controller.Fingerprint)
            {
                controller.Revoke(now);
                forgotten++;
                logger.LogInformation("TrustedControllerGone {ControllerDeviceId} {StillOnAccount}", controller.ControllerDeviceId, fingerprint is not null);
            }
        }

        if (forgotten > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}

public sealed record RegisterThisController;

/// <summary>
/// O celular se apresenta ao servidor (E7): só controlador, com a chave dele e
/// sem ferramenta nenhuma. Sem o registro, o servidor não aceita o pareamento
/// nem os pedidos assinados. Com o remoto desligado, não faz nada.
/// </summary>
public sealed class RegisterThisControllerHandler(
    IRemoteSettingsStore settings,
    IRemoteApi remote,
    IDeviceSigner signer,
    IDeviceIdentity identity,
    ILogger<RegisterThisControllerHandler> logger)
{
    public async Task<RemoteAvailability> HandleAsync(RegisterThisController command, CancellationToken cancellationToken = default)
    {
        if (!(await settings.GetAsync(cancellationToken)).Enabled)
        {
            return RemoteAvailability.Off;
        }

        var capabilities = new DeviceCapabilities(
            RemoteLimits.ProtocolVersion,
            [],
            OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsIOS() ? "ios" : "outro",
            ToolStatus.Missing,
            ToolStatus.Missing,
            ToolStatus.Missing,
            ToolStatus.Missing,
            // O servidor pede de 1 a 16, mesmo de quem não executa nada.
            MaxConcurrentOperations: 1,
            CatalogHash: string.Empty);

        try
        {
            await remote.RegisterAsync(
                new DeviceRegistration(identity.DeviceId, DeviceRoles.Controller, await signer.GetPublicKeyAsync(cancellationToken), capabilities),
                cancellationToken);
            return RemoteAvailability.Ready;
        }
        catch (SyncApiException exception)
        {
            var (availability, _) = RemoteProblems.Of(exception);
            logger.LogWarning("RemoteControllerRegistrationFailed {Kind} {Code}", exception.Kind, exception.Code);
            return availability;
        }
    }
}

/// <summary>O código que este controlador mostra, para a pessoa digitar na outra máquina.</summary>
public sealed record PairingCodeView(Guid PairingId, Guid ExecutorDeviceId, string Code, DateTimeOffset ExpiresAt);

public sealed record RequestPairing(Guid ExecutorDeviceId);

/// <summary>
/// Esta máquina, como controladora, pede para controlar outra (RS-08). O
/// código sai da chave desta máquina, e não da que o servidor devolveu: se
/// ele trocou alguma, os códigos das duas telas não batem.
/// </summary>
public sealed class RequestPairingHandler(IRemoteApi remote, IDeviceSigner signer)
{
    public async Task<PairingCodeView> HandleAsync(RequestPairing command, CancellationToken cancellationToken = default)
    {
        var pairing = await remote.RequestPairingAsync(command.ExecutorDeviceId, cancellationToken);
        var key = await signer.GetPublicKeyAsync(cancellationToken);

        if (pairing.ControllerKey.Fingerprint != key.Fingerprint)
        {
            throw new DomainException("O servidor devolveu outra chave para este computador. Este pareamento não é seguro: não use o código.");
        }

        var code = PairingCode.Derive(key.Fingerprint, pairing.ExecutorKeyFingerprint, pairing.Nonce);

        return new PairingCodeView(pairing.PairingId, pairing.ExecutorDeviceId, code, pairing.ExpiresAt);
    }
}

public sealed record GetPairingStatus(Guid PairingId);

public sealed class GetPairingStatusHandler(IRemoteApi remote)
{
    public async Task<PairingStatus> HandleAsync(GetPairingStatus query, CancellationToken cancellationToken = default) =>
        (await remote.GetPairingAsync(query.PairingId, cancellationToken)).Status;
}

public sealed record RevokeTrustedController(Guid Id);

/// <summary>Esta máquina deixa de confiar no controlador. Só aqui: o servidor não decide confiança.</summary>
public sealed class RevokeTrustedControllerHandler(
    ITrustedControllerRepository controllers,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RevokeTrustedControllerHandler> logger)
{
    public async Task HandleAsync(RevokeTrustedController command, CancellationToken cancellationToken = default)
    {
        var controller = await controllers.FindAsync(command.Id, cancellationToken)
            ?? throw new DomainException("Esse controlador não está mais na lista.");

        controller.Revoke(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TrustedControllerRevoked {ControllerDeviceId}", controller.ControllerDeviceId);
    }
}
