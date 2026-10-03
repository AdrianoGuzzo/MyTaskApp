using Avalonia.Controls;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// "Janela e comportamento" (ADR-047). O DataContext é a própria moldura da
/// janela principal: não há cópia para sincronizar, e cada escolha vale na
/// hora em que é feita.
/// </summary>
public sealed partial class WindowSettingsWindow : Window
{
    public WindowSettingsWindow() => InitializeComponent();

    public WindowSettingsWindow(WidgetChromeViewModel chrome)
        : this() => DataContext = chrome;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
