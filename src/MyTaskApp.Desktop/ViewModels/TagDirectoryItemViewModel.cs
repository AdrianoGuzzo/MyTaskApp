using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Uma pasta na lista da etiqueta, com o aviso de que ela não existe mais no
/// disco. A existência é conferida só aqui, na exibição: cadastrar uma pasta
/// que ainda vai ser criada é permitido (ADR-026).
/// </summary>
public sealed partial class TagDirectoryItemViewModel(TagListItemViewModel owner, TagDirectoryRow row)
    : ObservableObject
{
    public TagListItemViewModel Owner { get; } = owner;

    public TagDirectoryRow Row { get; } = row;

    public string Alias => Row.Alias;

    public string Path => Row.Path;

    public string? Name => Row.Name;

    public string? Description => Row.Description;

    public bool HasName => !string.IsNullOrEmpty(Row.Name);

    public string? DefaultBranch => Row.DefaultBranch;

    public bool HasDefaultBranch => !string.IsNullOrEmpty(Row.DefaultBranch);

    public string DefaultBranchLabel => $"Branch padrão: {Row.DefaultBranch}";

    /// <summary>Os comandos rápidos que os worktrees deste repositório mostram (ADR-051).</summary>
    public ObservableCollection<TagDirectoryCommandItemViewModel> Commands { get; } = [];

    /// <summary>Os comandos globais que ainda não estão aqui, para "+ Adicionar global".</summary>
    public ObservableCollection<DevelopmentCommandRow> AvailableCommands { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCommands))]
    private bool _isCommandsExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandsLabel), nameof(HasNoCommands))]
    private int _commandCount = row.CommandCount;

    [ObservableProperty]
    private DevelopmentCommandRow? _selectedNewCommand;

    public string CommandsLabel => CommandCount == 0 ? "Comandos" : $"Comandos ({CommandCount})";

    public bool HasNoCommands => IsCommandsExpanded && CommandCount == 0;

    public bool HasAvailableCommands => AvailableCommands.Count > 0;

    /// <summary>Ao lado do "+ Novo comando" (ADR-054): o que ele faz, e o que fazer sem global a oferecer.</summary>
    public string NewCommandHint => HasAvailableCommands
        ? "Ou crie um comando só deste diretório, sem cadastrá-lo como global."
        : "Nenhum comando global a adicionar. Crie um só deste diretório, ou cadastre um em Comandos globais… (menu do painel).";

    /// <summary>Troca as listas de uma vez, depois de cada mudança.</summary>
    internal void ShowCommands(IReadOnlyList<TagDirectoryCommandRow> bindings, IReadOnlyList<DevelopmentCommandRow> globals)
    {
        Commands.Clear();

        for (var index = 0; index < bindings.Count; index++)
        {
            Commands.Add(new TagDirectoryCommandItemViewModel(this, bindings[index], index, bindings.Count));
        }

        var used = bindings.Select(binding => binding.CommandId).ToHashSet();

        AvailableCommands.Clear();

        foreach (var global in globals.Where(global => !used.Contains(global.Id)).OrderBy(global => global.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            AvailableCommands.Add(global);
        }

        SelectedNewCommand = AvailableCommands.FirstOrDefault();
        CommandCount = bindings.Count;
        OnPropertyChanged(nameof(HasAvailableCommands));
        OnPropertyChanged(nameof(NewCommandHint));
    }

    /// <summary><c>null</c> enquanto a conferência não volta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing), nameof(IsFound), nameof(ExistenceTip))]
    private bool? _exists;

    public bool IsFound => Exists is true;

    public bool IsMissing => Exists is false;

    public string ExistenceTip => Exists switch
    {
        true => "Pasta encontrada",
        false => "Pasta não encontrada — o caminho continua salvo e pode ser usado",
        null => "Conferindo a pasta…",
    };

    /// <summary>
    /// Um alias a partir do nome da pasta, para o "Procurar…" já preencher:
    /// <c>C:\Projetos\Ecossistema Core</c> vira <c>@ecossistema-core</c>.
    /// </summary>
    public static string SuggestAlias(string path)
    {
        var folder = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));

        // Acentos saem pela decomposição: "Integração" vira "integracao", e não "integra--o".
        var chars = folder
            .Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant)
            .Select(c => AliasCompletion.IsAliasChar(c) ? c : '-')
            .ToArray();

        var body = new string(chars).Trim('-', '.', '_');

        while (body.Contains("--", StringComparison.Ordinal))
        {
            body = body.Replace("--", "-", StringComparison.Ordinal);
        }

        // O "@" conta no limite: um nome de pasta comprido sugeriria um alias
        // que o próprio domínio recusaria ao salvar.
        if (body.Length > TagDirectory.MaxAliasLength - 1)
        {
            body = body[..(TagDirectory.MaxAliasLength - 1)].TrimEnd('-', '.', '_');
        }

        return body.Length == 0 ? string.Empty : "@" + body;
    }
}
