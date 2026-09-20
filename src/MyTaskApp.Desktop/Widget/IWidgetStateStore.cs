namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Guarda a posição, o tamanho e as preferências do painel. Síncrono de
/// propósito: são duzentos bytes, e o último salvamento acontece no caminho de
/// encerramento, onde não há a quem esperar.
/// </summary>
public interface IWidgetStateStore
{
    WidgetState Load();

    void Save(WidgetState state);
}
