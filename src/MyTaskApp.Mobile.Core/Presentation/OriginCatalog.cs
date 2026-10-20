using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Devices;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>
/// Uma opção do filtro de dispositivo; <see cref="Match"/> nulo = todas.
/// <see cref="Key"/> é o que fica gravado para a próxima abertura.
/// </summary>
public sealed record OriginFilterOption(string Key, string Label, PlacementMatch? Match)
{
    public override string ToString() => Label;
}

/// <summary>Uma escolha de "onde aparece" no formulário; <see cref="Placement"/> nulo = o legado, em todos.</summary>
public sealed record OriginChoice(string Label, DevicePlacement? Placement)
{
    public override string ToString() => Label;
}

/// <summary>
/// A origem de uma tarefa no celular (ADR-067) é o "onde aparece" do ADR-062:
/// compartilhada, só neste celular, ou só num dos computadores da conta.
/// Contextos como Trabalho ou Estudos continuam sendo etiquetas.
/// </summary>
/// <remarks>
/// Sem conta ligada não há dispositivos: o filtro só tem "Todos os dispositivos" e o
/// formulário não mostra a escolha — tudo é deste celular, como no desktop
/// antes de conectar.
/// </remarks>
public sealed class OriginCatalog(DevicePlacementState placements, SyncDeviceDirectory directory)
{
    public const string Shared = "Compartilhada";

    public const string ThisPhone = "Este celular";

    public const string RemovedDevice = "Dispositivo removido";

    public const string AnyKey = "all";

    public const string SharedKey = "shared";

    public const string HereKey = "here";

    public static readonly OriginFilterOption AnyOrigin = new(AnyKey, "Todos os dispositivos", null);

    /// <summary>A escolha só faz sentido com a conta ligada.</summary>
    public bool IsActive => placements.IsActive;

    public IReadOnlyList<OriginFilterOption> FilterOptions()
    {
        if (!placements.IsActive)
        {
            return [AnyOrigin];
        }

        var others = directory.Devices.Values
            .Where(device => !placements.IsHere(device.DeviceId))
            .OrderBy(device => device.RevokedAt is not null)
            .ThenBy(device => device.Nickname, StringComparer.Create(Labels.Culture, ignoreCase: true))
            .Select(device => new OriginFilterOption(
                device.DeviceId.ToString("D"),
                device.RevokedAt is null ? device.Nickname : $"{device.Nickname} (removido)",
                PlacementMatch.On([device.DeviceId])));

        return
        [
            AnyOrigin,
            new(SharedKey, "Compartilhadas", PlacementMatch.Shared),
            new(HereKey, ThisPhone, PlacementMatch.On(placements.HereIds)),
            .. others,
        ];
    }

    /// <summary>
    /// O filtro de uma escolha gravada, sem depender da lista de opções: o
    /// diretório de dispositivos pode ainda não ter chegado na abertura, e a
    /// escolha já vale. Um dispositivo que o diretório (já lido) não conhece
    /// mais, ou a conta desligada, voltam a "Todos os dispositivos".
    /// </summary>
    public PlacementMatch? MatchFor(string? key)
    {
        if (!placements.IsActive)
        {
            return null;
        }

        return key switch
        {
            SharedKey => PlacementMatch.Shared,
            HereKey => PlacementMatch.On(placements.HereIds),
            _ when Guid.TryParse(key, out var id) && !placements.IsHere(id) && (directory.Devices.Count == 0 || directory.Devices.ContainsKey(id))
                => PlacementMatch.On([id]),
            _ => null,
        };
    }

    /// <summary>
    /// Para onde uma tarefa pode ir: compartilhada, este celular, ou um
    /// computador ativo — os revogados não recebem nada novo.
    /// </summary>
    public IReadOnlyList<OriginChoice> FormChoices()
    {
        if (placements.Here is not { } here)
        {
            return [];
        }

        return
        [
            new("Compartilhada (todos os dispositivos)", DevicePlacement.Everywhere),
            new("Só neste celular", DevicePlacement.On(here)),
            .. placements.OtherDevices.Select(device => new OriginChoice($"Só em {device.Nickname}", DevicePlacement.On(device.DeviceId))),
        ];
    }

    /// <summary>
    /// O padrão de uma tarefa nova: compartilhada. Só no celular, ela sumiria da
    /// visão padrão do desktop, que mostra o computador dele e as compartilhadas.
    /// </summary>
    public DevicePlacement? DefaultForNew() => placements.IsActive ? DevicePlacement.Everywhere : null;

    /// <summary>A escolha do formulário que corresponde a um lugar; a primeira se não houver.</summary>
    public OriginChoice? ChoiceFor(DevicePlacement? placement)
    {
        var choices = FormChoices();

        if (choices.Count == 0)
        {
            return null;
        }

        if (placement is null || placement.IsEverywhere)
        {
            return choices[0];
        }

        var normalized = placements.IsHere(placement.DeviceId) ? DevicePlacement.On(placements.Here!.Value) : placement;

        return choices.FirstOrDefault(choice => choice.Placement == normalized)
            ?? new OriginChoice($"Só em {Label(placement)}", placement);
    }

    /// <summary>Como a linha e o detalhe escrevem a origem.</summary>
    public string Label(DevicePlacement? placement) => placement switch
    {
        null or { IsEverywhere: true } => Shared,
        { DeviceId: var id } when placements.IsHere(id) => ThisPhone,
        { DeviceId: var id } => directory.NicknameOf(id) ?? RemovedDevice,
    };
}
