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

/// <summary>
/// Um atalho global por combinação (ADR-054): o do HUD é fixo, o do post-it é
/// escolha do usuário e muda em tempo de execução.
/// </summary>
public interface IGlobalHotkeyFactory
{
    bool IsSupported { get; }

    IGlobalHotkeyService Create(HotkeyGesture gesture);
}

/// <summary>O objeto nulo: fora do Windows a opção some da tela.</summary>
public sealed class UnsupportedGlobalHotkey(string gestureLabel) : IGlobalHotkeyService
{
    public static UnsupportedGlobalHotkey Instance { get; } = new(HotkeyGesture.ToggleHud.Label);

    public bool IsSupported => false;

    public string GestureLabel { get; } = gestureLabel;

    public bool Register(Window owner, Action pressed) => false;

    public void Unregister()
    {
    }
}

/// <summary>Fora do Windows, e no host headless: nenhum atalho global.</summary>
public sealed class UnsupportedGlobalHotkeyFactory : IGlobalHotkeyFactory
{
    public static UnsupportedGlobalHotkeyFactory Instance { get; } = new();

    public bool IsSupported => false;

    public IGlobalHotkeyService Create(HotkeyGesture gesture) => new UnsupportedGlobalHotkey(gesture.Label);
}
