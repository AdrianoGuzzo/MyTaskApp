using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Domain.Tests.StickyNotes;

/// <summary>
/// O post-it (ADR-054): conteúdo, aparência, janela e ciclo de vida, cada um
/// mexendo só no que é seu.
/// </summary>
public class StickyNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid TagId = Guid.CreateVersion7();

    [Fact]
    public void ANewNote_IsBlankOpenUnpinnedAndActive()
    {
        var note = StickyNote.Create(Now);

        note.Id.Should().NotBeEmpty();
        note.Content.Should().BeEmpty();
        note.IsBlank.Should().BeTrue();
        note.IsOpen.Should().BeTrue();
        note.IsPinned.Should().BeFalse();
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Active);
        note.ColorMode.Should().Be(StickyNoteColorMode.Theme);
        note.Emphasis.Should().Be(StickyNoteEmphasis.Normal);
        note.Opacity.Should().Be(100);
        note.Width.Should().Be(StickyNote.DefaultWidth);
        note.Height.Should().Be(StickyNote.DefaultHeight);
        note.X.Should().BeNull();
        note.CreatedAt.Should().Be(Now);
        note.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Editing_ChangesTheTextAndTheUpdateTime()
    {
        var note = StickyNote.Create(Now);

        note.Edit("Falar com o Marcelo\nsobre o deploy", Now.AddMinutes(1));

        note.Content.Should().Be("Falar com o Marcelo\nsobre o deploy");
        note.IsBlank.Should().BeFalse();
        note.UpdatedAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void EditingWithTheSameText_DoesNotTouchTheUpdateTime()
    {
        var note = StickyNote.Create(Now);
        note.Edit("igual", Now.AddMinutes(1));

        note.Edit("igual", Now.AddMinutes(5));

        note.UpdatedAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void OnlyWhitespace_IsTheSameAsNothing()
    {
        var note = StickyNote.Create(Now);

        note.Edit("  \n\t ", Now);

        note.Content.Should().BeEmpty();
        note.IsBlank.Should().BeTrue();
    }

    [Fact]
    public void TextAboveTheLimit_IsRefusedAndTheOldTextStays()
    {
        var note = StickyNote.Create(Now);
        note.Edit("antes", Now);

        var paste = () => note.Edit(new string('x', StickyNote.MaxContentLength + 1), Now);

        paste.Should().Throw<DomainException>().WithMessage("*não pode passar de*");
        note.Content.Should().Be("antes");
    }

    [Fact]
    public void PaletteColor_IsKeptOnlyInPaletteMode()
    {
        var note = StickyNote.Create(Now);

        note.ChangeAppearance(null, StickyNoteColorMode.Palette, StickyNotePaletteColor.Blue, StickyNoteEmphasis.Normal, 100);
        note.PaletteColor.Should().Be(StickyNotePaletteColor.Blue);

        note.ChangeAppearance(null, StickyNoteColorMode.Theme, StickyNotePaletteColor.Blue, StickyNoteEmphasis.Normal, 100);
        note.PaletteColor.Should().BeNull();
        note.ColorMode.Should().Be(StickyNoteColorMode.Theme);
    }

    [Fact]
    public void TagColor_UsesTheAssociatedTag()
    {
        var note = StickyNote.Create(Now);

        note.ChangeAppearance(TagId, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Attention, 90);

        note.TagId.Should().Be(TagId);
        note.ColorMode.Should().Be(StickyNoteColorMode.Tag);
        note.EffectiveColorMode.Should().Be(StickyNoteColorMode.Tag);
        note.Emphasis.Should().Be(StickyNoteEmphasis.Attention);
        note.Opacity.Should().Be(90);
    }

    [Fact]
    public void ATagCanBeAssociatedWhileKeepingACustomColor()
    {
        var note = StickyNote.Create(Now);

        note.ChangeAppearance(TagId, StickyNoteColorMode.Palette, StickyNotePaletteColor.Green, StickyNoteEmphasis.Normal, 100);

        note.TagId.Should().Be(TagId);
        note.EffectiveColorMode.Should().Be(StickyNoteColorMode.Palette);
    }

    [Fact]
    public void TagColorWithoutATag_IsRefused()
    {
        var note = StickyNote.Create(Now);

        var change = () => note.ChangeAppearance(null, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);

        change.Should().Throw<DomainException>().WithMessage("*etiqueta*");
        note.ColorMode.Should().Be(StickyNoteColorMode.Theme);
    }

    [Fact]
    public void PaletteModeWithoutAColor_IsRefused()
    {
        var note = StickyNote.Create(Now);

        var change = () => note.ChangeAppearance(null, StickyNoteColorMode.Palette, null, StickyNoteEmphasis.Normal, 100);

        change.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(69)]
    [InlineData(101)]
    public void OpacityOutsideTheRange_IsRefusedAtomically(int opacity)
    {
        var note = StickyNote.Create(Now);

        var change = () => note.ChangeAppearance(TagId, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Attention, opacity);

        change.Should().Throw<DomainException>().WithMessage("*opacidade*");

        // Nada da chamada recusada vazou para a entidade.
        note.TagId.Should().BeNull();
        note.Emphasis.Should().Be(StickyNoteEmphasis.Normal);
    }

    [Fact]
    public void UnknownEnumValues_AreRefused()
    {
        var note = StickyNote.Create(Now);

        var badMode = () => note.ChangeAppearance(null, (StickyNoteColorMode)42, null, StickyNoteEmphasis.Normal, 100);
        var badColor = () => note.ChangeAppearance(null, StickyNoteColorMode.Palette, (StickyNotePaletteColor)42, StickyNoteEmphasis.Normal, 100);

        badMode.Should().Throw<DomainException>();
        badColor.Should().Throw<DomainException>();
    }

    [Fact]
    public void ADeletedTag_FallsBackToTheThemeColor()
    {
        var note = StickyNote.Create(Now);
        note.ChangeAppearance(TagId, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);

        // O que o SET NULL do banco faz quando a etiqueta é excluída.
        typeof(StickyNote).GetProperty(nameof(StickyNote.TagId))!.SetValue(note, null);

        note.ColorMode.Should().Be(StickyNoteColorMode.Tag);
        note.EffectiveColorMode.Should().Be(StickyNoteColorMode.Theme);
    }

    [Fact]
    public void Pinning_IsIndependentOfTheGeometry()
    {
        var note = StickyNote.Create(Now);
        note.Place(100, 200, 300, 220);

        note.Pin(true);

        note.IsPinned.Should().BeTrue();
        (note.X, note.Y, note.Width, note.Height).Should().Be((100, 200, 300.0, 220.0));

        note.Pin(false);
        note.IsPinned.Should().BeFalse();
    }

    [Fact]
    public void Placing_KeepsTheSizeUsable()
    {
        var note = StickyNote.Create(Now);

        note.Place(-1920, 40, 10, 5);
        note.X.Should().Be(-1920);
        note.Width.Should().Be(StickyNote.MinWidth);
        note.Height.Should().Be(StickyNote.MinHeight);

        note.Place(0, 0, double.NaN, double.PositiveInfinity);
        note.Width.Should().Be(StickyNote.DefaultWidth);
        note.Height.Should().Be(StickyNote.DefaultHeight);

        note.Place(0, 0, 99_999, 99_999);
        note.Width.Should().Be(StickyNote.MaxSize);
    }

    [Fact]
    public void ClosingAndOpening_OnlyToggleTheWindow()
    {
        var note = StickyNote.Create(Now);
        note.Edit("algo", Now);

        note.Close();
        note.IsOpen.Should().BeFalse();
        note.Content.Should().Be("algo");
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Active);

        note.Open();
        note.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Archiving_ClosesTheWindowAndKeepsEverything()
    {
        var note = StickyNote.Create(Now);
        note.Edit("algo", Now);
        note.Pin(true);

        note.Archive(Now.AddHours(1));

        note.Lifecycle.Should().Be(StickyNoteLifecycle.Archived);
        note.ArchivedAt.Should().Be(Now.AddHours(1));
        note.IsOpen.Should().BeFalse();
        note.IsPinned.Should().BeTrue();
        note.Content.Should().Be("algo");
    }

    [Fact]
    public void AnArchivedNote_IsReadOnlyUntilRestored()
    {
        var note = StickyNote.Create(Now);
        note.Archive(Now);

        note.Invoking(n => n.Edit("x", Now)).Should().Throw<DomainException>().WithMessage("*arquivado*");
        note.Invoking(n => n.Pin(true)).Should().Throw<DomainException>();
        note.Invoking(n => n.Open()).Should().Throw<DomainException>();
        note.Invoking(n => n.ChangeAppearance(null, StickyNoteColorMode.Theme, null, StickyNoteEmphasis.Normal, 100))
            .Should().Throw<DomainException>();
        note.Invoking(n => n.Archive(Now)).Should().Throw<DomainException>().WithMessage("*já está arquivado*");

        note.RestoreFromArchive();

        note.Lifecycle.Should().Be(StickyNoteLifecycle.Active);
        note.Edit("x", Now);
        note.Content.Should().Be("x");
    }

    [Fact]
    public void RestoringWhatIsNotArchived_IsRefused()
    {
        var note = StickyNote.Create(Now);

        note.Invoking(n => n.RestoreFromArchive()).Should().Throw<DomainException>();
        note.Invoking(n => n.RestoreFromTrash()).Should().Throw<DomainException>();
    }

    [Fact]
    public void ArchivedThenTrashed_GoesBackToTheArchive()
    {
        var note = StickyNote.Create(Now);
        note.Archive(Now);

        note.MoveToTrash(Now.AddDays(1));
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Trashed);
        note.Invoking(n => n.MoveToTrash(Now)).Should().Throw<DomainException>().WithMessage("*já está na lixeira*");
        note.Invoking(n => n.RestoreFromArchive()).Should().Throw<DomainException>().WithMessage("*lixeira*");
        note.Invoking(n => n.Archive(Now)).Should().Throw<DomainException>().WithMessage("*lixeira*");

        note.RestoreFromTrash();

        note.Lifecycle.Should().Be(StickyNoteLifecycle.Archived);
        note.DeletedAt.Should().BeNull();
    }

    [Fact]
    public void Trashing_ClosesTheWindow()
    {
        var note = StickyNote.Create(Now);

        note.MoveToTrash(Now);

        note.IsOpen.Should().BeFalse();
        note.IsInTrash.Should().BeTrue();
        note.IsOutOfTheMainList.Should().BeTrue();
    }

    [Fact]
    public void ClosingStillWorksOutOfTheMainList()
    {
        var note = StickyNote.Create(Now);
        note.MoveToTrash(Now);

        note.Close();

        note.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void PermanentDeletion_OnlyForStoredOrBlankNotes()
    {
        var blank = StickyNote.Create(Now);
        blank.Invoking(n => n.EnsurePermanentDeletionIsAllowed()).Should().NotThrow();

        var written = StickyNote.Create(Now);
        written.Edit("algo", Now);
        written.Invoking(n => n.EnsurePermanentDeletionIsAllowed())
            .Should().Throw<DomainException>().WithMessage("*arquivado ou na lixeira*");

        written.MoveToTrash(Now);
        written.Invoking(n => n.EnsurePermanentDeletionIsAllowed()).Should().NotThrow();
    }

    [Fact]
    public void Converting_ArchivesAndRemembersTheTask()
    {
        var note = StickyNote.Create(Now);
        note.Edit("Ligar para o cliente", Now);
        var taskId = Guid.CreateVersion7();

        note.MarkConverted(taskId, Now.AddMinutes(3));

        note.ConvertedTaskId.Should().Be(taskId);
        note.Lifecycle.Should().Be(StickyNoteLifecycle.Archived);
        note.ArchivedAt.Should().Be(Now.AddMinutes(3));
        note.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void AStoredNote_CannotBeConverted()
    {
        var note = StickyNote.Create(Now);
        note.Edit("algo", Now);
        note.Archive(Now);

        note.Invoking(n => n.EnsureCanBeConverted()).Should().Throw<DomainException>().WithMessage("*arquivado*");
        note.Invoking(n => n.MarkConverted(Guid.CreateVersion7(), Now)).Should().Throw<DomainException>();
        note.ConvertedTaskId.Should().BeNull();
    }

    [Fact]
    public void ThePalette_HasAHexAndANameForEveryColor()
    {
        StickyNotePalette.All.Should().HaveCount(7).And.OnlyHaveUniqueItems();

        foreach (var color in StickyNotePalette.All)
        {
            StickyNotePalette.HexOf(color).Should().MatchRegex("^#[0-9A-F]{6}$");
            StickyNotePalette.NameOf(color).Should().NotBeNullOrWhiteSpace();
        }

        var unknown = () => StickyNotePalette.HexOf((StickyNotePaletteColor)0);
        var unknownName = () => StickyNotePalette.NameOf((StickyNotePaletteColor)0);
        unknown.Should().Throw<DomainException>();
        unknownName.Should().Throw<DomainException>();
    }
}
