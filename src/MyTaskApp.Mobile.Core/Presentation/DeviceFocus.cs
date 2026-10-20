using MyTaskApp.Application.Devices;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>
/// O dispositivo em que a lista do celular está focada (ADR-076): o mesmo para
/// as abas Tarefas e Histórico, e lembrado na próxima abertura do app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grava a chave, não o filtro.</b> A chave (<see cref="OriginCatalog.HereKey"/>,
/// o id de um computador…) vira filtro na hora de buscar, por
/// <see cref="OriginCatalog.MatchFor"/>: os ids antigos deste celular e a
/// conta desligada entram ali, e não ficam congelados na preferência.
/// </para>
/// <para>
/// <b>A conta desligada não apaga a escolha.</b> Sem conta, a lista mostra
/// tudo; ligada de novo, volta ao dispositivo de antes.
/// </para>
/// </remarks>
public sealed class DeviceFocus(IPreferenceStore preferences, OriginCatalog origins)
{
    public const string PreferenceKey = "task-list-device";

    private readonly Lock _gate = new();

    private string? _key;

    /// <summary>A escolha mudou, em qualquer aba: as duas listas recarregam.</summary>
    public event EventHandler? Changed;

    /// <summary>O que foi escolhido, mesmo que hoje não filtre nada.</summary>
    public string Key
    {
        get
        {
            lock (_gate)
            {
                return _key ??= preferences.Get(PreferenceKey) ?? OriginCatalog.AnyKey;
            }
        }
    }

    /// <summary>O filtro que a busca usa agora; <c>null</c> = todas.</summary>
    public PlacementMatch? Match => origins.MatchFor(Key);

    /// <summary>O dispositivo que aparece marcado: a escolha, ou "Todos os dispositivos" quando ela não vale agora.</summary>
    public string SelectedKey => Match is null ? OriginCatalog.AnyKey : Key;

    public void Select(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (key == _key)
            {
                return;
            }

            _key = key;
            preferences.Set(PreferenceKey, key == OriginCatalog.AnyKey ? null : key);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
