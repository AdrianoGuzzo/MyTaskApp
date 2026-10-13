using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// Uma recusa da requisição inteira, com o código do contrato e o status HTTP.
/// A borda transforma em ProblemDetails; a frase não leva dado nenhum do usuário.
/// </summary>
public sealed class SyncServerException(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;

    public int Status { get; } = status;

    /// <summary>404, e não 403: quem não é membro não fica sabendo que o workspace existe.</summary>
    public static SyncServerException NotFound() =>
        new(SyncErrorCodes.NotFound, 404, "Não encontrado.");

    public static SyncServerException BadRequest(string message) =>
        new(SyncErrorCodes.BadRequest, 400, message);

    public static SyncServerException Forbidden(string message) =>
        new(SyncErrorCodes.Forbidden, 403, message);

    public static SyncServerException DeviceRevoked() =>
        new(SyncErrorCodes.DeviceRevoked, 403, "Este dispositivo foi revogado. Conecte de novo para registrá-lo como novo.");

    public static SyncServerException UpgradeRequired() =>
        new(SyncErrorCodes.UpgradeRequired, 426, "Esta versão do MyTaskApp é antiga demais para sincronizar. Atualize o app.");

    public static SyncServerException CursorAhead() =>
        new(SyncErrorCodes.CursorAhead, 410, "O cursor está à frente do servidor. Sincronize de novo desde o começo.");
}
