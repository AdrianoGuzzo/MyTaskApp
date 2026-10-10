using System.Globalization;
using System.Text.RegularExpressions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>As ferramentas de linha de comando do PostgreSQL que o app usa (ADR-056).</summary>
public enum PostgresTool
{
    Psql = 1,
    PgDump = 2,
    PgRestore = 3,
    PgIsReady = 4,
    CreateDb = 5,
    DropDb = 6,
}

/// <summary>Uma versão do PostgreSQL ou de uma ferramenta dele. Só major e minor contam para compatibilidade.</summary>
public sealed partial record PostgresVersion(int Major, int Minor) : IComparable<PostgresVersion>
{
    /// <summary>
    /// "pg_dump (PostgreSQL) 17.2", "PostgreSQL 14.10 on x86_64…", "17.2 (Debian 17.2-1)",
    /// "18beta1" — o primeiro número com ponto (ou o major sozinho) que aparecer.
    /// </summary>
    public static PostgresVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionPattern().Match(text);

        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
        return new PostgresVersion(major, minor);
    }

    public int CompareTo(PostgresVersion? other) =>
        other is null ? 1 : Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";

    [GeneratedRegex(@"(?<![\d.])(?<major>\d{1,3})(?:\.(?<minor>\d{1,3}))?(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>O que se achou de uma ferramenta: onde, e que versão ela diz ser.</summary>
public sealed record PostgresToolStatus(PostgresTool Tool, string Name, string? Path, PostgresVersion? Version, string? VersionText)
{
    public bool Found => Path is not null;
}

/// <summary>Como instalar o que falta, no sistema atual. Só instruções: o app nunca instala nada.</summary>
public sealed record PostgresInstallGuide(string Platform, IReadOnlyList<string> Steps, IReadOnlyList<string> CheckCommands);

/// <summary>As ferramentas locais, todas — achadas ou não.</summary>
public sealed record PostgresClientTools(IReadOnlyList<PostgresToolStatus> Tools, PostgresInstallGuide Guide)
{
    public PostgresToolStatus Find(PostgresTool tool) =>
        Tools.FirstOrDefault(status => status.Tool == tool)
        ?? new PostgresToolStatus(tool, PostgresToolNames.Of(tool), null, null, null);

    /// <summary>
    /// Cada conjunto instalado — a pasta de um <c>pg_dump</c> com versão lida —,
    /// do mais novo ao mais antigo. <see cref="Tools"/> é o primeiro: o do diagnóstico.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<PostgresToolStatus>> Sets { get; init; } = [];

    public bool AllFound => Tools.All(status => status.Found);

    /// <summary>
    /// O conjunto para copiar de um servidor: o de <b>menor</b> versão que ainda
    /// lê a origem. O mais novo nem sempre serve: o <c>pg_restore</c> 17+ manda
    /// <c>SET transaction_timeout</c>, que um servidor 16 ou mais antigo recusa —
    /// e um pgAdmin 18 ao lado de um PostgreSQL 14 é comum. Sem a versão da
    /// origem, ou sem conjunto que a leia, fica o mais novo.
    /// </summary>
    public PostgresClientTools ForSource(PostgresVersion? source)
    {
        if (source is null)
        {
            return this;
        }

        var fitting = Sets
            .Select(set => (Set: set, Version: set.FirstOrDefault(status => status.Tool == PostgresTool.PgDump)?.Version))
            .Where(candidate => candidate.Version is { } version && version.Major >= source.Major)
            .OrderBy(candidate => candidate.Version)
            .Select(candidate => candidate.Set)
            .FirstOrDefault();

        return fitting is null ? this : this with { Tools = fitting };
    }

    public IReadOnlyList<string> Missing => Tools.Where(status => !status.Found).Select(status => status.Name).ToList();

    /// <summary>"pg_dump 17.2; pg_restore 17.2" — para a auditoria.</summary>
    public string Describe(params PostgresTool[] tools) =>
        string.Join("; ", tools.Select(Find).Where(status => status.Found)
            .Select(status => $"{status.Name} {status.Version?.ToString() ?? "?"}"));
}

public static class PostgresToolNames
{
    public static string Of(PostgresTool tool) => tool switch
    {
        PostgresTool.Psql => "psql",
        PostgresTool.PgDump => "pg_dump",
        PostgresTool.PgRestore => "pg_restore",
        PostgresTool.PgIsReady => "pg_isready",
        PostgresTool.CreateDb => "createdb",
        PostgresTool.DropDb => "dropdb",
        _ => tool.ToString(),
    };
}

/// <summary>O que o servidor respondeu sobre a conexão, o banco e o usuário. Sem dado de tabela nenhum.</summary>
public sealed record ServerDiagnostics(
    bool Connected,
    string? Error,
    string? ServerVersionText,
    PostgresVersion? ServerVersion,
    string? CurrentUser,
    string? Database,
    long? DatabaseSizeBytes,
    IReadOnlyList<string> Schemas,
    int TableCount,
    ServerPrivileges Privileges)
{
    public static ServerDiagnostics Failed(string error) =>
        new(false, error, null, null, null, null, null, [], 0, ServerPrivileges.None);
}

/// <summary>
/// O que o usuário da conexão pode. <see cref="TablesWithoutSelect"/> &gt; 0 é
/// o dump que vai falhar no meio por "permission denied".
/// </summary>
public sealed record ServerPrivileges(
    bool IsSuperuser,
    bool CanCreateDatabase,
    bool CanReadAllData,
    int TablesWithoutSelect,
    int ConnectionLimit)
{
    public static ServerPrivileges None { get; } = new(false, false, false, 0, -1);
}

/// <summary>Uma coluna como a cópia mascarada a vê: tipo, se aceita NULL e se é gerada (essa o destino calcula sozinho).</summary>
public sealed record SourceColumn(string Name, string DataType, bool IsNullable, bool IsGenerated);

/// <summary>
/// Uma tabela com linhas próprias (<c>relkind 'r'</c>): as comuns e as
/// partições. <see cref="Root"/> é a tabela particionada de cima, quando é
/// partição — as regras escritas para ela valem para as partições.
/// </summary>
public sealed record SourceTable(
    string Schema,
    string Table,
    long Bytes,
    long EstimatedRows,
    IReadOnlyList<SourceColumn> Columns,
    string? Root = null)
{
    public string QualifiedName => $"{Schema}.{Table}";
}

/// <summary>Como uma coluna participa de uma chave.</summary>
public enum KeyRole
{
    Primary,

    Unique,

    /// <summary>A coluna que referencia outra tabela.</summary>
    Foreign,

    /// <summary>A coluna referenciada por uma FK de outra tabela.</summary>
    Referenced,
}

public sealed record KeyColumn(string Schema, string Table, string Column, KeyRole Role)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";
}

/// <summary>Uma FK de uma tabela para outra, pelo nome das duas ("public.pedidos" → "public.clientes").</summary>
public sealed record ForeignKeyLink(string From, string To);

/// <summary>O que a cópia mascarada precisa saber da origem antes de ler qualquer linha.</summary>
public sealed record SourceCatalog(
    IReadOnlyList<SourceTable> Tables,
    IReadOnlyList<KeyColumn> Keys,
    IReadOnlyList<ColumnInfo> AllColumns,
    long LargeObjects)
{
    /// <summary>Quem referencia quem: uma tabela sem dados não pode ser referenciada por uma com dados.</summary>
    public IReadOnlyList<ForeignKeyLink> ForeignKeys { get; init; } = [];
}

/// <summary>Uma coluna no SELECT da cópia: como está, ou mascarada.</summary>
public sealed record MaskedColumnPlan(string Name, string DataType, MaskingMethod? Method = null, string? Argument = null)
{
    public bool IsMasked => Method is not null;
}

/// <summary>
/// Uma tabela a copiar: as colunas na ordem, sem as geradas.
/// <see cref="SkipData"/>: a tabela vai vazia — nenhuma linha sai da origem.
/// </summary>
public sealed record MaskedTablePlan(
    string Schema,
    string Table,
    long Bytes,
    IReadOnlyList<MaskedColumnPlan> Columns,
    long EstimatedRows = 0,
    bool SkipData = false)
{
    public string QualifiedName => $"{Schema}.{Table}";

    public bool HasMaskedColumns => Columns.Any(column => column.IsMasked);
}

/// <summary>Uma coluna mascarada como o servidor devolveu, para a pré-visualização: nunca o valor real.</summary>
public sealed record MaskedPreview(string Schema, string Table, string Column, IReadOnlyList<string?> Values)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";
}

public sealed record ColumnInfo(string Schema, string Table, string Column, string DataType, string? Comment)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";
}

public sealed record TableInfo(string Schema, string Table, long Bytes, long EstimatedRows)
{
    public string QualifiedName => $"{Schema}.{Table}";
}

/// <summary>A forma do banco, para comparar origem e destino depois do restore.</summary>
public sealed record DatabaseStructure(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<string> Tables,
    IReadOnlyDictionary<string, int> ConstraintsByType,
    int Indexes,
    int Sequences);

/// <summary>Contagem de uma tabela; <see cref="IsEstimate"/> quando contar de verdade seria caro demais.</summary>
public sealed record RowCount(string Table, long Rows, bool IsEstimate);

public sealed record ColumnReference(string Schema, string Table, string Column)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";
}

/// <summary>
/// Pares <c>md5(sal || chave)</c> → <c>md5(sal || valor)</c> de uma coluna.
/// Com sal aleatório por execução, nem o hash serve para achar o valor; o
/// dado em si nunca sai do servidor. <c>null</c> no valor é valor nulo.
/// </summary>
public sealed record ColumnFingerprint(ColumnReference Column, IReadOnlyDictionary<string, string?> Values)
{
    public static ColumnFingerprint Empty(ColumnReference column) => new(column, new Dictionary<string, string?>());
}

/// <summary>O que se leu do índice de um dump (<c>pg_restore --list</c>).</summary>
public sealed record ArchiveSummary(
    int TableDataEntries,
    int IndexEntries,
    int ConstraintEntries,
    IReadOnlyList<string> Tables);

/// <summary>Como terminou uma ferramenta do PostgreSQL. O texto de erro já vem mascarado.</summary>
public sealed record PgToolRun(int ExitCode, bool TimedOut, TimeSpan Duration, string ErrorTail)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public enum PgToolEventKind
{
    Line = 0,

    /// <summary>Começou (ou terminou, com <c>--jobs</c>) os dados de uma tabela.</summary>
    TableData = 1,

    /// <summary>Índice, constraint, chave estrangeira: a parte depois dos dados.</summary>
    PostData = 2,
}

/// <summary>Uma linha de uma ferramenta, já mascarada, e o que ela significa para o progresso.</summary>
public sealed record PgToolEvent(CommandOutputLine Line, PgToolEventKind Kind = PgToolEventKind.Line, string? Table = null);
