namespace MyTaskApp.Sync.Contracts;

/// <summary>
/// Os códigos que o servidor devolve numa operação recusada ou numa resposta
/// de erro (extensão <c>code</c> do ProblemDetails). O cliente decide pelo
/// código, nunca pela frase.
/// </summary>
public static class SyncErrorCodes
{
    // ---- Operação (push) ----

    /// <summary>Um valor não passou na validação do contrato. Permanente.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>Tentou mudar um campo que só se define na criação. Permanente.</summary>
    public const string ImmutableField = "immutable_field";

    /// <summary>Tipo de entidade que este servidor não conhece. Permanente.</summary>
    public const string UnknownEntityType = "unknown_entity_type";

    /// <summary>A entidade foi excluída: nada a ressuscitar.</summary>
    public const string EntityDeleted = "entity_deleted";

    /// <summary>O pai ainda não chegou ao servidor. Temporário: tenta de novo depois.</summary>
    public const string ParentMissing = "parent_missing";

    /// <summary>O pai foi excluído. Permanente.</summary>
    public const string ParentDeleted = "parent_deleted";

    /// <summary>O mesmo id de operação veio com outro conteúdo. Permanente.</summary>
    public const string IdempotencyKeyReuse = "idempotency_key_reuse";

    /// <summary>O dispositivo da operação não é o do token. Permanente.</summary>
    public const string DeviceMismatch = "device_mismatch";

    /// <summary>O papel no workspace não deixa escrever. Permanente.</summary>
    public const string Forbidden = "forbidden";

    // ---- Campo recusado (push) ----

    /// <summary>Outro dispositivo mudou o mesmo campo antes: vale o do servidor.</summary>
    public const string ConcurrentChange = "concurrent_change";

    /// <summary>O valor colide com o de outra entidade (nome de etiqueta, alias).</summary>
    public const string UniqueViolation = "unique_violation";

    /// <summary>
    /// O mesmo campo mudou antes em outro dispositivo, mas é um carimbo (data
    /// de arquivamento, "atualizado em"): vale o do servidor, sem conflito
    /// visível. O cliente só grava o valor que veio.
    /// </summary>
    public const string Superseded = "superseded";

    // ---- Resposta inteira ----

    public const string DeviceRevoked = "device_revoked";

    /// <summary>A conta Google não está na lista de quem pode usar o servidor (ADR-068).</summary>
    public const string RegistrationClosed = "registration_closed";

    public const string CursorAhead = "cursor_ahead";

    public const string UpgradeRequired = "upgrade_required";

    public const string NotFound = "not_found";

    public const string BadRequest = "bad_request";

    /// <summary>Só o pai que falta se resolve sozinho com o tempo; o resto pede gente.</summary>
    public static bool IsRetryable(string code) => code == ParentMissing;
}
