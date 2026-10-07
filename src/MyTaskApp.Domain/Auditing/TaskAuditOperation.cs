namespace MyTaskApp.Domain.Auditing;

/// <summary>As operações do ciclo de vida que ficam registradas (§8).</summary>
public enum TaskAuditOperation
{
    Created = 0,
    Completed = 1,
    Reopened = 2,
    Archived = 3,
    Restored = 4,
    MovedToTrash = 5,
    RestoredFromTrash = 6,

    /// <summary>
    /// O estado terminal do §9. Quem executou — usuário ou sistema — está em
    /// <see cref="TaskAuditEntry.Actor"/>, e é por isso que "exclusão automática
    /// realizada pelo sistema" não precisa de uma operação própria.
    /// </summary>
    PermanentlyDeleted = 7,

    /// <summary>
    /// Um período lançado à mão (ADR-052). O ▶/⏹ não entra na trilha: o próprio
    /// período já é o registro, e auditá-lo dobraria a tabela sem dizer nada novo.
    /// </summary>
    TimeEntryAdded = 8,

    /// <summary>Um período corrigido; os detalhes dizem o antes e o depois.</summary>
    TimeEntryChanged = 9,

    TimeEntryDeleted = 10,
}
