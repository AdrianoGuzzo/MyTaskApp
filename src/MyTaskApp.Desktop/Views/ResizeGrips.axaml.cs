using Avalonia.Controls;
using Avalonia.Input;

namespace MyTaskApp.Desktop.Views;

/// <summary>As oito alças de uma janela sem moldura, compartilhadas pelo widget e pelos post-its.</summary>
public partial class ResizeGrips : UserControl
{
    public ResizeGrips() => InitializeComponent();

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window { CanResize: true } window
            || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var side))
        {
            window.BeginResizeDrag(side, e);
        }
    }
}
