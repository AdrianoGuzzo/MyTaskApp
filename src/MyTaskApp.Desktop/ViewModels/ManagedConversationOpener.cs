using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Abre a conversa sem terminal de um ambiente desta máquina (ADR-092): a tela
/// <i>Controlar</i> daqui mesmo, já no ambiente e na conversa. É o "Abrir
/// conversa" do card, do selo da lista e do aviso de que alguém abriu um
/// Claude aqui de longe — a conversa roda pelo agente, não tem janela de
/// terminal para trazer à frente.
/// </summary>
/// <remarks>
/// Quem mostra a janela é a <see cref="App"/>, pelo <see cref="ControlOpened"/>,
/// como no "Preparar em…".
/// </remarks>
public sealed class ManagedConversationOpener(
    IUseCaseRunner runner,
    Func<RemoteControlViewModel> control,
    ILogger<ManagedConversationOpener> logger)
{
    public const string NotAcceptingMessage =
        "Esta máquina não está aceitando controle: a conversa só abre com o controle remoto ligado.";

    public event EventHandler<RemoteControlViewModel>? ControlOpened;

    /// <summary>Devolve o motivo, em palavras, quando não deu para abrir.</summary>
    public async Task<string?> OpenAsync(Guid developmentId, Guid? sessionId, CancellationToken cancellationToken = default)
    {
        MachineView? here;

        try
        {
            var view = await runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token),
                cancellationToken);

            here = view.Availability == RemoteAvailability.Ready && view.Settings.AcceptControl
                ? view.Machines.FirstOrDefault(machine => machine.IsThis && machine.AcceptsControl)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "ManagedConversationMachinesUnavailable");
            return "Não foi possível falar com o servidor do controle remoto agora. Tente de novo.";
        }

        if (here is null)
        {
            return NotAcceptingMessage;
        }

        var opened = control();
        ControlOpened?.Invoke(this, opened);
        await opened.OpenAsync(here, cancellationToken, developmentId, sessionId);
        return null;
    }

    /// <summary>
    /// A conversa de outra máquina (ADR-103): o selo e o aviso do Claude de
    /// longe abrem a tela <i>Controlar</i> dela, já na conversa.
    /// </summary>
    public async Task<string?> OpenRemoteAsync(Guid deviceId, Guid? developmentId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        MachineView? machine;

        try
        {
            var view = await runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token),
                cancellationToken);

            machine = view.Machines.FirstOrDefault(item => item.DeviceId == deviceId && item.AcceptsControl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "RemoteConversationMachinesUnavailable");
            return "Não foi possível falar com o servidor do controle remoto agora. Tente de novo.";
        }

        if (machine is null)
        {
            return "A outra máquina não está mais aceitando controle.";
        }

        var opened = control();
        ControlOpened?.Invoke(this, opened);
        await opened.OpenAsync(machine, cancellationToken, developmentId, sessionId);
        return null;
    }
}
