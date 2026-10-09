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
    /// <summary><c>--exclude-extension</c>, que tira o anon do dump anônimo, chegou no pg_dump 17.</summary>
    public const int MinimumAnonymousDumpMajor = 17;

    /// <summary>O dump anônimo por role mascarada é do Anonymizer 2.</summary>
    public const int MinimumAnonymizerMajor = 2;

    /// <summary><c>dropdb --force</c> existe desde o PostgreSQL 13.</summary>
    public const int MinimumForceDropMajor = 13;

    public static IReadOnlyList<CheckResult> Evaluate(
        PostgresClientTools tools,
        PostgresVersion? sourceServer,
        PostgresVersion? destinationServer,
        bool anonymous)
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

        if (anonymous && dump.Version is { } anonymousDump && anonymousDump.Major < MinimumAnonymousDumpMajor)
        {
            results.Add(new(category, "Dump anônimo", CheckOutcome.Fail,
                $"O dump anônimo precisa do pg_dump {MinimumAnonymousDumpMajor} ou mais novo (--exclude-extension); achado {anonymousDump}."));
        }

        if (dump.Version is { } producer && restore.Version is { } consumer)
        {
            results.Add(consumer.Major >= producer.Major
                ? new(category, "pg_restore × pg_dump", CheckOutcome.Pass)
                : new(category, "pg_restore × pg_dump", CheckOutcome.Fail,
                    $"pg_restore {consumer} não lê dumps do pg_dump {producer}."));
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
