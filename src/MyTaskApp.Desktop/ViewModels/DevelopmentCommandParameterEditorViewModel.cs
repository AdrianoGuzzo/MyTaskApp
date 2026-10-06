using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Uma linha do formulário de parâmetros de um comando global (ADR-051): como a
/// tela vai perguntar o <c>{nome}</c> antes de executar.
/// </summary>
public sealed partial class DevelopmentCommandParameterEditorViewModel : ObservableObject
{
    /// <summary>Na ordem de <see cref="CommandParameterType"/>.</summary>
    public static IReadOnlyList<string> TypeOptions { get; } = ["Texto", "Número", "Lista de opções"];

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice))]
    private int _typeIndex;

    [ObservableProperty]
    private string _defaultValue = string.Empty;

    [ObservableProperty]
    private bool _isRequired = true;

    /// <summary>As opções da lista, uma por linha.</summary>
    [ObservableProperty]
    private string _optionsText = string.Empty;

    public DevelopmentCommandParameterEditorViewModel(string name, CommandParameterSpec? spec = null)
    {
        Name = name;

        if (spec is not null)
        {
            _label = spec.Label ?? string.Empty;
            _typeIndex = (int)spec.Type;
            _defaultValue = spec.DefaultValue ?? string.Empty;
            _isRequired = spec.IsRequired;
            _optionsText = string.Join(Environment.NewLine, spec.Choices);
        }
    }

    public string Name { get; }

    public string Placeholder => $"{{{Name}}}";

    public bool IsChoice => TypeIndex == (int)CommandParameterType.Choice;

    public CommandParameterSpec ToSpec() =>
        new(
            Name,
            string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            (CommandParameterType)Math.Clamp(TypeIndex, 0, TypeOptions.Count - 1),
            string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue.Trim(),
            IsRequired,
            IsChoice
                ? [.. OptionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : null);
}
