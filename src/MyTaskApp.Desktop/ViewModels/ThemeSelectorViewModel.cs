using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Desktop.Themes;

namespace MyTaskApp.Desktop.ViewModels;

public sealed partial class ThemeSelectorViewModel : ObservableObject
{
    private readonly IThemeManager _themeManager;

    [ObservableProperty]
    private ThemeDefinition selectedTheme;

    [ObservableProperty]
    private IReadOnlyList<ThemeDefinition> availableThemes;

    public ThemeSelectorViewModel(IThemeManager themeManager)
    {
        _themeManager = themeManager;
        selectedTheme = themeManager.CurrentTheme;
        availableThemes = ThemeDefinition.All;

        _themeManager.ThemeChanged += OnThemeChanged;
    }

    [RelayCommand]
    public async Task SelectThemeAsync(ThemeDefinition theme)
    {
        if (theme == SelectedTheme)
        {
            return;
        }

        await _themeManager.SetThemeAsync(theme.Name);
    }

    [RelayCommand]
    public void DetectSystemPreference()
    {
        var systemTheme = _themeManager.GetSystemPreference();
        var theme = ThemeDefinition.All.First(t => t.Name == systemTheme);
        _ = SelectThemeAsync(theme);
    }

    private void OnThemeChanged(ThemeDefinition newTheme)
    {
        SelectedTheme = newTheme;
    }
}
