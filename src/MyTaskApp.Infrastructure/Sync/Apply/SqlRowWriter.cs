using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync.Apply;

/// <summary>Um valor do servidor que este computador não sabe guardar.</summary>
internal sealed class RemoteValueException(string message) : Exception(message);

/// <summary>
/// Escreve uma linha que veio do servidor direto nas colunas, por SQL com
/// parâmetros (ADR-060). Os nomes das colunas e os conversores (ticks em UTC,
/// enum como número) saem do próprio modelo do EF, então o formato no disco é
/// o mesmo que o EF gravaria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que não pelo domínio.</b> As regras locais recusariam um estado
/// legítimo que chegou de fora — editar uma tarefa arquivada, reagendar uma
/// ocorrência concluída —, e criar a linha pediria construtores que o domínio
/// não expõe. A regra que vale para o que vem de fora é a do contrato, conferida
/// pelo servidor e de novo aqui.
/// </para>
/// <para>
/// <b>Só as colunas do mapper são escritas.</b> As locais (o caminho do
/// diretório, a posição na lista, a janela do post-it) não estão no mapa: não
/// há como um valor de outro computador sobrescrevê-las. Numa linha nova, elas
/// recebem o padrão local.
/// </para>
/// <para>
/// <b>SQL não passa pelo <c>SaveChanges</c></b>, e portanto não passa pela
/// captura: aplicar o que veio de fora não gera operação de volta.
/// </para>
/// </remarks>
internal sealed class SqlRowWriter(MyTaskAppDbContext context, TimeProvider timeProvider)
{
    private const string IdProperty = "Id";

    public async Task<bool> ExistsAsync(
        SyncEntityMapper mapper,
        Guid id,
        IReadOnlyDictionary<string, JsonElement> keyFields,
        CancellationToken cancellationToken)
    {
        var entity = Entity(mapper);
        var (where, parameters) = KeyCondition(mapper, entity, id, keyFields);

        return await ScalarAsync($"SELECT 1 FROM {Quote(entity.GetTableName()!)} WHERE {where} LIMIT 1", parameters, cancellationToken) is not null;
    }

    public async Task InsertAsync(
        SyncEntityMapper mapper,
        Guid id,
        IReadOnlyDictionary<string, JsonElement> fields,
        CancellationToken cancellationToken)
    {
        var entity = Entity(mapper);
        var values = new Dictionary<string, (IProperty Property, object? Value)>(StringComparer.Ordinal);

        if (entity.FindProperty(IdProperty) is { } idProperty && idProperty.IsPrimaryKey())
        {
            values[IdProperty] = (idProperty, id);
        }

        foreach (var (column, property, value) in SharedValues(mapper, entity, fields, onlyPresent: false))
        {
            values[column.Property] = (property, value);
        }

        var now = timeProvider.GetUtcNow();

        foreach (var path in mapper.LocalProperties)
        {
            var property = FindProperty(entity, path);
            values[path] = (property, mapper.InsertDefaults.TryGetValue(path, out var preset) ? preset : EmptyOf(property, now));
        }

        await InsertRowAsync(entity, values.Values, cancellationToken);
        await ReplaceChildrenAsync(mapper, id, fields, cancellationToken);
    }

    /// <summary>Só os campos dados; os outros ficam como estão.</summary>
    public async Task UpdateAsync(
        SyncEntityMapper mapper,
        Guid id,
        IReadOnlyDictionary<string, JsonElement> fields,
        IReadOnlyDictionary<string, JsonElement> keyFields,
        CancellationToken cancellationToken)
    {
        var entity = Entity(mapper);
        var assignments = SharedValues(mapper, entity, fields, onlyPresent: true).ToList();

        if (assignments.Count > 0)
        {
            var parameters = new List<(IProperty?, object?)>();
            var set = new StringBuilder();

            foreach (var (_, property, value) in assignments)
            {
                set.Append(set.Length == 0 ? string.Empty : ", ")
                    .Append(Quote(property.GetColumnName()))
                    .Append(" = @p").Append(parameters.Count);
                parameters.Add((property, value));
            }

            var (where, keyParameters) = KeyCondition(mapper, entity, id, keyFields, parameters.Count);
            parameters.AddRange(keyParameters);

            await ExecuteAsync($"UPDATE {Quote(entity.GetTableName()!)} SET {set} WHERE {where}", parameters, cancellationToken);
        }

        await ReplaceChildrenAsync(mapper, id, fields, cancellationToken);
    }

    /// <summary>Apaga a linha; o banco leva os filhos pela cascata, como a exclusão local.</summary>
    public async Task<int> DeleteAsync(
        SyncEntityMapper mapper,
        Guid id,
        IReadOnlyDictionary<string, JsonElement> keyFields,
        CancellationToken cancellationToken)
    {
        var entity = Entity(mapper);
        var (where, parameters) = KeyCondition(mapper, entity, id, keyFields);

        return await ExecuteAsync($"DELETE FROM {Quote(entity.GetTableName()!)} WHERE {where}", parameters, cancellationToken);
    }

    /// <summary>
    /// Outra linha local com a mesma chave única (nome da etiqueta, alias).
    /// A comparação segue a collation da coluna (<c>NOCASE</c>), como o índice.
    /// </summary>
    public async Task<Guid?> FindCollisionAsync(
        SyncEntityMapper mapper,
        Guid id,
        IReadOnlyDictionary<string, JsonElement> fields,
        CancellationToken cancellationToken)
    {
        if (mapper.Definition.UniqueKeyFields.Count == 0 || mapper == SyncMappers.TaskTag)
        {
            return null;
        }

        var entity = Entity(mapper);
        var parameters = new List<(IProperty?, object?)>();
        var where = new StringBuilder();

        foreach (var field in mapper.Definition.UniqueKeyFields)
        {
            if (!fields.TryGetValue(field, out var value) || SyncValue.IsNull(value))
            {
                return null;
            }

            var property = FindProperty(entity, mapper.Columns.Single(column => column.Field == field && column.Member is null).Property);
            where.Append(Quote(property.GetColumnName())).Append(" = @p").Append(parameters.Count).Append(" AND ");
            parameters.Add((property, ToClr(value, property)));
        }

        where.Append(Quote(IdColumn(entity))).Append(" <> @p").Append(parameters.Count);
        parameters.Add((entity.FindProperty(IdProperty), id));

        var found = await ScalarAsync(
            $"SELECT {Quote(IdColumn(entity))} FROM {Quote(entity.GetTableName()!)} WHERE {where} LIMIT 1",
            parameters,
            cancellationToken);

        return found is null ? null : ReadGuid(found);
    }

    /// <summary>
    /// Tira uma linha local do caminho de outra que chegou com a mesma chave:
    /// <c>Casa (conflito)</c>, <c>@api-conflito</c>. Só local — a operação
    /// pendente dela leva o valor original, e o servidor responde.
    /// </summary>
    public Task RenameAsideAsync(SyncEntityMapper mapper, Guid id, CancellationToken cancellationToken)
    {
        var entity = Entity(mapper);
        var table = Quote(entity.GetTableName()!);
        var key = Quote(IdColumn(entity));
        var idParameter = new List<(IProperty?, object?)> { (entity.FindProperty(IdProperty), id) };

        if (mapper == SyncMappers.Tag)
        {
            return ExecuteAsync($"UPDATE {table} SET \"Name\" = substr(\"Name\", 1, 29) || ' (conflito)' WHERE {key} = @p0", idParameter, cancellationToken);
        }

        if (mapper == SyncMappers.TagDirectory || mapper == SyncMappers.Command)
        {
            return ExecuteAsync($"UPDATE {table} SET \"Alias\" = substr(\"Alias\", 1, 31) || '-conflito' WHERE {key} = @p0", idParameter, cancellationToken);
        }

        // O mesmo comando duas vezes no mesmo diretório: a cópia local sai.
        return ExecuteAsync($"DELETE FROM {table} WHERE {key} = @p0", idParameter, cancellationToken);
    }

    /// <summary>
    /// Troca a referência de <paramref name="from"/> para <paramref name="to"/>
    /// numa tabela filha. O que colidir com uma linha que já aponta para o
    /// destino fica para trás e é devolvido para quem chamou decidir.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RepointAsync(
        SyncEntityMapper child,
        string field,
        Guid from,
        Guid to,
        CancellationToken cancellationToken)
    {
        var entity = Entity(child);
        var property = FindProperty(entity, child.Columns.Single(column => column.Field == field && column.Member is null).Property);
        var table = Quote(entity.GetTableName()!);
        var column = Quote(property.GetColumnName());

        await ExecuteAsync(
            $"UPDATE OR IGNORE {table} SET {column} = @p0 WHERE {column} = @p1",
            [(property, to), (property, from)],
            cancellationToken);

        if (child == SyncMappers.TaskTag)
        {
            // Vínculo repetido é o mesmo vínculo: some.
            await ExecuteAsync($"DELETE FROM {table} WHERE {column} = @p0", [(property, from)], cancellationToken);
            return [];
        }

        var leftovers = new List<Guid>();
        var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, $"SELECT {Quote(IdColumn(entity))} FROM {table} WHERE {column} = @p0", [(property, from)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            leftovers.Add(ReadGuid(reader.GetValue(0)));
        }

        return leftovers;
    }

    /// <summary>Copia colunas locais de uma linha para outra (o caminho do diretório fundido).</summary>
    public Task CopyLocalAsync(SyncEntityMapper mapper, Guid from, Guid to, string path, CancellationToken cancellationToken)
    {
        var entity = Entity(mapper);
        var table = Quote(entity.GetTableName()!);
        var column = Quote(FindProperty(entity, path).GetColumnName());
        var key = Quote(IdColumn(entity));
        var idProperty = entity.FindProperty(IdProperty);

        return ExecuteAsync(
            $"UPDATE {table} SET {column} = (SELECT {column} FROM {table} WHERE {key} = @p0) WHERE {key} = @p1 AND {column} IS NULL",
            [(idProperty, from), (idProperty, to)],
            cancellationToken);
    }

    /// <summary>O vínculo tarefa↔etiqueta pelo id do contrato, quando não se sabe o par.</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>?> FindTaskTagAsync(Guid id, CancellationToken cancellationToken)
    {
        var pairs = await context.TaskItemTags.AsNoTracking()
            .Select(link => new { link.TaskItemId, link.TagId })
            .ToListAsync(cancellationToken);

        var match = pairs.FirstOrDefault(pair => SyncIds.TaskTag(pair.TaskItemId, pair.TagId) == id);

        return match is null
            ? null
            : new Dictionary<string, JsonElement>
            {
                [SyncFields.TaskTag.TaskId] = SyncValue.Guid(match.TaskItemId),
                [SyncFields.TaskTag.TagId] = SyncValue.Guid(match.TagId),
            };
    }

    private IEntityType Entity(SyncEntityMapper mapper) => context.Model.FindEntityType(mapper.ClrType)!;

    private static string IdColumn(IEntityType entity) => entity.FindProperty(IdProperty)!.GetColumnName();

    /// <summary>A linha pela chave: o id, ou o par do vínculo tarefa↔etiqueta.</summary>
    private static (string Where, List<(IProperty?, object?)> Parameters) KeyCondition(
        SyncEntityMapper mapper,
        IEntityType entity,
        Guid id,
        IReadOnlyDictionary<string, JsonElement> keyFields,
        int offset = 0)
    {
        if (mapper != SyncMappers.TaskTag)
        {
            return ($"{Quote(IdColumn(entity))} = @p{offset}", [(entity.FindProperty(IdProperty), id)]);
        }

        var task = entity.FindProperty("TaskItemId")!;
        var tag = entity.FindProperty("TagId")!;

        if (!keyFields.TryGetValue(SyncFields.TaskTag.TaskId, out var taskValue)
            || !keyFields.TryGetValue(SyncFields.TaskTag.TagId, out var tagValue))
        {
            throw new RemoteValueException("O vínculo tarefa↔etiqueta chegou sem as pontas.");
        }

        return (
            $"{Quote(task.GetColumnName())} = @p{offset} AND {Quote(tag.GetColumnName())} = @p{offset + 1}",
            [(task, SyncValue.ReadGuid(taskValue)), (tag, SyncValue.ReadGuid(tagValue))]);
    }

    /// <summary>As colunas compartilhadas com o valor do fio já convertido para o tipo da propriedade.</summary>
    private static IEnumerable<(SyncColumn Column, IProperty Property, object? Value)> SharedValues(
        SyncEntityMapper mapper,
        IEntityType entity,
        IReadOnlyDictionary<string, JsonElement> fields,
        bool onlyPresent)
    {
        foreach (var column in mapper.Columns)
        {
            var present = fields.TryGetValue(column.Field, out var value);

            if (!present && onlyPresent)
            {
                continue;
            }

            var source = present ? value : SyncValue.Null;

            // Owned opcional ausente (sem vínculo com o Jira): as colunas dele
            // ficam nulas, mesmo as que são obrigatórias dentro do objeto.
            var absentObject = column.Member is not null && SyncValue.IsNull(source);

            if (column.Member is not null)
            {
                source = SyncValue.Member(source, column.Member);
            }

            var property = FindProperty(entity, column.Property);
            yield return (column, property, absentObject ? null : ToClr(source, property));
        }
    }

    private async Task ReplaceChildrenAsync(
        SyncEntityMapper mapper,
        Guid parentId,
        IReadOnlyDictionary<string, JsonElement> fields,
        CancellationToken cancellationToken)
    {
        foreach (var child in mapper.Children)
        {
            if (!fields.TryGetValue(child.Field, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var entity = context.Model.FindEntityType(child.ChildType)!;
            var parentKey = entity.FindProperty(child.ParentKey)!;

            await ExecuteAsync(
                $"DELETE FROM {Quote(entity.GetTableName()!)} WHERE {Quote(parentKey.GetColumnName())} = @p0",
                [(parentKey, parentId)],
                cancellationToken);

            var index = 0;

            foreach (var item in list.EnumerateArray())
            {
                var row = child.Row(item, index++, parentId);
                await InsertRowAsync(
                    entity,
                    row.Select(pair => (entity.FindProperty(pair.Key)!, pair.Value)),
                    cancellationToken);
            }
        }
    }

    private Task InsertRowAsync(
        IEntityType entity,
        IEnumerable<(IProperty Property, object? Value)> values,
        CancellationToken cancellationToken)
    {
        var list = values.ToList();
        var columns = string.Join(", ", list.Select(value => Quote(value.Property.GetColumnName())));
        var placeholders = string.Join(", ", list.Select((_, index) => $"@p{index}"));

        return ExecuteAsync(
            $"INSERT INTO {Quote(entity.GetTableName()!)} ({columns}) VALUES ({placeholders})",
            [.. list.Select(value => ((IProperty?)value.Property, value.Value))],
            cancellationToken);
    }

    private async Task<int> ExecuteAsync(string sql, IReadOnlyList<(IProperty?, object?)> parameters, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql, IReadOnlyList<(IProperty?, object?)> parameters, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return context.Database.GetDbConnection();
    }

    private DbCommand CreateCommand(DbConnection connection, string sql, IReadOnlyList<(IProperty?, object?)> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        for (var index = 0; index < parameters.Count; index++)
        {
            var (property, value) = parameters[index];
            var name = $"@p{index}";

            if (property?.GetRelationalTypeMapping() is { } mapping)
            {
                command.Parameters.Add(mapping.CreateParameter(command, name, value, property.IsNullable));
            }
            else
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        return command;
    }

    private static IProperty FindProperty(IEntityType entity, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);

        return dot < 0
            ? entity.FindProperty(path) ?? throw new InvalidOperationException($"{entity.ClrType.Name}.{path} não existe.")
            : entity.FindNavigation(path[..dot])!.TargetEntityType.FindProperty(path[(dot + 1)..])
                ?? throw new InvalidOperationException($"{entity.ClrType.Name}.{path} não existe.");
    }

    /// <summary>O valor do fio no tipo CLR da propriedade; enum fora da lista é recusado.</summary>
    private static object? ToClr(JsonElement value, IProperty property)
    {
        if (SyncValue.IsNull(value))
        {
            if (!property.IsNullable)
            {
                throw new RemoteValueException($"{property.DeclaringType.ClrType.Name}.{property.Name} não aceita vazio.");
            }

            return null;
        }

        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        try
        {
            if (type.IsEnum)
            {
                var number = value.GetInt64();
                var parsed = Enum.ToObject(type, number);

                if (!type.IsDefined(typeof(FlagsAttribute), inherit: false) && !Enum.IsDefined(type, parsed))
                {
                    throw new RemoteValueException($"{type.Name} {number} não existe nesta versão do app.");
                }

                return parsed;
            }

            return type switch
            {
                _ when type == typeof(string) => value.GetString(),
                _ when type == typeof(bool) => value.GetBoolean(),
                _ when type == typeof(int) => checked((int)value.GetInt64()),
                _ when type == typeof(long) => value.GetInt64(),
                _ when type == typeof(double) => value.GetDouble(),
                _ when type == typeof(Guid) => SyncValue.ReadGuid(value),
                _ when type == typeof(DateTimeOffset) => SyncValue.ReadInstant(value),
                _ when type == typeof(DateOnly) => SyncValue.ReadDate(value),
                _ when type == typeof(TimeOnly) => SyncValue.ReadTime(value),
                _ when type == typeof(TimeSpan) => SyncValue.ReadDuration(value),
                _ => throw new RemoteValueException($"O tipo {type.Name} não vem do fio."),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            throw new RemoteValueException($"{property.DeclaringType.ClrType.Name}.{property.Name}: valor fora do formato.");
        }
    }

    /// <summary>O vazio de uma coluna local numa linha nova.</summary>
    private static object? EmptyOf(IProperty property, DateTimeOffset now)
    {
        if (property.IsNullable)
        {
            return null;
        }

        var type = property.ClrType;

        return type == typeof(string) ? string.Empty
            : type == typeof(DateTimeOffset) ? now
            : Activator.CreateInstance(type);
    }

    private static Guid ReadGuid(object value) => value switch
    {
        Guid guid => guid,
        string text => Guid.Parse(text),
        byte[] bytes => new Guid(bytes),
        _ => throw new InvalidOperationException($"Id ilegível: {value.GetType().Name}."),
    };

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
