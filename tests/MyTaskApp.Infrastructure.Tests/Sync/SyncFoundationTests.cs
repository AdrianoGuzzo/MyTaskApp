using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Sync;
using MyTaskApp.Infrastructure.Tests.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>A base local da sincronização: a identidade do dispositivo e a migração.</summary>
public sealed class SyncFoundationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-device-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void TheDeviceId_IsCreatedOnce_AndSurvivesARestart()
    {
        var first = new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance).DeviceId;
        var second = new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance).DeviceId;

        first.Should().NotBe(Guid.Empty);
        second.Should().Be(first);
        File.Exists(Path.Combine(_folder, DeviceIdentityFile.FileName)).Should().BeTrue();
    }

    [Fact]
    public void TheDeviceId_IsNotTheMachineName()
    {
        var identity = new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance);
        _ = identity.DeviceId;

        identity.DefaultNickname.Should().Be($"{Environment.MachineName} ({Environment.UserName})");
        File.ReadAllText(Path.Combine(_folder, DeviceIdentityFile.FileName)).Should().NotContain(Environment.MachineName);
    }

    [Theory]
    [InlineData(null, "PC")]
    [InlineData("", "PC")]
    [InlineData("  ", "PC")]
    [InlineData("ana", "PC (ana)")]
    [InlineData(" ana ", "PC (ana)")]
    public void TheNickname_IsTheMachineAndTheWindowsUser(string? user, string expected)
    {
        DeviceIdentityFile.NicknameFor("PC", user).Should().Be(expected);
    }

    [Fact]
    public void ALongWindowsUser_IsCutToTheLimit_KeepingTheMachine()
    {
        var nickname = DeviceIdentityFile.NicknameFor("DESKTOP-XYZ", new string('a', 100));

        nickname.Should().HaveLength(SyncSchema.MaxNicknameLength);
        nickname.Should().StartWith("DESKTOP-XYZ (a").And.EndWith("a)");
    }

    [Fact]
    public void Rotating_GivesANewIdThatAlsoSurvives()
    {
        var identity = new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance);
        var before = identity.DeviceId;

        identity.Rotate();

        identity.DeviceId.Should().NotBe(before);
        new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance).DeviceId.Should().Be(identity.DeviceId);
    }

    [Fact]
    public void AnUnreadableFile_BecomesANewDevice()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, DeviceIdentityFile.FileName), "{ isto não é json");

        var identity = new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance);

        identity.DeviceId.Should().NotBe(Guid.Empty);
        new DeviceIdentityFile(_folder, NullLogger<DeviceIdentityFile>.Instance).DeviceId.Should().Be(identity.DeviceId);
    }

    /// <summary>
    /// Uma instalação de antes da sincronização: a migração só acrescenta, os
    /// ids continuam os mesmos e nenhuma linha de sync aparece sozinha.
    /// </summary>
    [Fact]
    public async Task UpgradingAnExistingInstallation_KeepsTheDataAndCreatesNothingToSend()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("TagDirectoryPathOptional", Ct);
        var tag = Tag.Create("Casa", "#00FF00", Now);
        var task = TaskItem.Create("Pagar boleto", Now);
        task.SetTags([tag.Id]);

        await db.SeedOnOlderSchemaAsync(Ct, tag, task);

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        (await read.Tasks.SingleAsync(Ct)).Id.Should().Be(task.Id);
        (await read.Tags.SingleAsync(Ct)).Id.Should().Be(tag.Id);
        (await read.SyncOutbox.CountAsync(Ct)).Should().Be(0);
        (await read.SyncEntityStates.CountAsync(Ct)).Should().Be(0);
        (await read.SyncState.CountAsync(Ct)).Should().Be(0);
    }
}
