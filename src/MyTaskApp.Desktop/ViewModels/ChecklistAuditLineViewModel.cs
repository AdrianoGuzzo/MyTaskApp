using System.Globalization;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha da trilha de auditoria, já em português e já formatada.</summary>
public sealed class ChecklistAuditLineViewModel
{
    public ChecklistAuditLineViewModel(TaskAuditEntry entry)
    {
        Operation = Describe(entry.Operation);
        When = entry.OccurredAt.ToLocalTime().ToString(
            "dd/MM/yyyy HH:mm",
            CultureInfo.InvariantCulture);

        // "pelo sistema" não é enfeite: é o §10 pedindo que operação automática
        // seja distinguível de operação de gente, meses depois.
        By = entry.Actor is AuditActor.System
            ? "pelo sistema"
            : entry.ActorName is { } name
                ? $"por {name}"
                : "pelo usuário";

        Details = entry.Details;
        HasDetails = !string.IsNullOrWhiteSpace(entry.Details);
        IsSystem = entry.Actor is AuditActor.System;
        IsDestructive = entry.Operation is TaskAuditOperation.PermanentlyDeleted;
    }

    public string Operation { get; }

    public string When { get; }

    public string By { get; }

    public string? Details { get; }

    public bool HasDetails { get; }

    public bool IsSystem { get; }

    public bool IsDestructive { get; }

    public static string Describe(TaskAuditOperation operation) => operation switch
    {
        TaskAuditOperation.Created => "Criado",
        TaskAuditOperation.Completed => "Concluído",
        TaskAuditOperation.Reopened => "Reaberto",
        TaskAuditOperation.Archived => "Arquivado",
        TaskAuditOperation.Restored => "Restaurado do arquivo",
        TaskAuditOperation.MovedToTrash => "Movido para a lixeira",
        TaskAuditOperation.RestoredFromTrash => "Restaurado da lixeira",
        TaskAuditOperation.PermanentlyDeleted => "Excluído definitivamente",
        _ => operation.ToString(),
    };
}
