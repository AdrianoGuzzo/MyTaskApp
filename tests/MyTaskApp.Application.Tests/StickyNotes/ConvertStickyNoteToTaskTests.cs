using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tests.StickyNotes;

/// <summary>Post-it → tarefa (ADR-054): rápido, sem pergunta, e tudo ou nada.</summary>
public class ConvertStickyNoteToTaskTests
{
    // 14:00 UTC = 11:00 em São Paulo, no mesmo dia.
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeStickyNoteRepository _notes = new();
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTagRepository _tags = new();
    private readonly FakeTaskAuditLog _audit = new();
    private readonly FakeTimeProvider _time = new(Now);

    private ConvertStickyNoteToTaskHandler Handler() =>
        new(
            _notes,
            _tasks,
            _tags,
            _tasks,
            new FakeReminderSettingsStore(),
            _audit,
            new FakeCurrentUser(),
            TestClock.Over(_time),
            _time,
            NullLogger<ConvertStickyNoteToTaskHandler>.Instance);

    private StickyNote Seed(string content)
    {
        var note = StickyNote.Create(Now.AddHours(-2));
        note.Edit(content, Now.AddHours(-2));
        _notes.Seed(note);
        return note;
    }

    [Fact]
    public async Task TheWholeNote_BecomesATaskForTodayAndTheNoteIsArchived()
    {
        var note = Seed("Verificar performance do dashboard\nO gráfico demora 4s para abrir");

        var result = await Handler().HandleAsync(new ConvertStickyNoteToTask(note.Id), Ct);

        var task = _tasks.Tasks.Should().ContainSingle().Subject;
        task.Id.Should().Be(result.TaskId);
        task.Title.Should().Be("Verificar performance do dashboard");
        task.Description.Should().Be("Verificar performance do dashboard\nO gráfico demora 4s para abrir");
        task.Occurrences.Single().ScheduledDate.Should().Be(new DateOnly(2026, 10, 7));
        task.Occurrences.Single().ScheduledTime.Should().BeNull();

        result.Title.Should().Be(task.Title);
        result.NoteArchived.Should().BeTrue();
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Archived);
        note.ConvertedTaskId.Should().Be(task.Id);
        _audit.Operations.Should().Equal(TaskAuditOperation.Created);

        // Tarefa e post-it arquivado na mesma transação.
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task TheTag_ComesAlong()
    {
        var tag = Tag.Create("ECO CORE", "#3B82F6", Now);
        _tags.Seed(tag);
        var note = Seed("Perguntar ao cliente sobre integração");
        note.ChangeAppearance(tag.Id, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);

        await Handler().HandleAsync(new ConvertStickyNoteToTask(note.Id), Ct);

        _tasks.Tasks.Single().Tags.Select(link => link.TagId).Should().Equal(tag.Id);
    }

    [Fact]
    public async Task ATagDeletedMeanwhile_IsDroppedInsteadOfRefusingTheConversion()
    {
        var note = Seed("Ideia de cache");
        note.ChangeAppearance(Guid.CreateVersion7(), StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);

        await Handler().HandleAsync(new ConvertStickyNoteToTask(note.Id), Ct);

        _tasks.Tasks.Single().Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task ASelection_BecomesATaskAndTheNoteStays()
    {
        var note = Seed("Falar com Marcelo sobre deploy.\nPerguntar sobre homologação.\nVerificar logs.");

        var result = await Handler().HandleAsync(
            new ConvertStickyNoteToTask(note.Id, "Perguntar sobre homologação."),
            Ct);

        _tasks.Tasks.Single().Title.Should().Be("Perguntar sobre homologação.");
        _tasks.Tasks.Single().Description.Should().BeNull();
        result.NoteArchived.Should().BeFalse();
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Active);
        note.ConvertedTaskId.Should().BeNull();
    }

    [Fact]
    public async Task ABlankNote_IsRefusedAndNothingIsSaved()
    {
        var note = StickyNote.Create(Now);
        _notes.Seed(note);

        var convert = () => Handler().HandleAsync(new ConvertStickyNoteToTask(note.Id), Ct);

        await convert.Should().ThrowAsync<DomainException>().WithMessage("*vazio*");
        _tasks.Tasks.Should().BeEmpty();
        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task AnArchivedNote_IsRefusedBeforeAnyTaskIsCreated()
    {
        var note = Seed("Guardado");
        note.Archive(Now);

        var convert = () => Handler().HandleAsync(new ConvertStickyNoteToTask(note.Id, "Guardado"), Ct);

        await convert.Should().ThrowAsync<DomainException>().WithMessage("*arquivado*");
        _tasks.Tasks.Should().BeEmpty();
        _audit.Entries.Should().BeEmpty();
    }
}
