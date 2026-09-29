using Avalonia.Controls;
using Avalonia.Media;

namespace MyTaskApp.Desktop.Theming;

/// <summary>
/// Monta o dicionário de um tema: as chaves <c>Widget*</c> que as telas pedem
/// e as chaves do Fluent que o app reaponta (ADR-017). Um dicionário novo por
/// troca, e não o mesmo reescrito chave a chave: trocar um dicionário inteiro
/// avisa a árvore uma vez; setenta atribuições avisariam setenta.
/// </summary>
/// <remarks>
/// As telas pedem tudo daqui por <c>DynamicResource</c> — é o que faz a troca
/// valer sem reabrir janela. Um <c>StaticResource</c> para uma destas chaves
/// congelaria a cor do tema que estava ativo quando o XAML carregou.
/// </remarks>
public static class ThemeResources
{
    /// <summary>A opacidade do halo da bolinha de worktree na tarefa concluída.</summary>
    private const double HaloOpacity = 0.35;

    public static ResourceDictionary Build(AppTheme theme)
    {
        var p = theme.Palette;
        var resources = new ResourceDictionary();

        void Token(string name, Color color)
        {
            resources[$"Widget{name}Color"] = color;
            resources[$"Widget{name}Brush"] = Brush(color);
        }

        Token("Canvas", p.Canvas);
        Token("Surface", p.Surface);
        Token("SurfaceHover", p.SurfaceHover);
        Token("Stroke", p.Stroke);
        Token("StrokeSoft", p.StrokeSoft);
        Token("TextHigh", p.TextHigh);
        Token("TextMid", p.TextMid);
        Token("TextLow", p.TextLow);
        Token("Accent", p.Accent);
        Token("AccentHover", p.AccentHover);
        Token("AccentPressed", p.AccentPressed);
        Token("AccentSoft", p.AccentSoft);
        Token("AccentLine", p.AccentLine);
        Token("AccentText", p.AccentText);
        Token("OnAccent", p.OnAccent);
        Token("Danger", p.Danger);
        Token("DangerSoft", p.DangerSoft);
        Token("Caution", p.Caution);
        Token("CautionSoft", p.CautionSoft);
        Token("Info", p.Info);
        Token("Success", p.Success);

        resources["WidgetCautionHaloBrush"] = Brush(p.Caution, HaloOpacity);
        resources["WidgetInfoHaloBrush"] = Brush(p.Info, HaloOpacity);
        resources["WidgetSuccessHaloBrush"] = Brush(p.Success, HaloOpacity);
        resources["WidgetTextMidHaloBrush"] = Brush(p.TextMid, HaloOpacity);

        // Os botões do modo discreto flutuam sobre a área de trabalho. Eram
        // preto translúcido fixo: no tema claro, ícone escuro sobre preto sumia.
        resources["WidgetGhostChromeBrush"] = Brush(p.Canvas, 0.65);
        resources["WidgetGhostChromeHoverBrush"] = Brush(p.Canvas, 0.85);

        AddShadows(resources, theme.IsDark);
        AddFluentOverrides(resources, p);
        AddButtons(resources, p, theme.IsDark);

        return resources;
    }

    /// <summary>
    /// A sombra cai sobre a área de trabalho, e não sobre o tema — mas no claro
    /// a mesma sombra do escuro vira uma mancha em volta de um painel branco.
    /// </summary>
    private static void AddShadows(ResourceDictionary resources, bool isDark)
    {
        resources["WidgetShellShadow"] = BoxShadows.Parse(isDark ? "0 8 28 -6 #7A000000" : "0 8 24 -6 #38000000");
        resources["WidgetPopupShadow"] = BoxShadows.Parse(isDark ? "0 8 24 -6 #7A000000" : "0 8 22 -6 #38000000");

        // O item levantado da lista (ADR-022): mais curta e mais dura que a do
        // painel — "poucos milímetros acima", e não "outra janela".
        resources["WidgetLiftShadow"] = BoxShadows.Parse(isDark ? "0 6 16 -4 #99000000" : "0 6 16 -4 #40000000");
    }

    /// <summary>
    /// O botão comum. O Fluent o pinta com branco ou preto translúcido — no
    /// Sépia, um cinza frio em cima do creme. Aqui é o texto do próprio tema,
    /// translúcido do mesmo jeito: continua servindo sobre fundo e cartão, e
    /// pega a matiz da paleta. No Carvão, 20% de quase-branco é o que já era.
    /// </summary>
    private static void AddButtons(ResourceDictionary resources, ThemePalette p, bool isDark)
    {
        var (rest, hover, pressed) = isDark ? (0.20, 0.28, 0.14) : (0.10, 0.15, 0.20);

        resources["ButtonBackground"] = Brush(p.TextHigh, rest);
        resources["ButtonBackgroundPointerOver"] = Brush(p.TextHigh, hover);
        resources["ButtonBackgroundPressed"] = Brush(p.TextHigh, pressed);
        resources["ButtonForeground"] = Brush(p.TextHigh);
        resources["ButtonForegroundPointerOver"] = Brush(p.TextHigh);
        resources["ButtonForegroundPressed"] = Brush(p.TextHigh);

        // Botão de destaque e alternância ligada. Mesma armadilha do visto da
        // checkbox: o Fluent liga a letra deles ao branco por StaticResource,
        // e no Nórdico o "Abrir" saía branco sobre gelo.
        foreach (var (state, fill) in new[] { ("", p.Accent), ("PointerOver", p.AccentHover), ("Pressed", p.AccentPressed) })
        {
            resources[$"AccentButtonBackground{state}"] = Brush(fill);
            resources[$"AccentButtonForeground{state}"] = Brush(p.OnAccent);
            resources[$"ToggleButtonBackgroundChecked{state}"] = Brush(fill);
            resources[$"ToggleButtonForegroundChecked{state}"] = Brush(p.OnAccent);
        }
    }

    /// <summary>
    /// As chaves do Fluent que as telas já pediam, ou que os controles padrão
    /// usam por dentro. Reapontá-las veste botão, campo, checkbox, menu e
    /// balão de uma vez; reescrever <c>ControlTheme</c> faria o mesmo com
    /// centenas de linhas a mais e um template para manter a cada atualização.
    /// </summary>
    private static void AddFluentOverrides(ResourceDictionary resources, ThemePalette p)
    {
        void Set(string key, Color color) => resources[key] = Brush(color);

        Set("SystemFillColorCautionBrush", p.Caution);
        Set("SystemFillColorCautionBackgroundBrush", p.CautionSoft);
        Set("SystemFillColorCriticalBrush", p.Danger);
        Set("SystemFillColorCriticalBackgroundBrush", p.DangerSoft);
        Set("ControlStrokeColorDefaultBrush", p.Stroke);
        Set("ControlStrokeColorSecondaryBrush", p.Stroke);
        Set("SolidBackgroundFillColorBaseBrush", p.Canvas);
        Set("SolidBackgroundFillColorSecondaryBrush", p.Surface);
        Set("SolidBackgroundFillColorTertiaryBrush", p.SurfaceHover);

        // O Fluent deriva botão de acento, checkbox marcado e barra de
        // progresso destas. Clarear e escurecer por mistura mantém a matiz.
        resources["SystemAccentColor"] = p.Accent;
        resources["SystemAccentColorLight1"] = Mix(p.Accent, Colors.White, 0.15);
        resources["SystemAccentColorLight2"] = Mix(p.Accent, Colors.White, 0.30);
        resources["SystemAccentColorLight3"] = Mix(p.Accent, Colors.White, 0.45);
        resources["SystemAccentColorDark1"] = Mix(p.Accent, Colors.Black, 0.10);
        resources["SystemAccentColorDark2"] = Mix(p.Accent, Colors.Black, 0.20);
        resources["SystemAccentColorDark3"] = Mix(p.Accent, Colors.Black, 0.30);
        Set("AccentFillColorDefaultBrush", p.Accent);
        Set("AccentFillColorSecondaryBrush", p.AccentHover);
        Set("AccentFillColorTertiaryBrush", p.AccentPressed);
        Set("TextOnAccentFillColorPrimaryBrush", p.OnAccent);
        Set("TextOnAccentFillColorSecondaryBrush", Mix(p.OnAccent, p.Accent, 0.08));

        // O visto da checkbox e o miolo do rádio. O Fluent os liga ao "texto
        // sobre o destaque" por StaticResource, dentro do próprio dicionário:
        // reapontar só a chave de cima deixava o visto branco sobre o amarelo
        // do alto contraste.
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            Set($"CheckBoxCheckGlyphForegroundChecked{state}", p.OnAccent);
            Set($"CheckBoxCheckGlyphForegroundIndeterminate{state}", p.OnAccent);
            Set($"RadioButtonCheckGlyphFill{state}", p.OnAccent);
        }

        Set("TextFillColorPrimaryBrush", p.TextHigh);
        Set("TextFillColorSecondaryBrush", p.TextMid);
        Set("TextFillColorTertiaryBrush", p.TextLow);

        // Botão e campo: repouso, hover e pressionado a partir das superfícies,
        // para cada tema não precisar de mais três cores escolhidas à mão.
        Set("ControlFillColorDefaultBrush", p.StrokeSoft);
        Set("ControlFillColorSecondaryBrush", Mix(p.SurfaceHover, p.Stroke, 0.5));
        Set("ControlFillColorTertiaryBrush", Mix(p.Surface, p.StrokeSoft, 0.35));

        // Checkbox desmarcado: círculo vazado, sem caixa cinza pesada.
        resources["ControlAltFillColorSecondaryBrush"] = Brush(Colors.Transparent);
        Set("ControlAltFillColorTertiaryBrush", p.SurfaceHover);
        Set("ControlAltFillColorQuarternaryBrush", p.Stroke);

        Set("TextControlPlaceholderForeground", p.TextLow);
        Set("TextControlPlaceholderForegroundPointerOver", p.TextLow);
        Set("TextControlPlaceholderForegroundFocused", p.TextMid);

        // Balões: o título cortado só se lê por eles, então não são enfeite.
        Set("ToolTipBackground", p.Surface);
        Set("ToolTipForeground", p.TextHigh);
        Set("ToolTipBorderBrush", p.Stroke);

        // Menus e listas suspensas. Sem isto sairia o cinza do Fluent, igual em
        // todo tema escuro — um menu grafite em cima de um painel azulado.
        Set("MenuFlyoutPresenterBackground", p.Surface);
        Set("MenuFlyoutPresenterBorderBrush", p.Stroke);
        Set("FlyoutPresenterBackground", p.Surface);
        Set("FlyoutBorderThemeBrush", p.Stroke);
        Set("ComboBoxDropDownBackground", p.Surface);
        Set("ComboBoxDropDownBorderBrush", p.Stroke);

        // O anel de foco do teclado: texto principal por fora, fundo por
        // dentro. Contrasta com qualquer superfície do próprio tema.
        Set("SystemControlFocusVisualPrimaryBrush", p.TextHigh);
        Set("SystemControlFocusVisualSecondaryBrush", p.Canvas);
    }

    /// <summary>Imutável: o mesmo pincel pode servir a várias janelas e threads.</summary>
    private static IBrush Brush(Color color, double opacity = 1) =>
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color, opacity);

    /// <summary>Mistura linear em sRGB: <paramref name="amount"/> de <paramref name="to"/>.</summary>
    internal static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + ((b - a) * amount));

        return Color.FromArgb(
            Channel(from.A, to.A),
            Channel(from.R, to.R),
            Channel(from.G, to.G),
            Channel(from.B, to.B));
    }
}
