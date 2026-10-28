namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// Os motivos de recusa que os dois lados entendem, como o <c>SyncErrorCodes</c>:
/// o servidor os devolve no ProblemDetails, e o agente os grava em
/// <see cref="RemoteOperationStatus.Rejected"/>.
/// </summary>
public static class RemoteErrorCodes
{
    public const string UnknownKind = "unknown_kind";
    public const string MissingIdentity = "missing_identity";
    public const string InvalidPayload = "invalid_payload";
    public const string AlreadyExpired = "already_expired";
    public const string WindowTooLong = "window_too_long";

    /// <summary>O pedido diz ter sido criado adiante do relógio de quem confere: o relógio de quem pediu está adiantado.</summary>
    public const string ClockSkew = "clock_skew";
    public const string WrongTarget = "wrong_target";
    public const string UntrustedSignature = "untrusted_signature";
    public const string AlreadySeen = "already_seen";
    public const string InvalidTransition = "invalid_transition";

    /// <summary>O dispositivo ainda não registrou chave na orquestração.</summary>
    public const string NotRegistered = "not_registered";

    /// <summary>A máquina pedida não aceita controle remoto (não tem o papel de executor).</summary>
    public const string NotAnExecutor = "not_an_executor";

    public const string TooManyPairings = "too_many_pairings";

    public const string PairingExpired = "pairing_expired";

    /// <summary>Uma das chaves mudou depois do pedido: o código já não seria o mesmo.</summary>
    public const string KeyChanged = "key_changed";

    /// <summary>O mesmo <c>OperationId</c> com outro pedido (o padrão do <c>ProcessedOperations</c>).</summary>
    public const string IdempotencyKeyReuse = "idempotency_key_reuse";

    /// <summary>O pedido é para uma conta (workspace) que não é a deste executor.</summary>
    public const string WrongWorkspace = "wrong_workspace";

    /// <summary>"Aceitar controle remoto" está desligado nesta máquina.</summary>
    public const string RemoteDisabled = "remote_disabled";

    /// <summary>Um id do pedido não existe aqui, ou não foi liberado para uso remoto.</summary>
    public const string NotInCatalog = "not_in_catalog";

    /// <summary>A regra do caso de uso recusou (a mensagem diz qual).</summary>
    public const string PolicyRefused = "policy_refused";

    /// <summary>As entidades pedidas não chegaram pelo sync a tempo.</summary>
    public const string EntitiesMissing = "entities_missing";

    /// <summary>Operações simultâneas ou por hora demais.</summary>
    public const string TooManyOperations = "too_many_operations";

    public const string ApprovalAnswered = "approval_answered";

    public const string ApprovalExpired = "approval_expired";

    /// <summary>Um humano negou a aprovação, ou a resposta não conferiu: a operação para aí.</summary>
    public const string ApprovalDenied = "approval_denied";

    /// <summary>A resposta não é para o pedido de aprovação que o agente fez.</summary>
    public const string HashMismatch = "hash_mismatch";

    /// <summary>O processo morreu sem o agente saber como terminou.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Uma falha que não é regra do caso de uso (o processo saiu com erro, a rede caiu).</summary>
    public const string ExecutionFailed = "execution_failed";
}
