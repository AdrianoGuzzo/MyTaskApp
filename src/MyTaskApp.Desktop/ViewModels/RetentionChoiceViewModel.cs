using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um prazo em dias, escolhido numa lista curta ou digitado. Existe uma vez e é
/// usado duas — arquivamento e lixeira —, porque a regra "ou é um dos prazos
/// oferecidos, ou é personalizado" é a mesma nos dois (§11).
/// </summary>
public sealed partial class RetentionChoiceViewModel : ObservableObject
{
    /// <summary>O rótulo da última posição; selecioná-la revela o campo livre.</summary>
    public const string CustomLabel = "Personalizado…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    [NotifyPropertyChangedFor(nameof(Days))]
    private int _selectedIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Days))]
    private int _customDays = 30;

    public IReadOnlyList<string> Options { get; } =
    [
        .. DataRetentionPolicy.PresetDays.Select(Describe),
        CustomLabel,
    ];

    public bool IsCustom => SelectedIndex == Options.Count - 1;

    /// <summary>O valor que vale, venha da lista ou do campo livre.</summary>
    public int Days => IsCustom
        ? Math.Clamp(CustomDays, DataRetentionPolicy.MinDays, DataRetentionPolicy.MaxDays)
        : DataRetentionPolicy.PresetDays[SelectedIndex];

    public int Minimum => DataRetentionPolicy.MinDays;

    public int Maximum => DataRetentionPolicy.MaxDays;

    /// <summary>
    /// Mostra um prazo já gravado. Um valor que não está na lista não vira o
    /// preset mais próximo — cai em "Personalizado" com o número intacto, senão
    /// abrir a tela alteraria em silêncio o que o usuário tinha escolhido.
    /// </summary>
    public void Load(int days)
    {
        var index = DataRetentionPolicy.PresetDays
            .Select((preset, position) => (preset, position))
            .FirstOrDefault(entry => entry.preset == days, (preset: -1, position: -1))
            .position;

        if (index >= 0)
        {
            SelectedIndex = index;
            CustomDays = days;
            return;
        }

        CustomDays = Math.Clamp(days, DataRetentionPolicy.MinDays, DataRetentionPolicy.MaxDays);
        SelectedIndex = Options.Count - 1;
    }

    private static string Describe(int days) => days == 1 ? "1 dia" : $"{days} dias";
}
