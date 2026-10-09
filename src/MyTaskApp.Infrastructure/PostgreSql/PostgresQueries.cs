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

    public const string AnonymizerAvailable =
        "SELECT default_version, installed_version FROM pg_available_extensions WHERE name = 'anon'";

    public const string TransparentMasking =
        "SELECT coalesce(current_setting('anon.transparent_dynamic_masking', true), '')";

    /// <summary>
    /// $1 = o provedor da política (<c>anon</c>). Role é objeto compartilhado
    /// do cluster: o rótulo dela fica em <c>pg_shseclabel</c>, não em <c>pg_seclabel</c>.
    /// </summary>
    public const string RoleIsMasked =
        "SELECT EXISTS (SELECT 1 FROM pg_shseclabel s JOIN pg_roles r ON r.oid = s.objoid " +
        "WHERE s.classoid = 'pg_authid'::regclass AND s.provider = $1 AND r.rolname = current_user AND upper(s.label) = 'MASKED')";

    public const string AnonymizerFunctions =
        "SELECT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
        "WHERE n.nspname = 'anon' AND has_function_privilege(p.oid, 'EXECUTE'))";

    /// <summary>As regras por coluna. $1 = o provedor. Lido do catálogo base, igual no 1.x e no 2.x.</summary>
    public const string MaskingRules =
        "SELECT n.nspname::text, c.relname::text, a.attname::text, s.label FROM pg_seclabel s " +
        "JOIN pg_class c ON s.classoid = 'pg_class'::regclass AND s.objoid = c.oid " +
        "JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = s.objsubid " +
        "WHERE s.provider = $1 AND s.objsubid > 0 ORDER BY 1, 2, 3";

    /// <summary>Estimativa do catálogo, para decidir se vale contar de verdade. $1 = tabela citada.</summary>
    public const string EstimatedRows =
        "SELECT greatest(reltuples, 0)::bigint FROM pg_class WHERE oid = to_regclass($1)";

    public static IEnumerable<string> All() =>
    [
        ServerInfo, Databases, Schemas, Tables, Privileges, Columns, ConstraintsByType, IndexCount, SequenceCount,
        PrimaryKey, AnonymizerAvailable, TransparentMasking, RoleIsMasked, AnonymizerFunctions, MaskingRules, EstimatedRows,
        CountRows("public.x"),
        Fingerprint(new ColumnReference("public", "x", "y"), ["id"], 10),
    ];

    /// <summary>"public.clientes" → "public"."clientes", aspas dobradas por dentro.</summary>
    public static string QuoteTable(string qualified)
    {
        var dot = qualified.IndexOf('.', StringComparison.Ordinal);
        return dot < 0
            ? MaskingScriptBuilder.QuoteIdentifier(qualified)
            : $"{MaskingScriptBuilder.QuoteIdentifier(qualified[..dot])}.{MaskingScriptBuilder.QuoteIdentifier(qualified[(dot + 1)..])}";
    }

    public static string CountRows(string qualifiedTable) => $"SELECT count(*) FROM {QuoteTable(qualifiedTable)}";

    /// <summary>
    /// Os pares salgados de uma coluna: $1 = o sal. O valor fica no servidor;
    /// só o hash, com sal novo a cada execução, chega ao app.
    /// </summary>
    public static string Fingerprint(ColumnReference column, IReadOnlyList<string> primaryKey, int limit)
    {
        var key = string.Join(", ", primaryKey.Select(MaskingScriptBuilder.QuoteIdentifier));
        var table = $"{MaskingScriptBuilder.QuoteIdentifier(column.Schema)}.{MaskingScriptBuilder.QuoteIdentifier(column.Table)}";
        var value = MaskingScriptBuilder.QuoteIdentifier(column.Column);

        return $"SELECT md5($1 || ROW({key})::text), md5($1 || ({value})::text) FROM {table} " +
               $"ORDER BY {key} LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
    }
}
