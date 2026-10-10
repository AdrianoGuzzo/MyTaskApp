namespace MyTaskApp.Application.Abstractions;

/// <summary>O que mudou, para a tela saber o que recarregar.</summary>
[Flags]
public enum DataArea
{
    None = 0,
    Tasks = 1,
    Time = 2,
    Tags = 4,
    StickyNotes = 8,
    Databases = 16,
}

/// <summary>
/// "Alguém de fora mudou os dados" (ADR-059). A tela recarrega sozinha a cada
/// minuto, e as próprias telas avisam umas às outras por evento; quem grava sem
/// passar por tela nenhuma — o servidor MCP — avisa por aqui, e a lista mostra a
/// tarefa que a IA acabou de criar sem esperar o tique.
/// </summary>
/// <remarks>
/// O evento dispara na thread de quem gravou, nunca na da interface: quem
/// assina é que leva para a thread certa, como no <c>AgentSessionMonitor</c>.
/// </remarks>
public interface IDataChangeNotifier
{
    event Action<DataArea>? Changed;

    void NotifyChanged(DataArea areas);
}

public sealed class DataChangeNotifier : IDataChangeNotifier
{
    public event Action<DataArea>? Changed;

    public void NotifyChanged(DataArea areas)
    {
        if (areas is not DataArea.None)
        {
            Changed?.Invoke(areas);
        }
    }
}
