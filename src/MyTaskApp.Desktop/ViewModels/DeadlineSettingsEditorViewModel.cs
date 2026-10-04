using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// "Alertas de prazo" na configuração (ADR-050, §19). Três escolhas na
/// primeira tela — Padrão, Personalizado, Desativado —, e as caixas de cada
/// degrau só aparecem para quem pediu personalizar.
/// </summary>
public sealed partial class DeadlineSettingsEditorViewModel : ObservableObject
{
    /// <summary>As opções de repetição do atraso, na ordem do combo.</summary>
    public static readonly IReadOnlyList<TimeSpan?> RepeatChoices =
        [null, TimeSpan.FromHours(1), TimeSpan.FromHours(4), TimeSpan.FromDays(1)];

    public static IReadOnlyList<string> RepeatLabels { get; } =
        ["Não repetir", "A cada hora", "A cada 4 horas", "Uma vez por dia"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefault))]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    [NotifyPropertyChangedFor(nameof(ShowsOptions))]
    private DeadlineSettingsMode _mode;

    [ObservableProperty]
    private bool _alertSevenDays;

    [ObservableProperty]
    private bool _alertThreeDays;

    [ObservableProperty]
    private bool _alertOneDay;

    [ObservableProperty]
    private bool _alertEightHours;

    [ObservableProperty]
    private bool _alertTwoHours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRepeat))]
    private bool _alertOverdue;

    /// <summary>O índice em <see cref="RepeatChoices"/>.</summary>
    [ObservableProperty]
    private int _repeatIndex;

    /// <summary>O horário de um prazo escolhido só pelo dia. <c>TimeSpan</c> é o que o <c>TimePicker</c> fala.</summary>
    [ObservableProperty]
    private TimeSpan? _defaultTime;

    public bool IsDefault
    {
        get => Mode == DeadlineSettingsMode.Default;
        set => Choose(value, DeadlineSettingsMode.Default);
    }

    public bool IsCustom
    {
        get => Mode == DeadlineSettingsMode.Custom;
        set => Choose(value, DeadlineSettingsMode.Custom);
    }

    public bool IsDisabled
    {
        get => Mode == DeadlineSettingsMode.Disabled;
        set => Choose(value, DeadlineSettingsMode.Disabled);
    }

    /// <summary>Repetição e horário padrão não importam com tudo desligado.</summary>
    public bool ShowsOptions => Mode != DeadlineSettingsMode.Disabled;

    public bool CanRepeat => AlertOverdue;

    public void Load(DeadlineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var alerts = settings.Alerts;
        var factory = DeadlineAlertPolicy.Default;

        Mode = !alerts.IsEnabled ? DeadlineSettingsMode.Disabled
            : alerts == factory ? DeadlineSettingsMode.Default
            : DeadlineSettingsMode.Custom;

        ShowStages(alerts.Stages);

        // Sem o degrau de atraso a política não guarda repetição; o combo
        // mostra a de fábrica, para quando o atraso for ligado.
        RepeatIndex = IndexOfRepeat(alerts.Stages.HasFlag(DeadlineAlertStage.Overdue)
            ? alerts.OverdueRepeatEvery
            : factory.OverdueRepeatEvery);

        DefaultTime = settings.DefaultTime.ToTimeSpan();
    }

    /// <summary>O pedido de gravação. A política valida na Application.</summary>
    public UpdateDeadlineSettings ToCommand()
    {
        var time = TimeOnly.FromTimeSpan(DefaultTime ?? DeadlineSettings.FactoryDefaultTime.ToTimeSpan());
        var factory = DeadlineAlertPolicy.Default;

        return Mode switch
        {
            DeadlineSettingsMode.Default =>
                new UpdateDeadlineSettings(true, factory.Stages, factory.OverdueRepeatEvery, time),
            DeadlineSettingsMode.Disabled =>
                new UpdateDeadlineSettings(false, SelectedStagesOrDefault(), SelectedRepeat(), time),
            _ => new UpdateDeadlineSettings(true, SelectedStages(), SelectedRepeat(), time),
        };
    }

    private void Choose(bool chosen, DeadlineSettingsMode mode)
    {
        if (!chosen)
        {
            return;
        }

        // Personalizar parte do padrão que estava valendo, e não do zero.
        if (mode == DeadlineSettingsMode.Custom && Mode == DeadlineSettingsMode.Default)
        {
            ShowStages(DeadlineAlertPolicy.Default.Stages);
        }

        Mode = mode;
    }

    private void ShowStages(DeadlineAlertStage stages)
    {
        AlertSevenDays = stages.HasFlag(DeadlineAlertStage.SevenDays);
        AlertThreeDays = stages.HasFlag(DeadlineAlertStage.ThreeDays);
        AlertOneDay = stages.HasFlag(DeadlineAlertStage.OneDay);
        AlertEightHours = stages.HasFlag(DeadlineAlertStage.EightHours);
        AlertTwoHours = stages.HasFlag(DeadlineAlertStage.TwoHours);
        AlertOverdue = stages.HasFlag(DeadlineAlertStage.Overdue);
    }

    private DeadlineAlertStage SelectedStages() =>
        (AlertSevenDays ? DeadlineAlertStage.SevenDays : 0)
        | (AlertThreeDays ? DeadlineAlertStage.ThreeDays : 0)
        | (AlertOneDay ? DeadlineAlertStage.OneDay : 0)
        | (AlertEightHours ? DeadlineAlertStage.EightHours : 0)
        | (AlertTwoHours ? DeadlineAlertStage.TwoHours : 0)
        | (AlertOverdue ? DeadlineAlertStage.Overdue : 0);

    /// <summary>Desativado guarda a escolha para quando voltar; vazia, guarda o padrão.</summary>
    private DeadlineAlertStage SelectedStagesOrDefault() =>
        SelectedStages() is var stages and not DeadlineAlertStage.None ? stages : DeadlineAlertPolicy.Default.Stages;

    private TimeSpan? SelectedRepeat() =>
        RepeatIndex >= 0 && RepeatIndex < RepeatChoices.Count ? RepeatChoices[RepeatIndex] : null;

    private static int IndexOfRepeat(TimeSpan? repeat)
    {
        for (var index = 0; index < RepeatChoices.Count; index++)
        {
            if (RepeatChoices[index] == repeat)
            {
                return index;
            }
        }

        // Um intervalo gravado que não está nas opções vira "uma vez por dia".
        return RepeatChoices.Count - 1;
    }
}

public enum DeadlineSettingsMode
{
    Default,
    Custom,
    Disabled,
}
