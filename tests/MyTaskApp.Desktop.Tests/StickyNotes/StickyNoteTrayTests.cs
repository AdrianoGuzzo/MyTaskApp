using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Desktop.Reminders;
using MyTaskApp.Desktop.Tests.ViewModels;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>Post-it sem abrir o painel: bandeja → Novo post-it → texto (ADR-054).</summary>
public class StickyNoteTrayTests
{
    [AvaloniaFact]
    public void TheTrayMenu_OffersANewNoteAndTheList()
    {
        var newNote = 0;
        var list = 0;
        using var tray = new TrayIconHost(new FakeUseCaseRunner(), NullLogger<TrayIconHost>.Instance);

        var menu = tray.BuildMenu(new TrayActions(
            Open: () => { },
            Settings: () => { },
            Exit: () => { },
            ToggleTopmost: () => { },
            ToggleHud: () => { },
            WindowSettings: () => { },
            UseCompact: () => { },
            Hide: () => { },
            NewStickyNote: () => newNote++,
            StickyNotes: () => list++));

        var items = menu.Items.OfType<NativeMenuItem>().ToList();
        var headers = items.Select(item => item.Header).ToList();

        headers.Should().ContainInOrder("Abrir", "Novo post-it", "Post-its…");

        Click(items.Single(item => item.Header == "Novo post-it"));
        Click(items.Single(item => item.Header == "Post-its…"));

        newNote.Should().Be(1);
        list.Should().Be(1);
    }

    /// <summary>O clique que a bandeja do sistema entrega ao item.</summary>
    private static void Click(NativeMenuItem item) =>
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
}
