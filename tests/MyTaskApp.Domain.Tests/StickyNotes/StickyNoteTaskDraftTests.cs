using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.StickyNotes;

/// <summary>Post-it → tarefa sem perguntar nada (ADR-054).</summary>
public class StickyNoteTaskDraftTests
{
    [Fact]
    public void OneLine_BecomesTheTitleAlone()
    {
        var draft = StickyNoteTaskDraft.From("  Perguntar sobre homologação  ");

        draft.Title.Should().Be("Perguntar sobre homologação");
        draft.Description.Should().BeNull();
    }

    [Fact]
    public void SeveralLines_TitleIsTheFirstAndTheWholeTextGoesToTheNote()
    {
        var draft = StickyNoteTaskDraft.From("\r\nFalar com Marcelo\r\nsobre o deploy de sexta\r\n");

        draft.Title.Should().Be("Falar com Marcelo");
        draft.Description.Should().Be("Falar com Marcelo\nsobre o deploy de sexta");
    }

    [Fact]
    public void ALongLine_IsCutAtAWordAndKeptWholeInTheNote()
    {
        var words = string.Join(' ', Enumerable.Repeat("palavra", 60));

        var draft = StickyNoteTaskDraft.From(words);

        draft.Title.Length.Should().BeLessThanOrEqualTo(TaskItem.MaxTitleLength);
        draft.Title.Should().EndWith("palavra…");
        draft.Description.Should().Be(words);
    }

    [Fact]
    public void ALongLineWithoutSpaces_IsCutHard()
    {
        var text = new string('x', 500);

        var draft = StickyNoteTaskDraft.From(text);

        draft.Title.Should().HaveLength(TaskItem.MaxTitleLength).And.EndWith("…");
        draft.Description.Should().Be(text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void NothingWritten_IsRefused(string? text)
    {
        var from = () => StickyNoteTaskDraft.From(text);

        from.Should().Throw<DomainException>().WithMessage("*vazio*");
    }
}
