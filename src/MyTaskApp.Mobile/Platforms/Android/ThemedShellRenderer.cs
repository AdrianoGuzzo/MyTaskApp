using Google.Android.Material.AppBar;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace MyTaskApp.Mobile;

/// <summary>
/// A Shell do Android com a barra do topo inteira na cor do tema (ADR-075).
/// </summary>
/// <remarks>
/// A barra de ferramentas fica dentro de uma <c>AppBarLayout</c> que vai até
/// debaixo da barra de status. O <c>Shell.BackgroundColor</c> pinta só a
/// barra de ferramentas; a <c>AppBarLayout</c> ficava no <c>colorPrimary</c>
/// do pacote do MAUI (#2C3E50, azul-marinho) e com sombra. Aqui ela ganha a
/// mesma cor, sem sombra, a cada vez que a Shell aplica a aparência —
/// inclusive na troca de tema.
/// </remarks>
internal sealed class ThemedShellRenderer : ShellRenderer
{
    protected override IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() =>
        new SeamlessToolbarTracker(this);

    private sealed class SeamlessToolbarTracker(IShellContext context) : ShellToolbarAppearanceTracker(context)
    {
        public override void SetAppearance(AToolbar toolbar, IShellToolbarTracker toolbarTracker, ShellAppearance appearance)
        {
            base.SetAppearance(toolbar, toolbarTracker, appearance);
            Blend(toolbar, appearance.BackgroundColor);
        }

        public override void ResetAppearance(AToolbar toolbar, IShellToolbarTracker toolbarTracker)
        {
            base.ResetAppearance(toolbar, toolbarTracker);
            Blend(toolbar, null);
        }

        private static void Blend(AToolbar toolbar, Color? color)
        {
            if (toolbar.Parent is not AppBarLayout bar)
            {
                return;
            }

            bar.OutlineProvider = null;

            if (color is not null)
            {
                bar.SetBackgroundColor(color.ToPlatform());
            }
        }
    }
}
