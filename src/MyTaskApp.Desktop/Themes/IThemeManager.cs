namespace MyTaskApp.Desktop.Themes;

public interface IThemeManager
{
    ThemeDefinition CurrentTheme { get; }
    event Action<ThemeDefinition>? ThemeChanged;
    Task InitializeAsync();
    Task SetThemeAsync(string themeName);
    string GetSystemPreference();
}
