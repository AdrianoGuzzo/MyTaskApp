using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um comando global na lista da janela.</summary>
public sealed class DevelopmentCommandListItemViewModel(DevelopmentCommandRow row)
{
    public DevelopmentCommandRow Row { get; } = row;

    public string Alias => Row.Alias;

    public string Command => Row.Command;

    public string? Description => Row.Description;

    public bool HasDescription => !string.IsNullOrEmpty(Row.Description);

    public bool Matches(string query) =>
        Row.Alias.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Row.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (Row.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// A janela "Comandos globais" (ADR-028): listar, pesquisar, criar, editar,
/// excluir e testar os comandos que a lista pós-Worktree chama por <c>@alias</c>.
/// </summary>
/// <remarks>
/// "Testar" roda o comando de verdade, numa pasta que o usuário escolhe — por
/// isso só por clique, e com o terminal e o cancelar na mesma tela.
/// </remarks>
public sealed partial class DevelopmentCommandsViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<DevelopmentCommandsViewModel> logger) : ObservableObject
{
    private IReadOnlyList<DevelopmentCommandListItemViewModel> _all = [];

    private CancellationTokenSource? _test;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>O comando em edição; <c>null</c> = o formulário cria um novo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveLabel))]
    private Guid? _editingId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _alias = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Onde "Testar" roda. Fica entre um teste e outro.</summary>
    [ObservableProperty]
    private string _testDirectory = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelTestCommand))]
    private bool _isTesting;

    [ObservableProperty]
    private bool _hasTest;

    [ObservableProperty]
    private string? _testTitle;

    public ObservableCollection<DevelopmentCommandListItemViewModel> Commands { get; } = [];

    public CommandOutputViewModel TestOutput { get; } = new();

    public bool IsEditing => EditingId is not null;

    public string FormTitle => IsEditing ? "Editar comando" : "Novo comando";

    public string SaveLabel => IsEditing ? "Salvar alterações" : "Criar comando";

    public bool CanSave => !string.IsNullOrWhiteSpace(Alias) && !string.IsNullOrWhiteSpace(Command);

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DevelopmentCommandRow> rows = [];

        var loaded = await TryAsync(
            async () => rows = await runner.RunAsync<GetDevelopmentCommandsHandler, IReadOnlyList<DevelopmentCommandRow>>(
                (handler, token) => handler.HandleAsync(new GetDevelopmentCommands(), token),
                cancellationToken),
            "Não foi possível carregar os comandos.");

        if (!loaded)
        {
            return;
        }

        _all = [.. rows.Select(row => new DevelopmentCommandListItemViewModel(row))];
        ApplySearch();
    }

    partial void OnSearchChanged(string value) => ApplySearch();

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var editingId = EditingId;
        var alias = Alias;
        var command = Command;
        var description = Description;

        var saved = await TryAsync(
            () => editingId is { } id
                ? runner.RunAsync<UpdateDevelopmentCommandHandler, DevelopmentCommandRow>(
                    (handler, token) => handler.HandleAsync(
                        new UpdateDevelopmentCommand(id, alias, command, description), token),
                    cancellationToken)
                : runner.RunAsync<CreateDevelopmentCommandHandler, DevelopmentCommandRow>(
                    (handler, token) => handler.HandleAsync(
                        new CreateDevelopmentCommand(alias, command, description), token),
                    cancellationToken),
            "Não foi possível salvar o comando.");

        if (!saved)
        {
            // Formulário preservado: o usuário corrige e tenta de novo.
            return;
        }

        ResetForm();
        await LoadAsync(cancellationToken);
        StatusMessage = editingId is null ? "Comando criado." : "Comando atualizado.";
    }

    [RelayCommand]
    public void Edit(DevelopmentCommandListItemViewModel item)
    {
        EditingId = item.Row.Id;
        Alias = item.Alias;
        Command = item.Command;
        Description = item.Description ?? string.Empty;
        ErrorMessage = null;
        StatusMessage = null;
    }

    [RelayCommand]
    public void CancelEdit() => ResetForm();

    [RelayCommand]
    public async Task DeleteAsync(DevelopmentCommandListItemViewModel item, CancellationToken cancellationToken)
    {
        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            $"Excluir {item.Alias}?",
            $"As tarefas que chamam {item.Alias} vão avisar que ele não existe na próxima execução. "
            + "Nenhuma lista de tarefa é alterada.",
            "Excluir"));

        if (!confirmed)
        {
            return;
        }

        var deleted = await TryAsync(
            () => runner.RunAsync<DeleteDevelopmentCommandHandler>(
                (handler, token) => handler.HandleAsync(new DeleteDevelopmentCommand(item.Row.Id), token),
                cancellationToken),
            "Não foi possível excluir o comando.");

        if (!deleted)
        {
            return;
        }

        if (EditingId == item.Row.Id)
        {
            ResetForm();
        }

        await LoadAsync(cancellationToken);
        StatusMessage = $"{item.Alias} excluído.";
    }

    /// <summary>
    /// Roda o comando na pasta de teste e mostra o output aqui. Só por clique:
    /// é o único lugar da janela que executa algo.
    /// </summary>
    [RelayCommand]
    public async Task TestAsync(DevelopmentCommandListItemViewModel item)
    {
        if (IsTesting)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        if (string.IsNullOrWhiteSpace(TestDirectory))
        {
            ErrorMessage = "Escolha a pasta onde testar o comando.";
            return;
        }

        _test?.Dispose();
        _test = new CancellationTokenSource();

        TestTitle = $"Teste de {item.Alias} em {TestDirectory.Trim()}";
        TestOutput.Reset(item.Command);
        HasTest = true;
        IsTesting = true;

        var progress = new UiProgress<CommandStepProgress>(ApplyTestProgress);
        // Pelo apelido, como a lista da tarefa chama: testa a resolução junto.
        var entry = item.Alias;
        var directory = TestDirectory;

        try
        {
            var summary = await runner.RunAsync<RunCommandHandler, CommandRunSummary>(
                (handler, token) => handler.HandleAsync(new RunCommand(entry, directory), progress, token),
                _test.Token);

            if (summary.Steps.FirstOrDefault() is { } step)
            {
                TestOutput.State = step.State;
                TestOutput.Result = step.Result ?? TestOutput.Result;
                TestOutput.Error = step.Error ?? TestOutput.Error;
            }
        }
        catch (OperationCanceledException)
        {
            TestOutput.State = CommandStepState.Canceled;
        }
        catch (DomainException exception)
        {
            TestOutput.State = CommandStepState.Failed;
            TestOutput.Error = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentCommandTestFailed {Alias}", item.Alias);
            TestOutput.State = CommandStepState.Failed;
            TestOutput.Error = "Não foi possível executar o comando.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    public void CancelTest() => _test?.Cancel();

    public void UseTestDirectory(string path) => TestDirectory = path;

    private void ApplyTestProgress(CommandStepProgress progress)
    {
        if (progress.Line is { } line)
        {
            TestOutput.Append(line);
            return;
        }

        TestOutput.State = progress.State;

        if (progress.Command is not null)
        {
            TestOutput.Command = progress.Command;
        }

        if (progress.Result is not null)
        {
            TestOutput.Result = progress.Result;
        }

        if (progress.Error is not null)
        {
            TestOutput.Error = progress.Error;
        }
    }

    private void ApplySearch()
    {
        var query = Search.Trim();

        Commands.Clear();

        foreach (var item in _all.Where(item => query.Length == 0 || item.Matches(query)))
        {
            Commands.Add(item);
        }

        IsEmpty = _all.Count == 0;
    }

    private void ResetForm()
    {
        EditingId = null;
        Alias = string.Empty;
        Command = string.Empty;
        Description = string.Empty;
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentCommandsScreenOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
