using CoreGraphics;
using Foundation;
using UIKit;

namespace MyTaskApp.Mobile;

/// <summary>
/// O aviso curto que o iOS não tem: "Tarefa salva" numa etiqueta acima da
/// barra de abas, que aparece, espera e some, sem tomar o toque. O leitor de
/// tela anuncia o mesmo texto.
/// </summary>
internal static class IosToast
{
    private const double Visible = 2.0;

    /// <summary>A distância da borda de baixo: acima da barra de abas e do botão +.</summary>
    private const double AboveTabs = 104;

    public static void Show(string message)
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(candidate => candidate.IsKeyWindow);

        if (window is null)
        {
            return;
        }

        var label = new UILabel
        {
            Text = message,
            TextColor = UIColor.White,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Subheadline),
            AdjustsFontForContentSizeCategory = true,
            Lines = 0,
            TextAlignment = UITextAlignment.Center,
        };

        var size = label.SizeThatFits(new CGSize(window.Bounds.Width - 96, double.MaxValue));
        var toast = new UIView
        {
            BackgroundColor = UIColor.FromWhiteAlpha(0.15f, 0.94f),
            Alpha = 0,
            UserInteractionEnabled = false,
            Frame = new CGRect(0, 0, size.Width + 32, size.Height + 20),
        };

        toast.Layer.CornerRadius = 14;
        label.Frame = new CGRect(16, 10, size.Width, size.Height);
        toast.AddSubview(label);
        toast.Center = new CGPoint(
            window.Bounds.GetMidX(),
            window.Bounds.Height - window.SafeAreaInsets.Bottom - AboveTabs - (toast.Frame.Height / 2));

        window.AddSubview(toast);
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new NSString(message));

        UIView.Animate(
            0.2,
            () => toast.Alpha = 1,
            () => UIView.Animate(0.3, Visible, UIViewAnimationOptions.CurveEaseIn, () => toast.Alpha = 0, toast.RemoveFromSuperview));
    }
}
