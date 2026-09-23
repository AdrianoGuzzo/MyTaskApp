using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Tags;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um item do autocomplete de diretórios: o alias, o path que ele insere e a etiqueta.</summary>
public sealed partial class AliasSuggestionViewModel(TagDirectoryRow row, bool? exists) : ObservableObject
{
    public TagDirectoryRow Row { get; } = row;

    public string Alias => Row.Alias;

    public string Path => Row.Path;

    public TagChipViewModel Tag { get; } = new(row.TagId, row.TagName, row.TagColorHex);

    /// <summary>A pasta sumiu do disco. <c>null</c> = ainda não se sabe, e não se avisa.</summary>
    public bool IsMissing { get; } = exists is false;

    /// <summary>Nome e descrição, quando houver, no balão do item.</summary>
    public string? Details => (Row.Name, Row.Description) switch
    {
        (null, null) => null,
        ({ } name, null) => name,
        (null, { } description) => description,
        ({ } name, { } description) => $"{name} — {description}",
    };

    [ObservableProperty]
    private bool _isSelected;
}
