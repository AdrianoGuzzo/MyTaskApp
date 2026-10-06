using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um botão "▶" da seção ⚡ Comandos (ADR-051), com o estado da última
/// execução: rodando, concluído, falhou.
/// </summary>
public sealed partial class QuickCommandItemViewModel(QuickCommandEntry entry) : ObservableObject
{
    public QuickCommandEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string RunLabel => $"▶ {Entry.Name}";

    /// <summary>A linha como cadastrada — com os <c>{nome}</c> ainda por preencher.</summary>
    public string Template => Entry.Template;

    public string Tip =>
        Entry.Mode == CommandMode.Terminal
            ? $"Abre um terminal no worktree e roda: {Entry.Template}"
            : $"Roda no worktree e mostra o resultado aqui: {Entry.Template}";

    /// <summary>A última execução deste comando no ambiente.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsActive), nameof(HasTerminal), nameof(StatusText), nameof(HasStatus),
        nameof(IsOk), nameof(IsFailed), nameof(CanRun), nameof(HasOutput))]
    private CommandExecutionView? _latest;

    /// <summary>Rodando escondido nesta janela: o "Cancelar" alcança.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(StatusText), nameof(HasStatus), nameof(CanRun), nameof(CanCancel))]
    private bool _isRunningHere;

    public bool IsActive => IsRunningHere || Latest is { IsActive: true };

    public bool CanRun => !IsActive;

    public bool CanCancel => IsRunningHere;

    public bool HasTerminal => Latest is { HasTerminal: true };

    public bool IsOk => !IsActive && Latest?.Status == CommandExecutionStatus.Completed;

    public bool IsFailed => !IsActive && Latest?.Status is CommandExecutionStatus.Failed or CommandExecutionStatus.Stopped;

    /// <summary>A execução escondida guardou output: dá para revê-lo.</summary>
    public bool HasOutput => !IsRunningHere && Latest is { Mode: CommandMode.Execute, IsActive: false };

    public bool HasStatus => StatusText.Length > 0;

    public string StatusText
    {
        get
        {
            if (IsRunningHere)
            {
                return "● Executando…";
            }

            if (Latest is not { } latest)
            {
                return string.Empty;
            }

            var at = latest.StartedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            return latest.Status switch
            {
                CommandExecutionStatus.Queued => "● Abrindo…",
                CommandExecutionStatus.Running when latest.Mode == CommandMode.Terminal => $"🟢 Executando desde {at}",
                CommandExecutionStatus.Running => "● Executando…",
                CommandExecutionStatus.Completed when latest.Mode == CommandMode.Terminal && latest.ExitCode is null =>
                    $"Terminal fechado · {at}",
                CommandExecutionStatus.Completed => $"✓ Concluído · exit {latest.ExitCode ?? 0} · {at}",
                CommandExecutionStatus.Stopped => $"✗ Cancelado · {at}",
                CommandExecutionStatus.Failed when latest.ExitCode is { } code => $"✗ Falhou · exit {code} · {at}",
                CommandExecutionStatus.Failed => $"✗ {latest.FailureReason ?? "Falhou"} · {at}",
                _ => string.Empty,
            };
        }
    }
}
