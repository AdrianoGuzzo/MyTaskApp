using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A seção "⚡ Comandos" do ambiente pronto (ADR-051): um botão por comando do
/// diretório da etiqueta, e "+ Executar comando…" para qualquer outro global.
/// </summary>
/// <remarks>
/// <para>
/// O usuário pensa "quero executar a aplicação", e não "abrir um terminal, ir até
/// a pasta e digitar <c>dotnet run</c>": o worktree, a pasta e as variáveis vêm
/// do ambiente. Só pergunta o que o comando pede — parâmetros, confirmação.
/// </para>
/// <para>
/// Tudo passa por casos de uso: esta tela não sabe abrir processo. É o que deixa
/// a mesma execução ao alcance de um controle remoto no futuro.
/// </para>
/// </remarks>
public sealed partial class QuickCommandsViewModel(
    IUseCaseRunner runner,
    IQuickCommandPrompt prompt,
    ILogger<QuickCommandsViewModel> logger) : ObservableObject
{
    private Guid _taskId;

    private Guid? _developmentId;

    /// <summary>A execução escondida em andamento nesta janela, para o "Cancelar".</summary>
    private CancellationTokenSource? _run;

    public ObservableCollection<QuickCommandItemViewModel> Items { get; } = [];

    /// <summary>Todos os comandos globais, para "+ Executar comando…".</summary>
    public ObservableCollection<DevelopmentCommandRow> Globals { get; } = [];

    /// <summary>O output da última execução escondida, ou da que o usuário pediu para rever.</summary>
    public CommandOutputViewModel Output { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoItems))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _hasOutput;

    [ObservableProperty]
    private string? _outputTitle;

    /// <summary>Um comando escondido rodando: fechar a janela o interromperia.</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isAdHocOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdHocPreview), nameof(HasAdHocSelection))]
    [NotifyCanExecuteChangedFor(nameof(RunAdHocCommand))]
    private DevelopmentCommandRow? _selectedAdHoc;

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => IsLoaded && Items.Count == 0;

    public bool HasGlobals => Globals.Count > 0;

    public bool HasMessage => Message is not null;

    public bool HasAdHocSelection => SelectedAdHoc is not null;

    public string? AdHocPreview => SelectedAdHoc is { } global
        ? $"{global.Command}   ·   {(global.WorkingDirectory is { } folder ? folder : "raiz do worktree")}"
        : null;

    /// <summary>O ambiente que os botões usam. Trocar de ambiente esvazia a seção até a próxima leitura.</summary>
    public void Load(Guid taskId, Guid? developmentId)
    {
        if (_taskId == taskId && _developmentId == developmentId)
        {
            return;
        }

        _taskId = taskId;
        _developmentId = developmentId;
        Items.Clear();
        Globals.Clear();
        IsLoaded = false;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasGlobals));
    }

    /// <summary>Lê os botões e o estado de cada um de novo: o terminal pode ter fechado lá fora.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_developmentId is not { } developmentId)
        {
            return;
        }

        QuickCommandsView view;

        try
        {
            view = await runner.RunAsync<GetQuickCommandsHandler, QuickCommandsView>(
                (handler, token) => handler.HandleAsync(new GetQuickCommands(_taskId, developmentId), token),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickCommandsLoadFailed {TaskId} {DevelopmentId}", _taskId, developmentId);
            Message = "Não foi possível carregar os comandos deste ambiente.";
            return;
        }

        if (developmentId != _developmentId)
        {
            return;
        }

        Show(view ?? QuickCommandsView.Empty);
    }

    private void Show(QuickCommandsView view)
    {
        var runningHere = Items.Where(item => item.IsRunningHere).Select(item => item.Entry.CommandId).ToHashSet();

        Items.Clear();

        foreach (var entry in view.Commands)
        {
            Items.Add(new QuickCommandItemViewModel(entry)
            {
                Latest = view.Recent.FirstOrDefault(execution => execution.CommandId == entry.CommandId),
                IsRunningHere = runningHere.Contains(entry.CommandId),
            });
        }

        var selected = SelectedAdHoc?.Id;

        Globals.Clear();

        foreach (var global in view.Globals.OrderBy(global => global.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Globals.Add(global);
        }

        SelectedAdHoc = Globals.FirstOrDefault(global => global.Id == selected) ?? Globals.FirstOrDefault();
        IsLoaded = true;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(HasGlobals));
    }

    [RelayCommand]
    public Task RunAsync(QuickCommandItemViewModel item) =>
        ExecuteAsync(item.Entry.CommandId, item.Entry.BindingId, item.Entry.Name, item);

    [RelayCommand]
    public void ToggleAdHoc()
    {
        IsAdHocOpen = !IsAdHocOpen;
        Message = null;
    }

    [RelayCommand(CanExecute = nameof(HasAdHocSelection))]
    public async Task RunAdHocAsync()
    {
        if (SelectedAdHoc is not { } global)
        {
            return;
        }

        var item = Items.FirstOrDefault(candidate => candidate.Entry.CommandId == global.Id);

        // O avulso é o global como cadastrado: sem a personalização do diretório.
        await ExecuteAsync(global.Id, null, global.DisplayName, item);
        IsAdHocOpen = false;
    }

    [RelayCommand]
    public void Cancel() => _run?.Cancel();

    /// <summary>Traz para a frente o terminal do comando.</summary>
    [RelayCommand]
    public async Task ShowTerminalAsync(QuickCommandItemViewModel item)
    {
        if (item.Latest is not { } latest)
        {
            return;
        }

        Message = null;

        try
        {
            var result = await runner.RunAsync<FocusCommandExecutionHandler, CommandFocusResult>(
                (handler, token) => handler.HandleAsync(new FocusCommandExecution(latest.Id), token),
                CancellationToken.None);

            item.Latest = result.Execution;

            if (!result.Focused)
            {
                Message = result.Execution.IsActive
                    ? "Não foi possível trazer o terminal para a frente. Ele continua aberto."
                    : "O terminal já foi fechado.";
            }
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickCommandFocusFailed {ExecutionId}", latest.Id);
            Message = "Não foi possível trazer o terminal para a frente.";
        }
    }

    /// <summary>Mostra de novo o output guardado da última execução escondida.</summary>
    [RelayCommand]
    public void ShowOutput(QuickCommandItemViewModel item)
    {
        if (item.Latest is not { } latest)
        {
            return;
        }

        Output.Reset(latest.CommandLine);

        foreach (var line in Lines(latest.Output, isError: false).Concat(Lines(latest.ErrorOutput, isError: true)))
        {
            Output.Append(line);
        }

        Apply(latest);
        OutputTitle = $"{latest.CommandName} · {latest.WorkingDirectory}";
        HasOutput = true;
    }

    private async Task ExecuteAsync(Guid commandId, Guid? bindingId, string name, QuickCommandItemViewModel? item)
    {
        if (_developmentId is not { } developmentId)
        {
            return;
        }

        Message = null;

        QuickCommandPlan plan;
        IReadOnlyDictionary<string, string>? values = null;

        try
        {
            plan = await runner.RunAsync<PrepareQuickCommandHandler, QuickCommandPlan>(
                (handler, token) => handler.HandleAsync(
                    new PrepareQuickCommand(_taskId, developmentId, commandId, bindingId), token),
                CancellationToken.None);

            // Um escondido por vez: o painel de output é um só. Terminal pode,
            // cada um tem a sua janela.
            if (plan.Entry.Mode == CommandMode.Execute && IsRunning)
            {
                Message = "Outro comando está rodando aqui. Espere ele terminar, ou cancele.";
                return;
            }

            if (plan.NeedsPrompt)
            {
                values = await prompt.AskAsync(plan);

                if (values is null)
                {
                    return;
                }
            }
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickCommandPrepareFailed {CommandId}", commandId);
            Message = $"Não foi possível preparar {name}.";
            return;
        }

        var command = new RunQuickCommand(_taskId, developmentId, commandId, bindingId, values);

        if (plan.Entry.Mode == CommandMode.Terminal)
        {
            await OpenTerminalAsync(command, name, item);
        }
        else
        {
            await RunHiddenAsync(command, plan, values, item);
        }

        await RefreshAsync(CancellationToken.None);
    }

    private async Task OpenTerminalAsync(RunQuickCommand command, string name, QuickCommandItemViewModel? item)
    {
        try
        {
            var execution = await runner.RunAsync<RunQuickCommandHandler, CommandExecutionView>(
                (handler, token) => handler.HandleAsync(command, null, token),
                CancellationToken.None);

            if (item is not null)
            {
                item.Latest = execution;
            }

            if (execution.Status == CommandExecutionStatus.Failed)
            {
                Message = execution.FailureReason ?? $"Não foi possível abrir {name}.";
            }
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickCommandTerminalFailed {CommandId}", command.CommandId);
            Message = $"Não foi possível abrir {name}.";
        }
    }

    private async Task RunHiddenAsync(
        RunQuickCommand command,
        QuickCommandPlan plan,
        IReadOnlyDictionary<string, string>? values,
        QuickCommandItemViewModel? item)
    {
        _run?.Dispose();
        _run = new CancellationTokenSource();

        Output.Reset(plan.Build(values).Line ?? plan.Entry.Template);
        Output.State = CommandStepState.Running;
        OutputTitle = $"{plan.Entry.Name} · {plan.WorkingDirectory}";
        HasOutput = true;
        IsRunning = true;

        if (item is not null)
        {
            item.IsRunningHere = true;
        }

        var progress = new UiProgress<CommandOutputLine>(Output.Append);

        try
        {
            var execution = await runner.RunAsync<RunQuickCommandHandler, CommandExecutionView>(
                (handler, token) => handler.HandleAsync(command, progress, token),
                _run.Token);

            Apply(execution);

            if (item is not null)
            {
                item.Latest = execution;
            }
        }
        catch (OperationCanceledException)
        {
            Output.State = CommandStepState.Canceled;
        }
        catch (DomainException exception)
        {
            Output.State = CommandStepState.Failed;
            Output.Error = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickCommandRunFailed {CommandId}", command.CommandId);
            Output.State = CommandStepState.Failed;
            Output.Error = "Não foi possível executar o comando.";
        }
        finally
        {
            IsRunning = false;

            if (item is not null)
            {
                item.IsRunningHere = false;
            }
        }
    }

    /// <summary>O rodapé do "terminal" a partir do que ficou gravado.</summary>
    private void Apply(CommandExecutionView execution)
    {
        Output.Command = execution.CommandLine;
        Output.Error = execution.FailureReason;
        Output.Result = execution.FinishedAt is { } finished
                        && (execution.ExitCode is not null || execution.Status == CommandExecutionStatus.Stopped)
            ? new CommandExecutionResult(
                execution.ExitCode ?? -1,
                execution.Output ?? string.Empty,
                execution.ErrorOutput ?? string.Empty,
                execution.StartedAt,
                finished,
                TimedOut: false,
                Canceled: execution.Status == CommandExecutionStatus.Stopped)
            : null;
        Output.State = execution.Status switch
        {
            CommandExecutionStatus.Completed => CommandStepState.Succeeded,
            CommandExecutionStatus.Stopped => CommandStepState.Canceled,
            CommandExecutionStatus.Failed => CommandStepState.Failed,
            _ => CommandStepState.Running,
        };
    }

    private static IEnumerable<CommandOutputLine> Lines(string? text, bool isError) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split('\n').Select(line => new CommandOutputLine(line.TrimEnd('\r'), isError)).SkipLast(text.EndsWith('\n') ? 1 : 0);
}
