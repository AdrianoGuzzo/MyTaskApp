using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O resumo do dia. Os números saem do próprio quadro: se a tela recontasse,
/// ela poderia discordar do que o domínio classificou.
/// </summary>
public class TodayProgressTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    private static TodayTask Row(string title) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false);

    private static async Task<TodayViewModel> ShowAsync(TodayBoard board)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        return viewModel;
    }

    [Fact]
    public async Task ItCountsWhatIsDoneAgainstWhatTheBoardShows()
    {
        var viewModel = await ShowAsync(new TodayBoard(
            Date,
            Overdue: [Row("a")],
            Now: [],
            Today: [Row("b"), Row("c")],
            Unscheduled: [Row("d")],
            Completed: [Row("e"), Row("f"), Row("g")]));

        viewModel.TotalCount.Should().Be(7);
        viewModel.CompletedCount.Should().Be(3);
        viewModel.PendingCount.Should().Be(4);
        viewModel.ProgressLabel.Should().Be("3 de 7 concluídas");
        viewModel.CountLabel.Should().Be("3/7");
        viewModel.HasProgress.Should().BeTrue();
    }

    [Fact]
    public async Task TheProgressValueIsAPercentage()
    {
        var viewModel = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("a")], [], [Row("b"), Row("c"), Row("d")]));

        viewModel.ProgressValue.Should().Be(75);
    }

    [Fact]
    public async Task AnEmptyDay_ShowsNoProgressBarAtAll()
    {
        var viewModel = await ShowAsync(new TodayBoard(Date, [], [], [], [], []));

        viewModel.HasProgress.Should().BeFalse();
        viewModel.ProgressValue.Should().Be(0);
        viewModel.ProgressLabel.Should().BeEmpty();
        viewModel.CountLabel.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "Tudo em dia")]
    [InlineData(1, "1 tarefa pendente")]
    [InlineData(3, "3 tarefas pendentes")]
    public async Task ThePendingLabelReadsLikeSomeoneWroteIt(int pending, string expected)
    {
        var todo = Enumerable.Range(0, pending).Select(index => Row($"t{index}")).ToList();

        var viewModel = await ShowAsync(new TodayBoard(Date, [], [], todo, [], []));

        viewModel.PendingLabel.Should().Be(expected);
    }

    [Fact]
    public async Task TheTrayIsToldHowManyAreLeft()
    {
        // É o "badge" do widget: sem este aviso o ícone nunca atualiza.
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner
            {
                Result = new TodayBoard(Date, [], [], [Row("a"), Row("b")], [], [Row("c")]),
            },
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        var reported = new List<int>();
        viewModel.PendingChanged += reported.Add;

        await viewModel.LoadAsync(CancellationToken.None);

        reported.Should().Equal(2);
    }

    [Fact]
    public async Task TheDateHeaderKeepsItsShape()
    {
        // O cabeçalho compacto mostra este mesmo rótulo como linha de data.
        var viewModel = await ShowAsync(new TodayBoard(Date, [], [], [Row("a")], [], []));

        viewModel.Title.Should().Be("HOJE — 17/09/2026");
    }
}
