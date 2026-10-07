namespace MyTaskApp.Domain.TimeTracking;

/// <summary>Como o período nasceu. Os valores vão para o banco: não reordene.</summary>
public enum TimeEntrySource
{
    /// <summary>▶ e ⏹ na tarefa: o início e o fim são os cliques.</summary>
    Timer = 1,

    /// <summary>"+ Adicionar tempo": o usuário esqueceu de iniciar e lançou depois.</summary>
    Manual = 2,
}

/// <summary>
/// Um período de trabalho numa ocorrência (ADR-052): quando começou e quando
/// terminou. A duração é sempre <c>EndedAt − StartedAt</c>, nunca uma coluna.
/// </summary>
/// <remarks>
/// <para>
/// Agregado próprio, e não filho de <c>TaskItem</c>: a lista cresce sem teto,
/// não tem por que vir junto em toda leitura da tarefa, e a regra que mais
/// importa — um cronômetro ativo no app inteiro — atravessa tarefas de qualquer
/// jeito. Quem a garante é o caso de uso, que relê o banco, e um índice único.
/// </para>
/// <para>
/// Ativo é <see cref="EndedAt"/> nulo, e é só isso: o início está gravado desde
/// o clique, então fechar o app, cair ou suspender não perde nada — o tempo
/// corrido é <c>agora − StartedAt</c> na próxima vez que alguém olhar.
/// </para>
/// </remarks>
public sealed class TimeEntry
{
    public const int MaxNoteLength = 500;

    private TimeEntry(
        Guid id,
        Guid taskOccurrenceId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        TimeEntrySource source,
        string? note,
        DateTimeOffset createdAt)
    {
        Id = id;
        TaskOccurrenceId = taskOccurrenceId;
        StartedAt = startedAt;
        EndedAt = endedAt;
        Source = source;
        Note = note;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    /// <summary>A unidade de trabalho (ADR-001): é a ocorrência que se conclui, e é nela que o tempo foi gasto.</summary>
    public Guid TaskOccurrenceId { get; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary><c>null</c> enquanto o cronômetro corre.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public TimeEntrySource Source { get; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>A última escrita depois da criação: o ⏹ ou uma edição.</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsActive => EndedAt is null;

    /// <summary>
    /// Quanto o período durou — ou, se ainda corre, quanto já dura em
    /// <paramref name="now"/>. Nunca negativo, nem com o relógio do sistema
    /// voltando.
    /// </summary>
    public TimeSpan Duration(DateTimeOffset now)
    {
        var duration = (EndedAt ?? now) - StartedAt;

        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>▶: o período começa agora e fica aberto.</summary>
    public static TimeEntry StartTimer(Guid taskOccurrenceId, DateTimeOffset at) =>
        new(
            Guid.CreateVersion7(at),
            RequireOccurrence(taskOccurrenceId),
            at,
            endedAt: null,
            TimeEntrySource.Timer,
            note: null,
            createdAt: at);

    /// <summary>"+ Adicionar tempo": um período já encerrado, lançado depois.</summary>
    public static TimeEntry Manual(
        Guid taskOccurrenceId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? note,
        DateTimeOffset now)
    {
        var occurrenceId = RequireOccurrence(taskOccurrenceId);
        EnsurePeriod(startedAt, endedAt, now);

        return new(
            Guid.CreateVersion7(now),
            occurrenceId,
            startedAt,
            endedAt,
            TimeEntrySource.Manual,
            NormalizeNote(note),
            createdAt: now);
    }

    /// <summary>⏹: o período passa a ser histórico.</summary>
    /// <returns><c>false</c> quando já estava encerrado — parar duas vezes não muda nada.</returns>
    public bool Stop(DateTimeOffset at)
    {
        if (!IsActive)
        {
            return false;
        }

        if (at <= StartedAt)
        {
            throw new DomainException("O horário final deve ser posterior ao horário inicial.");
        }

        EndedAt = at;
        UpdatedAt = at;

        return true;
    }

    /// <summary>
    /// Corrige um período encerrado. Valida tudo antes de atribuir qualquer
    /// campo, e a origem fica: um período do cronômetro corrigido à mão
    /// continua sendo do cronômetro.
    /// </summary>
    public void Change(DateTimeOffset startedAt, DateTimeOffset endedAt, string? note, DateTimeOffset now)
    {
        if (IsActive)
        {
            throw new DomainException("Pare o cronômetro antes de editar este período.");
        }

        EnsurePeriod(startedAt, endedAt, now);
        var normalizedNote = NormalizeNote(note);

        StartedAt = startedAt;
        EndedAt = endedAt;
        Note = normalizedNote;
        UpdatedAt = now;
    }

    /// <summary>
    /// As regras de um período encerrado, sem criar nem mudar nada: o caso de uso
    /// confere antes de olhar a sobreposição, para "fim antes do início" não
    /// virar uma mensagem de sobreposição.
    /// </summary>
    public static void EnsurePeriod(DateTimeOffset startedAt, DateTimeOffset endedAt, DateTimeOffset now)
    {
        if (endedAt <= startedAt)
        {
            throw new DomainException("O horário final deve ser posterior ao horário inicial.");
        }

        // Lançar o que ainda vai acontecer não é registrar trabalho, é planejá-lo.
        if (endedAt > now)
        {
            throw new DomainException("O período não pode terminar no futuro.");
        }
    }

    private static Guid RequireOccurrence(Guid taskOccurrenceId) =>
        taskOccurrenceId == Guid.Empty
            ? throw new DomainException("O período precisa de uma tarefa.")
            : taskOccurrenceId;

    private static string? NormalizeNote(string? note)
    {
        var normalized = note?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= MaxNoteLength
            ? normalized
            : throw new DomainException($"A observação não pode passar de {MaxNoteLength} caracteres.");
    }
}
