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
}
