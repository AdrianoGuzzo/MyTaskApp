using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.StickyNotes;

/// <summary>
/// Os post-its abertos (ADR-054): uma janela por post-it, nunca duas para o
/// mesmo — duas telas do mesmo texto teriam duas versões dele, e a última a
/// gravar apagaria a outra. Mesma razão do dicionário de anotações do <c>App</c>.
/// </summary>
/// <remarks>
/// Sem processo, timer ou consulta por post-it: cada janela é uma janela da
/// Avalonia comum, e o que ela grava passa pelo mesmo <see cref="IUseCaseRunner"/>
/// do resto do app.
/// </remarks>
public sealed class StickyNoteWindowManager(IUseCaseRunner runner, ILogger<StickyNoteWindowManager> logger)
{
    private const string OpenFailedMessage = "Não foi possível abrir o post-it.";

    private readonly Dictionary<Guid, StickyNoteWindow> _open = [];

    /// <summary>
    /// Onde um post-it novo nasce: na tela do widget, como os avisos
    /// (ADR-017). Nulo, ou sem resposta, usa a tela principal.
    /// </summary>
    public Func<PixelPoint?>? Anchor { get; set; }

    public IReadOnlyCollection<Guid> OpenNoteIds => _open.Keys;

    /// <summary>Algo que a lista de post-its mostra mudou.</summary>
    public event Action? NotesChanged;

    /// <summary>Uma falha sem janela onde mostrá-la — criar ou abrir.</summary>
    public event Action<string>? Failed;

    public bool IsOpen(Guid noteId) => _open.ContainsKey(noteId);

    public StickyNoteWindow? Find(Guid noteId) => _open.GetValueOrDefault(noteId);

    /// <summary>"Novo Post-it": grava em branco e abre com o cursor no texto.</summary>
    public async Task<StickyNoteWindow?> CreateNewAsync()
    {
        var view = await TryAsync(() => runner.RunAsync<CreateStickyNoteHandler, StickyNoteView>(
            (handler, ct) => handler.HandleAsync(new CreateStickyNote(), ct)));

        if (view is null)
        {
            return null;
        }

        NotesChanged?.Invoke();

        return Show(view, activate: true);
    }

    /// <summary>
    /// Reabre um post-it fechado — com o tamanho, a posição e o pino dele. Se já
    /// está aberto, só o traz para a frente.
    /// </summary>
    public async Task<StickyNoteWindow?> OpenAsync(Guid noteId)
    {
        if (_open.TryGetValue(noteId, out var opened))
        {
            Reveal(opened);
            return opened;
        }

        var view = await TryAsync(async () =>
        {
            await runner.RunAsync<SetStickyNoteOpenHandler, SetStickyNoteOpenResult>(
                (handler, ct) => handler.HandleAsync(new SetStickyNoteOpen(noteId, Open: true), ct));

            return await runner.RunAsync<GetStickyNoteHandler, StickyNoteView>(
                (handler, ct) => handler.HandleAsync(new GetStickyNote(noteId), ct));
        });

        if (view is null)
        {
            return null;
        }

        NotesChanged?.Invoke();

        // A leitura é assíncrona: um segundo clique pode ter aberto a janela
        // enquanto esta esperava o banco.
        return _open.TryGetValue(noteId, out var raced) ? raced : Show(view, activate: true);
    }

    /// <summary>
    /// Na subida do app, só os fixados voltam para a tela — e sem roubar o foco
    /// de quem acabou de fazer login (ADR-054).
    /// </summary>
    public async Task OpenStartupNotesAsync()
    {
        var views = await TryAsync(() => runner.RunAsync<GetStartupStickyNotesHandler, IReadOnlyList<StickyNoteView>>(
            (handler, ct) => handler.HandleAsync(new GetStartupStickyNotes(), ct)));

        foreach (var view in views ?? [])
        {
            if (!_open.ContainsKey(view.Id))
            {
                Show(view, activate: false);
            }
        }
    }

    /// <summary>
    /// Fecha a janela sem gravar nada: o post-it já foi guardado por outro
    /// caminho — arquivado ou excluído pela lista.
    /// </summary>
    public void Dismiss(Guid noteId)
    {
        if (_open.TryGetValue(noteId, out var window))
        {
            window.CloseWithoutSaving();
        }
    }

    private StickyNoteWindow Show(StickyNoteView view, bool activate)
    {
        var viewModel = new StickyNoteViewModel(view, runner, logger);
        var window = new StickyNoteWindow(viewModel, Anchor?.Invoke(), _open.Count)
        {
            ShowActivated = activate,
        };

        _open[view.Id] = window;

        viewModel.Changed += OnChanged;
        viewModel.NewNoteRequested += OnNewNoteRequested;

        window.Closed += (_, _) =>
        {
            viewModel.Changed -= OnChanged;
            viewModel.NewNoteRequested -= OnNewNoteRequested;

            if (_open.TryGetValue(view.Id, out var current) && ReferenceEquals(current, window))
            {
                _open.Remove(view.Id);
            }
        };

        window.Show();

        if (activate)
        {
            window.Activate();

            // Depois do layout: antes dele o TextBox ainda não aceita foco.
            Dispatcher.UIThread.Post(window.FocusEditor, DispatcherPriority.Loaded);
        }

        return window;
    }

    private static void Reveal(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnChanged() => NotesChanged?.Invoke();

    private void OnNewNoteRequested() => _ = CreateNewAsync();

    private async Task<T?> TryAsync<T>(Func<Task<T>> operation)
        where T : class
    {
        try
        {
            return await operation();
        }
        catch (DomainException exception)
        {
            Failed?.Invoke(exception.Message);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "StickyNoteWindowFailed");
            Failed?.Invoke(OpenFailedMessage);
            return null;
        }
    }
}
