using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync.Mapping;

/// <summary>
/// Um campo do contrato e a propriedade do EF de onde ele sai. <see cref="Member"/>
/// preenchido é um membro de um campo composto; <see cref="Property"/> com ponto
/// é uma propriedade de tipo owned (<c>Reminder.IsEnabled</c>).
/// </summary>
internal sealed record SyncColumn(string Field, string? Member, string Property);

/// <summary>
/// Uma lista que viaja dentro de um campo: as linhas filhas (parâmetros do
/// comando, comandos do ambiente) mesclam e conflitam como um valor só.
/// </summary>
/// <param name="ParentKey">A chave estrangeira do filho, para achar o pai a partir de uma linha alterada.</param>
/// <param name="Project">A lista do fio a partir do pai já carregado.</param>
/// <param name="Row">As colunas de uma linha filha a partir do item do fio, da posição e do id do pai.</param>
internal sealed record SyncChildList(
    string Field,
    Type ChildType,
    string ParentKey,
    IReadOnlyList<string> SharedProperties,
    IReadOnlyList<string> LocalProperties,
    Func<object, JsonElement> Project,
    Func<JsonElement, int, Guid, IReadOnlyDictionary<string, object?>> Row);

/// <summary>
/// A ponte entre uma entidade do domínio e o contrato (ADR-061): o que sai e o
/// que fica. É uma <b>lista de permissão</b> — só as propriedades em
/// <see cref="Columns"/> viajam — e o teste de completude exige que toda
/// propriedade mapeada esteja aqui ou em <see cref="LocalProperties"/>.
/// </summary>
internal abstract class SyncEntityMapper
{
    public abstract SyncEntityDefinition Definition { get; }

    public abstract Type ClrType { get; }

    public abstract IReadOnlyList<SyncColumn> Columns { get; }

    /// <summary>As propriedades que nunca saem deste computador.</summary>
    public abstract IReadOnlyList<string> LocalProperties { get; }

    /// <summary>Campos compostos que são <c>null</c> quando esta propriedade é nula.</summary>
    public virtual IReadOnlyDictionary<string, string> PresenceProperties { get; } = new Dictionary<string, string>();

    public virtual IReadOnlyList<SyncChildList> Children { get; } = [];

    /// <summary>
    /// O valor das colunas locais numa linha que chega de outro computador.
    /// O que não está aqui recebe o vazio do tipo (nulo, zero, falso, texto
    /// vazio) — ou "agora", num instante obrigatório.
    /// </summary>
    public virtual IReadOnlyDictionary<string, object?> InsertDefaults { get; } = new Dictionary<string, object?>();

    public virtual Guid IdOf(EntityEntry entry) => (Guid)entry.Property("Id").CurrentValue!;

    /// <summary>Uma entidade que ainda não pode viajar (o cronômetro rodando).</summary>
    public virtual bool IsSyncable(EntityEntry entry) => true;

    /// <summary>
    /// Uma entidade que este computador escolheu não compartilhar (o comando
    /// global desmarcado, ADR-065). Ao contrário de <see cref="IsSyncable"/>,
    /// não é "ainda não": nada vai, nem a exclusão, e o que vem do servidor
    /// para ela é ignorado. Vale também para a entrada excluída.
    /// </summary>
    public virtual bool IsLocalOnly(EntityEntry entry) => false;

    public virtual DateTimeOffset? OriginCreatedAt(EntityEntry entry) =>
        Columns.Any(column => column.Field == SyncFields.CreatedAt)
            ? (DateTimeOffset?)entry.Property("CreatedAt").CurrentValue
            : null;

    /// <summary>Os campos compartilhados do estado atual da entidade.</summary>
    public Dictionary<string, JsonElement> Project(EntityEntry entry)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var group in Columns.GroupBy(column => column.Field))
        {
            var definition = Definition.FindField(group.Key)
                ?? throw new InvalidOperationException($"{Definition.Type}.{group.Key} não está no contrato.");

            if (group.All(column => column.Member is null))
            {
                fields[group.Key] = ToJson(Read(entry, group.Single().Property), definition.Kind);
                continue;
            }

            if (PresenceProperties.TryGetValue(group.Key, out var presence) && Read(entry, presence) is null)
            {
                fields[group.Key] = SyncValue.Null;
                continue;
            }

            fields[group.Key] = SyncValue.Object(
            [
                .. group.Select(column =>
                {
                    var member = definition.Members.Single(candidate => candidate.Name == column.Member);
                    return (member.Name, ToJson(Read(entry, column.Property), member.Kind));
                }),
            ]);
        }

        foreach (var child in Children)
        {
            fields[child.Field] = child.Project(entry.Entity);
        }

        return fields;
    }

    /// <summary>O valor de uma propriedade, atravessando o owned; owned ausente é <c>null</c>.</summary>
    public static object? Read(EntityEntry entry, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);

        if (dot < 0)
        {
            return entry.Property(path).CurrentValue;
        }

        var owned = entry.Reference(path[..dot]).TargetEntry;

        return owned?.Property(path[(dot + 1)..]).CurrentValue;
    }

    public static JsonElement ToJson(object? value, SyncFieldKind kind) => value is null
        ? SyncValue.Null
        : kind switch
        {
            SyncFieldKind.String => SyncValue.String((string)value),
            SyncFieldKind.Integer => SyncValue.Integer(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            SyncFieldKind.Boolean => SyncValue.Boolean((bool)value),
            SyncFieldKind.Instant => SyncValue.Instant((DateTimeOffset)value),
            SyncFieldKind.Date => SyncValue.Date((DateOnly)value),
            SyncFieldKind.Time => SyncValue.Time((TimeOnly)value),
            SyncFieldKind.Duration => SyncValue.Duration((TimeSpan)value),
            SyncFieldKind.Guid => SyncValue.Guid((Guid)value),
            _ => throw new InvalidOperationException($"O tipo {kind} não sai de uma coluna."),
        };
}
