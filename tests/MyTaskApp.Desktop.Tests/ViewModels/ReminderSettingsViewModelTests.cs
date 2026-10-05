using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

public class ReminderSettingsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    [Fact]
    public async Task Loading_FillsTheFormWithTheStoredDefault()
    {
        _runner.Result = new ReminderSettings(ReminderPolicy.Urgent, null);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.Editor.IsEnabled.Should().BeTrue();
        viewModel.Editor.ToPolicy().Should().Be(ReminderPolicy.Urgent);
        viewModel.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task Loading_ShowsAnActivePause()
    {
        var until = DateTimeOffset.UtcNow.AddHours(1);
        _runner.Result = new ReminderSettings(ReminderPolicy.Default, until);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsPaused.Should().BeTrue();
        viewModel.PausedLabel.Should().Contain("pausados");
    }

    [Fact]
    public async Task Saving_AsksTheUpdateDefaultsUseCase()
    {
        var viewModel = await LoadedAsync();

        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(UpdateReminderDefaultsHandler));
    }

    [Fact]
    public async Task Saving_TellsTheWindowItCanClose()
    {
        var viewModel = await LoadedAsync();
        var saved = false;
        viewModel.Saved += () => saved = true;

        await viewModel.SaveAsync(Ct);

        saved.Should().BeTrue();
    }

    [Fact]
    public async Task AnImpossibleCombination_IsRefusedBeforeItReachesTheUseCase()
    {
        // Todos os canais desligados: o domínio recusa, e a tela mostra a
        // mensagem dele como está (ADR-008).
        var viewModel = await LoadedAsync();
        viewModel.Editor.Notify = false;
        viewModel.Editor.PlaySound = false;
        viewModel.Editor.BringToFront = false;

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Contain("forma de aviso");
        _runner.Invoked.Should().NotContain(typeof(UpdateReminderDefaultsHandler));
    }

    [Fact]
    public async Task ARefusedSave_ShowsTheDomainMessageAsItIs()
    {
        var viewModel = await LoadedAsync();
        _runner.NextFailure = new DomainException("O intervalo de repetição precisa ser maior.");

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Be("O intervalo de repetição precisa ser maior.");
    }

    [Fact]
    public async Task AnInfrastructureFailure_IsReplacedByAMessageTheUserUnderstands()
    {
        var viewModel = await LoadedAsync();
        _runner.NextFailure = new InvalidOperationException("SQLITE_BUSY");

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível salvar a configuração.");
    }

    [Fact]
    public async Task RestoringDefaults_PutsTheFactoryPolicyInTheFormWithoutSaving()
    {
        _runner.Result = new ReminderSettings(ReminderPolicy.None, null);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.RestoreDefaultsAsync(Ct);

        viewModel.Editor.ToPolicy().Should().Be(ReminderPolicy.Default);
        _runner.Invoked.Should().NotContain(typeof(UpdateReminderDefaultsHandler));
        viewModel.StatusMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task Resuming_AsksTheResumeUseCaseAndClearsTheBanner()
    {
        _runner.Result = new ReminderSettings(
            ReminderPolicy.Default, DateTimeOffset.UtcNow.AddHours(1));

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ResumeAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(ResumeRemindersHandler));
        viewModel.IsPaused.Should().BeFalse();
    }

    private async Task<ReminderSettingsViewModel> LoadedAsync()
    {
        _runner.Result = new ReminderSettings(ReminderPolicy.Default, null);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        return viewModel;
    }

    [Fact]
    public async Task Loading_AlsoFillsTheDeadlineAlerts()
    {
        _runner.Result = new ReminderSettings(ReminderPolicy.Default, null);
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(GetDeadlineSettingsHandler)] = new DeadlineSettings(
            new DeadlineAlertPolicy(false, DeadlineAlertPolicy.Default.Stages, TimeSpan.FromDays(1)),
            new TimeOnly(17, 0));

        await viewModel.LoadAsync(Ct);

        viewModel.Deadlines.IsDisabled.Should().BeTrue();
        viewModel.Deadlines.DefaultTime.Should().Be(TimeSpan.FromHours(17));
    }

    [Fact]
    public async Task Saving_AlsoSavesTheDeadlineAlerts_BeforeTheReminder()
    {
        var viewModel = await LoadedAsync();

        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().ContainInOrder(
            typeof(UpdateDeadlineSettingsHandler), typeof(UpdateReminderDefaultsHandler));
    }

    [Fact]
    public async Task RestoringDefaults_AlsoRestoresTheDeadlineAlerts()
    {
        var viewModel = await LoadedAsync();
        viewModel.Deadlines.IsDisabled = true;

        await viewModel.RestoreDefaultsAsync(Ct);

        viewModel.Deadlines.IsDefault.Should().BeTrue();
    }

    private ReminderSettingsViewModel ViewModel()
    {
        // A mesma janela carrega e grava os alertas de prazo (ADR-050).
        _runner.ResultsByHandler.TryAdd(typeof(GetDeadlineSettingsHandler), DeadlineSettings.Factory);
        _runner.ResultsByHandler.TryAdd(typeof(UpdateDeadlineSettingsHandler), DeadlineSettings.Factory);

        return new(_runner, NullLogger<ReminderSettingsViewModel>.Instance);
    }
}

public class DeadlineSettingsEditorViewModelTests
{
    [Fact]
    public void TheFactorySettings_ShowAsDefault_AndSaveAsThem()
    {
        var editor = new DeadlineSettingsEditorViewModel();

        editor.Load(DeadlineSettings.Factory);

        editor.IsDefault.Should().BeTrue();
        editor.RepeatIndex.Should().Be(3, "uma vez por dia");
        editor.ToCommand().Should().Be(new UpdateDeadlineSettings(
            true,
            DeadlineAlertPolicy.Default.Stages,
            TimeSpan.FromDays(1),
            DeadlineSettings.FactoryDefaultTime));
    }

    [Fact]
    public void Customizing_StartsFromTheDefaultStages()
    {
        var editor = new DeadlineSettingsEditorViewModel();
        editor.Load(DeadlineSettings.Factory);

        editor.IsCustom = true;
        editor.AlertThreeDays = true;
        editor.AlertTwoHours = false;
        editor.RepeatIndex = 2;

        var command = editor.ToCommand();

        command.IsEnabled.Should().BeTrue();
        command.Stages.Should().Be(
            DeadlineAlertStage.ThreeDays | DeadlineAlertStage.OneDay
            | DeadlineAlertStage.EightHours | DeadlineAlertStage.Overdue);
        command.OverdueRepeatEvery.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void ACustomPolicy_LoadsAsCustom()
    {
        var editor = new DeadlineSettingsEditorViewModel();

        editor.Load(new DeadlineSettings(
            new DeadlineAlertPolicy(true, DeadlineAlertStage.SevenDays | DeadlineAlertStage.Overdue, null),
            new TimeOnly(9, 30)));

        editor.IsCustom.Should().BeTrue();
        editor.AlertSevenDays.Should().BeTrue();
        editor.AlertOneDay.Should().BeFalse();
        editor.RepeatIndex.Should().Be(0, "o atraso avisa uma vez só");
        editor.DefaultTime.Should().Be(new TimeSpan(9, 30, 0));
    }

    [Fact]
    public void Disabling_KeepsTheChosenStagesForLater()
    {
        var editor = new DeadlineSettingsEditorViewModel();
        editor.Load(DeadlineSettings.Factory);
        editor.IsCustom = true;
        editor.AlertSevenDays = true;

        editor.IsDisabled = true;

        var command = editor.ToCommand();
        command.IsEnabled.Should().BeFalse();
        command.Stages.Should().HaveFlag(DeadlineAlertStage.SevenDays);
        editor.ShowsOptions.Should().BeFalse();
    }

    [Fact]
    public void WithoutTheOverdueStage_RepeatCannotBeChosen()
    {
        var editor = new DeadlineSettingsEditorViewModel();
        editor.Load(DeadlineSettings.Factory);
        editor.IsCustom = true;

        editor.AlertOverdue = false;

        editor.CanRepeat.Should().BeFalse();
    }
}

public class ReminderEditorViewModelTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("urgent")]
    [InlineData("none")]
    public void EveryPreset_RoundTripsThroughTheForm(string preset)
    {
        var expected = preset switch
        {
            "urgent" => ReminderPolicy.Urgent,
            "none" => ReminderPolicy.None,
            _ => ReminderPolicy.Default,
        };

        var editor = new ReminderEditorViewModel();

        editor.UsePreset(preset);

        editor.ToPolicy().Should().Be(expected);
    }

    [Fact]
    public void AnIntervalWithNoPreset_BecomesTheCustomFieldInsteadOfBeingLost()
    {
        var policy = new ReminderPolicy(
            IsEnabled: true,
            ReminderAnchor.AfterCreation,
            TimeSpan.FromMinutes(37),
            RepeatUntilAcknowledged: true,
            TimeSpan.FromMinutes(7),
            AlertChannels.All);

        var editor = new ReminderEditorViewModel();
        editor.Load(policy);

        editor.OffsetIsCustom.Should().BeTrue();
        editor.CustomOffsetMinutes.Should().Be(37);
        editor.RepeatIsCustom.Should().BeTrue();
        editor.CustomRepeatMinutes.Should().Be(7);
        editor.ToPolicy().Should().Be(policy);
    }

    [Fact]
    public void WithoutAScheduledTime_TheBeforeTheTimeOptionIsNotOffered()
    {
        // Ocorrência sem horário não tem "antes de quê" — a tela desabilita a
        // opção em vez de deixar escolher algo que nunca dispararia.
        var editor = new ReminderEditorViewModel { CanRemindAtScheduledTime = true };
        editor.RemindAfterCreation = false;

        editor.CanRemindAtScheduledTime = false;

        editor.RemindAfterCreation.Should().BeTrue();
        editor.ToPolicy().Anchor.Should().Be(ReminderAnchor.AfterCreation);
    }

    [Fact]
    public void TurningTheReminderOff_ProducesTheDisabledPolicy()
    {
        var editor = new ReminderEditorViewModel { IsEnabled = false };

        editor.ToPolicy().Should().Be(ReminderPolicy.None);
    }
}
