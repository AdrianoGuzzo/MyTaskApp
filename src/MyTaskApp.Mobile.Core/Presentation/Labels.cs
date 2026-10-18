using System.Globalization;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>Como o celular escreve os valores do domínio, em português.</summary>
public static class Labels
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static string Priority(TaskPriority priority) => priority switch
    {
        TaskPriority.Low => "Baixa",
        TaskPriority.High => "Alta",
        TaskPriority.Urgent => "Urgente",
        _ => "Normal",
    };

    /// <summary>O estado de relance: o ciclo de vida vence o status (arquivada concluída é "Arquivada").</summary>
    public static string Status(TaskItemStatus status, TaskLifecycle lifecycle) => lifecycle switch
    {
        TaskLifecycle.Trashed => "Na lixeira",
        TaskLifecycle.Archived => "Arquivada",
        _ => status switch
        {
            TaskItemStatus.Completed => "Concluída",
            TaskItemStatus.Cancelled => "Cancelada",
            _ => "Pendente",
        },
    };

    public static string StatusChoice(TaskItemStatus status) => status switch
    {
        TaskItemStatus.Completed => "Concluída",
        TaskItemStatus.Cancelled => "Cancelada",
        _ => "Pendente",
    };

    public static string Audit(TaskAuditOperation operation) => operation switch
    {
        TaskAuditOperation.Created => "Criada",
        TaskAuditOperation.Completed => "Concluída",
        TaskAuditOperation.Reopened => "Reaberta",
        TaskAuditOperation.Archived => "Arquivada",
        TaskAuditOperation.Restored => "Restaurada do arquivo",
        TaskAuditOperation.MovedToTrash => "Movida para a lixeira",
        TaskAuditOperation.RestoredFromTrash => "Restaurada da lixeira",
        TaskAuditOperation.PermanentlyDeleted => "Excluída de vez",
        TaskAuditOperation.TimeEntryAdded => "Tempo lançado",
        TaskAuditOperation.TimeEntryChanged => "Tempo alterado",
        TaskAuditOperation.TimeEntryDeleted => "Tempo removido",
        _ => operation.ToString(),
    };

    public static string Development(TaskDevelopmentStatus status) => status switch
    {
        TaskDevelopmentStatus.Creating => "Criando",
        TaskDevelopmentStatus.Ready => "Pronto",
        TaskDevelopmentStatus.Error => "Com erro",
        TaskDevelopmentStatus.Removed => "Removido",
        TaskDevelopmentStatus.NotCreatedHere => "Em outro computador",
        _ => status.ToString(),
    };

    /// <summary>"15/10/2026 14:30", no fuso de quem olha.</summary>
    public static string Instant(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("dd/MM/yyyy HH:mm", Culture);

    /// <summary>"agora há pouco", "há 5 min", "há 2 h" — a mesma escala da bolinha do desktop.</summary>
    public static string Ago(DateTimeOffset instant, TimeProvider time)
    {
        var elapsed = time.GetUtcNow() - instant;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "agora há pouco",
            { TotalMinutes: < 60 } => $"há {(int)elapsed.TotalMinutes} min",
            { TotalHours: < 24 } => $"há {(int)elapsed.TotalHours} h",
            _ => "em " + Instant(instant, time.LocalTimeZone),
        };
    }

    /// <summary>
    /// A primeira linha com texto da anotação, sem os marcadores do Markdown e
    /// curta o bastante para a lista. <c>null</c> = sem anotação.
    /// </summary>
    public static string? Summary(string? notes, int max = 120)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var line = notes
            .Split('\n')
            .Select(text => text.Trim().TrimStart('#', '-', '*', '>', ' ').Trim())
            .FirstOrDefault(text => text.Length > 0);

        if (line is null)
        {
            return null;
        }

        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
