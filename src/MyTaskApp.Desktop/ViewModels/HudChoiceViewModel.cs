using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um item de rádio do menu do HUD (tamanho ou posição). Mesmo desenho do
/// <see cref="ThemeOptionViewModel"/>: o item sabe se está marcado e o que
/// escolher, e a moldura mantém o visto em um só.
/// </summary>
public sealed partial class HudChoiceViewModel(int value, string label, Action<int> select) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public int Value { get; } = value;

    public string Label { get; } = label;

    [RelayCommand]
    private void Select() => select(Value);
}
