using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.PostgreSql;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

internal static class PostgresTestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 41, 2, TimeSpan.Zero);

    public const string Password = "s3nh@-Pr0d-xyz";

    public static DatabaseConnectionSnapshot Connection(
        DatabaseEnvironment environment,
        string? database = "eco_core",
        string username = "backup_user",
        string host = "192.168.15.112") =>
        DatabaseConnection.Create(
                environment.ToString(), host, 5432, database, username, environment, DatabaseSslMode.Require, null,
                ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.RequireAnonymization), Now)
            .Snapshot() with { SecretReference = "postgres-" + new string('a', 32) };

    public static PostgresClientTools Tools(string directory = @"C:\pg\17\bin") => new(
        Enum.GetValues<PostgresTool>()
            .Select(tool => new PostgresToolStatus(tool, PostgresToolNames.Of(tool), Path.Combine(directory, PostgresToolNames.Of(tool) + ".exe"),
                new PostgresVersion(17, 2), "17.2"))
            .ToList(),
        new PostgresInstallGuide("Windows", [], []));
}

internal sealed class StaticToolLocator(PostgresClientTools tools) : IPostgresToolLocator
{
    public Task<PostgresClientTools> DetectAsync(bool refresh, CancellationToken cancellationToken = default) => Task.FromResult(tools);
}

internal sealed class StaticPasswordReader(string? password = PostgresTestSupport.Password) : IPostgresPasswordReader
{
    public List<string?> Reads { get; } = [];

    public Task<string?> ReadAsync(string? reference, CancellationToken cancellationToken = default)
    {
        Reads.Add(reference);
        return Task.FromResult(password);
    }
}

/// <summary>Um servidor de mentira: responde por SQL e guarda o que foi perguntado.</summary>
internal sealed class FakePostgresSessions : IPostgresSessionFactory
{
    private readonly List<(Func<string, bool> Matches, Func<IReadOnlyList<object?>, IReadOnlyList<object?[]>> Rows)> _answers = [];

    public List<(string Sql, IReadOnlyList<object?> Parameters)> Queries { get; } = [];

    public List<(DatabaseConnectionSnapshot Connection, SecretText? Password)> Opened { get; } = [];

    public Exception? OpenFailure { get; set; }

    public FakePostgresSessions Answer(string sql, params object?[][] rows)
    {
        _answers.Add((candidate => candidate == sql, _ => rows));
        return this;
    }

    public FakePostgresSessions AnswerWhen(Func<string, bool> matches, Func<IReadOnlyList<object?>, IReadOnlyList<object?[]>> rows)
    {
        _answers.Add((matches, rows));
        return this;
    }

    public Task<IPostgresSession> OpenAsync(DatabaseConnectionSnapshot connection, SecretText? password, CancellationToken cancellationToken = default)
    {
        Opened.Add((connection, password));

        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        return Task.FromResult<IPostgresSession>(new Session(this));
    }

    private sealed class Session(FakePostgresSessions owner) : IPostgresSession
    {
        public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
        {
            owner.Queries.Add((sql, parameters));
            var answer = owner._answers.LastOrDefault(candidate => candidate.Matches(sql));
            return Task.FromResult(answer.Rows is null ? (IReadOnlyList<object?[]>)[] : answer.Rows(parameters));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
