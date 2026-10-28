using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O que o agente decidiu sobre um pedido que chegou.</summary>
public abstract record RemoteAdmission
{
    /// <summary>Conferido e no diário como <see cref="RemoteOperationStatus.Accepted"/>.</summary>
    public sealed record Admitted(RemoteJournalEntry Entry, RemoteOperationRequest Request) : RemoteAdmission;

    /// <summary>
    /// Recusado com o motivo. <see cref="Entry"/> existe quando a assinatura
    /// conferiu: aí o id entra no diário e não volta a ser considerado. Sem
    /// assinatura válida, nada é gravado — senão bastaria ao servidor inventar
    /// um envelope com o id de um pedido legítimo para bloqueá-lo.
    /// </summary>
    /// <param name="Summary">O motivo em palavras, quando o executor deu um.</param>
    public sealed record Refused(string Code, RemoteJournalEntry? Entry, string? Summary = null) : RemoteAdmission;

    /// <summary>Faltam entidades que o sync ainda vai trazer: fica para a próxima busca.</summary>
    public sealed record Deferred(IReadOnlyList<Guid> Missing) : RemoteAdmission;

    /// <summary>O id já está no diário: o que o diário diz é o que vale.</summary>
    public sealed record Known(RemoteJournalEntry Entry) : RemoteAdmission;
}

/// <param name="Running">Quantas este agente roda agora (4.4, item 8).</param>
public sealed record AdmitRemoteOperation(SignedEnvelope Envelope, int Running);

/// <summary>
/// Em quem o agente confia (ADR-087 e ADR-092): os controladores pareados e
/// esta própria instalação, pelo mesmo <c>DeviceId</c> e pela chave do cofre,
/// a mesma que o servidor tem no registro. É o que deixa o desktop pedir ao
/// agente da própria máquina a "sessão gerenciada", que o celular continua.
/// </summary>
/// <remarks>
/// A própria instalação não é um pareamento: não fica no banco, não aparece em
/// Máquinas e não se revoga. Deixa de valer quando a máquina para de aceitar
/// controle (o item 1 da admissão) ou quando a chave do cofre muda. Quem tem a
/// chave já roda tudo nesta máquina como este usuário: confiar nela não abre
/// porta nova.
/// </remarks>
public sealed class RemoteTrust(ITrustedControllerRepository trusted, IDeviceIdentity identity, IDeviceSigner signer)
{
    /// <summary>O nome com que esta instalação aparece na auditoria ("Remoto: este computador").</summary>
    public const string OwnNickname = "este computador";

    public async Task<IReadOnlyList<TrustedController>> ListAsync(CancellationToken cancellationToken = default)
    {
        var key = await signer.GetPublicKeyAsync(cancellationToken);
        var paired = await trusted.ListAllActiveAsync(cancellationToken);

        // O "pareamento" da própria é o próprio dispositivo: nunca o de um pedido de pareamento de verdade.
        var own = TrustedController.Approve(identity.DeviceId, OwnNickname, key.Fingerprint, key.SubjectPublicKeyInfo, identity.DeviceId, DateTimeOffset.UnixEpoch);

        return [.. paired.Where(controller => controller.Fingerprint != key.Fingerprint), own];
    }
}

/// <summary>
/// A validação local da autorização (seção 4.4 da arquitetura, ADR-087), na
/// ordem, e a primeira que falha é o motivo. O servidor confere quase tudo
/// isso também, mas quem decide é o agente: o servidor não tem como fabricar
/// uma operação aceitável.
/// </summary>
/// <remarks>
/// O item 7 (a regra do próprio caso de uso, a revisão de comando, a política
/// do banco) não é daqui: vale quando o caso de uso roda, e uma recusa dele
/// termina a operação como <see cref="RemoteOperationStatus.Failed"/>.
/// </remarks>
public sealed class AdmitRemoteOperationHandler(
    IRemoteSettingsStore settings,
    RemoteTrust trust,
    IDeviceIdentity identity,
    ISyncLocalStore sync,
    IRemoteOperationJournal journal,
    IRemoteEntityProbe entities,
    IEnumerable<IRemoteOperationExecutor> executors,
    RemoteDispatchLimits limits,
    TimeProvider timeProvider)
{
    /// <summary>Faltando entidades, espera o sync até esta folga antes do prazo; depois, recusa.</summary>
    public static readonly TimeSpan EntitiesGrace = TimeSpan.FromSeconds(30);

    public async Task<RemoteAdmission> HandleAsync(AdmitRemoteOperation command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        // 1. Aceitar controle remoto, nesta máquina.
        if (!(await settings.GetAsync(cancellationToken)).AcceptControl)
        {
            return new RemoteAdmission.Refused(RemoteErrorCodes.RemoteDisabled, null);
        }

        // 2. Assinado por um controlador que esta máquina aprovou e não revogou, ou por ela mesma (ADR-092).
        if (!RemoteJws.TryParse(command.Envelope, out var parts) || parts.Type != RemoteJws.OperationType)
        {
            return new RemoteAdmission.Refused(RemoteErrorCodes.UntrustedSignature, null);
        }

        var controller = (await trust.ListAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.Fingerprint == parts.KeyFingerprint);

        if (controller is null
            || !RemoteJws.TryVerify<RemoteOperationRequest>(command.Envelope, RemoteJws.OperationType, KeyOf(controller), out var request)
            || request.RequestedByDeviceId != controller.ControllerDeviceId)
        {
            return new RemoteAdmission.Refused(RemoteErrorCodes.UntrustedSignature, null);
        }

        // Daqui em diante a assinatura vale: o id entra no diário, aceito ou não.
        var entry = new RemoteJournalEntry(
            request.OperationId,
            controller.ControllerDeviceId,
            controller.Nickname,
            request.Kind,
            request.TaskId,
            command.Envelope.Jws,
            RemoteOperationStatus.Accepted,
            ReportedStatus: null,
            ResultCode: null,
            ResultSummary: null,
            Step: null,
            NextSeq: 1,
            AckedSeq: 0,
            now,
            now);

        if (await journal.FindAsync(request.OperationId, cancellationToken) is { } known)
        {
            return new RemoteAdmission.Known(known);
        }

        if (await RefusalAsync(request, controller, command.Running, now, cancellationToken) is { } refusal)
        {
            return refusal is RemoteAdmission.Refused refused
                ? await RecordAsync(entry with { Status = RemoteOperationStatus.Rejected, ResultCode = refused.Code, ResultSummary = refused.Summary }, refused, cancellationToken)
                : refusal;
        }

        return await journal.TryAddAsync(entry, cancellationToken)
            ? new RemoteAdmission.Admitted(entry, request)
            : new RemoteAdmission.Known((await journal.FindAsync(request.OperationId, cancellationToken))!);
    }

    /// <summary>Os itens 3 a 6 e 8, na ordem.</summary>
    private async Task<RemoteAdmission?> RefusalAsync(
        RemoteOperationRequest request,
        TrustedController controller,
        int running,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 3. Para esta máquina, e da conta ligada aqui.
        if (request.TargetDeviceId != identity.DeviceId)
        {
            return Refuse(RemoteErrorCodes.WrongTarget);
        }

        if ((await sync.ReadAsync(cancellationToken)).WorkspaceId != request.WorkspaceId)
        {
            return Refuse(RemoteErrorCodes.WrongWorkspace);
        }

        // 4. No prazo, e bem formado (o "nunca visto" é o diário).
        if (RemoteOperationRules.Problems(request, now) is [var problem, ..])
        {
            return Refuse(problem);
        }

        // 5. Um tipo que esta versão do agente sabe rodar.
        if (executors.FirstOrDefault(candidate => candidate.Kind == request.Kind) is not { } executor)
        {
            return Refuse(RemoteErrorCodes.UnknownKind);
        }

        // 6. Cada id existe aqui — o sync pode estar trazendo — e está liberado.
        var ids = new HashSet<Guid>(request.RequiredEntities);

        if (request.TaskId is { } taskId)
        {
            ids.Add(taskId);
        }

        if (ids.Count > 0 && await entities.MissingAsync(ids, cancellationToken) is { Count: > 0 } missing)
        {
            return request.NotAfter - now > EntitiesGrace
                ? new RemoteAdmission.Deferred(missing)
                : Refuse(RemoteErrorCodes.EntitiesMissing);
        }

        if (await executor.CheckAsync(request, cancellationToken) is { } refusal)
        {
            return new RemoteAdmission.Refused(refusal.Code, null, Clip(refusal.Summary));
        }

        // 8. Os tetos: quantas ao mesmo tempo, e quantas por hora de cada controlador.
        if (running >= limits.MaxConcurrent
            || await journal.CountFromControllerSinceAsync(controller.ControllerDeviceId, now.AddHours(-1), cancellationToken)
                >= limits.MaxPerControllerPerHour)
        {
            return Refuse(RemoteErrorCodes.TooManyOperations);
        }

        return null;

        static RemoteAdmission.Refused Refuse(string code) => new(code, null);

        static string? Clip(string? summary) =>
            summary is { Length: > RemoteLimits.MaxResultSummaryLength } ? summary[..RemoteLimits.MaxResultSummaryLength] : summary;
    }

    private async Task<RemoteAdmission> RecordAsync(RemoteJournalEntry entry, RemoteAdmission.Refused refused, CancellationToken cancellationToken) =>
        await journal.TryAddAsync(entry, cancellationToken)
            ? refused with { Entry = entry }
            : new RemoteAdmission.Known((await journal.FindAsync(entry.OperationId, cancellationToken))!);

    private static DevicePublicKey KeyOf(TrustedController controller) =>
        new(DevicePublicKey.Es256, controller.SubjectPublicKeyInfo, controller.Fingerprint);
}
