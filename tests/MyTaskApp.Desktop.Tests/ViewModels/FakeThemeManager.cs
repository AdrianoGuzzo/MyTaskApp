using MyTaskApp.Desktop.Themes;

namespace MyTaskApp.Desktop.Tests.ViewModels;

public sealed class FakeThemeManager : IThemeManager
{
    public ThemeDefinition CurrentTheme => ThemeDefinition.Dark;

#pragma warning disable CS0067 // Event is never used
    public event Action<ThemeDefinition>? ThemeChanged;
#pragma warning restore CS0067

    public Task InitializeAsync() => Task.CompletedTask;

    public Task SetThemeAsync(string themeName) => Task.CompletedTask;

    public string GetSystemPreference() => "dark";
}
