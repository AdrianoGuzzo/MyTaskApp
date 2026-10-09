using System.Globalization;
using System.Text.RegularExpressions;
using MyTaskApp.Application.Abstractions;

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

    public bool AllFound => Tools.All(status => status.Found);

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

/// <summary>Uma regra de mascaramento como o servidor a tem (<c>pg_seclabels</c>).</summary>
public sealed record ServerMaskingRule(string Schema, string Table, string Column, string Label)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";
}

/// <summary>O PostgreSQL Anonymizer no banco, visto pela conexão mascarada.</summary>
public sealed record AnonymizerStatus(
    bool Available,
    string? AvailableVersion,
    bool Installed,
    string? InstalledVersion,
    bool TransparentMaskingOn,
    bool CurrentRoleMasked,
    bool CanExecuteFunctions,
    IReadOnlyList<ServerMaskingRule> Rules)
{
    public static AnonymizerStatus Unavailable { get; } = new(false, null, false, null, false, false, false, []);

    /// <summary>O dump anônimo por role mascarada é do 2.x; o 1.x fazia de outro jeito.</summary>
    public bool IsSupportedVersion => PostgresVersion.TryParse(InstalledVersion) is { Major: >= 2 };
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
    int SecurityLabelEntries,
    bool HasAnonExtension,
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
