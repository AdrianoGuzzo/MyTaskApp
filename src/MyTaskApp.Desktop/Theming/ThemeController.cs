using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MyTaskApp.Desktop.Theming;

/// <summary>
/// Põe um tema na tela e o mantém lá (ADR-041). Com "seguir o Windows", trocar
/// o modo do sistema troca o app na hora, sem reiniciar.
/// </summary>
/// <remarks>
/// A variante do Fluent (<c>RequestedThemeVariant</c>) acompanha o tema: é ela
/// que pinta o que a paleta não cobre, como a barra de título das janelas
/// auxiliares e o seletor de cor.
/// </remarks>
public sealed class ThemeController
{
    private readonly Avalonia.Application _application;
    private readonly IPlatformSettings? _platform;

    private ResourceDictionary? _applied;
    private string _choice = ThemeCatalog.SystemId;

    public ThemeController(Avalonia.Application application)
    {
        _application = application;

        // Nulo no designer; no headless existe, mas nunca muda.
        _platform = application.PlatformSettings;

        if (_platform is not null)
        {
            _platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(OnSystemChanged);
        }
    }

    public AppTheme? Current { get; private set; }

    /// <summary>
    /// O tema na tela mudou. Para quem desenha cor calculada em C# — os post-its
    /// (ADR-054) —, que um <c>DynamicResource</c> não alcança.
    /// </summary>
    public event Action<AppTheme>? Changed;

    /// <param name="choice">O id de um tema do catálogo, ou <see cref="ThemeCatalog.SystemId"/>.</param>
    public void Use(string? choice)
    {
        _choice = ThemeCatalog.Normalize(choice);
        Apply(ThemeCatalog.Resolve(_choice, ReadSystem()));
    }

    /// <summary>Quem escolheu um tema de propósito não é afetado pelo sistema.</summary>
    private void OnSystemChanged()
    {
        if (_choice == ThemeCatalog.SystemId)
        {
            Apply(ThemeCatalog.Resolve(_choice, ReadSystem()));
        }
    }

    private void Apply(AppTheme theme)
    {
        if (ReferenceEquals(theme, Current))
        {
            return;
        }

        var resources = ThemeResources.Build(theme);
        var merged = _application.Resources.MergedDictionaries;
        var index = _applied is null ? -1 : merged.IndexOf(_applied);

        // Substituir no lugar, e não remover e somar: um aviso à árvore em vez
        // de dois, cada um refazendo a busca de todo DynamicResource aberto.
        if (index >= 0)
        {
            merged[index] = resources;
        }
        else
        {
            merged.Add(resources);
        }

        _applied = resources;
        _application.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        Current = theme;

        Changed?.Invoke(theme);
    }

    private SystemAppearance ReadSystem()
    {
        // Sem plataforma, escuro: é o que o app sempre foi.
        if (_platform?.GetColorValues() is not { } values)
        {
            return new SystemAppearance(IsDark: true, HighContrast: false);
        }

        return new SystemAppearance(
            IsDark: values.ThemeVariant == PlatformThemeVariant.Dark,
            HighContrast: values.ContrastPreference == ColorContrastPreference.High);
    }
}
