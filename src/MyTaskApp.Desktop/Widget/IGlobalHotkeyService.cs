using Avalonia.Controls;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Um atalho que funciona com o app em segundo plano (ADR-048). Porta, e não
/// código na janela, porque cada sistema tem o seu jeito — e o Wayland, por
/// desenho, nenhum.
/// </summary>
public interface IGlobalHotkeyService
{
    bool IsSupported { get; }

    /// <summary>Como o atalho aparece para o usuário.</summary>
    string GestureLabel { get; }

    /// <summary>
    /// <c>false</c> quando outro programa já é dono da combinação: o sistema
    /// não divide atalho global, e quem chama precisa contar isso ao usuário.
    /// </summary>
    bool Register(Window owner, Action pressed);

    void Unregister();
}

/// <summary>O objeto nulo: fora do Windows a opção some da tela.</summary>
public sealed class UnsupportedGlobalHotkey : IGlobalHotkeyService
{
    public static UnsupportedGlobalHotkey Instance { get; } = new();

    public bool IsSupported => false;

    public string GestureLabel => "Ctrl+Shift+Espaço";

    public bool Register(Window owner, Action pressed) => false;

    public void Unregister()
    {
    }
}
