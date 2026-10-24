using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>Este processo, como dono de lease: o PID sozinho se repete depois que o processo sai.</summary>
internal sealed record ProcessOwner(int ProcessId, DateTimeOffset StartedAt)
{
    public static ProcessOwner Current()
    {
        using var process = Process.GetCurrentProcess();

        try
        {
            return new ProcessOwner(process.Id, process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or NotSupportedException or Win32Exception)
        {
            // Sem o início do processo (sistema que não diz): agora, que é único o bastante para este processo.
            return new ProcessOwner(process.Id, DateTimeOffset.UtcNow);
        }
    }
}

/// <summary>Se o dono de um lease ainda existe.</summary>
internal interface IProcessLiveness
{
    bool IsAlive(int processId, DateTimeOffset startedAt);
}

internal sealed class SystemProcessLiveness : IProcessLiveness
{
    /// <summary>O Linux calcula o início a partir do boot, com diferença de milissegundos entre quem pergunta.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            return !process.HasExited && (process.StartTime.ToUniversalTime() - startedAt).Duration() < StartTolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Não existe mais, ou saiu entre achar e perguntar.
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or NotSupportedException or PlatformNotSupportedException)
        {
            // Não dá para saber (processo de outro usuário com o PID reaproveitado): espera vencer.
            return true;
        }
    }
}

/// <summary>
/// Os leases na tabela <c>ProcessLeases</c> do SQLite que o desktop e o agente
/// abrem juntos (ADR-085). Tomar é uma instrução só, um upsert que só grava se
/// o lease estiver livre, vencido ou já for deste processo; o SQLite serializa
/// os escritores, então dois processos nunca saem os dois com ele.
/// </summary>
/// <remarks>
/// Conexão direta, e não o <c>MyTaskAppDbContext</c>: é chamado por singletons
/// (o ciclo da sincronização, a renovação do token), não passa pela captura do
/// sync, e o upsert condicional não tem tradução no EF.
/// </remarks>
internal sealed class SqliteProcessLeases(
    string connectionString,
    ProcessOwner owner,
    IProcessLiveness liveness,
    TimeProvider timeProvider) : IProcessLeases
{
    private const string Take = """
        INSERT INTO ProcessLeases (Name, ProcessId, ProcessStartedAt, AcquiredAt, ExpiresAt)
        VALUES ($name, $pid, $started, $now, $expires)
        ON CONFLICT (Name) DO UPDATE SET
            AcquiredAt = CASE WHEN ProcessLeases.ProcessId = excluded.ProcessId AND ProcessLeases.ProcessStartedAt = excluded.ProcessStartedAt
                THEN ProcessLeases.AcquiredAt ELSE excluded.AcquiredAt END,
            ProcessId = excluded.ProcessId,
            ProcessStartedAt = excluded.ProcessStartedAt,
            ExpiresAt = excluded.ExpiresAt
        WHERE (ProcessLeases.ProcessId = excluded.ProcessId AND ProcessLeases.ProcessStartedAt = excluded.ProcessStartedAt)
            OR ProcessLeases.ExpiresAt <= $now
        """;

    private const string Holder = "SELECT ProcessId, ProcessStartedAt FROM ProcessLeases WHERE Name = $name";

    /// <summary>Do dono que morreu, e só dele: se outro o tomou no meio-tempo, a troca não acontece.</summary>
    private const string TakeFromDead = """
        UPDATE ProcessLeases
        SET ProcessId = $pid, ProcessStartedAt = $started, AcquiredAt = $now, ExpiresAt = $expires
        WHERE Name = $name AND ProcessId = $deadPid AND ProcessStartedAt = $deadStarted
        """;

    private const string Release = """
        DELETE FROM ProcessLeases
        WHERE Name = $name AND ProcessId = $pid AND ProcessStartedAt = $started
        """;

    public async Task<bool> TryAcquireAsync(string name, TimeSpan duration, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();

        if (await ExecuteAsync(connection, Take, name, now, duration, cancellationToken, []) == 1)
        {
            return true;
        }

        if (await ReadHolderAsync(connection, name, cancellationToken) is not { } holder)
        {
            // Solto entre as duas instruções: tenta de novo, uma vez.
            return await ExecuteAsync(connection, Take, name, now, duration, cancellationToken, []) == 1;
        }

        if (liveness.IsAlive(holder.ProcessId, holder.StartedAt))
        {
            return false;
        }

        return await ExecuteAsync(
            connection,
            TakeFromDead,
            name,
            now,
            duration,
            cancellationToken,
            [("$deadPid", holder.ProcessId), ("$deadStarted", holder.StartedAt.UtcTicks)]) == 1;
    }

    public async Task ReleaseAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = Release;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$pid", owner.ProcessId);
        command.Parameters.AddWithValue("$started", owner.StartedAt.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        string name,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken,
        (string Name, object Value)[] extra)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$pid", owner.ProcessId);
        command.Parameters.AddWithValue("$started", owner.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.Parameters.AddWithValue("$expires", (now + duration).UtcTicks);

        foreach (var (parameter, value) in extra)
        {
            command.Parameters.AddWithValue(parameter, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ProcessOwner?> ReadHolderAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Holder;
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new ProcessOwner(reader.GetInt32(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero))
            : null;
    }
}

/// <summary>
/// Um processo só usa o banco (o celular, ADR-067): todo lease é dele. Sem
/// tabela, sem conferir PID, que o iOS nem deixa perguntar.
/// </summary>
internal sealed class SingleProcessLeases : IProcessLeases
{
    public Task<bool> TryAcquireAsync(string name, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task ReleaseAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
}
