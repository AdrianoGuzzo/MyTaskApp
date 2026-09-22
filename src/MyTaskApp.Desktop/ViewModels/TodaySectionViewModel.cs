using System.Collections.ObjectModel;
using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Desktop.ViewModels;

public sealed class TodaySectionViewModel(
    string header,
    TodaySection section,
    IEnumerable<TaskRowViewModel> items,
    bool canReorder)
{
    public string Header { get; } = header;

    /// <summary>
    /// Qual lista do <c>TodayBoard</c> esta seção desenha. É por aqui que o
    /// quadro guardado em memória acompanha um arrasto, sem custar consulta —
    /// sem isso, fixar o painel remontaria a lista a partir do quadro velho e
    /// ressuscitaria a ordem anterior (ADR-017, ADR-022).
    /// </summary>
    public TodaySection Section { get; } = section;

    /// <summary>
    /// Observável, e não <c>IReadOnlyList</c>: é o <c>Move</c> desta coleção que
    /// a lista na tela acompanha enquanto o item está sendo arrastado.
    /// </summary>
    public ObservableCollection<TaskRowViewModel> Items { get; } = [.. items];

    /// <summary>
    /// CONCLUÍDAS não reordena: ali a ordem é a da conclusão (ADR-022). A alça
    /// some nessas linhas — alça que não faz nada é pior do que alça nenhuma.
    /// </summary>
    public bool CanReorder { get; } = canReorder;

    public void Move(int from, int to) => Items.Move(from, to);
}
