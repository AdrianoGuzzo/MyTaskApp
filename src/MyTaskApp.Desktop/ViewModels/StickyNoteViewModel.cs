using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Onde a janela do post-it está, na convenção do banco: posição física e tamanho em DIP.</summary>
public readonly record struct StickyNoteGeometry(int X, int Y, double Width, double Height);

/// <summary>
/// Um post-it aberto (ADR-054). Sem botão Salvar: o texto grava sozinho numa
/// pausa da digitação, e a posição quando o arrasto termina. Quem marca o tempo
/// das duas pausas é a janela — aqui fica o que gravar e como reagir.
/// </summary>
/// <remarks>
/// Toda gravação passa por um semáforo. Um "fechar" logo depois de digitar
/// dispararia o texto e o fechamento juntos, e o fechamento chegando primeiro
/// apagaria como "em branco" um post-it que estava sendo escrito.
/// </remarks>
public sealed partial class StickyNoteViewModel : ObservableObject
{
    private const string SaveFailedMessage = "Não foi possível salvar o post-it. Ele continua aberto.";

    private readonly IUseCaseRunner _runner;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writes = new(1, 1);

    private string _savedContent;
    private StickyNoteGeometry? _pendingGeometry;
    private bool _closing;

    public StickyNoteViewModel(StickyNoteView view, IUseCaseRunner runner, ILogger logger)
    {
        _runner = runner;
        _logger = logger;

        Id = view.Id;
        _savedContent = view.Content;
        _content = view.Content;
        _isPinned = view.IsPinned;

        View = view;
    }

    public Guid Id { get; }

    /// <summary>
    /// O post-it como veio do banco na última leitura: geometria de abertura,
    /// cor, etiqueta. O texto vivo é <see cref="Content"/>.
    /// </summary>
    public StickyNoteView View { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle), nameof(WindowTitle), nameof(HasUnsavedText))]
    private string _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinGlyph), nameof(PinTip))]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;

    public bool HasUnsavedText => !string.Equals(Content, _savedContent, StringComparison.Ordinal);

    /// <summary>A primeira linha, apagada no cabeçalho — o que identifica o post-it de longe.</summary>
    public string HeaderTitle => StickyNoteText.FirstLine(Content) ?? string.Empty;

    /// <summary>O nome que o Alt+Tab e o leitor de tela anunciam.</summary>
    public string WindowTitle => StickyNoteText.FirstLine(Content) is { } first ? $"Post-it — {first}" : "Post-it";

    /// <summary>Alfinete cheio quando fixado (E840), vazado quando solto (E718).</summary>
    public string PinGlyph => IsPinned ? "" : "";

    public string PinTip => IsPinned
        ? "Fixado na tela — clique para soltar"
        : "Fixar na tela — fica sobre as outras janelas";

    /// <summary>A janela fecha de verdade: o post-it já foi gravado, guardado ou descartado.</summary>
    public event Action? CloseRequested;

    /// <summary>Algo que a lista de post-its mostra mudou: texto, pino, ciclo de vida.</summary>
    public event Action? Changed;

    /// <summary>Ctrl+Shift+N de dentro do post-it: outro post-it, sem passar pelo painel.</summary>
    public event Action? NewNoteRequested;

    /// <summary>A janela informa onde está; a gravação espera o fim do arrasto (<see cref="SaveGeometryAsync"/>).</summary>
    public void TrackGeometry(StickyNoteGeometry geometry) => _pendingGeometry = geometry;

    /// <summary>Grava o texto, se mudou. Chamado na pausa da digitação, ao perder o foco e ao fechar.</summary>
    public Task FlushAsync() => WriteAsync(SaveContentAsync);

    /// <summary>Grava a posição e o tamanho pendentes, se houver.</summary>
    public Task SaveGeometryAsync() => WriteAsync(SaveGeometryCoreAsync);

    [RelayCommand]
    private Task TogglePinAsync() => WriteAsync(async () =>
    {
        var pinned = !IsPinned;

        // Na tela primeiro: o Topmost acompanha o clique, e a gravação vem atrás.
        IsPinned = pinned;

        if (!await TryAsync(() => _runner.RunAsync<PinStickyNoteHandler>(
                (handler, ct) => handler.HandleAsync(new PinStickyNote(Id, pinned), ct))))
        {
            IsPinned = !pinned;
            return;
        }

        Changed?.Invoke();
    });

    /// <summary>
    /// O X, o Esc e o Ctrl+W. Fechar é esconder: grava o texto e a posição, e só
    /// então diz ao banco que a janela fechou. Em branco, o post-it some.
    /// </summary>
    [RelayCommand]
    private Task CloseAsync() => WriteAsync(async () =>
    {
        if (_closing)
        {
            return;
        }

        if (!await SaveContentAsync() || !await SaveGeometryCoreAsync())
        {
            return;
        }

        if (!await TryAsync(() => _runner.RunAsync<SetStickyNoteOpenHandler, SetStickyNoteOpenResult>(
                (handler, ct) => handler.HandleAsync(new SetStickyNoteOpen(Id, Open: false), ct))))
        {
            return;
        }

        _closing = true;
        Changed?.Invoke();
        CloseRequested?.Invoke();
    });

    [RelayCommand]
    private Task ArchiveAsync() => LeaveAsync(() => _runner.RunAsync<ArchiveStickyNoteHandler>(
        (handler, ct) => handler.HandleAsync(new ArchiveStickyNote(Id), ct)));

    /// <summary>Para a lixeira, sem perguntar: se desfaz na aba Lixeira da lista.</summary>
    [RelayCommand]
    private Task MoveToTrashAsync() => LeaveAsync(() => _runner.RunAsync<MoveStickyNoteToTrashHandler>(
        (handler, ct) => handler.HandleAsync(new MoveStickyNoteToTrash(Id), ct)));

    [RelayCommand]
    private void NewNote() => NewNoteRequested?.Invoke();

    /// <summary>Arquivar e excluir tiram o post-it da tela, depois de gravar o que estava escrito.</summary>
    private Task LeaveAsync(Func<Task> operation) => WriteAsync(async () =>
    {
        if (_closing || !await SaveContentAsync() || !await SaveGeometryCoreAsync())
        {
            return;
        }

        if (!await TryAsync(operation))
        {
            return;
        }

        _closing = true;
        Changed?.Invoke();
        CloseRequested?.Invoke();
    });

    private async Task<bool> SaveContentAsync()
    {
        if (_closing || !HasUnsavedText)
        {
            return true;
        }

        var content = Content;

        if (!await TryAsync(() => _runner.RunAsync<EditStickyNoteHandler>(
                (handler, ct) => handler.HandleAsync(new EditStickyNote(Id, content), ct))))
        {
            return false;
        }

        _savedContent = content;
        OnPropertyChanged(nameof(HasUnsavedText));
        Changed?.Invoke();

        return true;
    }

    private async Task<bool> SaveGeometryCoreAsync()
    {
        if (_closing || _pendingGeometry is not { } geometry)
        {
            return true;
        }

        _pendingGeometry = null;

        // Posição perdida não merece faixa de erro: o próximo arrasto regrava.
        try
        {
            await _runner.RunAsync<PlaceStickyNoteHandler>((handler, ct) => handler.HandleAsync(
                new PlaceStickyNote(Id, geometry.X, geometry.Y, geometry.Width, geometry.Height),
                ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "StickyNotePlacementNotSaved {NoteId}", Id);
        }

        return true;
    }

    private async Task WriteAsync(Func<Task> write)
    {
        await _writes.WaitAsync();

        try
        {
            await write();
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>ADR-008: <see cref="DomainException"/> se lê como está; o resto vira log e mensagem genérica.</summary>
    private async Task<bool> TryAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            ErrorMessage = null;
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "StickyNoteOperationFailed {NoteId}", Id);
            ErrorMessage = SaveFailedMessage;
            return false;
        }
    }
}
