using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Storage;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Um JSON ao lado do banco. Deliberadamente não é tabela: posição de janela
/// não é dado do usuário, não entra em backup e não merece uma migração — e um
/// arquivo perdido custa ao usuário exatamente um arrastar de painel.
/// </summary>
internal sealed class WidgetStateStore : IWidgetStateStore
{
    private const string FileName = "widget.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<WidgetStateStore> _logger;
    private readonly string _path;

    public WidgetStateStore(ILogger<WidgetStateStore> logger)
        : this(logger, DefaultDirectory())
    {
    }

    public WidgetStateStore(ILogger<WidgetStateStore> logger, string directory)
    {
        _logger = logger;
        _path = Path.Combine(directory, FileName);
    }

    public WidgetState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return WidgetState.Default;
            }

            var state = JsonSerializer.Deserialize<WidgetState>(File.ReadAllText(_path), Format);

            return state?.Sanitized() ?? WidgetState.Default;
        }
        catch (Exception exception)
        {
            // Nunca impedir o app de abrir por causa de onde a janela estava.
            _logger.LogWarning(exception, "WidgetStateUnreadable");
            return WidgetState.Default;
        }
    }

    public void Save(WidgetState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, Format));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "WidgetStateNotSaved");
        }
    }

    /// <summary>A mesma pasta do banco: tudo do app num lugar só.</summary>
    private static string DefaultDirectory() => UserDataLocation.Current.State;
}
