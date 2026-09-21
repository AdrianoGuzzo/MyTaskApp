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
}
