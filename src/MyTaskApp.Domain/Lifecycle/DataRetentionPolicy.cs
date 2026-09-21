namespace MyTaskApp.Domain.Lifecycle;

/// <summary>
/// As duas janelas de tempo que governam o ciclo de vida: quanto tempo um
/// checklist concluído espera antes de ser arquivado sozinho, e quanto tempo um
/// checklist excluído fica na lixeira antes de sumir de vez (§11).
/// </summary>
/// <remarks>
/// <para>
/// Valida no construtor, como <c>ReminderPolicy</c>: uma retenção de zero dia
/// apagaria a lixeira no mesmo tique em que o usuário clicou em excluir, que é
/// exatamente o que a lixeira existe para impedir.
/// </para>
/// <para>
/// <b>O arquivamento automático nasce desligado</b> — mesma razão do ADR-014
/// ("Upgrade não arma nada"): ligar sozinho faria a primeira abertura depois da
/// atualização varrer o histórico inteiro do usuário para fora da lista, sem
/// ninguém ter pedido. A lixeira não corre esse risco (numa atualização ela está
/// vazia), então lá o prazo já vale.
/// </para>
/// </remarks>
public sealed record DataRetentionPolicy
{
    /// <summary>Um dia é o menor prazo que ainda deixa o usuário voltar atrás.</summary>
    public const int MinDays = 1;

    /// <summary>Dez anos. Acima disso o número é engano de digitação, não escolha.</summary>
    public const int MaxDays = 3650;

    /// <summary>Os prazos oferecidos na tela; qualquer outro valor é "personalizado".</summary>
    public static readonly IReadOnlyList<int> PresetDays = [1, 7, 15, 30, 60, 90];

    public DataRetentionPolicy(
        bool autoArchiveEnabled,
        int autoArchiveAfterDays,
        int trashRetentionDays)
    {
        AutoArchiveEnabled = autoArchiveEnabled;
        AutoArchiveAfterDays = Validate(autoArchiveAfterDays, "O prazo de arquivamento");
        TrashRetentionDays = Validate(trashRetentionDays, "O prazo da lixeira");
    }

    /// <summary>O que uma instalação nova recebe, sem semear linha nenhuma.</summary>
    public static DataRetentionPolicy Factory { get; } = new(
        autoArchiveEnabled: false,
        autoArchiveAfterDays: 30,
        trashRetentionDays: 30);

    public bool AutoArchiveEnabled { get; }

    /// <summary>
    /// Contado a partir da <b>data de conclusão</b> do checklist, não da criação:
    /// um checklist criado em janeiro e concluído em dezembro só começa a contar
    /// em dezembro.
    /// </summary>
    public int AutoArchiveAfterDays { get; }

    public int TrashRetentionDays { get; }

    /// <summary>
    /// A partir de que instante um checklist concluído já passou do prazo.
    /// Quem consulta compara <c>ConcludedAt &lt;= este valor</c>.
    /// </summary>
    public DateTimeOffset AutoArchiveCutoff(DateTimeOffset nowUtc) =>
        nowUtc.AddDays(-AutoArchiveAfterDays);

    public DateTimeOffset TrashCutoff(DateTimeOffset nowUtc) =>
        nowUtc.AddDays(-TrashRetentionDays);

    /// <summary>
    /// Quantos dias faltam até a exclusão definitiva de algo excluído em
    /// <paramref name="deletedAt"/>. Nunca negativo: o que já venceu mostra 0 —
    /// ele ainda está lá, esperando a próxima varredura.
    /// </summary>
    public int DaysLeftInTrash(DateTimeOffset deletedAt, DateTimeOffset nowUtc)
    {
        var remaining = deletedAt.AddDays(TrashRetentionDays) - nowUtc;

        return remaining <= TimeSpan.Zero
            ? 0
            : (int)Math.Ceiling(remaining.TotalDays);
    }

    private static int Validate(int days, string label) =>
        days is >= MinDays and <= MaxDays
            ? days
            : throw new DomainException(
                $"{label} precisa ficar entre {MinDays} e {MaxDays} dias.");
}
