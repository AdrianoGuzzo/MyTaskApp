using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O início automático sem o registro do Windows. Sabe recusar a escrita, que é
/// o caminho que importa: política de grupo e perfil em rede fazem o
/// <c>SetValue</c> falhar de verdade, e aí o visto do menu não pode continuar
/// dizendo que ligou.
/// </summary>
internal sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool IsSupported { get; set; } = true;

    public bool IsEnabled { get; set; }

    /// <summary>Liga a recusa — o registro travado, que o teste não consegue simular.</summary>
    public bool Refuses { get; set; }

    public int Enabled { get; private set; }

    public int Disabled { get; private set; }

    public bool Enable()
    {
        Enabled++;

        if (Refuses)
        {
            return false;
        }

        IsEnabled = true;
        return true;
    }

    public bool Disable()
    {
        Disabled++;

        if (Refuses)
        {
            return false;
        }

        IsEnabled = false;
        return true;
    }
}
