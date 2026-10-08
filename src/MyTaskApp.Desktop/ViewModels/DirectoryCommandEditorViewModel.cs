using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Commands;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O comando como o diálogo o devolve e o caso de uso o recebe (ADR-055).</summary>
/// <param name="Settings">Com o nome: no comando do diretório, ele é obrigatório.</param>
public sealed record DirectoryCommandDraft(string Command, string? Description, DevelopmentCommandSettings Settings);

/// <summary>
/// O que o diálogo precisa: o título, o rótulo do botão, o comando de partida
/// (<c>null</c> para um novo) e como gravar. <see cref="SaveAsync"/> devolve a
/// mensagem de erro, ou <c>null</c> quando gravou — como no
/// <see cref="TimeEntryEditorRequest"/>, a recusa aparece com o diálogo aberto.
/// </summary>
public sealed record DirectoryCommandEditorRequest(
    string Heading,
    string AcceptLabel,
    DevelopmentCommandRow? Initial,
    Func<DirectoryCommandDraft, CancellationToken, Task<string?>> SaveAsync);

/// <summary>
/// Cria ou edita um comando só do diretório da etiqueta (ADR-055). Porta, como
/// <see cref="ITimeEntryEditor"/>, para a janela de etiquetas ser testada sem janela.
/// </summary>
public interface IDirectoryCommandEditor
{
    /// <returns><c>true</c> quando o comando foi gravado; <c>false</c> se o usuário cancelou.</returns>
    Task<bool> EditAsync(DirectoryCommandEditorRequest request);
}

/// <summary>
/// Os campos do diálogo "Novo comando" do diretório: os mesmos do formulário de
/// Comandos globais, menos o apelido — ninguém chama este comando por <c>@</c>.
/// O nome é o rótulo do botão, e por isso obrigatório.
/// </summary>
public sealed partial class DirectoryCommandEditorViewModel : ObservableObject
{
    private readonly DirectoryCommandEditorRequest _request;

    public DirectoryCommandEditorViewModel(DirectoryCommandEditorRequest request)
    {
        _request = request;

        if (request.Initial is { } initial)
        {
            _name = initial.Name ?? string.Empty;
            _command = initial.Command;
            _description = initial.Description ?? string.Empty;
            _isTerminal = initial.Mode == CommandMode.Terminal;
            _workingDirectory = initial.WorkingDirectory ?? string.Empty;
            _keepTerminalOpen = initial.KeepTerminalOpen;
            _requiresConfirmation = initial.RequiresConfirmation;
        }

        DevelopmentCommandParameterEditorViewModel.Sync(ParameterEditors, Command, request.Initial?.Parameters ?? []);
    }

    public string Heading => _request.Heading;

    public string AcceptLabel => _request.AcceptLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Terminal visível (<c>npm run dev</c>) em vez de escondido com o resultado na tela.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExecute))]
    private bool _isTerminal;

    /// <summary>Relativa ao worktree; em branco é a raiz.</summary>
    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private bool _keepTerminalOpen = true;

    [ObservableProperty]
    private bool _requiresConfirmation;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _isBusy;

    /// <summary>Uma linha por <c>{nome}</c> do comando, enquanto o usuário digita.</summary>
    public ObservableCollection<DevelopmentCommandParameterEditorViewModel> ParameterEditors { get; } = [];

    public bool HasParameterEditors => ParameterEditors.Count > 0;

    public bool IsExecute
    {
        get => !IsTerminal;
        set => IsTerminal = !value;
    }

    public string VariablesHint => DevelopmentCommandParameterEditorViewModel.VariablesHint;

    public bool CanAccept => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Command) && !IsBusy;

    /// <summary>Acompanha os <c>{nome}</c> do texto, sem perder o que já foi preenchido de cada um.</summary>
    partial void OnCommandChanged(string value)
    {
        DevelopmentCommandParameterEditorViewModel.Sync(ParameterEditors, value, null);
        OnPropertyChanged(nameof(HasParameterEditors));
    }

    public DirectoryCommandDraft Draft() =>
        new(
            Command,
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            new DevelopmentCommandSettings(
                Name.Trim(),
                IsTerminal ? CommandMode.Terminal : CommandMode.Execute,
                string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
                KeepTerminalOpen,
                RequiresConfirmation,
                [.. ParameterEditors.Select(editor => editor.ToSpec())]));

    /// <summary>Grava. <c>false</c> mantém o diálogo aberto, com a mensagem à vista.</summary>
    public async Task<bool> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAccept)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            ErrorMessage = await _request.SaveAsync(Draft(), cancellationToken);
            return ErrorMessage is null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
