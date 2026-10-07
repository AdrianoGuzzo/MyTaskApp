using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>Post-its prontos para a tela, sem banco.</summary>
internal static class TestStickyNotes
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    public static StickyNoteView View(
        string content = "",
        bool pinned = false,
        int? x = null,
        int? y = null,
        double width = StickyNote.DefaultWidth,
        double height = StickyNote.DefaultHeight,
        StickyNoteColor? color = null,
        StickyNoteEmphasis emphasis = StickyNoteEmphasis.Normal,
        int opacity = 100,
        Guid? tagId = null,
        string? tagName = null,
        Guid? id = null) =>
        new(
            id ?? Guid.CreateVersion7(),
            content,
            tagId,
            tagName,
            color ?? StickyNoteColor.Theme,
            emphasis,
            opacity,
            IsOpen: true,
            IsPinned: pinned,
            x,
            y,
            width,
            height,
            Now,
            Now,
            ArchivedAt: null,
            DeletedAt: null);
}
