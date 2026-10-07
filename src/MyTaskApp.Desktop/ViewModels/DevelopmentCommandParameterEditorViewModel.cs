using System.Collections.ObjectModel;
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

    /// <summary>
    /// As variáveis que o app preenche sozinho, para a dica dos formulários de
    /// comando — o global e o do diretório (ADR-054).
    /// </summary>
    public static string VariablesHint { get; } =
        "Variáveis que o app preenche: " + string.Join(", ", CommandVariables.All
            .Where(variable => variable.Name is not (CommandVariables.WorktreePath or CommandVariables.RepositoryPath))
            .Select(variable => variable.Placeholder))
        + ". Caminho com espaço vai entre aspas: code \"{worktree}\".";

    /// <summary>Os <c>{nome}</c> que o usuário preenche: as variáveis de contexto ficam de fora.</summary>
    public static IReadOnlyList<string> UserParameters(string? command) =>
        [.. CommandParameters.Names(command).Where(name => !CommandVariables.IsContextName(name))];

    /// <summary>
    /// Uma linha por <c>{nome}</c> do texto. Com <paramref name="saved"/>, parte
    /// do que está gravado; sem, mantém o que o usuário já preencheu em cada um.
    /// </summary>
    public static void Sync(
        ObservableCollection<DevelopmentCommandParameterEditorViewModel> editors,
        string? command,
        IReadOnlyList<CommandParameterSpec>? saved)
    {
        var current = editors.ToDictionary(editor => editor.Name, StringComparer.OrdinalIgnoreCase);

        editors.Clear();

        foreach (var name in UserParameters(command))
        {
            var spec = saved?.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

            editors.Add(saved is null && current.TryGetValue(name, out var existing)
                ? existing
                : new DevelopmentCommandParameterEditorViewModel(name, spec));
        }
    }

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
