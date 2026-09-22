using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Interactions;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A alça do arrasto desenhada de verdade. Vale o teste headless por duas
/// razões, e só por elas: a linha mudou de coluna (o compilador não reclamaria
/// de um <c>Grid.Column</c> trocado) e o papel de decoração declarado na alça é
/// o que impede o arrasto de virar empurrão na janela no modo discreto — a
/// armadilha nº 4 do ADR-017, que passou pelo compilador e pelos testes e só
/// quebrou na máquina de quem usa.
/// <para>
/// A suavidade do pouso, o deslizamento do vizinho e a rolagem automática não
/// são testados: nada disso falha de um jeito que um <c>Settle</c> capturasse.
/// </para>
/// </summary>
public class TodayReorderRenderingTests
{
    private static readonly DateOnly Date = new(2026, 9, 21);

    private static TodayTask Row(string title) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            title,
            TaskPriority.Normal,
            Date,
            null,
            false);

    private static async Task<MainWindow> ShowAsync(TodayBoard board)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Settle(window);

        return window;
    }

    /// <summary>
    /// Ver <c>WidgetGhostTests.Settle</c>: o render forçado é o que faz o layout
    /// novo chegar à árvore de composição.
    /// </summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static TodayBoard Board(
        IReadOnlyList<TodayTask>? unscheduled = null,
        IReadOnlyList<TodayTask>? completed = null) =>
        new(Date, [], [], [], unscheduled ?? [], completed ?? []);

    private static IReadOnlyList<Border> Handles(Visual window) =>
        [.. window.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("dragHandle"))];

    private static ContentControl Ghost(Visual window) =>
        window.GetVisualDescendants()
            .OfType<ContentControl>()
            .Single(control => control.Name == "DragGhost");

    [AvaloniaFact]
    public async Task EveryPendingRow_CarriesADragHandle()
    {
        var window = await ShowAsync(Board(
            unscheduled: [Row("Comprar pão"), Row("Barbeiro")]));

        Handles(window).Should().HaveCount(2);
    }

    /// <summary>
    /// O papel declarado, e não o clique: no headless não há hit-test
    /// não-cliente, então um clique simulado passaria com qualquer valor. Foi
    /// exatamente assim que a armadilha nº 4 do ADR-017 escapou.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDragHandle_IsDeclaredAsUserSoItDoesNotShoveTheWindow()
    {
        var window = await ShowAsync(Board(unscheduled: [Row("Comprar pão")]));

        WindowDecorationProperties.GetElementRole(Handles(window).Single())
            .Should().Be(WindowDecorationsElementRole.User);
    }

    /// <summary>
    /// CONCLUÍDAS ordena pela conclusão e ignora a posição (ADR-022). Uma alça
    /// ali seria um convite a um gesto que o domínio recusa.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCompletedSection_HasNoDragHandle()
    {
        var window = await ShowAsync(Board(
            unscheduled: [Row("Pendente")],
            completed: [Row("Feita")]));

        Handles(window).Where(handle => handle.IsEffectivelyVisible)
            .Should().ContainSingle();
    }

    /// <summary>
    /// A linha ganhou uma coluna à esquerda, e tudo o que vinha depois andou uma
    /// casa. Um <c>Grid.Column</c> esquecido compila e só aparece na tela.
    /// </summary>
    [AvaloniaFact]
    public async Task MovingTheRowIntoANewColumn_DidNotUnwireTheCheckbox()
    {
        var window = await ShowAsync(Board(unscheduled: [Row("Comprar pão")]));

        var checkBox = window.GetVisualDescendants().OfType<CheckBox>().Single();

        checkBox.Command.Should().NotBeNull();
        checkBox.CommandParameter.Should().BeOfType<TaskRowViewModel>();
    }

    [AvaloniaFact]
    public async Task TheLiftedItem_StaysOutOfSightUntilSomethingIsDragged()
    {
        var window = await ShowAsync(Board(unscheduled: [Row("Comprar pão")]));

        var ghost = Ghost(window);

        ghost.IsVisible.Should().BeFalse();
        ghost.IsHitTestVisible.Should().BeFalse();
    }

    /// <summary>
    /// O gesto inteiro, sem display: pegar a alça, passar do limiar e ver a
    /// coleção trocar de ordem. É o fio entre o ponteiro e o ViewModel — se ele
    /// romper, a alça vira enfeite e nada avisa.
    /// </summary>
    [AvaloniaFact]
    public async Task DraggingTheHandlePastTheRowBelow_ReordersTheSection()
    {
        var window = await ShowAsync(Board(
            unscheduled: [Row("Primeira"), Row("Segunda")]));

        var view = window.GetVisualDescendants().OfType<TodayView>().Single();
        var section = ((TodayViewModel)window.DataContext!).Sections.Single();
        var handle = Handles(window)[0];

        var grab = handle.TranslatePoint(new Point(5, 5), view)!.Value;
        var rows = view.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("row"))
            .ToList();

        var below = rows[1].TranslatePoint(
            new Point(0, rows[1].Bounds.Height / 2 + 2), view)!.Value;

        window.MouseDown(handle.TranslatePoint(new Point(5, 5), window)!.Value, MouseButton.Left);
        window.MouseMove(view.TranslatePoint(grab.WithY(grab.Y + ReorderDrag.StartThreshold + 1), window)!.Value);
        window.MouseMove(view.TranslatePoint(below.WithY(below.Y + 1), window)!.Value);

        Settle(window);

        section.Items.Select(row => row.Title).Should().Equal("Segunda", "Primeira");

        window.MouseUp(view.TranslatePoint(below, window)!.Value, MouseButton.Left);
        Settle(window);
    }
}
