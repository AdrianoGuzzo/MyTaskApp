using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Adapters;

/// <summary>
/// Os temas do desktop no celular. Cada cor da paleta vira um recurso de mesmo
/// nome nos recursos do app (<c>Canvas</c>, <c>Accent</c>…), e as telas usam
/// <c>DynamicResource</c>: trocar o tema troca os valores, e tudo acompanha
/// na hora, sem reabrir.
/// </summary>
/// <remarks>
/// <para>
/// <b>O sistema acompanha.</b> Um tema escolhido fixa o claro/escuro do app
/// (<see cref="Microsoft.Maui.Controls.Application.UserAppTheme"/>): teclado, barra de status e
/// seletores nativos ficam na mesma variante da paleta. No "Automático" o app
/// fica livre e segue o claro/escuro do celular, inclusive ao vivo.
/// </para>
/// <para>
/// <b>Só neste celular.</b> A escolha vai para as preferências do aparelho,
/// como o <c>widget.json</c> do desktop fica só no computador.
/// </para>
/// </remarks>
public sealed class MauiThemeService : IThemeService
{
    private const string PreferenceKey = "theme";

    private Microsoft.Maui.Controls.Application? _app;

    private MobileTheme? _applied;

    /// <summary>
    /// A paleta na tela mudou. O que lê cor por conversor (as linhas da lista)
    /// precisa reler; o que usa <c>DynamicResource</c> já acompanhou.
    /// </summary>
    public event EventHandler? Changed;

    public string Choice { get; private set; } =
        MobileThemeCatalog.Normalize(Preferences.Default.Get<string?>(PreferenceKey, null));

    /// <summary>Liga o serviço ao app, antes da primeira tela: assim nada pisca na cor errada.</summary>
    public void Attach(Microsoft.Maui.Controls.Application app)
    {
        _app = app;
        app.RequestedThemeChanged += (_, _) =>
        {
            if (Choice == MobileThemeCatalog.SystemId)
            {
                Apply();
            }
        };

        Apply();
    }

    public void Use(string choice)
    {
        Choice = MobileThemeCatalog.Normalize(choice);
        Preferences.Default.Set(PreferenceKey, Choice);
        MainThread.BeginInvokeOnMainThread(Apply);
    }

    private void Apply()
    {
        if (_app is null)
        {
            return;
        }

        var theme = MobileThemeCatalog.Resolve(Choice, _app.PlatformAppTheme == AppTheme.Dark);

        foreach (var (name, hex) in theme.Palette.Entries())
        {
            _app.Resources[name] = Color.FromArgb(hex);
        }

        _app.UserAppTheme = Choice == MobileThemeCatalog.SystemId
            ? AppTheme.Unspecified
            : theme.IsDark ? AppTheme.Dark : AppTheme.Light;

        PaintSystemBars(theme);

        if (_applied is not null && !ReferenceEquals(_applied, theme))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _applied = theme;
    }

    /// <summary>A janela já existe: a barra de status pode ganhar a cor do tema.</summary>
    public void Refresh() => MainThread.BeginInvokeOnMainThread(Apply);

    /// <summary>
    /// A barra de status do Android na cor do fundo, com ícones claros ou
    /// escuros conforme o tema. Do Android 15 em diante a tela vai até a borda
    /// e o que aparece atrás da barra é o fundo da janela; antes, é a cor da
    /// própria barra. No iOS quem decide é o <c>UserAppTheme</c>.
    /// </summary>
    private static void PaintSystemBars(MobileTheme theme)
    {
#if ANDROID
        if (Platform.CurrentActivity?.Window is not { } window)
        {
            return;
        }

        var canvas = Android.Graphics.Color.ParseColor(theme.Palette.Canvas);
        window.DecorView.SetBackgroundColor(canvas);

#pragma warning disable CA1422
        window.SetStatusBarColor(canvas);
#pragma warning restore CA1422

        new AndroidX.Core.View.WindowInsetsControllerCompat(window, window.DecorView).AppearanceLightStatusBars = !theme.IsDark;
#else
        _ = theme;
#endif
    }
}
