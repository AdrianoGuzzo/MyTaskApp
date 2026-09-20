using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um intervalo com nome, para a caixa de seleção falar português.</summary>
public sealed record DurationChoice(string Label, int Minutes)
{
    /// <summary>A entrada que revela o campo livre.</summary>
    public static DurationChoice Custom { get; } = new("Personalizado", 0);

    public bool IsCustom => Minutes == 0;

    public override string ToString() => Label;
}

/// <summary>
/// Os seis campos do lembrete, em forma de tela. Compartilhado entre a
/// configuração padrão e o ajuste de uma tarefa — são a mesma política, então
/// são o mesmo editor.
/// </summary>
public sealed partial class ReminderEditorViewModel : ObservableObject
{
    private static readonly DurationChoice[] OffsetPresets =
    [
        new("10 minutos", 10),
        new("30 minutos", 30),
        new("1 hora", 60),
        new("2 horas", 120),
        new("4 horas", 240),
        new("1 dia", 1440),
        DurationChoice.Custom,
    ];

    private static readonly DurationChoice[] RepeatPresets =
    [
        new("5 minutos", 5),
        new("10 minutos", 10),
        new("15 minutos", 15),
        new("30 minutos", 30),
        new("1 hora", 60),
        DurationChoice.Custom,
    ];

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _remindAfterCreation = true;

    [ObservableProperty]
    private DurationChoice _offset = OffsetPresets[2];

    [ObservableProperty]
    private int _customOffsetMinutes = 60;

    [ObservableProperty]
    private bool _repeatUntilAcknowledged = true;

    [ObservableProperty]
    private DurationChoice _repeatEvery = RepeatPresets[2];

    [ObservableProperty]
    private int _customRepeatMinutes = 15;

    [ObservableProperty]
    private bool _notify = true;

    [ObservableProperty]
    private bool _playSound = true;

    [ObservableProperty]
    private bool _bringToFront = true;

    /// <summary>
    /// "Antes do horário agendado" só faz sentido se houver horário. A tela
    /// desabilita a opção, em vez de deixar o usuário escolher algo que nunca
    /// dispararia.
    /// </summary>
    [ObservableProperty]
    private bool _canRemindAtScheduledTime = true;

    public ObservableCollection<DurationChoice> OffsetChoices { get; } = [.. OffsetPresets];

    public ObservableCollection<DurationChoice> RepeatChoices { get; } = [.. RepeatPresets];

    public bool OffsetIsCustom => Offset.IsCustom;

    public bool RepeatIsCustom => RepeatEvery.IsCustom;

    /// <summary>
    /// Os tres presets do pedido: "usar o padrao", "urgente" e "nao criar
    /// lembrete". Quem quiser algo diferente mexe nos campos abaixo.
    /// </summary>
    [RelayCommand]
    public void UsePreset(string preset) =>
        Load(preset switch
        {
            "urgent" => ReminderPolicy.Urgent,
            "none" => ReminderPolicy.None,
            _ => ReminderPolicy.Default,
        });

    public void Load(ReminderPolicy policy)
    {
        IsEnabled = policy.IsEnabled;
        RemindAfterCreation = policy.Anchor is ReminderAnchor.AfterCreation;
        RepeatUntilAcknowledged = policy.RepeatUntilAcknowledged;

        (Offset, CustomOffsetMinutes) = Match(
            OffsetChoices, policy.Offset, CustomOffsetMinutes);

        (RepeatEvery, CustomRepeatMinutes) = Match(
            RepeatChoices,
            policy.RepeatUntilAcknowledged ? policy.RepeatEvery : TimeSpan.FromMinutes(15),
            CustomRepeatMinutes);

        var channels = policy.IsEnabled ? policy.Channels : AlertChannels.All;

        Notify = channels.HasFlag(AlertChannels.Notification);
        PlaySound = channels.HasFlag(AlertChannels.Sound);
        BringToFront = channels.HasFlag(AlertChannels.BringToFront);
    }

    /// <summary>
    /// Monta a política. Pode lançar <c>DomainException</c> — a validação é do
    /// domínio, e a mensagem dele já está escrita para o usuário (ADR-008).
    /// </summary>
    public ReminderPolicy ToPolicy()
    {
        var anchor = RemindAfterCreation || !CanRemindAtScheduledTime
            ? ReminderAnchor.AfterCreation
            : ReminderAnchor.BeforeScheduledTime;

        return new ReminderPolicy(
            IsEnabled,
            anchor,
            TimeSpan.FromMinutes(MinutesOf(Offset, CustomOffsetMinutes)),
            RepeatUntilAcknowledged,
            RepeatUntilAcknowledged
                ? TimeSpan.FromMinutes(MinutesOf(RepeatEvery, CustomRepeatMinutes))
                : TimeSpan.Zero,
            SelectedChannels());
    }

    partial void OnOffsetChanged(DurationChoice value) =>
        OnPropertyChanged(nameof(OffsetIsCustom));

    partial void OnRepeatEveryChanged(DurationChoice value) =>
        OnPropertyChanged(nameof(RepeatIsCustom));

    partial void OnCanRemindAtScheduledTimeChanged(bool value)
    {
        if (!value)
        {
            RemindAfterCreation = true;
        }
    }

    private AlertChannels SelectedChannels()
    {
        var channels = AlertChannels.None;

        if (Notify)
        {
            channels |= AlertChannels.Notification;
        }

        if (PlaySound)
        {
            channels |= AlertChannels.Sound;
        }

        if (BringToFront)
        {
            channels |= AlertChannels.BringToFront;
        }

        return channels;
    }

    private static int MinutesOf(DurationChoice choice, int custom) =>
        choice.IsCustom ? custom : choice.Minutes;

    /// <summary>
    /// Casa o valor salvo com um preset; o que não casa vira "Personalizado"
    /// com o número preenchido, para nada ser perdido ao reabrir a tela.
    /// </summary>
    private static (DurationChoice Choice, int Custom) Match(
        IReadOnlyList<DurationChoice> choices,
        TimeSpan value,
        int currentCustom)
    {
        var minutes = Math.Max(1, (int)Math.Round(value.TotalMinutes));
        var preset = choices.FirstOrDefault(choice => choice.Minutes == minutes);

        return preset is not null
            ? (preset, currentCustom)
            : (DurationChoice.Custom, minutes);
    }
}
