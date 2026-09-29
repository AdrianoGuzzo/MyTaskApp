using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// A troca de tema com o app de pé (ADR-041): a janela aberta muda de cor sem
/// reabrir, e a variante do Fluent acompanha. Cada teste devolve o app a
/// "seguir o sistema", porque o App é um só para toda a suíte.
/// </summary>
public class ThemeControllerTests
{
    [AvaloniaFact]
    public void TheAppStartsWithAThemeOnScreen()
    {
        Themes().Current.Should().NotBeNull();
        Resolve("WidgetCanvasBrush").Should().NotBeNull();
    }

    [AvaloniaFact]
    public void ChoosingATheme_RepaintsAnOpenWindow()
    {
        var window = new MainWindow();
        window.Show();

        try
        {
            var shell = window.GetLogicalDescendants().OfType<Border>().First(border => border.Classes.Contains("widgetShell"));

            Themes().Use(ThemeCatalog.Sepia.Id);
            Background(shell).Should().Be(ThemeCatalog.Sepia.Palette.Canvas);

            Themes().Use(ThemeCatalog.Nordic.Id);
            Background(shell).Should().Be(ThemeCatalog.Nordic.Palette.Canvas);
        }
        finally
        {
            window.Close();
            Themes().Use(ThemeCatalog.SystemId);
        }
    }

    [AvaloniaFact]
    public void TheFluentVariant_FollowsTheTheme()
    {
        try
        {
            Themes().Use(ThemeCatalog.Paper.Id);
            Avalonia.Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);

            Themes().Use(ThemeCatalog.Plum.Id);
            Avalonia.Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
        }
        finally
        {
            Themes().Use(ThemeCatalog.SystemId);
        }
    }

    [AvaloniaFact]
    public void SwitchingBackAndForth_KeepsASingleThemeDictionary()
    {
        // Somar um dicionário por troca deixaria o app mais lento a cada clique
        // no menu — e o mais novo escondendo os velhos, sem nunca soltá-los.
        var merged = Avalonia.Application.Current!.Resources.MergedDictionaries;
        var before = merged.Count;

        try
        {
            foreach (var theme in ThemeCatalog.All)
            {
                Themes().Use(theme.Id);
            }

            merged.Count.Should().Be(before);
            Themes().Current.Should().BeSameAs(ThemeCatalog.HighContrast);
        }
        finally
        {
            Themes().Use(ThemeCatalog.SystemId);
        }
    }

    [AvaloniaFact]
    public void AnUnknownChoice_FollowsTheSystem()
    {
        try
        {
            Themes().Use("não-existe");

            ThemeCatalog.All.Should().Contain(Themes().Current!);
        }
        finally
        {
            Themes().Use(ThemeCatalog.SystemId);
        }
    }

    private static ThemeController Themes() =>
        ((App)Avalonia.Application.Current!).Themes!;

    private static object? Resolve(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, null, out var value) ? value : null;

    /// <summary>
    /// O valor de base, e não o vivo: o fundo tem BrushTransition, e a leitura
    /// imediata pegaria o meio do caminho (como em <c>WidgetGhostTests</c>).
    /// </summary>
    private static Color? Background(Border border) =>
        (border.GetBaseValue(Border.BackgroundProperty).GetValueOrDefault() as ISolidColorBrush)?.Color
        ?? (border.Background as ISolidColorBrush)?.Color;
}
