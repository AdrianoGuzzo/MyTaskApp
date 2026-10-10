using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Desktop.Reminders;
using MyTaskApp.Desktop.Tests.ViewModels;

namespace MyTaskApp.Desktop.Tests.Reminders;

/// <summary>"Servidor MCP…" na bandeja (ADR-059): junto das configurações, e só se o app oferece.</summary>
public class McpTrayMenuTests
{
    [AvaloniaFact]
    public void TheTrayMenu_OpensTheMcpServerWindow_RightAfterTheReminderSettings()
    {
        var opened = 0;
        using var tray = new TrayIconHost(new FakeUseCaseRunner(), NullLogger<TrayIconHost>.Instance);

        var menu = tray.BuildMenu(Actions(() => opened++));

        var items = menu.Items.OfType<NativeMenuItem>().ToList();
        items.Select(item => item.Header).Should().ContainInOrder("Configuração de lembretes…", "Servidor MCP…", "Sair");

        ((INativeMenuItemExporterEventsImplBridge)items.Single(item => item.Header == "Servidor MCP…")).RaiseClicked();
        opened.Should().Be(1);
    }

    [AvaloniaFact]
    public void WithoutTheAction_TheItemIsNotThere()
    {
        using var tray = new TrayIconHost(new FakeUseCaseRunner(), NullLogger<TrayIconHost>.Instance);

        tray.BuildMenu(Actions(null)).Items.OfType<NativeMenuItem>()
            .Select(item => item.Header).Should().NotContain("Servidor MCP…");
    }

    private static TrayActions Actions(Action? mcp) => new(
        Open: () => { },
        Settings: () => { },
        Exit: () => { },
        ToggleTopmost: () => { },
        ToggleHud: () => { },
        WindowSettings: () => { },
        UseCompact: () => { },
        Hide: () => { },
        NewStickyNote: () => { },
        StickyNotes: () => { },
        McpServer: mcp);
}
