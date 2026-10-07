using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tests.StickyNotes;

/// <summary>Os casos de uso do post-it (ADR-054), cada um gravando uma vez só.</summary>
public class StickyNoteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeStickyNoteRepository _notes = new();
    private readonly FakeTagRepository _tags = new();
    private readonly FakeTimeProvider _time = new(Now);

    private FakeStickyNoteQuery Query => new(_notes, _tags);

    private StickyNote Seed(string content = "Falar com o João")
    {
        var note = StickyNote.Create(Now.AddHours(-1));
        note.Edit(content, Now.AddHours(-1));
        _notes.Seed(note);
        return note;
    }

    [Fact]
    public async Task Create_SavesABlankOpenNote()
    {
        var view = await new CreateStickyNoteHandler(_notes, _notes, _time, NullLogger<CreateStickyNoteHandler>.Instance)
            .HandleAsync(new CreateStickyNote(), Ct);

        var note = _notes.Notes.Should().ContainSingle().Subject;
        view.Id.Should().Be(note.Id);
        view.Content.Should().BeEmpty();
        view.IsOpen.Should().BeTrue();
        view.Color.EffectiveMode.Should().Be(StickyNoteColorMode.Theme);
        view.Color.HueHex.Should().BeNull();
        view.CreatedAt.Should().Be(Now);
        view.Lifecycle.Should().Be(StickyNoteLifecycle.Active);
        _notes.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Edit_SavesTheTextWithTheCurrentTime()
    {
        var note = Seed();

        await new EditStickyNoteHandler(_notes, _notes, _time, NullLogger<EditStickyNoteHandler>.Instance)
            .HandleAsync(new EditStickyNote(note.Id, "Texto novo"), Ct);

        note.Content.Should().Be("Texto novo");
        note.UpdatedAt.Should().Be(Now);
        _notes.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Edit_OfAMissingNote_IsAReadableFailure()
    {
        var edit = () => new EditStickyNoteHandler(_notes, _notes, _time, NullLogger<EditStickyNoteHandler>.Instance)
            .HandleAsync(new EditStickyNote(Guid.CreateVersion7(), "x"), Ct);

        await edit.Should().ThrowAsync<DomainException>().WithMessage("Post-it não encontrado.");
        _notes.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ChangeAppearance_WithTagColor_ReturnsTheTagNameAndHue()
    {
        var note = Seed();
        var tag = Tag.Create("ECO CORE", "#3B82F6", Now);
        _tags.Seed(tag);

        var view = await new ChangeStickyNoteAppearanceHandler(
                _notes, _tags, _notes, Query, NullLogger<ChangeStickyNoteAppearanceHandler>.Instance)
            .HandleAsync(
                new ChangeStickyNoteAppearance(note.Id, tag.Id, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Attention, 90),
                Ct);

        view.TagName.Should().Be("ECO CORE");
        view.Color.HueHex.Should().Be("#3B82F6");
        view.Emphasis.Should().Be(StickyNoteEmphasis.Attention);
        view.Opacity.Should().Be(90);
        _notes.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task ChangeAppearance_FollowsTheTagWhenItIsRecolored()
    {
        var note = Seed();
        var tag = Tag.Create("Infra", "#3B82F6", Now);
        _tags.Seed(tag);
        var handler = new ChangeStickyNoteAppearanceHandler(
            _notes, _tags, _notes, Query, NullLogger<ChangeStickyNoteAppearanceHandler>.Instance);
        await handler.HandleAsync(
            new ChangeStickyNoteAppearance(note.Id, tag.Id, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100), Ct);

        tag.Update("Infra", "#EF4444");

        (await Query.FindAsync(note.Id, Ct))!.Color.HueHex.Should().Be("#EF4444");
    }

    [Fact]
    public async Task ChangeAppearance_WithACustomColor_IgnoresTheTagColor()
    {
        var note = Seed();
        var tag = Tag.Create("Infra", "#3B82F6", Now);
        _tags.Seed(tag);

        var view = await new ChangeStickyNoteAppearanceHandler(
                _notes, _tags, _notes, Query, NullLogger<ChangeStickyNoteAppearanceHandler>.Instance)
            .HandleAsync(
                new ChangeStickyNoteAppearance(note.Id, tag.Id, StickyNoteColorMode.Palette, StickyNotePaletteColor.Yellow, StickyNoteEmphasis.Normal, 100),
                Ct);

        view.TagName.Should().Be("Infra");
        view.Color.HueHex.Should().Be(StickyNotePalette.HexOf(StickyNotePaletteColor.Yellow));
    }

    [Fact]
    public async Task ChangeAppearance_WithADeletedTag_IsRefusedAndSavesNothing()
    {
        var note = Seed();

        var change = () => new ChangeStickyNoteAppearanceHandler(
                _notes, _tags, _notes, Query, NullLogger<ChangeStickyNoteAppearanceHandler>.Instance)
            .HandleAsync(
                new ChangeStickyNoteAppearance(note.Id, Guid.CreateVersion7(), StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100),
                Ct);

        await change.Should().ThrowAsync<DomainException>().WithMessage("*etiquetas não existe mais*");
        note.TagId.Should().BeNull();
        _notes.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ChangeAppearance_WithoutATag_FallsBackToTheEntityWhenTheQueryMisses()
    {
        var note = Seed();
        var emptyQuery = new FakeStickyNoteQuery(new FakeStickyNoteRepository());

        var view = await new ChangeStickyNoteAppearanceHandler(
                _notes, _tags, _notes, emptyQuery, NullLogger<ChangeStickyNoteAppearanceHandler>.Instance)
            .HandleAsync(
                new ChangeStickyNoteAppearance(note.Id, null, StickyNoteColorMode.Palette, StickyNotePaletteColor.Green, StickyNoteEmphasis.Normal, 80),
                Ct);

        view.Id.Should().Be(note.Id);
        view.Opacity.Should().Be(80);
    }

    [Fact]
    public async Task Pin_TogglesOnlyThePin()
    {
        var note = Seed();
        note.Place(10, 20, 300, 250);

        await new PinStickyNoteHandler(_notes, _notes, NullLogger<PinStickyNoteHandler>.Instance)
            .HandleAsync(new PinStickyNote(note.Id, true), Ct);

        note.IsPinned.Should().BeTrue();
        (note.X, note.Y, note.Width, note.Height).Should().Be((10, 20, 300.0, 250.0));
        _notes.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Closing_KeepsAWrittenNote()
    {
        var note = Seed();

        var result = await new SetStickyNoteOpenHandler(_notes, _notes, NullLogger<SetStickyNoteOpenHandler>.Instance)
            .HandleAsync(new SetStickyNoteOpen(note.Id, false), Ct);

        result.Discarded.Should().BeFalse();
        note.IsOpen.Should().BeFalse();
        _notes.Notes.Should().ContainSingle();
    }

    [Fact]
    public async Task Closing_DiscardsABlankNote()
    {
        var note = StickyNote.Create(Now);
        _notes.Seed(note);

        var result = await new SetStickyNoteOpenHandler(_notes, _notes, NullLogger<SetStickyNoteOpenHandler>.Instance)
            .HandleAsync(new SetStickyNoteOpen(note.Id, false), Ct);

        result.Discarded.Should().BeTrue();
        _notes.Notes.Should().BeEmpty();
        _notes.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Opening_MarksTheNoteOpen()
    {
        var note = Seed();
        note.Close();

        var result = await new SetStickyNoteOpenHandler(_notes, _notes, NullLogger<SetStickyNoteOpenHandler>.Instance)
            .HandleAsync(new SetStickyNoteOpen(note.Id, true), Ct);

        result.Discarded.Should().BeFalse();
        note.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Place_SavesTheGeometry()
    {
        var note = Seed();

        await new PlaceStickyNoteHandler(_notes, _notes, NullLogger<PlaceStickyNoteHandler>.Instance)
            .HandleAsync(new PlaceStickyNote(note.Id, -1500, 300, 320, 240), Ct);

        (note.X, note.Y, note.Width, note.Height).Should().Be((-1500, 300, 320.0, 240.0));
        _notes.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Place_OfADiscardedNote_IsQuietlyIgnored()
    {
        var place = () => new PlaceStickyNoteHandler(_notes, _notes, NullLogger<PlaceStickyNoteHandler>.Instance)
            .HandleAsync(new PlaceStickyNote(Guid.CreateVersion7(), 0, 0, 300, 200), Ct);

        await place.Should().NotThrowAsync();
        _notes.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ArchiveRestoreTrashAndPurge_FollowTheLifecycle()
    {
        var note = Seed();

        await new ArchiveStickyNoteHandler(_notes, _notes, _time, NullLogger<ArchiveStickyNoteHandler>.Instance)
            .HandleAsync(new ArchiveStickyNote(note.Id), Ct);
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Archived);
        note.ArchivedAt.Should().Be(Now);

        await new RestoreStickyNoteHandler(_notes, _notes, NullLogger<RestoreStickyNoteHandler>.Instance)
            .HandleAsync(new RestoreStickyNote(note.Id), Ct);
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Active);

        await new MoveStickyNoteToTrashHandler(_notes, _notes, _time, NullLogger<MoveStickyNoteToTrashHandler>.Instance)
            .HandleAsync(new MoveStickyNoteToTrash(note.Id), Ct);
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Trashed);
        note.DeletedAt.Should().Be(Now);

        await new RestoreStickyNoteFromTrashHandler(_notes, _notes, NullLogger<RestoreStickyNoteFromTrashHandler>.Instance)
            .HandleAsync(new RestoreStickyNoteFromTrash(note.Id), Ct);
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Active);

        note.MoveToTrash(Now);
        await new PurgeStickyNoteHandler(_notes, _notes, NullLogger<PurgeStickyNoteHandler>.Instance)
            .HandleAsync(new PurgeStickyNote(note.Id), Ct);
        _notes.Notes.Should().BeEmpty();
        _notes.SaveCount.Should().Be(5);
    }

    [Fact]
    public async Task Purge_OfAnActiveWrittenNote_IsRefused()
    {
        var note = Seed();

        var purge = () => new PurgeStickyNoteHandler(_notes, _notes, NullLogger<PurgeStickyNoteHandler>.Instance)
            .HandleAsync(new PurgeStickyNote(note.Id), Ct);

        await purge.Should().ThrowAsync<DomainException>();
        _notes.Notes.Should().ContainSingle();
        _notes.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TheQueries_ForwardToTheReadSide()
    {
        var pinned = Seed("Fixado");
        pinned.Pin(true);
        var loose = Seed("Solto");
        var archived = Seed("Guardado");
        archived.Archive(Now);

        var active = await new GetStickyNotesHandler(Query).HandleAsync(new GetStickyNotes(StickyNoteScope.Active), Ct);
        var stored = await new GetStickyNotesHandler(Query).HandleAsync(new GetStickyNotes(StickyNoteScope.Archived), Ct);
        var startup = await new GetStartupStickyNotesHandler(Query).HandleAsync(new GetStartupStickyNotes(), Ct);
        var one = await new GetStickyNoteHandler(Query).HandleAsync(new GetStickyNote(loose.Id), Ct);

        active.Select(row => row.Id).Should().BeEquivalentTo([pinned.Id, loose.Id]);
        stored.Should().ContainSingle().Which.Title.Should().Be("Guardado");
        startup.Should().ContainSingle().Which.Id.Should().Be(pinned.Id);
        one.Content.Should().Be("Solto");

        var missing = () => new GetStickyNoteHandler(Query).HandleAsync(new GetStickyNote(Guid.CreateVersion7()), Ct);
        await missing.Should().ThrowAsync<DomainException>();
    }
}
