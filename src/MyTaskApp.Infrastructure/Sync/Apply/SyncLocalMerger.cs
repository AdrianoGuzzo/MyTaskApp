using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync.Apply;

/// <summary>
/// Funde uma linha local na que veio do servidor com a mesma chave única
/// (ADR-061): a etiqueta "Casa" criada nos dois computadores antes de
/// conectar vira uma só. Os vínculos e diretórios passam a apontar para a
/// remota, o caminho local do diretório vai junto, e o que diferia nos valores
/// compartilhados vira conflito visível.
/// </summary>
internal sealed class SyncLocalMerger(MyTaskAppDbContext context, SqlRowWriter writer, TimeProvider timeProvider)
{
    /// <summary>As colunas locais que acompanham a fusão: a pasta já mapeada aqui.</summary>
    private static readonly Dictionary<SyncEntityMapper, string[]> CarriedOnMerge = new()
    {
        [SyncMappers.TagDirectory] = ["Path"],
    };

    /// <param name="remoteFields">O estado do servidor, para registrar o que diferia.</param>
    public async Task MergeAsync(
        SyncEntityMapper mapper,
        Guid localId,
        Guid remoteId,
        IReadOnlyDictionary<string, JsonElement> remoteFields,
        CancellationToken cancellationToken)
    {
        await RecordDifferencesAsync(mapper, localId, remoteId, remoteFields, cancellationToken);

        foreach (var path in CarriedOnMerge.GetValueOrDefault(mapper, []))
        {
            await writer.CopyLocalAsync(mapper, localId, remoteId, path, cancellationToken);
        }

        foreach (var child in SyncMappers.All)
        {
            foreach (var reference in child.Definition.References.Where(reference => reference.TargetType == mapper.Definition.Type))
            {
                var leftovers = await writer.RepointAsync(child, reference.Field, localId, remoteId, cancellationToken);

                // Colidiu com um filho que já é da remota (o mesmo alias): o
                // local também se funde, e só então troca de pai.
                foreach (var leftover in leftovers)
                {
                    await writer.RenameAsideAsync(child, leftover, cancellationToken);
                    await writer.RepointAsync(child, reference.Field, localId, remoteId, cancellationToken);
                }
            }
        }

        await writer.DeleteAsync(mapper, localId, new Dictionary<string, JsonElement>(), cancellationToken);
    }

    private async Task RecordDifferencesAsync(
        SyncEntityMapper mapper,
        Guid localId,
        Guid remoteId,
        IReadOnlyDictionary<string, JsonElement> remoteFields,
        CancellationToken cancellationToken)
    {
        if (await context.FindAsync(mapper.ClrType, [localId], cancellationToken) is not { } local)
        {
            return;
        }

        var entry = context.Entry(local);
        var projection = mapper.Project(entry);
        entry.State = EntityState.Detached;

        var now = timeProvider.GetUtcNow();

        foreach (var (field, localValue) in projection)
        {
            var definition = mapper.Definition.FindField(field);

            if (definition is null
                || definition.Policy == SyncConflictPolicy.Silent
                || field == SyncFields.CreatedAt
                || !remoteFields.TryGetValue(field, out var remoteValue)
                || SyncValue.AreEqual(localValue, remoteValue))
            {
                continue;
            }

            context.SyncConflicts.Add(new SyncConflictRow
            {
                Id = Guid.CreateVersion7(now),
                EntityType = mapper.Definition.Type,
                EntityId = remoteId,
                Field = field,
                LocalValue = localValue.GetRawText(),
                ServerValue = remoteValue.GetRawText(),
                Reason = SyncConflictReasons.MergedOnJoin,
                EntityLabel = LabelOf(projection),
                DetectedAt = now,
            });
        }
    }

    public static string? LabelOf(IReadOnlyDictionary<string, JsonElement> fields)
    {
        foreach (var name in new[] { SyncFields.Task.Title, SyncFields.Tag.Name, SyncFields.TagDirectory.Alias, SyncFields.StickyNote.Content })
        {
            if (fields.TryGetValue(name, out var value) && SyncValue.ReadString(value) is { Length: > 0 } text)
            {
                return text.Length > 200 ? text[..200] : text;
            }
        }

        return null;
    }
}

/// <summary>Por que um valor local ficou de fora.</summary>
internal static class SyncConflictReasons
{
    /// <summary>O mesmo campo mudou em outro dispositivo antes.</summary>
    public const string ConcurrentChange = SyncErrorCodes.ConcurrentChange;

    /// <summary>O valor colide com outra entidade (nome, alias).</summary>
    public const string UniqueViolation = SyncErrorCodes.UniqueViolation;

    /// <summary>A entidade foi excluída em outro dispositivo.</summary>
    public const string EntityDeleted = SyncErrorCodes.EntityDeleted;

    /// <summary>O pai foi excluído em outro dispositivo.</summary>
    public const string ParentDeleted = SyncErrorCodes.ParentDeleted;

    /// <summary>Duas entidades com a mesma chave viraram uma só na conexão.</summary>
    public const string MergedOnJoin = "merged_on_join";
}
