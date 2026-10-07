namespace MyTaskApp.Application.StickyNotes;

/// <summary>Leituras de texto que a lista e o cabeçalho do post-it fazem do mesmo jeito.</summary>
public static class StickyNoteText
{
    /// <summary>A primeira linha não vazia, aparada; nula quando não há texto.</summary>
    public static string? FirstLine(string? text) =>
        text?.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
