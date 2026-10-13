using System.Text.Json;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Sync.Contracts.Tests;

public sealed class SyncFieldValidatorTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AValidTaskCreation_Passes()
    {
        SyncFieldValidator.Validate(SyncRegistry.Task, ValidTask(), isCreate: true).Should().BeEmpty();
    }

    [Fact]
    public void Creation_WithoutARequiredField_IsRefused()
    {
        var fields = ValidTask();
        fields.Remove(SyncFields.Task.Title);

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: true)
            .Should().ContainSingle(error => error.Field == SyncFields.Task.Title);
    }

    [Fact]
    public void Update_WithOnlyTheChangedField_Passes()
    {
        var fields = new Dictionary<string, JsonElement> { [SyncFields.Task.Title] = SyncValue.String("Outro") };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false).Should().BeEmpty();
    }

    [Fact]
    public void ATitleAboveTheLimit_IsRefused()
    {
        var fields = new Dictionary<string, JsonElement> { [SyncFields.Task.Title] = SyncValue.String(new string('x', 201)) };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false)
            .Should().ContainSingle().Which.Message.Should().Contain("200");
    }

    [Fact]
    public void ARequiredFieldSetToNull_IsRefused()
    {
        var fields = new Dictionary<string, JsonElement> { [SyncFields.Task.Title] = SyncValue.Null };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false).Should().ContainSingle();
    }

    [Fact]
    public void WrongKinds_AreRefusedWithThePath()
    {
        var fields = new Dictionary<string, JsonElement>
        {
            [SyncFields.Task.Priority] = SyncValue.String("alta"),
            [SyncFields.Task.ArchivedAt] = SyncValue.String("ontem"),
            [SyncFields.Task.Reminder] = SyncValue.Object(
                (SyncFields.Reminder.IsEnabled, SyncValue.Integer(1)),
                (SyncFields.Reminder.Anchor, SyncValue.Integer(0)),
                (SyncFields.Reminder.Offset, SyncValue.Integer(0)),
                (SyncFields.Reminder.Repeat, SyncValue.Boolean(false)),
                (SyncFields.Reminder.RepeatEvery, SyncValue.Integer(0))),
        };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false)
            .Select(error => error.Field)
            .Should().BeEquivalentTo(
                SyncFields.Task.Priority,
                SyncFields.Task.ArchivedAt,
                "reminder.isEnabled",
                "reminder.channels");
    }

    [Fact]
    public void IntegerRanges_AreChecked()
    {
        var note = new Dictionary<string, JsonElement>
        {
            [SyncFields.StickyNote.Appearance] = SyncValue.Object(
                (SyncFields.StickyNote.ColorMode, SyncValue.Integer(0)),
                (SyncFields.StickyNote.Emphasis, SyncValue.Integer(0)),
                (SyncFields.StickyNote.Opacity, SyncValue.Integer(40))),
        };

        SyncFieldValidator.Validate(SyncRegistry.StickyNote, note, isCreate: false)
            .Should().ContainSingle(error => error.Field == "appearance.opacity");
    }

    [Fact]
    public void Lists_AreLimitedAndEachItemIsChecked()
    {
        var tooMany = new Dictionary<string, JsonElement>
        {
            [SyncFields.Development.Commands] = SyncValue.List(Enumerable.Range(0, 51).Select(i => SyncValue.String($"c{i}"))),
        };
        var badItem = new Dictionary<string, JsonElement>
        {
            [SyncFields.Development.Commands] = SyncValue.List([SyncValue.String("ok"), SyncValue.Integer(3)]),
        };

        SyncFieldValidator.Validate(SyncRegistry.Development, tooMany, isCreate: false).Should().ContainSingle();
        SyncFieldValidator.Validate(SyncRegistry.Development, badItem, isCreate: false)
            .Should().ContainSingle(error => error.Field == "commands[1]");
    }

    [Fact]
    public void UnknownFields_Pass_ForNewerClients()
    {
        var fields = new Dictionary<string, JsonElement> { ["colorOfTheFuture"] = SyncValue.String("?") };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false).Should().BeEmpty();
    }

    [Fact]
    public void UniqueKey_IsCaseInsensitiveAndScoped()
    {
        var tagId = Guid.NewGuid();

        SyncUniqueKey.Build(SyncRegistry.Tag, Lookup(new() { [SyncFields.Tag.Name] = SyncValue.String(" Ágil ") }))
            .Should().Be("ágil");

        SyncUniqueKey.Build(
                SyncRegistry.TagDirectory,
                Lookup(new()
                {
                    [SyncFields.TagDirectory.TagId] = SyncValue.Guid(tagId),
                    [SyncFields.TagDirectory.Alias] = SyncValue.String("@API"),
                }))
            .Should().Be($"{tagId:D}/@api");

        SyncUniqueKey.Build(SyncRegistry.Task, Lookup(new())).Should().BeNull();
        SyncUniqueKey.Build(SyncRegistry.Tag, Lookup(new())).Should().BeNull();
    }

    private static Func<string, JsonElement> Lookup(Dictionary<string, JsonElement> fields) =>
        name => fields.GetValueOrDefault(name, SyncValue.Null);

    private static Dictionary<string, JsonElement> ValidTask() => new()
    {
        [SyncFields.Task.Title] = SyncValue.String("Pagar boleto"),
        [SyncFields.Task.Description] = SyncValue.Null,
        [SyncFields.Task.Priority] = SyncValue.Integer(1),
        [SyncFields.Task.NextAction] = SyncValue.Null,
        [SyncFields.Task.Estimate] = SyncValue.Duration(TimeSpan.FromMinutes(30)),
        [SyncFields.Task.Reminder] = SyncValue.Object(
            (SyncFields.Reminder.IsEnabled, SyncValue.Boolean(false)),
            (SyncFields.Reminder.Anchor, SyncValue.Integer(0)),
            (SyncFields.Reminder.Offset, SyncValue.Duration(TimeSpan.Zero)),
            (SyncFields.Reminder.Repeat, SyncValue.Boolean(false)),
            (SyncFields.Reminder.RepeatEvery, SyncValue.Duration(TimeSpan.FromMinutes(15))),
            (SyncFields.Reminder.Channels, SyncValue.Integer(1))),
        [SyncFields.Task.External] = SyncValue.Null,
        [SyncFields.Task.DeadlineAlerts] = SyncValue.Null,
        [SyncFields.Task.ArchivedAt] = SyncValue.Null,
        [SyncFields.Task.DeletedAt] = SyncValue.Null,
        [SyncFields.Task.DeletedBy] = SyncValue.Null,
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
    };
}
