namespace MyTaskApp.Desktop.Themes;

public record ThemeDefinition(
    string Name,
    string DisplayName,
    string ResourceUri,
    bool IsLightTheme)
{
    public static readonly ThemeDefinition Dark = new(
        Name: "dark",
        DisplayName: "Escuro",
        ResourceUri: "avares://MyTaskApp/Styles/Themes/ThemeDark.axaml",
        IsLightTheme: false);

    public static readonly ThemeDefinition Light = new(
        Name: "light",
        DisplayName: "Claro",
        ResourceUri: "avares://MyTaskApp/Styles/Themes/ThemeLight.axaml",
        IsLightTheme: true);

    public static readonly ThemeDefinition Nordic = new(
        Name: "nordic",
        DisplayName: "Nórdico",
        ResourceUri: "avares://MyTaskApp/Styles/Themes/ThemeNordic.axaml",
        IsLightTheme: false);

    public static readonly ThemeDefinition Modern = new(
        Name: "modern",
        DisplayName: "Moderno",
        ResourceUri: "avares://MyTaskApp/Styles/Themes/ThemeModern.axaml",
        IsLightTheme: false);

    public static readonly ThemeDefinition Retro = new(
        Name: "retro",
        DisplayName: "Retrô",
        ResourceUri: "avares://MyTaskApp/Styles/Themes/ThemeRetro.axaml",
        IsLightTheme: false);

    public static readonly ThemeDefinition HighContrast = new(
        Name: "high-contrast",
        DisplayName: "Alto Contraste",
        ResourceUri: "avares://MyTaskApp/Styles/Themes/ThemeHigh.axaml",
        IsLightTheme: false);

    public static readonly IReadOnlyList<ThemeDefinition> All = new[]
    {
        Dark, Light, Nordic, Modern, Retro, HighContrast
    };
}
