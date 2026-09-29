using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyTaskApp.Desktop.Views;

public partial class ThemeSelectorView : UserControl
{
    public ThemeSelectorView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
