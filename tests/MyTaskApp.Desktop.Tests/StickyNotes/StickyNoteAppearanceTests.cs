using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>Cor, etiqueta, destaque e opacidade do post-it aberto (ADR-054).</summary>
public class StickyNoteAppearanceTests
{
    private static readonly Guid TagId = Guid.CreateVersion7();

    private readonly FakeUseCaseRunner _runner = new();

    private StickyNoteViewModel Create(StickyNoteView view, AppTheme? theme = null) =>
        new(view, _runner, NullLogger.Instance, theme);

    private static StickyNoteColor Palette(StickyNotePaletteColor color) =>
        new(StickyNoteColorMode.Palette, color, null);

    private static StickyNoteColor OfTag(string hex) => new(StickyNoteColorMode.Tag, null, hex);

    [Fact]
    public void WithoutColor_TheNoteIsTheThemeCard()
    {
        var viewModel = Create(TestStickyNotes.View("algo"));
        var palette = ThemeCatalog.Charcoal.Palette;

        ((ISolidColorBrush)viewModel.NoteBackground).Color.Should().Be(palette.Surface);
        viewModel.HasStripe.Should().BeFalse();
        viewModel.IsAttention.Should().BeFalse();
        viewModel.HasTag.Should().BeFalse();
    }

    [Fact]
    public void AColor_TintsTheCard_AndTheOpacityGoesIntoTheBrushOnly()
    {
        var viewModel = Create(TestStickyNotes.View("algo", color: Palette(StickyNotePaletteColor.Blue), opacity: 80));

        var background = (ISolidColorBrush)viewModel.NoteBackground;

        background.Color.Should().NotBe(ThemeCatalog.Charcoal.Palette.Surface);
        background.Opacity.Should().Be(0.8);
        viewModel.NoteBorderBrush.Opacity.Should().Be(1);
    }

    [Fact]
    public void Attention_AddsTheStripe()
    {
        var viewModel = Create(TestStickyNotes.View("algo", emphasis: StickyNoteEmphasis.Attention));

        viewModel.IsAttention.Should().BeTrue();
        viewModel.HasStripe.Should().BeTrue();
        viewModel.StripeBrush.Should().NotBeNull();
    }

    [Fact]
    public void ChangingTheTheme_Repaints()
    {
        var viewModel = Create(TestStickyNotes.View("algo", color: Palette(StickyNotePaletteColor.Green)));
        var dark = viewModel.Look;

        viewModel.UseTheme(ThemeCatalog.Paper);

        viewModel.Look.Should().NotBe(dark);
        viewModel.Look.Text.Should().Be(ThemeCatalog.Paper.Palette.TextHigh);
    }

    [Fact]
    public void TheTag_ShowsAsADotWithItsName()
    {
        var viewModel = Create(TestStickyNotes.View("algo", tagId: TagId, tagName: "ECO CORE", color: OfTag("#3B82F6")));

        viewModel.HasTag.Should().BeTrue();
        viewModel.TagTip.Should().Be("Etiqueta: ECO CORE");
        ((ISolidColorBrush)viewModel.TagDotBrush!).Color.Should().Be(Color.Parse("#3B82F6"));
    }

    [Fact]
    public void TheColorMenu_MarksTheCurrentChoice()
    {
        var viewModel = Create(TestStickyNotes.View("algo", color: Palette(StickyNotePaletteColor.Orange)));

        viewModel.ColorChoices.Should().HaveCount(9);
        viewModel.ColorChoices.Single(choice => choice.IsSelected).Label.Should().Be("Laranja");
        viewModel.ColorChoices[0].Label.Should().Be("Automática (tema)");

        // "Cor da etiqueta" sem etiqueta não tem o que usar.
        viewModel.ColorChoices[^1].Select.CanExecute(null).Should().BeFalse();
        viewModel.OpacityChoices.Single(choice => choice.IsSelected).Label.Should().Be("100%");
        viewModel.TagChoices.Should().ContainSingle().Which.Label.Should().Be("Nenhuma");
    }

    [Fact]
    public async Task OpeningTheMenu_ReadsTheTags()
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)
        [
            new TagRow(TagId, "ECO CORE", "#3B82F6", 2),
            new TagRow(Guid.CreateVersion7(), "Infra", "#22C55E", 0),
        ];
        var viewModel = Create(TestStickyNotes.View("algo"));

        await viewModel.LoadTagsCommand.ExecuteAsync(null);

        viewModel.TagChoices.Select(choice => choice.Label).Should().Equal("Nenhuma", "ECO CORE", "Infra");
        viewModel.TagChoices[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task TagsThatFailToLoad_LeaveJustNone()
    {
        _runner.NextFailure = new IOException("travado");
        var viewModel = Create(TestStickyNotes.View("algo"));

        await viewModel.LoadTagsCommand.ExecuteAsync(null);

        viewModel.TagChoices.Should().ContainSingle();
        viewModel.HasError.Should().BeFalse();
    }

    [Fact]
    public void BeforeTheTagsAreRead_TheNotesOwnTagIsAlreadyMarked()
    {
        var viewModel = Create(TestStickyNotes.View("algo", tagId: TagId, tagName: "ECO CORE", color: OfTag("#3B82F6")));

        viewModel.TagChoices.Select(choice => choice.Label).Should().Equal("Nenhuma", "ECO CORE");
        viewModel.TagChoices[1].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task ChoosingAColor_SavesAndRepaints()
    {
        var note = TestStickyNotes.View("algo");
        _runner.ResultsByHandler[typeof(ChangeStickyNoteAppearanceHandler)] =
            note with { Color = Palette(StickyNotePaletteColor.Red) };
        var viewModel = Create(note);
        var changed = 0;
        viewModel.Changed += () => changed++;

        viewModel.ColorChoices.Single(choice => choice.Label == "Vermelho").Select.Execute(null);
        await Task.Yield();

        _runner.Invoked.Should().Equal(typeof(ChangeStickyNoteAppearanceHandler));
        viewModel.ColorChoices.Single(choice => choice.IsSelected).Label.Should().Be("Vermelho");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task ARefusedChange_KeepsTheLookAndSaysWhy()
    {
        var viewModel = Create(TestStickyNotes.View("algo"));
        var before = viewModel.Look;
        _runner.NextFailure = new DomainException("Uma das etiquetas não existe mais. Reabra a lista e tente de novo.");

        await viewModel.ToggleAttentionCommand.ExecuteAsync(null);

        viewModel.Look.Should().Be(before);
        viewModel.IsAttention.Should().BeFalse();
        viewModel.ErrorMessage.Should().Contain("etiquetas");
    }

    // ------------------------------------------------------------------
    // A regra de cor ao escolher etiqueta, conferida no comando que chega ao
    // caso de uso de verdade.
    // ------------------------------------------------------------------

    private StickyNote UseRealAppearanceHandler(StickyNoteColorMode mode, StickyNotePaletteColor? palette, Guid? tagId)
    {
        var note = StickyNote.Create(TestStickyNotes.Now);
        note.Edit("algo", TestStickyNotes.Now);
        note.ChangeAppearance(tagId, mode, palette, StickyNoteEmphasis.Normal, 100);

        var notes = Substitute.For<IStickyNoteRepository>();
        notes.FindByIdAsync(note.Id, Arg.Any<CancellationToken>()).Returns(note);

        var tags = Substitute.For<ITagRepository>();
        tags.FindExistingIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyCollection<Guid>)[.. call.Arg<IReadOnlyCollection<Guid>>()]);

        _runner.Handlers[typeof(ChangeStickyNoteAppearanceHandler)] = new ChangeStickyNoteAppearanceHandler(
            notes,
            tags,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IStickyNoteQuery>(),
            NullLogger<ChangeStickyNoteAppearanceHandler>.Instance);

        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[new TagRow(TagId, "ECO CORE", "#3B82F6", 0)];

        return note;
    }

    private static StickyNoteView ViewOf(StickyNote note) => StickyNoteView.From(note, "ECO CORE", "#3B82F6");

    [Fact]
    public async Task ChoosingATag_UsesItsColor()
    {
        var note = UseRealAppearanceHandler(StickyNoteColorMode.Theme, null, null);
        var viewModel = Create(ViewOf(note));
        await viewModel.LoadTagsCommand.ExecuteAsync(null);

        viewModel.TagChoices.Single(choice => choice.Label == "ECO CORE").Select.Execute(null);
        await Task.Yield();

        note.TagId.Should().Be(TagId);
        note.ColorMode.Should().Be(StickyNoteColorMode.Tag);
    }

    [Fact]
    public async Task ChoosingATag_KeepsACustomColor()
    {
        var note = UseRealAppearanceHandler(StickyNoteColorMode.Palette, StickyNotePaletteColor.Yellow, null);
        var viewModel = Create(ViewOf(note));
        await viewModel.LoadTagsCommand.ExecuteAsync(null);

        viewModel.TagChoices.Single(choice => choice.Label == "ECO CORE").Select.Execute(null);
        await Task.Yield();

        note.TagId.Should().Be(TagId);
        note.ColorMode.Should().Be(StickyNoteColorMode.Palette);
        note.PaletteColor.Should().Be(StickyNotePaletteColor.Yellow);
    }

    [Fact]
    public async Task RemovingTheTagFromANoteUsingItsColor_GoesBackToTheTheme()
    {
        var note = UseRealAppearanceHandler(StickyNoteColorMode.Tag, null, TagId);
        var viewModel = Create(ViewOf(note));

        viewModel.TagChoices.Single(choice => choice.Label == "Nenhuma").Select.Execute(null);
        await Task.Yield();

        note.TagId.Should().BeNull();
        note.ColorMode.Should().Be(StickyNoteColorMode.Theme);
    }

    [Fact]
    public async Task OpacityAndAttention_ReachTheNote()
    {
        var note = UseRealAppearanceHandler(StickyNoteColorMode.Theme, null, null);
        var viewModel = Create(ViewOf(note));

        viewModel.OpacityChoices.Single(choice => choice.Label == "70%").Select.Execute(null);
        await Task.Yield();
        await viewModel.ToggleAttentionCommand.ExecuteAsync(null);

        note.Opacity.Should().Be(70);
        note.Emphasis.Should().Be(StickyNoteEmphasis.Attention);
        viewModel.IsAttention.Should().BeTrue();
    }
}
