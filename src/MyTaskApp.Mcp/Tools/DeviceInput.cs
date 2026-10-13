using MyTaskApp.Application.Devices;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>
/// "De qual computador" e "para onde" nas ferramentas (ADR-062): <c>here</c>,
/// <c>all</c> ou o apelido de um computador da conta. O padrão é sempre este
/// computador e o que é compartilhado, como a tela: o MCP roda no mesmo processo,
/// mas não lê o seletor da tela, que é de quem está olhando.
/// </summary>
internal static class DeviceInput
{
    public const string ViewHelp =
        "De qual computador: here (padrão: este e as compartilhadas), all (todos) ou o apelido de outro computador da conta.";

    public const string PlacementHelp =
        "Onde a tarefa aparece: here (só neste computador, o padrão), all (em todos) ou o apelido do computador para onde mandar.";

    public static DeviceView View(DevicePlacementState? placements, string? device) =>
        Normalize(device) switch
        {
            null or "here" or "aqui" => DeviceView.Here,
            "all" or "todos" => DeviceView.All,
            _ => DeviceView.Of(Find(placements, device!)),
        };

    /// <summary>O lugar pedido; <c>null</c> = o padrão de quem cria (este computador).</summary>
    public static DevicePlacement? Placement(DevicePlacementState? placements, string? placement) =>
        Normalize(placement) switch
        {
            null => null,
            "here" or "aqui" => placements?.Here is { } here ? DevicePlacement.On(here) : null,
            "all" or "todos" => DevicePlacement.Everywhere,
            _ => DevicePlacement.On(Find(placements, placement!)),
        };

    /// <summary>"este computador", "todos os computadores", o apelido; <c>null</c> sem outro computador na conta.</summary>
    public static string? Describe(DevicePlacementState? placements, DevicePlacement? placement) =>
        placements is { HasOtherComputers: true } ? placements.Describe(placement) : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static Guid Find(DevicePlacementState? placements, string device)
    {
        var wanted = device.Trim();

        if (placements is not { IsActive: true })
        {
            throw new DomainException("Este computador não está conectado à sincronização: só existe here.");
        }

        var found = placements.OtherDevices.FirstOrDefault(info =>
            string.Equals(info.Nickname, wanted, StringComparison.CurrentCultureIgnoreCase)
            || (Guid.TryParse(wanted, out var id) && info.DeviceId == id));

        return found?.DeviceId
            ?? throw new DomainException(
                $"Computador \"{McpInput.Shown(wanted)}\" não encontrado. Use here, all ou um destes: "
                + (placements.OtherDevices.Count == 0
                    ? "(nenhum outro computador ativo)"
                    : string.Join(", ", placements.OtherDevices.Select(info => info.Nickname)))
                + ".");
    }
}
