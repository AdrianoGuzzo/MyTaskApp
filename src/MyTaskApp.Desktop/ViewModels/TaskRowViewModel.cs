using System.Globalization;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha da lista. Só carrega o que a tela precisa desenhar.</summary>
public sealed class TaskRowViewModel
{
    public TaskRowViewModel(TodayTask task, bool isCompleted)
    {
        OccurrenceId = task.OccurrenceId;
        TaskId = task.TaskId;
        Title = task.Title;
        IsLate = task.IsLate;
        Priority = task.Priority;
        IsCompleted = isCompleted;
        Notes = task.Notes;
        Reminder = task.Reminder ?? ReminderPolicy.None;
        HasScheduledTime = task.ScheduledTime is not null;

        // Cultura invariante: em formato customizado, ':' e '/' seguem a cultura
        // corrente, e o rótulo passaria a depender da máquina.
        TimeLabel = task.ScheduledTime?.ToString("HH:mm", CultureInfo.InvariantCulture)
            ?? string.Empty;

        IsAwaitingAttention = task.WaitingForAttention is not null;

        AttentionLabel = task.WaitingForAttention is { } waiting
            ? DescribeWait(waiting)
            : string.Empty;

        // Quanto mais insistente o lembrete ficou, mais o ⚠ se destaca.
        IsUrgentlyAwaiting = task.ReminderStep >= ReminderEscalation.SoundStep;

        // Um editor por linha, e nao um compartilhado: o flyout abre junto com o
        // clique, e um editor so daria corrida entre carregar e desenhar.
        Editor = new ReminderEditorViewModel { CanRemindAtScheduledTime = HasScheduledTime };
        Editor.Load(Reminder);
    }

    public Guid OccurrenceId { get; }

    public Guid TaskId { get; }

    public string Title { get; }

    public string TimeLabel { get; }

    public TaskPriority Priority { get; }

    /// <summary>
    /// Prioridade vira um traço de 3px na lateral da linha. Cartão colorido
    /// inteiro competiria com o título pela atenção, que é justamente o que
    /// uma lista de checklist não pode fazer.
    /// </summary>
    public bool IsUrgent => Priority == TaskPriority.Urgent;

    public bool IsHighPriority => Priority == TaskPriority.High;

    public bool IsLate { get; }

    public bool IsCompleted { get; }

    public bool HasScheduledTime { get; }

    /// <summary>A anotação livre desta tarefa, em Markdown; nula quando não há.</summary>
    public string? Notes { get; }

    /// <summary>
    /// Acende o ícone de anotação mesmo sem o mouse na linha. É o indicador de
    /// "este item tem mais coisa escrita" — sem ele, descobrir onde há anotação
    /// custaria abrir item por item.
    /// </summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>
    /// Lápis enquanto dá para escrever, documento depois de concluída. O glifo
    /// é a única pista, antes do clique, de que a janela vai abrir só para ler.
    /// </summary>
    public string NotesGlyph => IsCompleted ? "" : "";

    public string NotesLabel => IsCompleted
        ? "Ver as anotações desta tarefa"
        : "Anotações desta tarefa";

    public ReminderPolicy Reminder { get; }

    /// <summary>Acende o ⚠: o lembrete avisou e ninguém reagiu.</summary>
    public bool IsAwaitingAttention { get; }

    public bool IsUrgentlyAwaiting { get; }

    public string AttentionLabel { get; }

    public bool HasReminder => Reminder.IsEnabled;

    /// <summary>O ajuste individual desta tarefa, aberto pelo botao "Lembrete".</summary>
    public ReminderEditorViewModel Editor { get; }

    /// <summary>"Atrasado há 35 minutos" — acima de 90 minutos passa a horas.</summary>
    public static string DescribeWait(TimeSpan waiting)
    {
        var minutes = (int)Math.Round(waiting.TotalMinutes, MidpointRounding.AwayFromZero);

        if (minutes < 1)
        {
            return "Aguardando sua atenção.";
        }

        if (minutes == 1)
        {
            return "Aguardando sua atenção há 1 minuto.";
        }

        if (minutes <= 90)
        {
            return $"Aguardando sua atenção há {minutes} minutos.";
        }

        var hours = (int)Math.Round(waiting.TotalHours, MidpointRounding.AwayFromZero);

        if (hours < 48)
        {
            return hours == 1
                ? "Aguardando sua atenção há 1 hora."
                : $"Aguardando sua atenção há {hours} horas.";
        }

        return $"Aguardando sua atenção há {(int)waiting.TotalDays} dias.";
    }
}
