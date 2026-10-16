using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>Uma opção marcável de uma lista (prioridade, etiqueta, atalho), com a cor quando houver.</summary>
public partial class Choice<T>(T value, string label, string? colorHex = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public T Value { get; } = value;

    public string Label { get; } = label;

    public string? ColorHex { get; } = colorHex;

    public override string ToString() => Label;
}

/// <summary>Um atalho do topo da lista (Todas, Pendentes…). Concreto para o XAML compilar o binding.</summary>
public sealed class PresetChip(TaskListPreset value, string label) : Choice<TaskListPreset>(value, label);

/// <summary>Uma prioridade marcável na folha de filtros.</summary>
public sealed class PriorityChip(TaskPriority value, string label) : Choice<TaskPriority>(value, label);

/// <summary>Uma etiqueta marcável, com a cor dela.</summary>
public sealed class TagOption(Guid value, string label, string colorHex) : Choice<Guid>(value, label, colorHex);
