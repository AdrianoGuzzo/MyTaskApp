using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.StickyNotes;

/// <summary>
/// Como um texto de post-it vira tarefa sem perguntar nada (ADR-054): a primeira
/// linha é o título, e o texto inteiro vai para a anotação quando sobrou algo
/// que o título não carregou.
/// </summary>
public sealed record StickyNoteTaskDraft(string Title, string? Description)
{
    private const string Ellipsis = "…";

    public static StickyNoteTaskDraft From(string? text)
    {
        var normalized = text?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException("O post-it está vazio. Escreva algo antes de transformá-lo em tarefa.");
        }

        var lines = normalized.Split('\n');
        var firstLine = lines[0].Trim();
        var title = Shorten(firstLine);

        // A anotação só existe quando acrescenta: uma linha que coube inteira
        // no título repetida embaixo seria ruído na tela de detalhes.
        var description = lines.Length > 1 || title.Length != firstLine.Length
            ? normalized
            : null;

        return new StickyNoteTaskDraft(title, description);
    }

    /// <summary>Corta na última palavra que cabe, e não no meio dela.</summary>
    private static string Shorten(string line)
    {
        if (line.Length <= TaskItem.MaxTitleLength)
        {
            return line;
        }

        var room = TaskItem.MaxTitleLength - Ellipsis.Length;
        var cut = line.LastIndexOf(' ', room);

        var head = cut > room / 2 ? line[..cut] : line[..room];

        return head.TrimEnd() + Ellipsis;
    }
}
