using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// Raiz do agregado: descreve o QUE é a tarefa. O QUANDO e o estado de execução
/// ficam nas ocorrências (ADR-001). Não existe tarefa sem ocorrência — é isso
/// que elimina o caminho duplo entre tarefa simples e recorrente.
/// </summary>
public sealed class TaskItem
{
    public const int MaxTitleLength = 200;

    private readonly List<TaskOccurrence> _occurrences = [];

    private TaskItem(
        Guid id,
        string title,
        string? description,
        TaskPriority priority,
        DateTimeOffset createdAt)
    {
        Id = id;
        Title = title;
        Description = description;
        Priority = priority;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public TaskPriority Priority { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<TaskOccurrence> Occurrences => _occurrences.AsReadOnly();

    /// <summary>
    /// A política de lembrete da série. O domínio não conhece o padrão global do
    /// usuário — ele mora no banco e é aplicado pela Application na criação.
    /// </summary>
    public ReminderPolicy Reminder { get; private set; } = ReminderPolicy.None;

    /// <summary>Quando foi arquivado. <c>null</c> = está na lista principal.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>Quando foi para a lixeira. <c>null</c> = não foi excluído.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Quem excluiu, quando há identificação do usuário (§5).</summary>
    public string? DeletedBy { get; private set; }

    /// <summary>
    /// A <b>data de conclusão do checklist</b> — a mais recente entre as
    /// ocorrências concluídas, e só enquanto nenhuma estiver pendente. É o que o
    /// §2 manda usar como base do arquivamento automático, em lugar da data de
    /// criação.
    /// </summary>
    /// <remarks>
    /// Derivado, mas <b>persistido e mantido pela raiz</b>: é a raiz que aplica
    /// toda transição de ocorrência (<see cref="CompleteOccurrence"/> e as
    /// irmãs) e recalcula isto na mesma operação, então o campo não tem como
    /// divergir das ocorrências. Guardar em coluna é o que deixa a varredura
    /// automática ser um predicado indexado em vez de um <c>GROUP BY</c> sobre a
    /// tabela de ocorrências a cada tique.
    /// </remarks>
    public DateTimeOffset? ConcludedAt { get; private set; }

    /// <summary>
    /// O estado do checklist, derivado por precedência: lixeira vence arquivo,
    /// que vence concluído (§9). <b>Nenhuma consulta, tela ou varredura deve
    /// remontar esta regra por conta própria.</b>
    /// </summary>
    public TaskLifecycle Lifecycle =>
        DeletedAt is not null ? TaskLifecycle.Trashed
        : ArchivedAt is not null ? TaskLifecycle.Archived
        : ConcludedAt is not null ? TaskLifecycle.Completed
        : TaskLifecycle.Active;

    public bool IsArchived => ArchivedAt is not null;

    public bool IsInTrash => DeletedAt is not null;

    /// <summary>Sai da lista principal quando arquivado ou na lixeira.</summary>
    public bool IsOutOfTheMainList => IsArchived || IsInTrash;

    public static TaskItem Create(
        string title,
        DateTimeOffset createdAt,
        string? description = null,
        TaskPriority priority = TaskPriority.Normal,
        TaskSchedule? schedule = null,
        ReminderPolicy? reminder = null)
    {
        var task = new TaskItem(
            Guid.CreateVersion7(createdAt),
            NormalizeTitle(title),
            NormalizeDescription(description),
            priority,
            createdAt);

        task.ChangeReminder(reminder ?? ReminderPolicy.None);
        task.AddOccurrence(schedule ?? TaskSchedule.Unscheduled, createdAt);

        return task;
    }

    /// <summary>
    /// Edição atômica: normaliza e valida tudo antes de alterar qualquer campo,
    /// para que uma recusa não deixe a entidade num estado intermediário.
    /// </summary>
    public void Update(string title, string? description, TaskPriority priority)
    {
        RefuseWhenOutOfTheMainList("editar");

        var normalizedTitle = NormalizeTitle(title);
        var normalizedDescription = NormalizeDescription(description);

        Title = normalizedTitle;
        Description = normalizedDescription;
        Priority = priority;
    }

    public void Rename(string title) => Title = NormalizeTitle(title);

    public void ChangeDescription(string? description) =>
        Description = NormalizeDescription(description);

    public void ChangePriority(TaskPriority priority) => Priority = priority;

    /// <summary>
    /// Troca a política de lembrete. Campo único, como <see cref="ChangePriority"/>:
    /// não há meia-alteração a proteger. Quem rearma as ocorrências pendentes é a
    /// Application, que tem o relógio do usuário.
    /// </summary>
    /// <remarks>
    /// A cópia (<c>with { }</c>) não é adorno. <see cref="ReminderPolicy"/> tem
    /// instâncias compartilhadas — <c>Default</c>, <c>Urgent</c>, <c>None</c> —
    /// e a captura rápida aplica a mesma a várias tarefas de uma vez. O EF trata
    /// um tipo owned como parte do dono e <b>não aceita a mesma instância em dois
    /// donos</b>: ele grava uma e deixa a outra com os valores default, em
    /// silêncio. O resultado era a primeira tarefa de cada captura nascer com o
    /// lembrete desligado. Copiar aqui protege todos os chamadores de uma vez.
    /// </remarks>
    public void ChangeReminder(ReminderPolicy policy)
    {
        RefuseWhenOutOfTheMainList("mudar o lembrete de");

        if (policy is null)
        {
            throw new DomainException("A tarefa precisa de uma política de lembrete.");
        }

        Reminder = policy with { };
    }

    public TaskOccurrence GetOccurrence(Guid occurrenceId) =>
        _occurrences.SingleOrDefault(occurrence => occurrence.Id == occurrenceId)
        ?? throw new DomainException("Ocorrência não encontrada nesta tarefa.");

    // ---------------------------------------------------------------------
    // Ciclo de vida (§1, §4, §5, §9)
    //
    // Arquivar não é excluir, e as duas marcas são independentes de propósito:
    // um checklist arquivado que vai para a lixeira conserva ArchivedAt, então
    // restaurá-lo da lixeira o devolve ao arquivo — o estado em que ele estava —
    // sem precisar de um campo "estado anterior" para lembrar disso.
    // ---------------------------------------------------------------------

    /// <summary>Tira o checklist da lista principal, preservando tudo.</summary>
    public void Archive(DateTimeOffset at)
    {
        RefuseWhenInTrash("arquivar");

        if (IsArchived)
        {
            throw new DomainException("Este checklist já está arquivado.");
        }

        ArchivedAt = at;

        // Fora da lista principal não se cobra atenção: um lembrete que
        // continuasse tocando seria o app insistindo por algo que o usuário
        // acabou de mandar guardar.
        SilenceReminders();
    }

    /// <summary>Devolve o checklist arquivado à lista principal.</summary>
    public void RestoreFromArchive()
    {
        RefuseWhenInTrash("restaurar");

        if (!IsArchived)
        {
            throw new DomainException("Este checklist não está arquivado.");
        }

        ArchivedAt = null;
    }

    /// <summary>
    /// Exclusão reversível: marca, registra quando e por quem, e não remove
    /// nada. O histórico, os itens e as configurações continuam no banco.
    /// </summary>
    public void MoveToTrash(DateTimeOffset at, string? deletedBy)
    {
        if (IsInTrash)
        {
            throw new DomainException("Este checklist já está na lixeira.");
        }

        DeletedAt = at;
        DeletedBy = string.IsNullOrWhiteSpace(deletedBy) ? null : deletedBy.Trim();

        SilenceReminders();
    }

    /// <summary>
    /// Tira da lixeira. Volta para onde estava: se tinha sido arquivado antes de
    /// ser excluído, continua arquivado.
    /// </summary>
    public void RestoreFromTrash()
    {
        if (!IsInTrash)
        {
            throw new DomainException("Este checklist não está na lixeira.");
        }

        DeletedAt = null;
        DeletedBy = null;
    }

    /// <summary>
    /// Confere que a exclusão definitiva é legítima. Só o que já saiu da lista
    /// principal — arquivado ou na lixeira — pode ser apagado de vez.
    /// </summary>
    /// <remarks>
    /// A regra mora no agregado, e não na tela, porque é a única forma de ela
    /// valer também para quem chamar o caso de uso por outro caminho. É o que
    /// torna estrutural o §10: nenhum clique isolado, em lugar nenhum, leva um
    /// checklist ativo direto para o nada.
    /// </remarks>
    public void EnsurePermanentDeletionIsAllowed()
    {
        if (!IsOutOfTheMainList)
        {
            throw new DomainException(
                "Só é possível excluir definitivamente um checklist que esteja "
                + "arquivado ou na lixeira.");
        }
    }

    // ---------------------------------------------------------------------
    // Transições de ocorrência pela raiz
    //
    // Passam por aqui, e não direto na ocorrência, por duas razões: é a raiz que
    // mantém ConcludedAt coerente com as ocorrências, e é a raiz que sabe se o
    // checklist ainda está na lista principal.
    // ---------------------------------------------------------------------

    public TaskOccurrence CompleteOccurrence(Guid occurrenceId, DateTimeOffset completedAt)
    {
        var occurrence = GetOccurrence(occurrenceId);

        RefuseWhenOutOfTheMainList("concluir");

        occurrence.Complete(completedAt);
        RefreshConclusion();

        return occurrence;
    }

    public TaskOccurrence ReopenOccurrence(Guid occurrenceId)
    {
        var occurrence = GetOccurrence(occurrenceId);

        RefuseWhenOutOfTheMainList("reabrir");

        occurrence.Reopen();
        RefreshConclusion();

        return occurrence;
    }

    public TaskOccurrence CancelOccurrence(Guid occurrenceId)
    {
        var occurrence = GetOccurrence(occurrenceId);

        RefuseWhenOutOfTheMainList("cancelar");

        occurrence.Cancel();
        RefreshConclusion();

        return occurrence;
    }

    /// <summary>
    /// Põe a ocorrência na n-ésima casa da seção em que ela aparece (ADR-022).
    /// </summary>
    /// <remarks>
    /// Entra pela raiz como as irmãs, e não direto na ocorrência, porque é a
    /// raiz que sabe se o checklist ainda está na lista principal — reordenar
    /// algo que o usuário arquivou seria mexer no que ele mandou guardar.
    /// <para>
    /// Não chama <c>RefreshConclusion</c>: posição não é estado de execução, e
    /// acrescentar a chamada "por simetria" com as irmãs faria um arrasto
    /// recalcular a data de conclusão do checklist.
    /// </para>
    /// </remarks>
    public TaskOccurrence PlaceOccurrence(Guid occurrenceId, int position)
    {
        var occurrence = GetOccurrence(occurrenceId);

        RefuseWhenOutOfTheMainList("reordenar");

        occurrence.PlaceAt(position);

        return occurrence;
    }

    /// <summary>
    /// Confere que a ocorrência aceita ser reordenada, sem alterar nada.
    /// </summary>
    /// <remarks>
    /// Mesma forma de <see cref="EnsurePermanentDeletionIsAllowed"/>, e pela
    /// mesma razão de sempre: a regra mora no agregado. Reordenar é escrita em
    /// vários agregados de uma vez, então quem coordena confere todos antes de
    /// mexer em qualquer um — do contrário uma recusa no meio da seção deixaria
    /// metade da lista renumerada em memória, e um <c>SaveChanges</c> posterior
    /// persistiria essa meia-alteração.
    /// </remarks>
    public void EnsureOccurrenceCanBePlaced(Guid occurrenceId)
    {
        var occurrence = GetOccurrence(occurrenceId);

        RefuseWhenOutOfTheMainList("reordenar");

        occurrence.EnsureCanBePlaced();
    }

    public TaskOccurrence RescheduleOccurrence(Guid occurrenceId, TaskSchedule schedule)
    {
        var occurrence = GetOccurrence(occurrenceId);

        RefuseWhenOutOfTheMainList("reagendar");

        occurrence.Reschedule(schedule);

        return occurrence;
    }

    /// <summary>
    /// Recalcula a conclusão da série. Uma ocorrência pendente basta para o
    /// checklist deixar de estar concluído — é o que faz reabrir um item
    /// devolvê-lo à lista ativa e zerar o relógio do arquivamento automático.
    /// </summary>
    /// <remarks>
    /// Cancelada não conta como concluída: um checklist inteiramente cancelado
    /// não tem data de conclusão e portanto nunca é arquivado sozinho. Arquivar
    /// o que foi desistido continua sendo escolha do usuário.
    /// </remarks>
    private void RefreshConclusion()
    {
        if (_occurrences.Exists(occurrence => occurrence.Status is TaskItemStatus.Pending))
        {
            ConcludedAt = null;
            return;
        }

        ConcludedAt = _occurrences
            .Where(occurrence => occurrence.Status is TaskItemStatus.Completed)
            .Max(occurrence => occurrence.CompletedAt);
    }

    private void SilenceReminders()
    {
        foreach (var occurrence in _occurrences)
        {
            occurrence.DisarmReminder();
        }
    }

    private void RefuseWhenInTrash(string verb)
    {
        if (IsInTrash)
        {
            throw new DomainException(
                $"Não é possível {verb} um checklist que está na lixeira.");
        }
    }

    /// <summary>
    /// Arquivado e na lixeira são somente leitura. Sem isto, uma tela de
    /// detalhes poderia mexer num checklist que o usuário já guardou — e a
    /// alteração só apareceria depois de restaurar, quando ninguém mais lembra
    /// de tê-la feito.
    /// </summary>
    private void RefuseWhenOutOfTheMainList(string verb)
    {
        RefuseWhenInTrash(verb);

        if (IsArchived)
        {
            throw new DomainException(
                $"Não é possível {verb} um checklist arquivado. Restaure-o primeiro.");
        }
    }

    private void AddOccurrence(TaskSchedule schedule, DateTimeOffset createdAt) =>
        _occurrences.Add(new TaskOccurrence(Guid.CreateVersion7(createdAt), Id, schedule));

    /// <summary>Título em branco é recusado; acima do limite, também.</summary>
    private static string NormalizeTitle(string? title)
    {
        var normalized = NormalizeOptionalText(title)
            ?? throw new DomainException("A tarefa precisa de um título.");

        if (normalized.Length > MaxTitleLength)
        {
            throw new DomainException($"O título não pode passar de {MaxTitleLength} caracteres.");
        }

        return normalized;
    }

    /// <summary>
    /// A descrição não tem teto: é onde cabe o que não coube no título, e cortar
    /// uma anotação longa no meio seria perder justamente o detalhe.
    /// </summary>
    private static string? NormalizeDescription(string? description) =>
        NormalizeOptionalText(description);

    /// <summary>Texto em branco vira nulo.</summary>
    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
