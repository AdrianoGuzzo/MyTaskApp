using System.Globalization;
using System.Text;

namespace MyTaskApp.Sync.Contracts;

/// <summary>
/// O cursor do pull. Opaco para o cliente — ele só guarda e devolve —, mas
/// versionado (<c>1.{seq}</c>) para o servidor poder mudar o formato sem
/// confundir um cursor antigo com um novo.
/// </summary>
public static class SyncCursor
{
    public const string Start = "";

    public static string Encode(long sequence) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"1.{sequence.ToString(CultureInfo.InvariantCulture)}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Vazio é o começo (0). Lixo é recusado, nunca lido como 0.</summary>
    public static bool TryDecode(string? cursor, out long sequence)
    {
        sequence = 0;

        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

        Span<byte> buffer = stackalloc byte[64];

        if (cursor.Length > 80 || !Convert.TryFromBase64String(base64, buffer, out var written))
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(buffer[..written]);

        return text.StartsWith("1.", StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }
}
