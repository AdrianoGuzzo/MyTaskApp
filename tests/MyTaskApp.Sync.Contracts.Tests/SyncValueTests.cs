using System.Text.Json;

namespace MyTaskApp.Sync.Contracts.Tests;

/// <summary>O formato dos valores: o mesmo dado vira sempre o mesmo texto, nos dois lados.</summary>
public sealed class SyncValueTests
{
    [Fact]
    public void Instants_AreWrittenInUtc_SoTheSameMomentCompareEqual()
    {
        var local = new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.FromHours(-3));
        var utc = local.ToUniversalTime();

        SyncValue.Instant(local).GetString().Should().Be("2026-10-13T12:30:00.0000000Z");
        SyncValue.AreEqual(SyncValue.Instant(local), SyncValue.Instant(utc)).Should().BeTrue();
        SyncValue.ReadInstant(SyncValue.Instant(local)).Should().Be(local);
    }

    [Fact]
    public void Instants_KeepTicks()
    {
        var precise = new DateTimeOffset(638_960_000_000_000_123, TimeSpan.Zero);

        SyncValue.ReadInstant(SyncValue.Instant(precise))!.Value.Ticks.Should().Be(precise.Ticks);
    }

    [Fact]
    public void WallClockValues_RoundTrip()
    {
        SyncValue.Date(new DateOnly(2026, 2, 3)).GetString().Should().Be("2026-02-03");
        SyncValue.Time(new TimeOnly(9, 30)).GetString().Should().Be("09:30:00");
        SyncValue.ReadTime(SyncValue.Time(new TimeOnly(9, 30, 15, 250))).Should().Be(new TimeOnly(9, 30, 15, 250));
        SyncValue.ReadDate(SyncValue.Date(new DateOnly(2026, 2, 3))).Should().Be(new DateOnly(2026, 2, 3));
        SyncValue.ReadDuration(SyncValue.Duration(TimeSpan.FromMinutes(90))).Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Nulls_AreNullForEveryKind()
    {
        SyncValue.ReadString(SyncValue.String(null)).Should().BeNull();
        SyncValue.ReadInteger(SyncValue.Integer(null)).Should().BeNull();
        SyncValue.ReadInstant(SyncValue.Instant(null)).Should().BeNull();
        SyncValue.ReadGuid(SyncValue.Guid(null)).Should().BeNull();
        SyncValue.ReadDate(SyncValue.Date(null)).Should().BeNull();
        SyncValue.ReadTime(SyncValue.Time(null)).Should().BeNull();
        SyncValue.ReadDuration(SyncValue.Duration(null)).Should().BeNull();
        SyncValue.IsNull(default).Should().BeTrue();
    }

    [Fact]
    public void Objects_CompareWithoutCaringAboutMemberOrder()
    {
        var first = SyncValue.Object(("a", SyncValue.Integer(1)), ("b", SyncValue.String("x")));
        var second = SyncValue.Object(("b", SyncValue.String("x")), ("a", SyncValue.Integer(1)));
        var different = SyncValue.Object(("a", SyncValue.Integer(2)), ("b", SyncValue.String("x")));

        SyncValue.AreEqual(first, second).Should().BeTrue();
        SyncValue.AreEqual(first, different).Should().BeFalse();
        SyncValue.Member(first, "b").GetString().Should().Be("x");
        SyncValue.IsNull(SyncValue.Member(first, "missing")).Should().BeTrue();
    }

    [Fact]
    public void Messages_RoundTripThroughTheSharedOptions()
    {
        var operation = new SyncOperation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "task",
            Guid.NewGuid(),
            SyncOperationKind.Upsert,
            BaseVersion: 3,
            new Dictionary<string, JsonElement> { ["title"] = SyncValue.String("Oi") },
            new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(new PushRequest(1, operation.DeviceId, [operation]), SyncJson.Options);
        var back = JsonSerializer.Deserialize<PushRequest>(json, SyncJson.Options)!;

        json.Should().Contain("\"kind\":\"upsert\"").And.Contain("\"baseVersion\":3");
        back.Operations.Should().ContainSingle();
        back.Operations[0].Fields["title"].GetString().Should().Be("Oi");
        (back.Operations[0] with { Fields = operation.Fields }).Should().Be(operation);
    }
}
