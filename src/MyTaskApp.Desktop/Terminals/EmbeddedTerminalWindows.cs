using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>
/// As vistas do terminal do agente dentro do app (ADR-098): uma por sessão,
/// ligada pelo canal local. Abrir de novo traz a que já está aberta; fechar só
/// desliga a vista — o Claude segue no agente.
/// </summary>
/// <remarks>
/// Quem cria a janela é a <see cref="App"/>, pelo <see cref="Opened"/>, como no
/// "Abrir conversa"; e é ela que avisa o fechamento por <see cref="ClosedAsync"/>.
/// </remarks>
public sealed class EmbeddedTerminalWindows(
    IAgentTerminalClient client,
    AgentTerminalWindows windows,
    IConfirmationDialog confirmations,
    ILogger<TerminalSessionViewModel> logger) : IEmbeddedTerminalPresenter
{
    /// <summary>Como aparece para as outras vistas da sessão.</summary>
    public const string Label = "MyTaskApp";

    private readonly Dictionary<Guid, TerminalSessionViewModel> _open = [];

    public event EventHandler<TerminalSessionViewModel>? Opened;

    /// <summary>A thread da tela; os testes trocam por chamada direta.</summary>
    public Action<Action> Post { get; init; } = action => Dispatcher.UIThread.Post(action);

    public bool Show(Guid sessionId, string title)
    {
        if (!client.Support.IsSupported || Opened is null)
        {
            return false;
        }

        Post(() => ShowHere(sessionId, title));
        return true;
    }

    /// <summary>A janela da vista fechou: a vista desliga, a sessão continua.</summary>
    public async Task ClosedAsync(TerminalSessionViewModel view)
    {
        if (_open.TryGetValue(view.SessionId, out var open) && open == view)
        {
            _open.Remove(view.SessionId);
            windows.SetEmbedded(view.SessionId, showing: false);
        }

        await view.DisposeAsync();
    }

    private void ShowHere(Guid sessionId, string title)
    {
        if (_open.TryGetValue(sessionId, out var open))
        {
            open.RequestActivation();
            return;
        }

        var view = new TerminalSessionViewModel(
            sessionId,
            title,
            TerminalClientKind.Embedded,
            (size, token) => client.AttachAsync(sessionId, TerminalClientKind.Embedded, Label, size, token),
            Post,
            confirmations,
            logger);

        _open[sessionId] = view;
        windows.SetEmbedded(sessionId, showing: true);
        Opened?.Invoke(this, view);
        _ = view.ConnectAsync();
    }
}
