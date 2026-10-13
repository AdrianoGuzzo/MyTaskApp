using System.Text.Json;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>A política de conflito campo a campo (ADR-061), sem banco e sem relógio.</summary>
public sealed class FieldMergePolicyTests
{
    private static readonly Guid DeviceA = Guid.CreateVersion7();

    private static readonly Guid DeviceB = Guid.CreateVersion7();

    /// <summary>Versão 2: o título foi mudado por A na versão 2; o resto nasceu na 1.</summary>
    private static StoredEntity Current() => new()
    {
        WorkspaceId = Guid.CreateVersion7(),
        EntityType = SyncEntityTypes.Task,
        EntityId = Guid.CreateVersion7(),
        Version = 2,
        Data = new Dictionary<string, JsonElement>(Ops.Task("Título de A"))
        {
            [SyncFields.Task.ArchivedAt] = SyncValue.Instant(Ops.Created),
        },
        FieldVersions = new Dictionary<string, FieldStamp>
        {
            [SyncFields.Task.Title] = new(2, DeviceA),
            [SyncFields.Task.Priority] = new(1, DeviceA),
            [SyncFields.Task.ArchivedAt] = new(2, DeviceA),
            [SyncFields.CreatedAt] = new(1, DeviceA),
        },
    };

    [Fact]
    public void DifferentFields_AlwaysMerge()
    {
        var merge = Merge(DeviceB, baseVersion: 1, (SyncFields.Task.Priority, SyncValue.Integer(3)));

        merge.Applied.Keys.Should().Equal(SyncFields.Task.Priority);
        merge.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void TheSameField_ChangedByAnotherDevice_KeepsTheFirstToArrive_AndReturnsIt()
    {
        var merge = Merge(DeviceB, baseVersion: 1, (SyncFields.Task.Title, SyncValue.String("Título de B")));

        merge.Applied.Should().BeEmpty();
        var rejected = merge.Rejected.Should().ContainSingle().Subject;
        rejected.Reason.Should().Be(SyncErrorCodes.ConcurrentChange);
        rejected.ServerValue.GetString().Should().Be("Título de A");
        FieldMergePolicy.IsConflict(rejected).Should().BeTrue();
    }

    [Fact]
    public void TheSameField_LastWrittenByTheSameDevice_IsNotAConflict()
    {
        // Duas edições de A na fila: a segunda tem base 1, mas quem escreveu a 2 foi A.
        var merge = Merge(DeviceA, baseVersion: 1, (SyncFields.Task.Title, SyncValue.String("Título de A, de novo")));

        merge.Applied.Keys.Should().Equal(SyncFields.Task.Title);
    }

    [Fact]
    public void ADeviceThatKnewTheLatestVersion_Wins()
    {
        var merge = Merge(DeviceB, baseVersion: 2, (SyncFields.Task.Title, SyncValue.String("Título de B")));

        merge.Applied.Keys.Should().Equal(SyncFields.Task.Title);
    }

    [Fact]
    public void AnEqualValue_IsNeitherAppliedNorAConflict()
    {
        var merge = Merge(DeviceB, baseVersion: 1, (SyncFields.Task.Title, SyncValue.String("Título de A")));

        merge.Applied.Should().BeEmpty();
        merge.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void TwoArchiveMarks_SayTheSameThing_AndResolveSilently()
    {
        var merge = Merge(DeviceB, baseVersion: 1, (SyncFields.Task.ArchivedAt, SyncValue.Instant(Ops.Created.AddMinutes(3))));

        merge.Applied.Should().BeEmpty();
        var rejected = merge.Rejected.Should().ContainSingle().Subject;
        rejected.Reason.Should().Be(SyncErrorCodes.Superseded);
        FieldMergePolicy.IsConflict(rejected).Should().BeFalse();
    }

    [Fact]
    public void UnarchivingWhatAnotherDeviceJustArchived_IsAVisibleConflict()
    {
        var merge = Merge(DeviceB, baseVersion: 1, (SyncFields.Task.ArchivedAt, SyncValue.Null));

        merge.Rejected.Should().ContainSingle().Which.Reason.Should().Be(SyncErrorCodes.ConcurrentChange);
    }

    [Fact]
    public void AnImmutableField_CannotChange()
    {
        var merge = Merge(DeviceA, baseVersion: 2, (SyncFields.CreatedAt, SyncValue.Instant(Ops.Created.AddDays(1))));

        merge.Error!.Code.Should().Be(SyncErrorCodes.ImmutableField);
        merge.Applied.Should().BeEmpty();
    }

    [Fact]
    public void AFieldThisServerDoesNotKnow_IsKept_ForNewerClients()
    {
        var merge = Merge(DeviceB, baseVersion: 1, ("colorOfTheFuture", SyncValue.String("#123456")));

        merge.Applied.Keys.Should().Equal("colorOfTheFuture");
    }

    private static FieldMerge Merge(Guid device, long baseVersion, params (string Name, JsonElement Value)[] fields)
    {
        var current = Current();
        var operation = Ops.Upsert(device, SyncEntityTypes.Task, current.EntityId, baseVersion, fields.ToDictionary(pair => pair.Name, pair => pair.Value));

        return FieldMergePolicy.Merge(SyncRegistry.Task, current, operation);
    }
}
