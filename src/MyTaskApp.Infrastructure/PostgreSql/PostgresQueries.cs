using System.Globalization;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Toda SQL que o app envia a um servidor PostgreSQL (ADR-056). Só leitura,
/// só catálogo e contagem; um teste confere que cada uma começa com SELECT ou
/// WITH e não tem nada que altere dado, regra ou extensão. As poucas montadas
/// em tempo de execução (contagem, hash) citam os identificadores e passam
/// valores por parâmetro.
/// </summary>
internal static class PostgresQueries
{
    /// <summary>Os schemas do sistema e o do anon não são "do banco" para comparar.</summary>
    private const string UserSchemas =
        "n.nspname NOT LIKE 'pg\\_%' AND n.nspname NOT IN ('information_schema', 'anon')";

    public const string ServerInfo =
        "SELECT version(), current_user::text, current_database()::text, pg_database_size(current_database())";

    /// <summary>
    /// Os bancos em que se pode conectar (ADR-057), sem templates nem o de
    /// manutenção: o que a cópia oferece para escolher numa conexão só de servidor.
    /// </summary>
    public const string Databases =
        "SELECT datname::text FROM pg_database WHERE datallowconn AND NOT datistemplate AND datname <> 'postgres' ORDER BY 1";

    public const string Schemas =
        "SELECT n.nspname::text FROM pg_namespace n WHERE " + UserSchemas + " ORDER BY 1";

    public const string Tables =
        "SELECT n.nspname::text, c.relname::text, pg_total_relation_size(c.oid), greatest(c.reltuples, 0)::bigint " +
        "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind IN ('r', 'p') AND " + UserSchemas + " ORDER BY 1, 2";

    /// <summary>pg_read_all_data só existe do PostgreSQL 14 em diante: o CASE não chega a avaliá-lo antes.</summary>
    public const string Privileges =
        "SELECT r.rolsuper, r.rolcreatedb, r.rolconnlimit, " +
        "CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pg_read_all_data') " +
        "THEN pg_has_role(current_user, (SELECT oid FROM pg_roles WHERE rolname = 'pg_read_all_data'), 'MEMBER') ELSE false END, " +
        "(SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind IN ('r', 'p') AND " + UserSchemas + " AND NOT has_table_privilege(c.oid, 'SELECT')) " +
        "FROM pg_roles r WHERE r.rolname = current_user";

    public const string Columns =
        "SELECT n.nspname::text, c.relname::text, a.attname::text, format_type(a.atttypid, a.atttypmod), col_description(c.oid, a.attnum) " +
        "FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped AND " + UserSchemas + " " +
        "ORDER BY 1, 2, a.attnum";

    public const string ConstraintsByType =
        "SELECT con.contype::text, count(*) FROM pg_constraint con JOIN pg_namespace n ON n.oid = con.connamespace " +
        "WHERE " + UserSchemas + " GROUP BY 1 ORDER BY 1";

    public const string IndexCount =
        "SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE " + UserSchemas;

    public const string SequenceCount =
        "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'S' AND " + UserSchemas;

    /// <summary>$1 = "schema"."tabela" citado.</summary>
    public const string PrimaryKey =
        "SELECT a.attname::text FROM pg_index i " +
        "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey) " +
        "WHERE i.indrelid = to_regclass($1) AND i.indisprimary " +
        "ORDER BY array_position(i.indkey::int2[], a.attnum)";

    /// <summary>
    /// As tabelas com linhas próprias (<c>relkind 'r'</c>: as comuns e as
    /// partições), com a tabela particionada de cima de cada partição (ADR-058).
    /// </summary>
    public const string CopyTables =
        "SELECT n.nspname::text, c.relname::text, pg_total_relation_size(c.oid), greatest(c.reltuples, 0)::bigint, " +
        "CASE WHEN c.relispartition THEN (SELECT rn.nspname::text || '.' || rc.relname::text FROM pg_class rc " +
        "JOIN pg_namespace rn ON rn.oid = rc.relnamespace WHERE rc.oid = pg_partition_root(c.oid)) END " +
        "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind = 'r' AND " + UserSchemas + " ORDER BY 1, 2";

    /// <summary>As colunas dessas tabelas: tipo, se aceita NULL e se é gerada.</summary>
    public const string CopyColumns =
        "SELECT n.nspname::text, c.relname::text, a.attname::text, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, a.attgenerated <> '' " +
        "FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped AND " + UserSchemas + " " +
        "ORDER BY 1, 2, a.attnum";

    /// <summary>
    /// As colunas que são chave: <c>p</c> primária, <c>u</c> em índice único,
    /// <c>f</c> que referencia outra tabela, <c>r</c> referenciada por uma FK.
    /// </summary>
    public const string KeyColumns =
        "SELECT n.nspname::text, c.relname::text, a.attname::text, CASE WHEN i.indisprimary THEN 'p' ELSE 'u' END " +
        "FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey) " +
        "WHERE i.indisunique AND " + UserSchemas + " " +
        "UNION ALL " +
        "SELECT n.nspname::text, c.relname::text, a.attname::text, 'f' " +
        "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY (con.conkey) " +
        "WHERE con.contype = 'f' AND " + UserSchemas + " " +
        "UNION ALL " +
        "SELECT n.nspname::text, c.relname::text, a.attname::text, 'r' " +
        "FROM pg_constraint con JOIN pg_class c ON c.oid = con.confrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = ANY (con.confkey) " +
        "WHERE con.contype = 'f' AND " + UserSchemas;

    /// <summary>Quem referencia quem, tabela a tabela: uma tabela sem dados não pode ser referenciada por uma com dados.</summary>
    public const string ForeignKeyTables =
        "SELECT DISTINCT n.nspname::text || '.' || c.relname::text, tn.nspname::text || '.' || tc.relname::text " +
        "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN pg_class tc ON tc.oid = con.confrelid JOIN pg_namespace tn ON tn.oid = tc.relnamespace " +
        "WHERE con.contype = 'f' AND con.conrelid <> con.confrelid AND " + UserSchemas;

    /// <summary>Objetos grandes não vão por COPY: a cópia mascarada avisa que ficam de fora.</summary>
    public const string LargeObjects = "SELECT count(*) FROM pg_largeobject_metadata";

    /// <summary>O valor atual de cada sequence; <c>NULL</c> quando nunca foi usada ou falta permissão.</summary>
    public const string SequenceValues =
        "SELECT schemaname::text, sequencename::text, last_value FROM pg_sequences " +
        "WHERE schemaname NOT LIKE 'pg\\_%' AND schemaname <> 'information_schema' AND last_value IS NOT NULL";

    /// <summary>A foto da transação de leitura, para o <c>pg_dump --snapshot</c> ver o mesmo instante.</summary>
    public const string ExportSnapshot = "SELECT pg_export_snapshot()";

    /// <summary>Estimativa do catálogo, para decidir se vale contar de verdade. $1 = tabela citada.</summary>
    public const string EstimatedRows =
        "SELECT greatest(reltuples, 0)::bigint FROM pg_class WHERE oid = to_regclass($1)";

    public static IEnumerable<string> All() =>
    [
        ServerInfo, Databases, Schemas, Tables, Privileges, Columns, ConstraintsByType, IndexCount, SequenceCount,
        PrimaryKey, CopyTables, CopyColumns, KeyColumns, ForeignKeyTables, LargeObjects, SequenceValues, ExportSnapshot, EstimatedRows,
        CountRows("public.x"),
        Fingerprint(new ColumnReference("public", "x", "y"), ["id"], 10),
    ];

    /// <summary>"public.clientes" → "public"."clientes", aspas dobradas por dentro.</summary>
    public static string QuoteTable(string qualified)
    {
        var dot = qualified.IndexOf('.', StringComparison.Ordinal);
        return dot < 0
            ? SqlQuoting.QuoteIdentifier(qualified)
            : $"{SqlQuoting.QuoteIdentifier(qualified[..dot])}.{SqlQuoting.QuoteIdentifier(qualified[(dot + 1)..])}";
    }

    public static string CountRows(string qualifiedTable) => $"SELECT count(*) FROM {QuoteTable(qualifiedTable)}";

    /// <summary>
    /// Os pares salgados de uma coluna: $1 = o sal. O valor fica no servidor;
    /// só o hash, com sal novo a cada execução, chega ao app.
    /// </summary>
    public static string Fingerprint(ColumnReference column, IReadOnlyList<string> primaryKey, int limit)
    {
        var key = string.Join(", ", primaryKey.Select(SqlQuoting.QuoteIdentifier));
        var table = $"{SqlQuoting.QuoteIdentifier(column.Schema)}.{SqlQuoting.QuoteIdentifier(column.Table)}";
        var value = SqlQuoting.QuoteIdentifier(column.Column);

        return $"SELECT md5($1 || ROW({key})::text), md5($1 || ({value})::text) FROM {table} " +
               $"ORDER BY {key} LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
    }
}
