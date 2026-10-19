using MyTaskApp.Application.Tasks;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>A aba Histórico: o que saiu da frente, com a mesma pilha da lista de Tarefas.</summary>
public sealed class HistoryViewModelTests : IAsyncLifetime
{
    private MobileHarness _app = null!;

    private HistoryViewModel History => _app.Get<HistoryViewModel>();

    public async ValueTask InitializeAsync() => _app = await MobileHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task ThePresets_SplitCompletedAndCancelled_AndLeavePendingOut()
    {
        var done = await _app.CreateTaskAsync("Feita");
        var cancelled = await _app.CreateTaskAsync("Desistida");
        await _app.CreateTaskAsync("Por fazer");
        await _app.Runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(done.OccurrenceId), token), TestContext.Current.CancellationToken);
        await _app.Runner.RunAsync<CancelOccurrenceHandler>((handler, token) => handler.HandleAsync(new CancelOccurrence(cancelled.OccurrenceId), token), TestContext.Current.CancellationToken);

        await History.LoadAsync();

        History.Presets.Select(choice => choice.Label).Should().Equal("Concluídas", "Canceladas");
        History.Tasks.Select(row => row.Title).Should().Equal("Feita");
        History.Tasks.Single().ShowsStatus.Should().BeTrue();
        History.Tasks.Single().ToggleLabel.Should().Be("Reabrir");

        await History.SelectPresetAsync(TaskListPreset.Cancelled);

        History.Tasks.Select(row => row.Title).Should().Equal("Desistida");
        History.Tasks.Single().CanToggle.Should().BeFalse("cancelada se reabre pelo detalhe");
    }

    [Fact]
    public async Task WithNothingDone_TheHistorySaysSo()
    {
        await _app.CreateTaskAsync("Por fazer");

        await History.LoadAsync();

        History.State.Should().Be(ListState.Empty);
        History.EmptyTitle.Should().Be("Nada no histórico");
        History.OffersSignInWhenEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task TheFilterSheet_IsTheHistorysOwn()
    {
        await History.OpenFiltersAsync();

        _app.Navigator.Last.Should().Be("filters/history");
    }
}
