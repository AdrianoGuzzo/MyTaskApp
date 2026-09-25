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
        Source = task;
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

        Tags = new TaskTagsViewModel(TaskId, task.Tags, Title);

        Agents = (task.ActiveAgents ?? []).Select(agent => new TaskAgentViewModel(this, agent)).ToList();

        Worktree = new TaskWorktreeViewModel(task.Worktrees, isCompleted);
    }

    /// <summary>
    /// A tarefa de onde esta linha veio. Guardada para o quadro em memória
    /// (<c>TodayViewModel._board</c>) poder ser remontado depois de um arrasto
    /// sem uma consulta nova (ADR-022).
    /// </summary>
    public TodayTask Source { get; }

    public Guid OccurrenceId { get; }

    public Guid TaskId { get; }

    public string Title { get; }

    /// <summary>Os agentes de IA abertos para a tarefa, um por ambiente (ADR-030, ADR-031).</summary>
    public IReadOnlyList<TaskAgentViewModel> Agents { get; }

    /// <summary>O nome do agente ("Claude Code"); <c>null</c> = nenhum aberto.</summary>
    public string? AgentName => Agents.Count > 0 ? Agents[0].AgentName : null;

    public bool HasActiveAgent => Agents.Count > 0;

    /// <summary>A bolinha de worktree e o que o balão diz dele (ADR-034).</summary>
    public TaskWorktreeViewModel Worktree { get; }

    /// <summary>"● Claude Code", ou "● Claude Code ×2" com um por repositório: o selo da linha.</summary>
    public string AgentLabel => Agents.Count switch
    {
        0 => string.Empty,
        1 => $"● {AgentName}",
        var count => $"● {AgentName} ×{count}",
    };

    public string AgentTip => Agents.Count switch
    {
        0 => string.Empty,
        1 => $"{AgentName} em execução para esta tarefa. Clique para ir ao terminal.",
        _ => $"Em execução: {string.Join(", ", Agents.Select(agent => agent.Label))}. Clique para escolher o terminal.",
    };

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
    /// "Abrir em janela" enquanto dá para mexer, olho depois de concluída. Não é
    /// mais um lápis: a janela deixou de ser só a anotação — tem o título e a aba
    /// Desenvolvimento —, então o ícone diz "abre esta tarefa", não "escreve".
    /// O olho é a única pista, antes do clique, de que ela vai abrir só para ler.
    /// </summary>
    public string NotesGlyph => IsCompleted ? "" : "";

    public string NotesLabel => IsCompleted
        ? "Ver esta tarefa (somente leitura)"
        : "Abrir esta tarefa";

    public ReminderPolicy Reminder { get; }

    /// <summary>Acende o ⚠: o lembrete avisou e ninguém reagiu.</summary>
    public bool IsAwaitingAttention { get; }

    public bool IsUrgentlyAwaiting { get; }

    public string AttentionLabel { get; }

    public bool HasReminder => Reminder.IsEnabled;

    /// <summary>As bolinhas das etiquetas e o seletor do botão de etiqueta (ADR-025).</summary>
    public TaskTagsViewModel Tags { get; }

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

/// <summary>
/// Um agente aberto da linha, com o repositório em que roda — o item do menu
/// do selo quando a tarefa tem mais de um (ADR-031).
/// </summary>
public sealed class TaskAgentViewModel(TaskRowViewModel row, ActiveAgent agent)
{
    public TaskRowViewModel Row { get; } = row;

    public Guid? DevelopmentId { get; } = agent.DevelopmentId;

    public string AgentName { get; } = agent.AgentName;

    /// <summary>"ecossistema-core · feature/x"; só o nome do agente, sem ambiente conhecido.</summary>
    public string Label { get; } = agent.RepositoryName is { } repository
        ? agent.Branch is { } branch ? $"{repository} · {branch}" : repository
        : agent.AgentName;
}
