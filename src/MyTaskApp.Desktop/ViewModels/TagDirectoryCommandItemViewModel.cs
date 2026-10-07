using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um comando rápido na lista do diretório (ADR-051): ligado ou não, em que
/// posição, se o diretório o personalizou, e se é só dele (ADR-054).
/// </summary>
public sealed partial class TagDirectoryCommandItemViewModel(
    TagDirectoryItemViewModel owner,
    TagDirectoryCommandRow row,
    int position,
    int count) : ObservableObject
{
    public TagDirectoryItemViewModel Owner { get; } = owner;

    public TagDirectoryCommandRow Row { get; } = row;

    public string DisplayName => Row.DisplayName;

    /// <summary>A linha que roda neste diretório: a personalizada, ou a global.</summary>
    public string EffectiveCommand => Row.EffectiveCommand;

    public bool IsEnabled => Row.IsEnabled;

    public bool IsCustomized => Row.IsCustomized;

    /// <summary>Criado aqui, e não um global (ADR-054): "Editar" no lugar de "Personalizar".</summary>
    public bool IsDirectoryOnly => Row.IsDirectoryOnly;

    public string RemoveTip => IsDirectoryOnly
        ? "Excluir este comando (ele só existe neste diretório)"
        : "Tirar deste diretório (o comando global continua)";

    public string CustomizedLabel =>
        Row.WorkingDirectoryOverride is { } folder
            ? $"Personalizado neste diretório · pasta {(folder == CommandWorkingDirectory.Root ? "raiz do worktree" : folder)}"
            : "Personalizado neste diretório";

    public bool CanMoveUp { get; } = position > 0;

    public bool CanMoveDown { get; } = position < count - 1;

    /// <summary>O formulário "Personalizar" aberto.</summary>
    [ObservableProperty]
    private bool _isCustomizing;

    /// <summary>"Usar configuração global" (desligado) × "Personalizar para este diretório".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesGlobal))]
    private bool _customize;

    [ObservableProperty]
    private string _commandOverride = string.Empty;

    [ObservableProperty]
    private string _workingDirectoryOverride = string.Empty;

    public bool UsesGlobal
    {
        get => !Customize;
        set => Customize = !value;
    }

    /// <summary>A linha global, para o usuário partir dela ao personalizar.</summary>
    public string GlobalCommand => Row.Command;

    public void BeginCustomize()
    {
        Customize = Row.IsCustomized;
        CommandOverride = Row.CommandOverride ?? Row.Command;
        WorkingDirectoryOverride = Row.WorkingDirectoryOverride ?? string.Empty;
        IsCustomizing = true;
    }
}
