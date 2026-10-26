using System.Text.Json;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>Um pedido feito deste celular: a operação, para qual máquina, e como a lista o chama.</summary>
public sealed record SentOperation(Guid OperationId, Guid DeviceId, string Title, DateTimeOffset At);

/// <summary>
/// Os pedidos feitos deste celular, para a lista "Pedidos" da aba Remoto. O
/// servidor não lista as operações por quem pediu, então o celular lembra as
/// últimas <see cref="Max"/>, nas preferências; o estado vem do servidor.
/// </summary>
public sealed class SentOperations(IPreferenceStore preferences)
{
    public const string PreferenceKey = "remote.sent";

    public const int Max = 20;

    private readonly Lock _gate = new();

    public IReadOnlyList<SentOperation> List()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    public void Add(SentOperation operation)
    {
        lock (_gate)
        {
            List<SentOperation> next = [operation, .. Read().Where(item => item.OperationId != operation.OperationId)];
            preferences.Set(PreferenceKey, JsonSerializer.Serialize(next.Take(Max).ToList()));
        }
    }

    /// <summary>Uma preferência estragada é uma lista vazia, e não uma aba que não abre.</summary>
    private List<SentOperation> Read()
    {
        if (preferences.Get(PreferenceKey) is not { Length: > 0 } stored)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SentOperation>>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
