using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha do terminal. stderr pinta de outra cor.</summary>
public sealed record CommandOutputLineViewModel(string Text, bool IsError);

/// <summary>
/// O "terminal" de uma etapa (ADR-028): o comando, as linhas na ordem em que
/// saíram e o rodapé com exit code e status.
/// </summary>
/// <remarks>
/// Guarda só as últimas <see cref="MaxLines"/> linhas: um <c>npm install</c>
/// verboso passa de dezenas de milhares, e o que decide uma falha costuma
/// estar no fim.
/// </remarks>
public sealed partial class CommandOutputViewModel : ObservableObject
{
    public const int MaxLines = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText), nameof(HasCommand))]
    private string? _command;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(FooterText), nameof(HasFooter), nameof(IsSucceeded), nameof(IsFailed), nameof(IsRunning))]
    private CommandStepState _state = CommandStepState.Waiting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private CommandExecutionResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText), nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDroppedLines), nameof(DroppedLinesText))]
    private int _droppedLines;

    public ObservableCollection<CommandOutputLineViewModel> Lines { get; } = [];

    public bool HasCommand => !string.IsNullOrEmpty(Command);

    public string HeaderText => $"> {Command}";

    public bool IsRunning => State is CommandStepState.Running;

    public bool IsSucceeded => State is CommandStepState.Succeeded;

    public bool IsFailed => State is CommandStepState.Failed or CommandStepState.Canceled;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasDroppedLines => DroppedLines > 0;

    public string DroppedLinesText => $"… {DroppedLines} linhas anteriores não são mostradas.";

    public bool HasFooter => State is CommandStepState.Succeeded or CommandStepState.Failed
        or CommandStepState.Canceled;

    public string FooterText
    {
        get
        {
            var status = State switch
            {
                CommandStepState.Succeeded => "✓ Processo concluído",
                CommandStepState.Canceled => "✗ Cancelado",
                CommandStepState.Failed when Result is null => "✗ Não executado",
                CommandStepState.Failed when Result.TimedOut => "✗ Tempo esgotado",
                CommandStepState.Failed => "✗ Falhou",
                _ => string.Empty,
            };

            if (Result is not { } result)
            {
                return status;
            }

            var seconds = result.Duration.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo("pt-BR"));

            return result.Canceled || result.TimedOut
                ? $"{status} · {seconds} s"
                : $"{status} · Exit Code {result.ExitCode} · {seconds} s";
        }
    }

    public void Reset(string? command)
    {
        Lines.Clear();
        DroppedLines = 0;
        Command = command;
        Result = null;
        Error = null;
        State = CommandStepState.Waiting;
    }

    public void Append(CommandOutputLine line)
    {
        if (Lines.Count >= MaxLines)
        {
            Lines.RemoveAt(0);
            DroppedLines++;
        }

        Lines.Add(new CommandOutputLineViewModel(line.Text, line.IsError));
    }

    /// <summary>O que "Copiar output" leva: comando, linhas e rodapé.</summary>
    public string ToText()
    {
        var text = new StringBuilder();

        if (HasCommand)
        {
            text.AppendLine(HeaderText).AppendLine();
        }

        foreach (var line in Lines)
        {
            text.AppendLine(line.Text);
        }

        if (HasError)
        {
            text.AppendLine().AppendLine(Error);
        }

        if (HasFooter)
        {
            text.AppendLine().AppendLine(FooterText);
        }

        return text.ToString();
    }
}
