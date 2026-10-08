using System.Text.RegularExpressions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Lê o <c>--verbose</c> do <c>pg_dump</c> e do <c>pg_restore</c> para o
/// progresso (ADR-056). Só entende inglês — o ambiente do processo força
/// <c>LC_MESSAGES=C</c>. Linha que não reconhece passa adiante como linha.
/// </summary>
internal static partial class PgVerboseOutputParser
{
    public static PgToolEvent Parse(CommandOutputLine line)
    {
        var text = line.Text;

        if (QuotedTableData().Match(text) is { Success: true } quoted)
        {
            return new PgToolEvent(line, PgToolEventKind.TableData, quoted.Groups["table"].Value);
        }

        if (ParallelTableData().Match(text) is { Success: true } parallel)
        {
            var schema = parallel.Groups["schema"].Success ? parallel.Groups["schema"].Value + "." : string.Empty;
            return new PgToolEvent(line, PgToolEventKind.TableData, schema + parallel.Groups["table"].Value);
        }

        return PostData().IsMatch(text)
            ? new PgToolEvent(line, PgToolEventKind.PostData)
            : new PgToolEvent(line);
    }

    // pg_dump: dumping contents of table "public.clientes"
    // pg_restore: processing data for table "public.clientes"
    [GeneratedRegex(@"(?:dumping contents of|processing data for) table ""(?<table>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedTableData();

    // pg_restore: finished item 3420 TABLE DATA public pedidos (com --jobs)
    [GeneratedRegex(@"finished item \d+ TABLE DATA (?:(?<schema>\S+) )?(?<table>\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParallelTableData();

    // pg_restore: creating INDEX "public.ix_x" / CONSTRAINT / FK CONSTRAINT / TRIGGER
    [GeneratedRegex(@"creating (?:INDEX|CONSTRAINT|FK CONSTRAINT|TRIGGER)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PostData();
}

/// <summary>
/// Lê o índice de um dump (<c>pg_restore --list</c>) sem restaurar nada:
/// quantas tabelas têm dados, e se vieram regras ou a extensão do anon —
/// que um dump anônimo não pode trazer.
/// </summary>
internal static partial class PgArchiveListParser
{
    private static readonly string[] MultiWordTypes =
    [
        "TABLE DATA",
        "FK CONSTRAINT",
        "SECURITY LABEL",
        "SEQUENCE SET",
        "SEQUENCE OWNED BY",
        "DEFAULT ACL",
        "MATERIALIZED VIEW DATA",
    ];

    public static ArchiveSummary Parse(string listing)
    {
        var tables = new List<string>();
        int tableData = 0, labels = 0, indexes = 0, constraints = 0;
        var anon = false;

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (Entry().Match(line) is not { Success: true } match)
            {
                continue;
            }

            var rest = match.Groups["rest"].Value;
            var type = MultiWordTypes.FirstOrDefault(candidate => rest.StartsWith(candidate + " ", StringComparison.Ordinal))
                ?? rest.Split(' ', 2)[0];
            var tokens = rest[type.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (type)
            {
                case "TABLE DATA":
                    tableData++;

                    if (tokens.Length >= 2)
                    {
                        tables.Add($"{tokens[0]}.{tokens[1]}");
                    }

                    break;

                case "SECURITY LABEL":
                    labels++;
                    break;

                case "INDEX":
                    indexes++;
                    break;

                case "CONSTRAINT":
                case "FK CONSTRAINT":
                    constraints++;
                    break;

                case "EXTENSION" when tokens.Length >= 2 && tokens[1] == "anon":
                case "SCHEMA" when tokens.Length >= 2 && tokens[1] == "anon":
                    anon = true;
                    break;
            }
        }

        return new ArchiveSummary(tableData, labels, anon, indexes, constraints, tables);
    }

    // 3420; 0 16385 TABLE DATA public clientes postgres
    [GeneratedRegex(@"^\s*\d+;\s+\d+\s+\d+\s+(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Entry();
}
