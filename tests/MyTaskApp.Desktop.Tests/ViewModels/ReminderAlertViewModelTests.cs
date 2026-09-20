using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

public class ReminderAlertViewModelTests
{
    private static readonly Guid OccurrenceId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    [Fact]
    public void ShowingAnAlert_FillsWhatTheWindowDraws()
    {
        var viewModel = Alert(step: 4, waiting: TimeSpan.FromMinutes(35));

        viewModel.Title.Should().Be("Verificar estoque");
        viewModel.TimeLabel.Should().Be("15:30");
        viewModel.WaitingLabel.Should().Be("Aguardando sua atenção há 35 minutos.");
        viewModel.Step.Should().Be(4);
        viewModel.IsProminent.Should().BeTrue();
    }

    [Fact]
    public async Task Opening_AsksToAcknowledgeTheReminder()
    {
        var viewModel = Alert();

        await viewModel.OpenAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(AcknowledgeReminderHandler));
    }

    [Fact]
    public async Task MarkingAsSeen_AsksToAcknowledgeTheReminder()
    {
        var viewModel = Alert();

        await viewModel.MarkSeenAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(AcknowledgeReminderHandler));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("30")]
    [InlineData("60")]
    public async Task EverySnoozeButton_AsksToSnooze(string minutes)
    {
        var viewModel = Alert();

        await viewModel.SnoozeAsync(minutes, Ct);

        _runner.Invoked.Should().Contain(typeof(SnoozeReminderHandler));
    }

    [Fact]
    public async Task ActingOnAnAlert_TellsTheWindowItCanGo()
    {
        var viewModel = Alert();
        var acted = false;
        viewModel.Acted += _ => acted = true;

        await viewModel.MarkSeenAsync(Ct);

        acted.Should().BeTrue();
    }

    [Fact]
    public async Task ARefusedActionShowsTheReasonAndKeepsTheAlertOnScreen()
    {
        // ADR-008: a mensagem do domínio já foi escrita para o usuário.
        var viewModel = Alert();
        var acted = false;
        viewModel.Acted += _ => acted = true;
        _runner.NextFailure = new DomainException("Este lembrete já foi atendido.");

        await viewModel.MarkSeenAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Este lembrete já foi atendido.");
        acted.Should().BeFalse();
    }

    [Fact]
    public async Task AnInfrastructureFailure_IsReplacedByAMessageTheUserUnderstands()
    {
        var viewModel = Alert();
        _runner.NextFailure = new InvalidOperationException("SQLITE_BUSY");

        await viewModel.MarkSeenAsync(Ct);

        viewModel.ErrorMessage.Should().Be(
            "Não foi possível registrar que você viu este lembrete.");
    }

    [Theory]
    [InlineData(0, "Esperando a sua atenção agora.")]
    [InlineData(1, "Aguardando sua atenção há 1 minuto.")]
    [InlineData(35, "Aguardando sua atenção há 35 minutos.")]
    [InlineData(90, "Aguardando sua atenção há 90 minutos.")]
    [InlineData(120, "Aguardando sua atenção há 2 horas.")]
    [InlineData(60 * 26, "Aguardando sua atenção há 26 horas.")]
    public void TheWaitIsWrittenInWordsAPersonReads(int minutes, string expected)
    {
        ReminderAlertViewModel.DescribeWait(TimeSpan.FromMinutes(minutes))
            .Should().Be(expected);
    }

    private ReminderAlertViewModel Alert(int step = 1, TimeSpan? waiting = null)
    {
        var viewModel = new ReminderAlertViewModel(
            _runner,
            NullLogger<ReminderAlertViewModel>.Instance);

        viewModel.Show(new ReminderAlert(
            OccurrenceId,
            Guid.CreateVersion7(),
            "Verificar estoque",
            "15:30",
            ReminderEscalation.LevelFor(step, AlertChannels.All),
            waiting ?? TimeSpan.Zero));

        return viewModel;
    }
}
