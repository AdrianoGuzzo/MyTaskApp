using Avalonia.Headless.XUnit;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests;

/// <summary>
/// Valida que a UI sobe sem display real. Serve de guarda para a infraestrutura
/// de teste headless — os testes de comportamento ficam nos ViewModels.
/// </summary>
public class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_CanBeConstructedWithoutADisplay()
    {
        var window = new MainWindow();

        window.Show();

        window.Title.Should().Be("MyTaskApp");
    }
}
