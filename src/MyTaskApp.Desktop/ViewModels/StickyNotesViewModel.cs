using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.StickyNotes;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um post-it na lista: o começo do texto, a cor, o pino e quando mexeu.</summary>
public sealed class StickyNoteRowViewModel(StickyNoteRow row, bool isOpen, string whenLabel)
{
    public StickyNoteRow Row { get; } = row;

    public Guid Id => Row.Id;

    public string Title => Row.Title;

    public bool IsOpen { get; } = isOpen;

    public bool IsPinned => Row.IsPinned;

    public bool IsAttention => Row.Emphasis == StickyNoteEmphasis.Attention;

    /// <summary>A bolinha na cor do post-it, como ele aparece na tela; sem cor, nenhuma.</summary>
    public IBrush? DotBrush => Row.Color.HueHex is { } hex ? new ImmutableSolidColorBrush(Color.Parse(hex)) : null;

    public bool HasDot => Row.Color.HueHex is not null;

    /// <summary>"há 5 min · ECO CORE · virou tarefa" — só o que existe.</summary>
    public string Meta { get; } = string.Join(
        " · ",
        new[]
        {
            whenLabel,
            row.TagName,
            row.ConvertedTaskId is null ? null : "virou tarefa",
            isOpen ? "aberto" : null,
        }.Where(part => !string.IsNullOrEmpty(part)));

    public string PinLabel => IsPinned ? "Soltar da tela" : "Fixar na tela";
}

/// <summary>
/// A lista de post-its (ADR-054): abrir, fixar, transformar em tarefa,
/// arquivar, restaurar e excluir. Uma janela pequena, e não uma tela de
/// gerenciamento — o post-it existe justamente para não exigir isso.
/// </summary>
/// <remarks>
/// Um post-it aberto é mexido pela janela dele, e não direto pelo caso de uso:
/// ela pode ter texto ainda não gravado, e arquivar por fora perderia a última
/// frase. Fechado, a lista chama o caso de uso.
/// </remarks>
public sealed partial class StickyNotesViewModel : ObservableObject
{
    private readonly IUseCaseRunner _runner;
    private readonly StickyNoteWindowManager _windows;
    private readonly IConfirmationDialog _confirmation;
    private readonly TimeProvider _time;
    private readonly ILogger<StickyNotesViewModel> _logger;

    public StickyNotesViewModel(
        IUseCaseRunner runner,
        StickyNoteWindowManager windows,
        IConfirmationDialog confirmation,
        TimeProvider time,
        ILogger<StickyNotesViewModel> logger)
    {
        _runner = runner;
        _windows = windows;
        _confirmation = confirmation;
        _time = time;
        _logger = logger;

        // Qualquer mudança num post-it aberto (texto, pino, arquivar) aparece
        // na lista sem reabri-la.
        _windows.NotesChanged += () => _ = LoadAsync();
    }

    public ObservableCollection<StickyNoteRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveScope), nameof(IsArchivedScope), nameof(IsTrashScope), nameof(EmptyMessage))]
    private StickyNoteScope _scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    public bool HasError => ErrorMessage is not null;

    public bool HasStatus => StatusMessage is not null;

    public bool IsActiveScope => Scope == StickyNoteScope.Active;

    public bool IsArchivedScope => Scope == StickyNoteScope.Archived;

    public bool IsTrashScope => Scope == StickyNoteScope.Trashed;

    public bool IsEmpty => IsLoaded && Rows.Count == 0;

    public string EmptyMessage => Scope switch
    {
        StickyNoteScope.Archived => "Nenhum post-it arquivado.",
        StickyNoteScope.Trashed => "A lixeira está vazia.",
        _ => "Nenhum post-it. Ctrl+Shift+N cria um.",
    };

    /// <summary>Uma tarefa nasceu de um post-it da lista: o quadro de hoje tem de mostrá-la.</summary>
    public event Action<ConvertStickyNoteToTaskResult>? TaskCreated;

    public async Task LoadAsync()
    {
        try
        {
            var rows = await _runner.RunAsync<GetStickyNotesHandler, IReadOnlyList<StickyNoteRow>>(
                (handler, ct) => handler.HandleAsync(new GetStickyNotes(Scope), ct));

            Rows.Clear();

            foreach (var row in rows)
            {
                Rows.Add(new StickyNoteRowViewModel(row, _windows.IsOpen(row.Id), WhenLabel(row)));
            }

            ErrorMessage = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "StickyNotesListFailed");
            ErrorMessage = "Não foi possível carregar os post-its.";
        }
        finally
        {
            IsLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private Task ShowScopeAsync(StickyNoteScope scope)
    {
        Scope = scope;
        StatusMessage = null;

        return LoadAsync();
    }

    [RelayCommand]
    private Task NewNoteAsync() => _windows.CreateNewAsync();

    [RelayCommand]
    private Task OpenAsync(StickyNoteRowViewModel row) => _windows.OpenAsync(row.Id);

    [RelayCommand]
    private async Task TogglePinAsync(StickyNoteRowViewModel row)
    {
        if (_windows.Find(row.Id)?.ViewModel is { } open)
        {
            await open.TogglePinCommand.ExecuteAsync(null);
            return;
        }

        await RunAsync(() => _runner.RunAsync<PinStickyNoteHandler>(
            (handler, ct) => handler.HandleAsync(new PinStickyNote(row.Id, !row.IsPinned), ct)));
    }

    [RelayCommand]
    private async Task ConvertToTaskAsync(StickyNoteRowViewModel row)
    {
        if (_windows.Find(row.Id)?.ViewModel is { } open)
        {
            await open.ConvertToTaskCommand.ExecuteAsync(null);
            return;
        }

        ConvertStickyNoteToTaskResult? result = null;

        await RunAsync(async () => result = await _runner.RunAsync<ConvertStickyNoteToTaskHandler, ConvertStickyNoteToTaskResult>(
            (handler, ct) => handler.HandleAsync(new ConvertStickyNoteToTask(row.Id), ct)));

        if (result is not null)
        {
            StatusMessage = $"Post-it convertido em tarefa: {result.Title}";
            TaskCreated?.Invoke(result);
        }
    }

    [RelayCommand]
    private async Task ArchiveAsync(StickyNoteRowViewModel row)
    {
        if (_windows.Find(row.Id)?.ViewModel is { } open)
        {
            await open.ArchiveCommand.ExecuteAsync(null);
            return;
        }

        await RunAsync(() => _runner.RunAsync<ArchiveStickyNoteHandler>(
            (handler, ct) => handler.HandleAsync(new ArchiveStickyNote(row.Id), ct)));
    }

    /// <summary>Para a lixeira, sem perguntar: se desfaz pela aba Lixeira.</summary>
    [RelayCommand]
    private async Task MoveToTrashAsync(StickyNoteRowViewModel row)
    {
        if (_windows.Find(row.Id)?.ViewModel is { } open)
        {
            await open.MoveToTrashCommand.ExecuteAsync(null);
            return;
        }

        await RunAsync(() => _runner.RunAsync<MoveStickyNoteToTrashHandler>(
            (handler, ct) => handler.HandleAsync(new MoveStickyNoteToTrash(row.Id), ct)));
    }

    [RelayCommand]
    private Task RestoreAsync(StickyNoteRowViewModel row) => Scope == StickyNoteScope.Trashed
        ? RunAsync(() => _runner.RunAsync<RestoreStickyNoteFromTrashHandler>(
            (handler, ct) => handler.HandleAsync(new RestoreStickyNoteFromTrash(row.Id), ct)))
        : RunAsync(() => _runner.RunAsync<RestoreStickyNoteHandler>(
            (handler, ct) => handler.HandleAsync(new RestoreStickyNote(row.Id), ct)));

    /// <summary>
    /// A única exclusão sem volta — e a única que pergunta, com o foco em
    /// Cancelar (ADR-021).
    /// </summary>
    [RelayCommand]
    private async Task PurgeAsync(StickyNoteRowViewModel row)
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Excluir definitivamente?",
            $"“{row.Title}” some de vez. Não dá para desfazer.",
            "Excluir definitivamente",
            IsIrreversible: true));

        if (!confirmed)
        {
            return;
        }

        await RunAsync(() => _runner.RunAsync<PurgeStickyNoteHandler>(
            (handler, ct) => handler.HandleAsync(new PurgeStickyNote(row.Id), ct)));
    }

    /// <summary>ADR-008: a regra se lê como está; o resto vira log e mensagem genérica.</summary>
    private async Task RunAsync(Func<Task> operation)
    {
        string? error = null;

        try
        {
            await operation();
        }
        catch (DomainException exception)
        {
            error = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "StickyNotesListOperationFailed");
            error = "Não foi possível concluir. Tente de novo.";
        }

        // Recarrega antes de mostrar o erro: a recarga bem-sucedida o apagaria.
        await LoadAsync();

        if (error is not null)
        {
            ErrorMessage = error;
        }
    }

    /// <summary>"agora", "há 5 min", "hoje 14:30", "ontem", "07/10" — no fuso de quem lê.</summary>
    private string WhenLabel(StickyNoteRow row)
    {
        var at = Scope switch
        {
            StickyNoteScope.Archived => row.ArchivedAt ?? row.UpdatedAt,
            StickyNoteScope.Trashed => row.DeletedAt ?? row.UpdatedAt,
            _ => row.UpdatedAt,
        };

        var now = _time.GetUtcNow();
        var elapsed = now - at;

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "agora";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"há {(int)elapsed.TotalMinutes} min";
        }

        var zone = _time.LocalTimeZone;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        return day == today ? $"hoje {TimeZoneInfo.ConvertTime(at, zone):HH:mm}"
            : day == today.AddDays(-1) ? "ontem"
            : day.ToString("dd/MM", CultureInfo.InvariantCulture);
    }
}
