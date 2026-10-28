using System.Text.Json;

namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// O que se confere num pedido só olhando para ele: tipo, ids, prazo e a forma
/// do payload. Servidor e agente rodam a mesma conta (seção 4.4 da arquitetura);
/// assinatura, alvo e catálogo dependem de quem confere, e ficam com cada um.
/// </summary>
public static class RemoteOperationRules
{
    /// <summary>Os códigos de <see cref="RemoteErrorCodes"/> que o pedido viola; vazio é válido.</summary>
    public static IReadOnlyList<string> Problems(RemoteOperationRequest request, DateTimeOffset now)
    {
        var problems = new List<string>();

        if (request.OperationId == Guid.Empty || request.WorkspaceId == Guid.Empty
            || request.TargetDeviceId == Guid.Empty || request.RequestedByDeviceId == Guid.Empty)
        {
            problems.Add(RemoteErrorCodes.MissingIdentity);
        }

        if (RemoteOperationKinds.PayloadTypeOf(request.Kind) is not { } payloadType)
        {
            problems.Add(RemoteErrorCodes.UnknownKind);
        }
        else if (!Reads(request.Payload, payloadType))
        {
            problems.Add(RemoteErrorCodes.InvalidPayload);
        }

        if (request.NotAfter <= now)
        {
            problems.Add(RemoteErrorCodes.AlreadyExpired);
        }

        if (request.NotAfter - request.CreatedAt > RemoteLimits.MaxAcceptanceWindow)
        {
            problems.Add(RemoteErrorCodes.WindowTooLong);
        }

        // Sem este teto, um pedido "criado no futuro" esticaria o NotAfter além
        // das 24 h, e a janela de replay deixaria de cobrir o prazo de aceite.
        if (request.CreatedAt > now + RemoteLimits.MaxClockSkew)
        {
            problems.Add(RemoteErrorCodes.ClockSkew);
        }

        return problems;
    }

    /// <summary>O payload lido no tipo da operação, já conferido por <see cref="Problems"/>.</summary>
    public static T PayloadOf<T>(RemoteOperationRequest request) =>
        request.Payload.Deserialize<T>(RemoteJson.Options)
        ?? throw new JsonException($"O payload de {request.Kind} veio vazio.");

    private static bool Reads(JsonElement payload, Type type)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            return payload.Deserialize(type, RemoteJson.Options) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
