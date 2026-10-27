using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Tests.Persistence;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// O lease entre processos (ADR-085) sobre o arquivo SQLite migrado: cada
/// <see cref="SqliteProcessLeases"/> com outro <see cref="ProcessOwner"/> é um
/// processo, como o desktop e o agente abrindo o mesmo banco.
/// </summary>
public sealed class SqliteProcessLeasesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    private readonly TempSqliteDatabase _database = new();

    private readonly FakeTimeProvider _clock = new(Now);

    private readonly FakeLiveness _liveness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _database.MigrateAsync(Ct);

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    private SqliteProcessLeases Process(int id) =>
        new(_database.ConnectionString, Owner(id), _liveness, _clock);

    private static ProcessOwner Owner(int id) => new(id, Now.AddHours(-id));

    [Fact]
    public async Task OneProcessTakesIt_AndTheOtherWaitsWhileTheFirstLives()
    {
        var desktop = Process(1);
        var agent = Process(2);

        (await desktop.TryAcquireAsync("sync", Duration, Ct)).Should().BeTrue();
        (await agent.TryAcquireAsync("sync", Duration, Ct)).Should().BeFalse();
        (await desktop.TryAcquireAsync("sync", Duration, Ct)).Should().BeTrue("renovar o próprio lease sempre dá");
    }

    [Fact]
    public async Task TwoDatabaseCopiesInDifferentProcesses_TheSecondIsRefused_UntilTheFirstEnds()
    {
        var desktop = new DatabaseOperationGate(Process(1), _clock);
        var agent = new DatabaseOperationGate(Process(2), _clock);

        await using (await agent.EnterAsync(Guid.CreateVersion7(), Ct))
        {
            await FluentActions.Awaiting(() => desktop.EnterAsync(Guid.CreateVersion7(), Ct))
                .Should().ThrowAsync<DomainException>().WithMessage("Outro processo deste computador*");
            desktop.IsBusy.Should().BeFalse("a recusa não deixa a vaga deste processo ocupada");
        }

        await using var turn = await desktop.EnterAsync(Guid.CreateVersion7(), Ct);
        desktop.IsBusy.Should().BeTrue();
    }

    [Fact]
    public async Task EachNameHasItsOwnOwner()
    {
        (await Process(1).TryAcquireAsync("sync", Duration, Ct)).Should().BeTrue();
        (await Process(2).TryAcquireAsync("sync-token", Duration, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Renewing_KeepsWhenItWasTaken_AndPushesTheExpiry()
    {
        var desktop = Process(1);
        await desktop.TryAcquireAsync("sync", Duration, Ct);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await desktop.TryAcquireAsync("sync", Duration, Ct);

        var row = await ReadAsync("sync");
        row.ProcessId.Should().Be(1);
        row.AcquiredAt.Should().Be(Now.UtcTicks);
        row.ExpiresAt.Should().Be((Now + TimeSpan.FromMinutes(1) + Duration).UtcTicks);
    }

    [Fact]
    public async Task AnExpiredLease_GoesToWhoeverAsks_EvenWithTheOwnerAlive()
    {
        await Process(1).TryAcquireAsync("sync", Duration, Ct);

        _clock.Advance(Duration);

        (await Process(2).TryAcquireAsync("sync", Duration, Ct)).Should().BeTrue();
        (await Process(1).TryAcquireAsync("sync", Duration, Ct)).Should().BeFalse("quem parou de renovar perdeu a vez");

        var row = await ReadAsync("sync");
        row.ProcessId.Should().Be(2);
        row.AcquiredAt.Should().Be((Now + Duration).UtcTicks);
    }

    [Fact]
    public async Task TheLeaseOfAProcessThatDied_PassesOnAtOnce()
    {
        await Process(1).TryAcquireAsync("sync", Duration, Ct);
        _liveness.Dead.Add(Owner(1));

        (await Process(2).TryAcquireAsync("sync", Duration, Ct)).Should().BeTrue("não espera vencer o lease de quem não existe mais");
        (await ReadAsync("sync")).ProcessId.Should().Be(2);
    }

    [Fact]
    public async Task TheSamePidStartedLater_IsAnotherProcess()
    {
        await new SqliteProcessLeases(_database.ConnectionString, new ProcessOwner(7, Now.AddHours(-1)), _liveness, _clock)
            .TryAcquireAsync("sync", Duration, Ct);

        var reused = new SqliteProcessLeases(_database.ConnectionString, new ProcessOwner(7, Now), _liveness, _clock);

        (await reused.TryAcquireAsync("sync", Duration, Ct)).Should().BeFalse("o PID reaproveitado não herda o lease");
    }

    [Fact]
    public async Task Releasing_LetsTheOtherTakeIt_AndOnlyTheOwnerReleases()
    {
        var desktop = Process(1);
        var agent = Process(2);
        await desktop.TryAcquireAsync("sync-token", Duration, Ct);

        await agent.ReleaseAsync("sync-token", Ct);
        (await agent.TryAcquireAsync("sync-token", Duration, Ct)).Should().BeFalse("soltar o lease dos outros não faz nada");

        await desktop.ReleaseAsync("sync-token", Ct);
        (await agent.TryAcquireAsync("sync-token", Duration, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task ManyProcessesAtOnce_ExactlyOneGetsIt()
    {
        var processes = Enumerable.Range(1, 8).Select(Process).ToList();

        var taken = await Task.WhenAll(processes.Select(process => Task.Run(() => process.TryAcquireAsync("sync", Duration, Ct), Ct)));

        taken.Count(won => won).Should().Be(1);
    }

    [Fact]
    public async Task ManyProcessesStealingFromTheDead_ExactlyOneGetsIt()
    {
        await Process(99).TryAcquireAsync("sync", Duration, Ct);
        _liveness.Dead.Add(Owner(99));
        var processes = Enumerable.Range(1, 8).Select(Process).ToList();

        var taken = await Task.WhenAll(processes.Select(process => Task.Run(() => process.TryAcquireAsync("sync", Duration, Ct), Ct)));

        taken.Count(won => won).Should().Be(1);
    }

    [Fact]
    public async Task TheRealLiveness_SeesAProcessWhileItRuns_AndNotAfter()
    {
        var liveness = new SystemProcessLiveness();
        var current = ProcessOwner.Current();

        liveness.IsAlive(current.ProcessId, current.StartedAt).Should().BeTrue();
        liveness.IsAlive(current.ProcessId, current.StartedAt.AddMinutes(-5)).Should().BeFalse("o mesmo PID com outro início é outro processo");

        using var child = System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            ArgumentList = { OperatingSystem.IsWindows() ? "/c" : "-c", OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var owner = new ProcessOwner(child.Id, child.StartTime.ToUniversalTime());
        liveness.IsAlive(owner.ProcessId, owner.StartedAt).Should().BeTrue();

        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync(Ct);

        liveness.IsAlive(owner.ProcessId, owner.StartedAt).Should().BeFalse();
    }

    private async Task<ProcessLeaseRow> ReadAsync(string name)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProcessId, AcquiredAt, ExpiresAt FROM ProcessLeases WHERE Name = $name";
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();

        return new ProcessLeaseRow { Name = name, ProcessId = reader.GetInt32(0), AcquiredAt = reader.GetInt64(1), ExpiresAt = reader.GetInt64(2) };
    }
}

/// <summary>Todo processo vive, menos os que o teste matou.</summary>
internal sealed class FakeLiveness : IProcessLiveness
{
    public HashSet<ProcessOwner> Dead { get; } = [];

    public bool IsAlive(int processId, DateTimeOffset startedAt) => !Dead.Contains(new ProcessOwner(processId, startedAt));
}
