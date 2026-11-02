using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>
/// As vistas de terminais de outras máquinas (ADR-099): uma por sessão, ligada
/// pelo relay do servidor. Abrir de novo traz a que já está aberta; fechar só
/// desliga a vista — o Claude segue lá.
/// </summary>
/// <remarks>Quem cria a janela é a <see cref="App"/>, pelo <see cref="Opened"/>, como nas vistas locais.</remarks>
public sealed class RemoteTerminalWindows(
    RemoteTerminalConnector connector,
    IConfirmationDialog confirmations,
    ILogger<TerminalSessionViewModel> logger)
{
    private readonly Dictionary<Guid, TerminalSessionViewModel> _open = [];

    public event EventHandler<TerminalSessionViewModel>? Opened;

    /// <summary>A thread da tela; os testes trocam por chamada direta.</summary>
    public Action<Action> Post { get; init; } = action => Dispatcher.UIThread.Post(action);

    /// <summary>Abre (ou traz para a frente) a vista da sessão. Sem quem crie a janela, não abre.</summary>
    public bool Show(RemoteTerminalTarget target, string title)
    {
        if (Opened is null)
        {
            return false;
        }

        Post(() => ShowHere(target, title));
        return true;
    }

    /// <summary>A janela fechou: a conexão cai, a sessão continua lá.</summary>
    public async Task ClosedAsync(TerminalSessionViewModel view)
    {
        if (_open.TryGetValue(view.SessionId, out var open) && open == view)
        {
            _open.Remove(view.SessionId);
        }

        await view.DisposeAsync();
    }

    private void ShowHere(RemoteTerminalTarget target, string title)
    {
        if (_open.TryGetValue(target.SessionId, out var open))
        {
            open.RequestActivation();
            return;
        }

        // Cada (re)conexão é outro terminal.attach, com outra chave: a de antes não serve mais.
        var view = new TerminalSessionViewModel(
            target.SessionId,
            title,
            TerminalClientKind.Remote,
            (size, token) => connector.ConnectAsync(target, size, token),
            Post,
            confirmations,
            logger);

        _open[target.SessionId] = view;
        Opened?.Invoke(this, view);
        _ = view.ConnectAsync();
    }
}
