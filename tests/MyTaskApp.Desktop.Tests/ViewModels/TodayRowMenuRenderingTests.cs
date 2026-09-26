using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O menu de ações secundárias da linha (§12). Vale o teste headless pelo mesmo
/// critério do checkbox: os comandos são ligados por um caminho com cast até o
/// <c>DataContext</c> do <c>UserControl</c>, <b>de dentro de um flyout</b> — se
/// esse caminho quebrar, o menu abre bonito e nenhum item faz nada, sem a build
/// reclamar de coisa alguma.
/// </summary>
public class TodayRowMenuRenderingTests
{
    private static readonly DateOnly Date = new(2026, 9, 21);

    private static TodayTask Row(string title, params TaskWorktree[] worktrees) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false,
            Worktrees: worktrees.Length == 0 ? null : worktrees);

    private static TaskWorktree Worktree(string repository = "eco-core") =>
        new(Guid.CreateVersion7(), repository, "feature/x", "origin/main", $@"C:\Projects\{repository}-feature-x");

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
        window.UpdateLayout();

        return window;
    }

    private static (Border Row, MenuFlyout Menu) RowMenuOf(Visual window)
    {
        var row = window.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.ContextFlyout is MenuFlyout);

        return (row, (MenuFlyout)row.ContextFlyout!);
    }

    /// <summary>
    /// Os itens de um flyout só entram na árvore visual quando ele abre — e é
    /// só aí que os bindings de comando são avaliados. Sem abrir, o teste
    /// passaria sobre objetos que nunca foram ligados a nada.
    /// </summary>
    private static MenuFlyout OpenRowMenu(Visual window)
    {
        var (row, menu) = RowMenuOf(window);

        menu.ShowAt(row);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return menu;
    }

    [AvaloniaFact]
    public async Task EachRow_CarriesTheSecondaryActionsMenu()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Fechar o mês")], [], []));

        var items = RowMenuOf(window).Menu.Items.OfType<MenuItem>().ToList();

        items.Select(item => item.Header).Should().Equal("Abrir Claude Code", "Arquivar", "Mover para a lixeira…");
    }

    /// <summary>
    /// "Abrir Claude Code" só existe para quem tem onde abrir: sem ambiente
    /// pronto, o item e o separador dele somem (ADR-036).
    /// </summary>
    [AvaloniaFact]
    public async Task StartAgent_OnlyShowsUpWhenTheTaskHasAnEnvironment()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Fechar o mês")], [], []));

        var menu = OpenRowMenu(window);

        menu.Items.OfType<MenuItem>().Single(item => item.Classes.Contains("startAgent")).IsVisible.Should().BeFalse();
        menu.Items.OfType<Separator>().First().IsVisible.Should().BeFalse();

        window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Corrigir animais", Worktree())], [], []));

        menu = OpenRowMenu(window);

        menu.Items.OfType<MenuItem>().Single(item => item.Classes.Contains("startAgent")).IsVisible.Should().BeTrue();
        menu.Items.OfType<Separator>().First().IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task EveryMenuItem_ResolvesItsCommandAndItsRow()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Fechar o mês")], [], []));

        // "Abrir Claude Code" é clique de code-behind: pode precisar perguntar o ambiente.
        var items = OpenRowMenu(window).Items.OfType<MenuItem>()
            .Where(item => !item.Classes.Contains("startAgent"))
            .ToList();

        items.Should().NotBeEmpty().And.AllSatisfy(item =>
        {
            item.Command.Should().NotBeNull();
            item.CommandParameter.Should().BeOfType<TaskRowViewModel>();
        });
    }

    /// <summary>
    /// O "⋯" abre o <c>ContextFlyout</c> da própria linha, e não um menu só
    /// dele: é isso que garante que clique direito e botão nunca ofereçam coisas
    /// diferentes.
    /// </summary>
    [AvaloniaFact]
    public async Task TheOverflowButton_HasNoMenuOfItsOwnToDriftFrom()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Fechar o mês")], [], []));

        var overflow = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("rowMenu"));

        overflow.Flyout.Should().BeNull();
    }

    /// <summary>
    /// A confirmação do que foi feito compartilha o lugar da mensagem de erro,
    /// para o usuário sempre olhar para o mesmo ponto depois de agir.
    /// </summary>
    [AvaloniaFact]
    public async Task TheSuccessBanner_OnlyShowsUpWhenThereIsSomethingToSay()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Fechar o mês")], [], []));
        var viewModel = (TodayViewModel)window.DataContext!;

        bool BannerIsVisible() =>
            window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(block => block.Text == "Checklist arquivado com sucesso.");

        BannerIsVisible().Should().BeFalse();

        viewModel.StatusMessage = "Checklist arquivado com sucesso.";
        window.UpdateLayout();

        BannerIsVisible().Should().BeTrue();
    }
}
