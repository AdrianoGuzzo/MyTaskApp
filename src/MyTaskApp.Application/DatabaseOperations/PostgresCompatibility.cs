namespace MyTaskApp.Application.DatabaseOperations;

public enum CheckOutcome
{
    Pass = 1,
    Warning = 2,
    Fail = 3,
}

/// <summary>Um item de diagnóstico ou de verificação: o que foi visto, e se está bom.</summary>
public sealed record CheckResult(string Category, string Name, CheckOutcome Outcome, string? Detail = null);

/// <summary>
/// As regras de versão entre ferramentas e servidores (ADR-056). Erradas,
/// elas aparecem como um <c>pg_dump</c> que recusa o servidor, ou um restore
/// que não lê o arquivo — melhor dizer antes de começar.
/// </summary>
public static class PostgresCompatibility
{

    /// <summary><c>dropdb --force</c> existe desde o PostgreSQL 13.</summary>
    public const int MinimumForceDropMajor = 13;

    /// <summary>
    /// O <c>pg_restore</c> 17 passou a mandar <c>SET transaction_timeout = 0</c>,
    /// e o parâmetro só existe a partir do servidor 17: num mais antigo, o
    /// restore para na primeira linha.
    /// </summary>
    public const int TransactionTimeoutMajor = 17;

    public static IReadOnlyList<CheckResult> Evaluate(
        PostgresClientTools tools,
        PostgresVersion? sourceServer,
        PostgresVersion? destinationServer)
    {
        const string category = "Compatibilidade";
        var results = new List<CheckResult>();
        var dump = tools.Find(PostgresTool.PgDump);
        var restore = tools.Find(PostgresTool.PgRestore);

        if (!dump.Found || !restore.Found)
        {
            results.Add(new(category, "Ferramentas", CheckOutcome.Fail, "pg_dump e pg_restore precisam estar instalados."));
            return results;
        }

        if (dump.Version is null || restore.Version is null)
        {
            results.Add(new(category, "Versões", CheckOutcome.Warning, "Não foi possível ler a versão de pg_dump ou pg_restore."));
        }

        if (dump.Version is { } dumpVersion && sourceServer is { } source)
        {
            results.Add(dumpVersion.Major >= source.Major
                ? new(category, "pg_dump × origem", CheckOutcome.Pass, $"pg_dump {dumpVersion} lê PostgreSQL {source}.")
                : new(category, "pg_dump × origem", CheckOutcome.Fail,
                    $"pg_dump {dumpVersion} é mais antigo que o servidor de origem (PostgreSQL {source}). Instale as ferramentas {source.Major} ou mais novas."));
        }


        if (dump.Version is { } producer && restore.Version is { } consumer)
        {
            results.Add(consumer.Major >= producer.Major
                ? new(category, "pg_restore × pg_dump", CheckOutcome.Pass)
                : new(category, "pg_restore × pg_dump", CheckOutcome.Fail,
                    $"pg_restore {consumer} não lê dumps do pg_dump {producer}."));
        }

        if (restore.Version is { } restorer && destinationServer is { } target
            && restorer.Major >= TransactionTimeoutMajor && target.Major < TransactionTimeoutMajor)
        {
            results.Add(new(category, "pg_restore × destino", CheckOutcome.Fail,
                $"pg_restore {restorer} não restaura no PostgreSQL {target}: ele manda SET transaction_timeout, que só existe a partir do 17. " +
                (sourceServer is { Major: < TransactionTimeoutMajor } origin
                    ? $"Instale as ferramentas cliente {origin.Major} (as da origem): o app usa o menor conjunto instalado que lê a origem."
                    : $"Use um destino PostgreSQL {TransactionTimeoutMajor} ou mais novo.")));
        }

        if (sourceServer is { } from && destinationServer is { } to && to.Major < from.Major)
        {
            results.Add(new(category, "Destino × origem", CheckOutcome.Warning,
                $"O destino (PostgreSQL {to}) é mais antigo que a origem (PostgreSQL {from}): o restore pode falhar em recursos novos."));
        }

        return results;
    }

    public static bool SupportsForceDrop(PostgresVersion? server) => server is { Major: >= MinimumForceDropMajor };
}
