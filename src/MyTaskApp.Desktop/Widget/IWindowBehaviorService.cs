using Avalonia;
using Avalonia.Controls;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// O que só o sistema operacional sabe fazer com a janela (ADR-048). Fica
/// atrás de uma porta para nenhuma chamada nativa vazar para o ViewModel, e
/// para o Linux e os testes headless receberem o objeto nulo em vez de um
/// <c>DllNotFoundException</c>.
/// </summary>
/// <remarks>
/// "Sempre no topo" não está aqui: <c>Window.Topmost</c> já é multiplataforma,
/// e embrulhá-lo seria indireção sem ganho.
/// </remarks>
public interface IWindowBehaviorService
{
    /// <summary>Se a plataforma consegue devolver ao app de trás o clique fora da região.</summary>
    bool SupportsClickThrough { get; }

    /// <summary>
    /// Restringe o mouse da janela a um retângulo arredondado, em DIPs e
    /// relativo à área cliente. Fora dele o clique vai para quem está atrás —
    /// é isso, e não a transparência do pixel, que decide no Windows.
    /// <c>null</c> devolve a janela inteira ao mouse.
    /// </summary>
    void SetInteractiveRegion(Window window, Rect? region, double cornerRadius);
}

/// <summary>
/// O objeto nulo. Sem recorte o HUD continua sem bloquear nada à toa: no modo
/// HUD a janela já tem o tamanho exato do cartão, sem margem transparente.
/// </summary>
public sealed class PortableWindowBehavior : IWindowBehaviorService
{
    public static PortableWindowBehavior Instance { get; } = new();

    public bool SupportsClickThrough => false;

    public void SetInteractiveRegion(Window window, Rect? region, double cornerRadius)
    {
    }
}
