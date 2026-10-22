using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O caminho de erro das telas (ADR-008), fixado tela a tela antes de ser
/// centralizado: a regra mostra a própria mensagem; o resto vai para o log com
/// o evento daquela tela e mostra a frase de fallback; o "ocupado" sempre volta;
/// e cada tela limpa ou não o status como sempre limpou. Onde o cancelamento é
/// silencioso, ele continua silencioso.
/// </summary>
public class ScreenErrorPathTests
{
    private const string Rule = "A regra recusou.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Screens => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task ABusinessRule_ShowsItsOwnMessage_AndLogsNothing(string screen)
    {
        var runner = new FakeUseCaseRunner { NextFailure = new DomainException(Rule) };
        var subject = Cases[screen](runner);

        await subject.Act();

        subject.Error().Should().Be(Rule);
        subject.Busy().Should().BeFalse();
        subject.Logged().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task AnUnexpectedFailure_LogsTheScreensEvent_AndShowsTheFallback(string screen)
    {
        var runner = new FakeUseCaseRunner { NextFailure = new InvalidOperationException("SQLITE_BUSY") };
        var subject = Cases[screen](runner);

        await subject.Act();

        subject.Error().Should().Be(subject.Fallback);
        subject.Busy().Should().BeFalse();
        subject.Logged().Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message, Exception? Exception)>(entry =>
                entry.Level == LogLevel.Error && entry.Message == subject.Event && entry.Exception is InvalidOperationException);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task TheStatusLine_IsClearedOnlyWhereItAlwaysWas(string screen)
    {
        var runner = new FakeUseCaseRunner { NextFailure = new DomainException(Rule) };
        var subject = Cases[screen](runner);
        subject.SetStatus("Salvo.");

        await subject.Act();

        subject.Status().Should().Be(subject.ClearsStatus ? null : "Salvo.");
    }

    [Theory]
    [InlineData("Integrações")]
    [InlineData("Conexões de banco")]
    public async Task ACancellation_IsSilent(string screen)
    {
        var runner = new FakeUseCaseRunner { NextFailure = new OperationCanceledException() };
        var subject = Cases[screen](runner);

        await subject.Act();

        subject.Error().Should().BeNull();
        subject.Busy().Should().BeFalse();
        subject.Logged().Should().BeEmpty();
    }

    private static readonly Dictionary<string, Func<FakeUseCaseRunner, Screen>> Cases = new()
    {
        ["Hoje"] = runner =>
        {
            var logger = new CapturingLogger<TodayViewModel>();
            var viewModel = new TodayViewModel(runner, new FakeConfirmationDialog(), new FakeClipboardWriter(), new FakeTimeProvider(), logger);
            return new Screen(
                () => viewModel.LoadAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível carregar suas tarefas.", "TodayScreenOperationFailed", ClearsStatus: true);
        },
        ["Etiquetas"] = runner =>
        {
            var logger = new CapturingLogger<TagsViewModel>();
            var viewModel = new TagsViewModel(runner, new FakeConfirmationDialog(), new FakeDirectoryProbe(), new FakeDirectoryCommandEditor(), new FakeClipboardWriter(), new FakeShellLauncher(), logger);
            return new Screen(
                () => viewModel.LoadAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível carregar as etiquetas.", "TagsScreenOperationFailed", ClearsStatus: true);
        },
        ["Comandos globais"] = runner =>
        {
            var logger = new CapturingLogger<DevelopmentCommandsViewModel>();
            var viewModel = new DevelopmentCommandsViewModel(runner, new FakeConfirmationDialog(), logger);
            return new Screen(
                () => viewModel.LoadAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível carregar os comandos.", "DevelopmentCommandsScreenOperationFailed", ClearsStatus: true);
        },
        ["Sons do agente"] = runner =>
        {
            var logger = new CapturingLogger<AgentAlertSoundsViewModel>();
            var viewModel = new AgentAlertSoundsViewModel(runner, new FakeConfirmationDialog(), logger);
            return new Screen(
                () => viewModel.LoadAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível carregar os sons.", "AgentAlertSoundsOperationFailed", ClearsStatus: false);
        },
        ["Lembretes"] = runner =>
        {
            var logger = new CapturingLogger<ReminderSettingsViewModel>();
            var viewModel = new ReminderSettingsViewModel(runner, logger);
            return new Screen(
                () => viewModel.LoadAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível carregar a configuração de lembretes.", "ReminderSettingsOperationFailed", ClearsStatus: false);
        },
        ["Integrações"] = runner =>
        {
            var logger = new CapturingLogger<IntegrationsViewModel>();
            var viewModel = new IntegrationsViewModel(runner, new FakeShellLauncher(), new FakeConfirmationDialog(), logger);
            return new Screen(
                () => viewModel.LoadAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível carregar as integrações.", "IntegrationsOperationFailed", ClearsStatus: false);
        },
        ["Conexões de banco"] = runner =>
        {
            var logger = new CapturingLogger<DatabaseConnectionsViewModel>();
            var viewModel = new DatabaseConnectionsViewModel(runner, new FakeConfirmationDialog(), logger);
            return new Screen(
                () => viewModel.TestAsync(Ct), () => viewModel.ErrorMessage, () => viewModel.IsBusy,
                () => viewModel.StatusMessage, value => viewModel.StatusMessage = value, () => logger.Entries,
                "Não foi possível testar a conexão.", "DatabaseConnectionsScreenOperationFailed", ClearsStatus: true);
        },
    };

    public sealed record Screen(
        Func<Task> Act,
        Func<string?> Error,
        Func<bool> Busy,
        Func<string?> Status,
        Action<string?> SetStatus,
        Func<List<(LogLevel Level, string Message, Exception? Exception)>> Logged,
        string Fallback,
        string Event,
        bool ClearsStatus);
}
