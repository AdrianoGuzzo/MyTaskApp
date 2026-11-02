using MyTaskApp.Application.Terminals;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O que uma conexão de longe ao terminal espera de cada lote (ADR-099).</summary>
/// <param name="AttachId">O id da operação <c>terminal.attach</c>, que virou o id da conexão.</param>
/// <param name="TargetDeviceId">Esta máquina: um lote assinado para outra não serve aqui.</param>
/// <param name="ViewerKey">A chave da conexão, que veio no pedido assinado pelo aparelho do controlador.</param>
public sealed record RemoteTerminalExpectation(Guid AttachId, Guid TargetDeviceId, Guid SessionId, DevicePublicKey ViewerKey);

/// <summary>
/// Confere um lote de entrada de longe, antes de chegar ao terminal (ADR-099):
/// a assinatura da chave da conexão, com o <c>typ</c> do teclado; a conexão, a
/// máquina e a sessão; a sequência estritamente crescente (repetir ou trocar a
/// ordem é recusado); e o horário. Não guarda estado: a última sequência fica
/// com a conexão.
/// </summary>
public static class RemoteTerminalInputVerifier
{
    /// <summary>A ação conferida, ou o motivo da recusa, em palavras.</summary>
    public static (TerminalInput? Input, long Sequence, string? Problem) Verify(
        SignedEnvelope envelope,
        RemoteTerminalExpectation expected,
        long lastSequence,
        DateTimeOffset now)
    {
        if (!RemoteJws.TryVerify<RemoteTerminalInput>(envelope, RemoteJws.TerminalInputType, expected.ViewerKey, out var input))
        {
            return Refused("A entrada não veio assinada pela chave desta conexão.");
        }

        if (input.AttachId != expected.AttachId || input.TargetDeviceId != expected.TargetDeviceId || input.SessionId != expected.SessionId)
        {
            return Refused("A entrada é de outra conexão, máquina ou sessão.");
        }

        if (input.Sequence <= lastSequence)
        {
            return Refused("A entrada chegou repetida ou fora de ordem.");
        }

        if (input.IssuedAt < now - RemoteLimits.TerminalInputLifetime || input.IssuedAt > now + RemoteLimits.MaxClockSkew)
        {
            return Refused("A entrada está fora do horário: confira o relógio das duas máquinas.");
        }

        var action = input.Action;
        TerminalInput? mapped = action.Kind switch
        {
            RemoteTerminalActions.Keys when action.Keys is { Length: > 0 and <= RemoteLimits.MaxTerminalKeys } keys => new TerminalKeys(keys),
            RemoteTerminalActions.Prompt when !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= TerminalSessionHost.MaxPromptLength => new TerminalPrompt(action.Text),
            RemoteTerminalActions.Choice when action.DialogId is { } dialogId => new TerminalChoice(dialogId, action.Allow),
            RemoteTerminalActions.Interrupt => new TerminalInterrupt(),
            RemoteTerminalActions.Resize when Fits(action.Columns, action.Rows) => new TerminalResize(new TerminalSize(action.Columns, action.Rows)),
            RemoteTerminalActions.TakeControl => new TerminalTakeControl(),
            RemoteTerminalActions.ReleaseControl => new TerminalReleaseControl(),
            RemoteTerminalActions.Resync => new TerminalResync(),
            _ => null,
        };

        return mapped is null
            ? (null, input.Sequence, "Ação de terminal desconhecida ou incompleta.")
            : (mapped, input.Sequence, null);

        static (TerminalInput?, long, string?) Refused(string problem) => (null, long.MinValue, problem);
    }

    private static bool Fits(int columns, int rows) =>
        columns is >= TerminalSize.MinColumns and <= TerminalSize.MaxColumns
        && rows is >= TerminalSize.MinRows and <= TerminalSize.MaxRows;
}
