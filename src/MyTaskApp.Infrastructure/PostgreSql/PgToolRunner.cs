using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// O que uma chamada de ferramenta pretende fazer — é o que a guarda julga.
/// <see cref="Target"/> é o banco em que ela age; <see cref="Anonymous"/> diz
/// se o dump sai pela role mascarada.
/// </summary>
internal sealed record PgInvocation(
    PostgresTool Tool,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    DatabaseConnectionSnapshot? Target = null,
    IReadOnlyCollection<string>? ProtectedEndpoints = null,
    bool Anonymous = false);

/// <summary>O fim de uma ferramenta, com a saída padrão para quem lê dela (o <c>--list</c>).</summary>
internal sealed record PgToolOutput(PgToolRun Run, string StandardOutput);

/// <summary>
/// A última barreira antes de um processo do PostgreSQL nascer (ADR-056).
/// Não confia em quem chamou: julga o alvo de novo, só pelo ambiente e pelas
/// chaves dos bancos de produção, e recusa com <see cref="DatabaseSecurityException"/>.
/// </summary>
internal static class PostgresProcessGuard
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "postgres", "template0", "template1" };

    public static void Check(PgInvocation invocation)
    {
        var violations = new List<SecurityViolation>();
        var asksVersion = invocation.Arguments is ["--version"];

        switch (invocation.Tool)
        {
            case PostgresTool.Psql:
                // O app não tem console SQL: o psql só responde a versão.
                if (!asksVersion)
                {
                    violations.Add(new(SecurityViolationCode.SqlExecutionForbidden, "O psql só é usado para ler a versão."));
                }

                break;

            case PostgresTool.PgDump when !asksVersion:
                if (invocation.Target is not { } source)
                {
                    violations.Add(new(SecurityViolationCode.MissingSource, "Dump sem conexão."));
                }
                else if (!source.HasDatabase)
                {
                    violations.Add(new(SecurityViolationCode.DatabaseNotChosen, $"{source.Name} é só o servidor: nenhum banco foi escolhido."));
                }
                else if (!source.Permissions.CanDump)
                {
                    violations.Add(new(SecurityViolationCode.MissingPermission, $"{source.Name} não tem permissão para fazer dump."));
                }
                else if (source.Permissions.RequireAnonymization && !invocation.Anonymous)
                {
                    violations.Add(new(SecurityViolationCode.PlainDumpFromProtectedSource, $"{source.Name} só sai por dump anônimo."));
                }

                break;

            case PostgresTool.PgRestore when invocation.Target is null:
                // --version e --list: lê um arquivo local, sem banco nenhum.
                if (!asksVersion && invocation.Arguments is not ["--list", _])
                {
                    violations.Add(new(SecurityViolationCode.MissingDestination, "Restore sem destino."));
                }

                break;

            case PostgresTool.PgRestore or PostgresTool.CreateDb or PostgresTool.DropDb when !asksVersion:
                CheckWritable(invocation, violations);
                break;
        }

        if (violations.Count > 0)
        {
            throw new DatabaseSecurityException(new SecurityDecision(violations));
        }
    }

    private static void CheckWritable(PgInvocation invocation, List<SecurityViolation> violations)
    {
        if (invocation.Target is not { } target)
        {
            violations.Add(new(SecurityViolationCode.MissingDestination, "Escrita sem destino."));
            return;
        }

        if (!target.HasDatabase)
        {
            violations.Add(new(SecurityViolationCode.DatabaseNotChosen, $"{target.Name} é só o servidor: nenhum banco foi escolhido."));
            return;
        }

        // Uma produção sem banco protege o servidor inteiro (host:porta/*).
        if (target.IsProtected || target.IsAmong(invocation.ProtectedEndpoints))
        {
            violations.Add(new(SecurityViolationCode.ProtectedTargetModification, $"{target.Name} é produção: nada é gravado nele."));
        }

        var permission = invocation.Tool switch
        {
            PostgresTool.CreateDb => ConnectionPermission.CreateDatabase,
            PostgresTool.DropDb => ConnectionPermission.DropDatabase,
            _ => ConnectionPermission.Restore,
        };

        if (!target.Permissions.Has(permission | ConnectionPermission.UseAsDestination))
        {
            violations.Add(new(SecurityViolationCode.MissingPermission, $"{target.Name} não permite esta operação."));
        }

        if (invocation.Tool is PostgresTool.CreateDb or PostgresTool.DropDb && SystemDatabases.Contains(target.Database!))
        {
            violations.Add(new(SecurityViolationCode.ProtectedSystemDatabase, $"{target.Database} é um banco do próprio servidor."));
        }
    }
}

/// <summary>
/// O ambiente de um processo do PostgreSQL (ADR-056): a senha vai por
/// <c>PGPASSWORD</c> — nunca em argumento, que qualquer usuário da máquina vê
/// na lista de processos — e tudo o que viria de fora é desligado.
/// </summary>
internal static class PgEnvironment
{
    public static IReadOnlyDictionary<string, string?> Build(
        DatabaseConnectionSnapshot? connection,
        string? password,
        string noPassFile,
        int connectTimeoutSeconds,
        IEnumerable<string> inheritedNames,
        bool readOnly)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // PGSERVICE, PGHOST, PGOPTIONS, PGPASSFILE herdados poderiam levar a
        // ferramenta a outro servidor, ou mudar o que ela faz: fora todos.
        foreach (var name in inheritedNames.Where(name => name.StartsWith("PG", StringComparison.OrdinalIgnoreCase)))
        {
            environment[name] = null;
        }

        // As mensagens em inglês: o progresso lê "dumping contents of table".
        environment["LC_MESSAGES"] = "C";
        environment["LANG"] = "C";
        environment["PGCLIENTENCODING"] = "UTF8";
        environment["PGAPPNAME"] = "MyTaskApp";
        environment["PGCONNECT_TIMEOUT"] = connectTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        // Um ~/.pgpass esquecido não pode decidir a senha (nem o servidor).
        environment["PGPASSFILE"] = noPassFile;

        // O dump nasce só leitura no servidor, além de a política já ter
        // julgado: mesmo que algo escapasse, a origem recusaria escrever.
        // Quem escreve (restore, createdb) só chega ao destino, que a guarda julgou.
        if (readOnly)
        {
            environment["PGOPTIONS"] = "-c default_transaction_read_only=on";
        }

        if (connection is not null)
        {
            environment["PGSSLMODE"] = SslMode(connection.SslMode);
        }

        if (password is not null)
        {
            environment["PGPASSWORD"] = password;
        }

        return environment;
    }

    public static string SslMode(DatabaseSslMode mode) => mode switch
    {
        DatabaseSslMode.Require => "require",
        DatabaseSslMode.VerifyFull => "verify-full",
        DatabaseSslMode.Disable => "disable",
        _ => "prefer",
    };
}

/// <summary>
/// O único lugar que inicia ferramentas do PostgreSQL (ADR-056): guarda,
/// ambiente sem senha nos argumentos, saída mascarada linha a linha, e um log
/// que leva ferramenta, código e duração — nunca argumentos, ambiente ou saída.
/// </summary>
internal sealed class PgToolRunner(
    IProcessRunner runner,
    IPostgresToolLocator locator,
    IPostgresPasswordReader passwords,
    DatabaseOperationsOptions options,
    ILogger<PgToolRunner> logger)
{
    /// <summary>Linhas do fim do erro guardadas para a mensagem: é onde o PostgreSQL diz o motivo.</summary>
    internal const int ErrorTailLines = 15;

    internal Func<IEnumerable<string>> InheritedVariables { get; init; } =
        () => Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(key => key.ToString() ?? string.Empty);

    public async Task<PgToolOutput> RunAsync(
        PgInvocation invocation,
        IProgress<PgToolEvent>? progress,
        CancellationToken cancellationToken)
    {
        PostgresProcessGuard.Check(invocation);

        var tools = await locator.DetectAsync(refresh: false, cancellationToken);
        var tool = tools.Find(invocation.Tool);

        if (!tool.Found)
        {
            throw new DomainException($"{tool.Name} não está instalado. Veja a aba Diagnóstico.");
        }

        var password = invocation.Target is { } target && invocation.Arguments is not ["--version"]
            ? await passwords.ReadAsync(target.SecretReference, cancellationToken)
            : null;

        var environment = PgEnvironment.Build(
            invocation.Target,
            password,
            Path.Combine(Path.GetTempPath(), "mytaskapp-no-pgpass"),
            options.ConnectTimeoutSeconds,
            InheritedVariables(),
            readOnly: invocation.Tool is PostgresTool.PgDump);

        var request = new ProcessRequest(tool.Path!, invocation.Arguments, invocation.Timeout, Environment: environment);
        var tail = new Queue<string>();
        var lines = new MaskingLines(password, tail, progress);
        var started = Stopwatch.GetTimestamp();

        ProcessResult result;

        try
        {
            result = await runner.RunAsync(request, lines, cancellationToken);
        }
        catch (ProcessStartException)
        {
            logger.LogWarning("PgToolStartFailed {Tool}", tool.Name);
            throw new DomainException($"Não foi possível iniciar o {tool.Name}.");
        }

        var duration = Stopwatch.GetElapsedTime(started);
        var errorTail = SensitiveText.Mask(string.Join(Environment.NewLine, tail), [password]);

        logger.LogInformation(
            "PgToolFinished {Tool} {ExitCode} {TimedOut} {DurationMs}",
            tool.Name,
            result.ExitCode,
            result.TimedOut,
            (long)duration.TotalMilliseconds);

        return new PgToolOutput(
            new PgToolRun(result.ExitCode, result.TimedOut, duration, errorTail),
            SensitiveText.Mask(result.StandardOutput, [password]));
    }

    /// <summary>Cada linha mascarada antes de qualquer um vê-la; o fim do erro fica para a mensagem.</summary>
    private sealed class MaskingLines(string? password, Queue<string> tail, IProgress<PgToolEvent>? progress) : IProgress<CommandOutputLine>
    {
        private readonly Lock _gate = new();

        public void Report(CommandOutputLine value)
        {
            // O --verbose das ferramentas sai todo pelo stderr: só é erro o que diz que é.
            var text = SensitiveText.Mask(value.Text, [password]);
            var masked = new CommandOutputLine(text, value.IsError && LooksLikeAProblem(text));

            if (value.IsError)
            {
                lock (_gate)
                {
                    tail.Enqueue(masked.Text);

                    while (tail.Count > ErrorTailLines)
                    {
                        tail.Dequeue();
                    }
                }
            }

            progress?.Report(PgVerboseOutputParser.Parse(masked));
        }

        private static bool LooksLikeAProblem(string text) =>
            text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || text.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not", StringComparison.OrdinalIgnoreCase);
    }
}
