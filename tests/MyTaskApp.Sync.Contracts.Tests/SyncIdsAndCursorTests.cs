namespace MyTaskApp.Sync.Contracts.Tests;

public sealed class SyncIdsAndCursorTests
{
    [Fact]
    public void TaskTag_IsDeterministic_AndAVersion5Uuid()
    {
        var task = new Guid("019a1f2e-0000-7000-8000-000000000001");
        var tag = new Guid("019a1f2e-0000-7000-8000-000000000002");

        var id = SyncIds.TaskTag(task, tag);

        id.Should().Be(SyncIds.TaskTag(task, tag));
        id.Should().NotBe(SyncIds.TaskTag(tag, task));
        id.Version.Should().Be(5);
        id.Variant.Should().Be(8, "a variante RFC é 10xx");

        // Valor fixo, calculado fora do .NET (uuid + hashlib do Python): mudar o
        // algoritmo separaria os vínculos de quem já sincronizou.
        id.Should().Be(new Guid("6a09e178-31a2-5b64-85f3-f94e1b7809f6"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(987_654_321_012)]
    public void Cursor_RoundTrips(long sequence)
    {
        var cursor = SyncCursor.Encode(sequence);

        SyncCursor.TryDecode(cursor, out var back).Should().BeTrue();
        back.Should().Be(sequence);
        cursor.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyCursor_IsTheStart(string? cursor)
    {
        SyncCursor.TryDecode(cursor, out var sequence).Should().BeTrue();
        sequence.Should().Be(0);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("MS5hYmM")]
    [InlineData("Mi4xMg")]
    public void GarbageCursor_IsRefused_NeverReadAsZero(string cursor)
    {
        SyncCursor.TryDecode(cursor, out _).Should().BeFalse();
    }

    [Fact]
    public void ErrorCodes_OnlyAMissingParentIsRetryable()
    {
        SyncErrorCodes.IsRetryable(SyncErrorCodes.ParentMissing).Should().BeTrue();
        SyncErrorCodes.IsRetryable(SyncErrorCodes.ValidationFailed).Should().BeFalse();
        SyncErrorCodes.IsRetryable(SyncErrorCodes.EntityDeleted).Should().BeFalse();
    }
}
