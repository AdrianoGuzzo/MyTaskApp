using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um parâmetro a preencher antes de executar (ADR-051).</summary>
public sealed partial class QuickCommandFieldViewModel : ObservableObject
{
    public QuickCommandFieldViewModel(CommandParameterSpec spec)
    {
        Spec = spec;
        _value = spec.Type == CommandParameterType.Choice
            ? spec.DefaultValue ?? spec.Choices.FirstOrDefault() ?? string.Empty
            : spec.DefaultValue ?? string.Empty;
    }

    public CommandParameterSpec Spec { get; }

    public string Name => Spec.Name;

    public string Label => Spec.IsRequired ? Spec.DisplayName : $"{Spec.DisplayName} (opcional)";

    public bool IsChoice => Spec.Type == CommandParameterType.Choice;

    public bool IsText => !IsChoice;

    public IReadOnlyList<string> Choices => Spec.Choices;

    public string Placeholder => Spec.Type == CommandParameterType.Number ? "Número" : string.Empty;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;
}

/// <summary>
/// O diálogo antes de um comando rápido (ADR-051): os parâmetros, se houver, e
/// a linha final ao vivo — que é a confirmação, quando o comando a pede.
/// </summary>
public sealed partial class QuickCommandPromptViewModel : ObservableObject
{
    private readonly QuickCommandPlan _plan;

    public QuickCommandPromptViewModel(QuickCommandPlan plan)
    {
        _plan = plan;
        Fields = [.. plan.Entry.Parameters.Select(spec => new QuickCommandFieldViewModel(spec))];

        foreach (var field in Fields)
        {
            field.PropertyChanged += OnFieldChanged;
        }

        Refresh();
    }

    public string Title => _plan.Entry.Name;

    public ObservableCollection<QuickCommandFieldViewModel> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    public string WorkingDirectory => _plan.WorkingDirectory;

    public bool IsTerminal => _plan.Entry.Mode == CommandMode.Terminal;

    public string ModeText => IsTerminal
        ? "Abre um terminal nesta pasta e roda:"
        : "Roda nesta pasta, com o resultado na tarefa:";

    public bool RequiresConfirmation => _plan.Entry.RequiresConfirmation;

    public string Headline => RequiresConfirmation ? "Este comando será executado:" : "Preencha para executar:";

    /// <summary>A linha que vai ao shell com o que está digitado; <c>null</c> enquanto falta algo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText), nameof(CanAccept))]
    private string? _line;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public string PreviewText => Line ?? _plan.Entry.Template;

    public bool HasProblem => Problem is not null;

    public bool CanAccept => Line is not null;

    /// <summary>Os valores digitados, por nome, para o caso de uso montar a linha de novo.</summary>
    public IReadOnlyDictionary<string, string> Values =>
        Fields.ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(QuickCommandFieldViewModel.Value))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var built = _plan.Build(Values);

        foreach (var field in Fields)
        {
            // Campo obrigatório ainda em branco não é "erro" enquanto o usuário não digitou.
            field.Error = built.ParameterErrors.TryGetValue(field.Name, out var error) && field.Value.Trim().Length > 0
                ? error
                : null;
        }

        Line = built.Line;
        Problem = built.Error;
    }
}
