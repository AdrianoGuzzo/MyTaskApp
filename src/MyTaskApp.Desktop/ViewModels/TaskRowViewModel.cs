using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha da lista. Só carrega o que a tela precisa desenhar.</summary>
public sealed class TaskRowViewModel : ObservableObject
{
    /// <summary>O usuário já clicou no selo desde a última pendência: a borda para de pulsar.</summary>
    private bool _agentAlertSeen;

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

        WorktreeChoices = Worktree.Worktrees.Select(worktree => new TaskWorktreeChoice(this, worktree)).ToList();
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

    /// <summary>
    /// Os ambientes prontos onde "Abrir Claude Code" pode abrir o agente
    /// (ADR-036). Vazio, o item nem aparece no menu.
    /// </summary>
    public IReadOnlyList<TaskWorktreeChoice> WorktreeChoices { get; }

    /// <summary>A bolinha de worktree e o que o balão diz dele (ADR-034).</summary>
    public TaskWorktreeViewModel Worktree { get; }

    /// <summary>
    /// O que o selo resume (ADR-037): com vários agentes, o que mais pede o
    /// usuário — uma pergunta vence uma resposta pronta, que vence "trabalhando".
    /// </summary>
    /// <summary>
    /// As pendências da linha, cada uma com o momento em que surgiu. É o que
    /// o quadro lembra como "já vista": uma pergunta nova do mesmo agente tem
    /// outro horário e volta a pulsar.
    /// </summary>
    public IReadOnlyList<AgentAlertKey> AgentAlerts => Agents
        .Where(agent => NeedsAttention(agent.Activity))
        .Select(agent => new AgentAlertKey(TaskId, agent.DevelopmentId, agent.Activity, agent.ActivityChangedAt))
        .ToList();

    /// <summary>Pendência que o usuário ainda não viu: a borda âmbar pulsa em volta da linha.</summary>
    public bool IsAgentAlerting => AgentNeedsAttention && !_agentAlertSeen;

    /// <summary>Já vista, mas o agente segue esperando: a borda fica, parada e mais fraca.</summary>
    public bool IsAgentAlertSeen => AgentNeedsAttention && _agentAlertSeen;

    /// <summary>A linha nasce sabendo o que já foi visto — o refresh não pode reacender a borda.</summary>
    public void ApplySeenAgentAlerts(IReadOnlySet<AgentAlertKey> seen) =>
        SetAgentAlertSeen(AgentAlerts is { Count: > 0 } alerts && alerts.All(seen.Contains));

    /// <summary>O clique no selo: para de pulsar e diz ao quadro o que foi visto.</summary>
    public IReadOnlyList<AgentAlertKey> SeeAgentAlerts()
    {
        SetAgentAlertSeen(true);
        return AgentAlerts;
    }

    private void SetAgentAlertSeen(bool seen)
    {
        if (_agentAlertSeen == seen)
        {
            return;
        }

        _agentAlertSeen = seen;
        OnPropertyChanged(nameof(IsAgentAlerting));
        OnPropertyChanged(nameof(IsAgentAlertSeen));
    }

    public AgentActivity AgentActivity => Agents.Count == 0
        ? AgentActivity.Unknown
        : Agents.Select(agent => agent.Activity).MaxBy(Urgency);

    /// <summary>Um agente parou esperando o usuário: o selo ganha a cor de atenção.</summary>
    public bool AgentNeedsAttention => NeedsAttention(AgentActivity);

    private static bool NeedsAttention(AgentActivity activity) =>
        activity is AgentActivity.WaitingForUser or AgentActivity.WaitingReview or AgentActivity.Failed;

    /// <summary>
    /// "● Claude Code", "● Claude Code ×2", e com os hooks: "⚠ Claude Code ·
    /// aguardando você", "✓ Claude Code · revisar". O selo da linha.
    /// </summary>
    public string AgentLabel => Agents.Count switch
    {
        0 => string.Empty,
        var count => $"{AgentMark(AgentActivity)} {AgentName}{(count > 1 ? $" ×{count}" : string.Empty)}{AgentSuffix(AgentActivity)}",
    };

    public string AgentTip => Agents.Count switch
    {
        0 => string.Empty,
        1 => $"{AgentName} {Agents[0].StatusText} nesta tarefa. Clique para ir ao terminal.",
        _ => $"{string.Join(", ", Agents.Select(agent => $"{agent.Label}: {agent.StatusText}"))}. Clique para escolher o terminal.",
    };

    private static int Urgency(AgentActivity activity) => activity switch
    {
        AgentActivity.WaitingForUser => 4,
        AgentActivity.Failed => 3,
        AgentActivity.WaitingReview => 2,
        AgentActivity.Working => 1,
        _ => 0,
    };

    private static string AgentMark(AgentActivity activity) => activity switch
    {
        AgentActivity.WaitingForUser or AgentActivity.Failed => "⚠",
        AgentActivity.WaitingReview => "✓",
        _ => "●",
    };

    private static string AgentSuffix(AgentActivity activity) => activity switch
    {
        AgentActivity.WaitingForUser => " · aguardando você",
        AgentActivity.WaitingReview => " · revisar",
        AgentActivity.Failed => " · erro",
        _ => string.Empty,
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

    /// <summary>O que os hooks disseram por último (ADR-037).</summary>
    public AgentActivity Activity { get; } = agent.Activity;

    /// <summary>Quando <see cref="Activity"/> mudou: separa a pendência nova da já vista.</summary>
    public DateTimeOffset? ActivityChangedAt { get; } = agent.ActivityChangedAt;

    /// <summary>"aguardando você", "trabalhando"… — para o balão e o menu do selo.</summary>
    public string StatusText => Activity switch
    {
        AgentActivity.Working => "trabalhando",
        AgentActivity.WaitingForUser => "aguardando você",
        AgentActivity.WaitingReview => "terminou, aguardando revisão",
        AgentActivity.Failed => "última resposta com erro",
        _ => "em execução",
    };
}

/// <summary>
/// Uma pendência de agente, identificada pelo momento em que surgiu (ADR-037).
/// A atividade entra junto porque um hook sem horário ainda assim muda o que se pede.
/// </summary>
public readonly record struct AgentAlertKey(
    Guid TaskId,
    Guid? DevelopmentId,
    AgentActivity Activity,
    DateTimeOffset? ChangedAt);

/// <summary>
/// Um ambiente da linha onde abrir o agente — o item do menu "Abrir Claude
/// Code" quando a tarefa tem mais de um (ADR-036).
/// </summary>
public sealed class TaskWorktreeChoice(TaskRowViewModel row, TaskWorktree worktree)
{
    public TaskRowViewModel Row { get; } = row;

    public TaskWorktree Worktree { get; } = worktree;

    /// <summary>O agente já aberto neste ambiente, se houver: aí a escolha só traz o terminal para a frente.</summary>
    public TaskAgentViewModel? RunningAgent { get; } =
        row.Agents.FirstOrDefault(agent => agent.DevelopmentId == worktree.DevelopmentId);

    /// <summary>"ecossistema-core · feature/x", com "(aberto)" quando já tem agente.</summary>
    public string Label => RunningAgent is null
        ? $"{Worktree.RepositoryName} · {Worktree.Branch}"
        : $"{Worktree.RepositoryName} · {Worktree.Branch} (aberto)";
}
