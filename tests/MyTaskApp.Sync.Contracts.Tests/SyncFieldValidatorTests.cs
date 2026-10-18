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
    public void Validate_IgnoresUnknownFields_TheServerRefusesThemApart()
    {
        // O desktop roda o Validate no pull e ignora o que não conhece; quem recusa é o push (ADR-072).
        var fields = new Dictionary<string, JsonElement> { ["colorOfTheFuture"] = SyncValue.String("?") };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false).Should().BeEmpty();
        SyncFieldValidator.FindUnknown(SyncRegistry.Task, fields).Should().Equal("colorOfTheFuture");
    }

    [Fact]
    public void FindUnknown_PointsAtTheField_TheObjectMember_AndTheListItemMember()
    {
        var task = ValidTask();
        task["isAdmin"] = SyncValue.Boolean(true);
        task[SyncFields.Task.Reminder] = SyncValue.Object(
            (SyncFields.Reminder.IsEnabled, SyncValue.Boolean(false)),
            ("role", SyncValue.String("owner")));
        var command = new Dictionary<string, JsonElement>
        {
            [SyncFields.Command.Parameters] = SyncValue.List([Parameter(), Parameter(("userId", SyncValue.Guid(Guid.NewGuid())))]),
        };

        SyncFieldValidator.FindUnknown(SyncRegistry.Task, task).Should().BeEquivalentTo("isAdmin", "reminder.role");
        SyncFieldValidator.FindUnknown(SyncRegistry.Command, command).Should().Equal("parameters[1].userId");
    }

    [Fact]
    public void FindUnknown_OnWhatTheContractKnows_IsEmpty()
    {
        var task = ValidTask();
        task[SyncFields.Task.Placement] = SyncValue.Object(
            (SyncFields.Placement.Kind, SyncValue.Integer(1)),
            (SyncFields.Placement.DeviceId, SyncValue.Guid(Guid.NewGuid())));

        SyncFieldValidator.FindUnknown(SyncRegistry.Task, task).Should().BeEmpty();
        SyncFieldValidator.FindUnknown(
                SyncRegistry.Command,
                new Dictionary<string, JsonElement> { [SyncFields.Command.Parameters] = SyncValue.List([Parameter()]) })
            .Should().BeEmpty();
    }

    [Fact]
    public void FindUnknown_WritesTheNulOfAName_AsText()
    {
        // A frase da recusa é gravada no servidor, e o PostgreSQL não guarda U+0000.
        var fields = new Dictionary<string, JsonElement> { ["is\0Admin"] = SyncValue.Boolean(true) };

        SyncFieldValidator.FindUnknown(SyncRegistry.Task, fields).Should().Equal("is\\0Admin");
    }

    [Fact]
    public void TheOptionsOfAParameter_AreLimited_ToWhatTheDomainAllows()
    {
        Dictionary<string, JsonElement> WithOptions(int length) => new()
        {
            [SyncFields.Command.Parameters] = SyncValue.List([Parameter((SyncFields.CommandParameter.Options, SyncValue.String(new string('x', length))))]),
        };

        SyncFieldValidator.Validate(SyncRegistry.Command, WithOptions(25_049), isCreate: false).Should().BeEmpty();
        SyncFieldValidator.Validate(SyncRegistry.Command, WithOptions(25_050), isCreate: false)
            .Should().ContainSingle(error => error.Field == "parameters[0].options");
    }

    [Fact]
    public void ALongDescription_HasNoFieldLimit()
    {
        // A descrição é segurada pelo teto da entidade no servidor, não por um máximo de campo.
        var fields = new Dictionary<string, JsonElement> { [SyncFields.Task.Description] = SyncValue.String(new string('x', 100_000)) };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false).Should().BeEmpty();
    }

    [Fact]
    public void ANulCharacter_IsRefused_InAnyFieldKnownOrNot()
    {
        var cases = new Dictionary<string, JsonElement>[]
        {
            new() { [SyncFields.Task.Title] = SyncValue.String("titulo\0nul") },
            new() { ["colorOfTheFuture"] = SyncValue.String("a\0b") },
            new() { ["colorOfTheFuture"] = SyncValue.List([SyncValue.Object(("x\0y", SyncValue.Integer(1)))]) },
            new() { [SyncFields.Task.Reminder] = SyncValue.Object(("note", SyncValue.String("\0"))) },
        };

        foreach (var fields in cases)
        {
            SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false)
                .Should().Contain(error => error.Message.Contains("U+0000"));
        }
    }

    [Fact]
    public void ANulInTheFieldName_IsRefused_WithoutTheNulInTheError()
    {
        var fields = new Dictionary<string, JsonElement> { ["ti\0tle"] = SyncValue.String("x") };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false)
            .Should().ContainSingle().Which.Field.Should().Be("ti\\0tle");
    }

    [Fact]
    public void LineBreaksAndTabs_Pass()
    {
        var fields = new Dictionary<string, JsonElement> { [SyncFields.Task.Description] = SyncValue.String("linha 1\r\n\tlinha 2") };

        SyncFieldValidator.Validate(SyncRegistry.Task, fields, isCreate: false).Should().BeEmpty();
    }

    [Fact]
    public void Placement_IsOptional_ForEverythingWrittenBeforeIt()
    {
        // ValidTask não tem placement, como tudo o que o servidor gravou antes do ADR-062.
        SyncFieldValidator.Validate(SyncRegistry.Task, ValidTask(), isCreate: true).Should().BeEmpty();
        SyncFieldValidator.Validate(SyncRegistry.Tag, new Dictionary<string, JsonElement>
        {
            [SyncFields.Tag.Name] = SyncValue.String("Casa"),
            [SyncFields.Tag.ColorHex] = SyncValue.String("#00FF00"),
            [SyncFields.CreatedAt] = SyncValue.Instant(Created),
        }, isCreate: true).Should().BeEmpty();
    }

    [Fact]
    public void Placement_ChecksTheKind()
    {
        Dictionary<string, JsonElement> Placement(JsonElement kind) => new()
        {
            [SyncFields.Task.Placement] = SyncValue.Object(
                (SyncFields.Placement.Kind, kind),
                (SyncFields.Placement.DeviceId, SyncValue.Guid(Guid.NewGuid()))),
        };

        SyncFieldValidator.Validate(SyncRegistry.Task, Placement(SyncValue.Integer(0)), isCreate: false).Should().BeEmpty();
        SyncFieldValidator.Validate(SyncRegistry.Task, Placement(SyncValue.Integer(1)), isCreate: false).Should().BeEmpty();
        SyncFieldValidator.Validate(SyncRegistry.Task, Placement(SyncValue.Integer(7)), isCreate: false).Should().ContainSingle();
        SyncFieldValidator.Validate(SyncRegistry.Task, Placement(SyncValue.Null), isCreate: false).Should().ContainSingle();
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

    /// <summary>Um item de <c>parameters</c> completo; os pares extras sobrescrevem ou acrescentam.</summary>
    private static JsonElement Parameter(params (string Name, JsonElement Value)[] extra)
    {
        var members = new Dictionary<string, JsonElement>
        {
            [SyncFields.CommandParameter.Id] = SyncValue.Guid(Guid.NewGuid()),
            [SyncFields.CommandParameter.Name] = SyncValue.String("ambiente"),
            [SyncFields.CommandParameter.Label] = SyncValue.Null,
            [SyncFields.CommandParameter.Type] = SyncValue.Integer(0),
            [SyncFields.CommandParameter.DefaultValue] = SyncValue.Null,
            [SyncFields.CommandParameter.IsRequired] = SyncValue.Boolean(true),
            [SyncFields.CommandParameter.Options] = SyncValue.Null,
        };

        foreach (var (name, value) in extra)
        {
            members[name] = value;
        }

        return SyncValue.Object([.. members.Select(pair => (pair.Key, pair.Value))]);
    }

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
