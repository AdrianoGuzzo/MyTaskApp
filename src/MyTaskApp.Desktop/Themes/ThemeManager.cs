using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Storage;

namespace MyTaskApp.Desktop.Themes;

internal sealed class ThemeManager : IThemeManager, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<ThemeManager> _logger;
    private ThemeDefinition _currentTheme;
    private ResourceDictionary? _currentThemeResources;

    public ThemeDefinition CurrentTheme => _currentTheme;
    public event Action<ThemeDefinition>? ThemeChanged;

    public ThemeManager(IConfiguration config, ILogger<ThemeManager> logger)
    {
        _config = config;
        _logger = logger;
        _currentTheme = ThemeDefinition.Dark;
    }

    public async Task InitializeAsync()
    {
        var savedTheme = LoadSavedTheme();
        await SetThemeAsync(savedTheme);
    }

    public async Task SetThemeAsync(string themeName)
    {
        var theme = ThemeDefinition.All.FirstOrDefault(t => t.Name == themeName)
                    ?? ThemeDefinition.Dark;

        if (_currentTheme == theme)
        {
            return;
        }

        _logger.LogInformation("ChangingTheme from {OldTheme} to {NewTheme}", _currentTheme.Name, theme.Name);

        try
        {
            await ApplyThemeAsync(theme);
            _currentTheme = theme;
            SaveTheme(theme.Name);
            ThemeChanged?.Invoke(theme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FailedToApplyTheme {ThemeName}", theme.Name);
            throw;
        }
    }

    public string GetSystemPreference()
    {
        try
        {
            var isLightTheme = DetectWindowsTheme();
            return isLightTheme ? "light" : "dark";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FailedToDetectWindowsTheme, defaulting to dark");
            return "dark";
        }
    }

    private async Task ApplyThemeAsync(ThemeDefinition theme)
    {
        var app = Avalonia.Application.Current;
        if (app is null)
        {
            throw new InvalidOperationException("Application not initialized");
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            RemoveCurrentThemeResources();
            LoadThemeResources(app, theme);
            app.RequestedThemeVariant = theme.IsLightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
        });
    }

    private void LoadThemeResources(Avalonia.Application app, ThemeDefinition theme)
    {
        try
        {
            var uri = new Uri(theme.ResourceUri);
            _currentThemeResources = (Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(uri) as ResourceDictionary)
                                     ?? new ResourceDictionary();

            app.Resources.MergedDictionaries.Insert(0, _currentThemeResources);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FailedToLoadThemeResources for {ThemeName}", theme.Name);
            throw;
        }
    }

    private void RemoveCurrentThemeResources()
    {
        var app = Avalonia.Application.Current;
        if (app is null || _currentThemeResources is null)
        {
            return;
        }

        app.Resources.MergedDictionaries.Remove(_currentThemeResources);
        _currentThemeResources = null;
    }

    private bool DetectWindowsTheme()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            // Detecta tema do Windows via registri
            var registryPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            var valueName = "AppsUseLightTheme";

            var value = Microsoft.Win32.Registry.GetValue(registryPath, valueName, null);
            return value is int intValue && intValue == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FailedToReadWindowsTheme from registry");
            return false;
        }
    }

    private string LoadSavedTheme()
    {
        try
        {
            var userSettings = Path.Combine(UserDataLocation.Current.State, "theme.txt");
            if (File.Exists(userSettings))
            {
                var saved = File.ReadAllText(userSettings).Trim();
                if (ThemeDefinition.All.Any(t => t.Name == saved))
                {
                    return saved;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FailedToLoadSavedTheme");
        }

        // Usar preferência do sistema na primeira execução
        return GetSystemPreference();
    }

    private void SaveTheme(string themeName)
    {
        try
        {
            var userSettings = Path.Combine(UserDataLocation.Current.State, "theme.txt");
            File.WriteAllText(userSettings, themeName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FailedToSaveTheme {ThemeName}", themeName);
        }
    }

    public void Dispose()
    {
        RemoveCurrentThemeResources();
    }
}
