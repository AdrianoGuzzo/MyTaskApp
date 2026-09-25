using Avalonia;
using Avalonia.Controls;

namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// <c>spell:SpellCheck.IsEnabled="True"</c> numa <see cref="TextBox"/> liga a
/// correção ortográfica nela (ADR-032). É uma attached property, e não código
/// em cada janela, porque são cinco campos em quatro telas e o comportamento
/// é sempre o mesmo.
/// </summary>
public sealed class SpellCheck
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SpellCheck, TextBox, bool>("IsEnabled");

    private static readonly AttachedProperty<SpellCheckBinder?> BinderProperty =
        AvaloniaProperty.RegisterAttached<SpellCheck, TextBox, SpellCheckBinder?>("Binder");

    static SpellCheck() =>
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnIsEnabledChanged);

    private SpellCheck()
    {
    }

    /// <summary>
    /// O corretor que as caixas usam. A <c>App</c> troca pelo do contêiner na
    /// inicialização; até lá — e nos testes headless e no designer — é o
    /// objeto nulo, e ligar a propriedade não faz nada visível.
    /// </summary>
    public static ISpellChecker Checker { get; set; } = NoSpellChecker.Instance;

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);

    internal static SpellCheckBinder? GetBinder(TextBox box) => box.GetValue(BinderProperty);

    private static void OnIsEnabledChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        box.GetValue(BinderProperty)?.Dispose();
        box.SetValue(BinderProperty, e.NewValue is true ? new SpellCheckBinder(box, Checker) : null);
    }
}
