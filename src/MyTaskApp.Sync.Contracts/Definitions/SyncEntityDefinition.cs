namespace MyTaskApp.Sync.Contracts.Definitions;

/// <summary>Um tipo de entidade sincronizada: campos, referências e regras.</summary>
public sealed class SyncEntityDefinition
{
    private readonly Dictionary<string, SyncFieldDefinition> _byName;

    public SyncEntityDefinition(string type, int rank, IReadOnlyList<SyncFieldDefinition> fields)
    {
        Type = type;
        Rank = rank;
        Fields = fields;
        _byName = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
    }

    /// <summary>O nome no fio (<c>task</c>, <c>tagDirectory</c>…).</summary>
    public string Type { get; }

    /// <summary>
    /// A ordem de aplicação: um tipo sempre tem rank maior que os tipos para
    /// os quais aponta, então "pai antes do filho" é ordenar por rank.
    /// </summary>
    public int Rank { get; }

    public IReadOnlyList<SyncFieldDefinition> Fields { get; }

    public IReadOnlyList<SyncReference> References { get; init; } = [];

    /// <summary>
    /// Vínculo puro (tarefa↔etiqueta): sem campos editáveis, o id deriva das
    /// pontas, e marcar de novo depois de desmarcar revive o vínculo.
    /// </summary>
    public bool IsAssociation { get; init; }

    /// <summary>
    /// O filho anda junto com o pai no pull: a ocorrência nunca chega sem a
    /// tarefa, nem a tarefa sem a ocorrência.
    /// </summary>
    public bool TravelsWithParent { get; init; }

    /// <summary>
    /// Os campos que, juntos, não podem se repetir no workspace (sem
    /// maiúsculas). Vazio quando não há unicidade.
    /// </summary>
    public IReadOnlyList<string> UniqueKeyFields { get; init; } = [];

    public SyncFieldDefinition? FindField(string name) => _byName.GetValueOrDefault(name);

    public SyncReference? FindReference(string field) =>
        References.FirstOrDefault(reference => reference.Field == field);
}
