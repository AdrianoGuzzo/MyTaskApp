namespace MyTaskApp.Application.Sync;

/// <summary>O endereço conferido: a URL normalizada, ou por que não serve.</summary>
public sealed record SyncServerUrlCheck(Uri? Uri, string? Error)
{
    public bool IsValid => Uri is not null;
}

/// <summary>
/// O endereço do servidor de sincronização que o usuário escolheu (ADR-078).
/// A mesma regra para o campo do celular, para o <c>appsettings.user.json</c>
/// do desktop e para o cliente HTTP: nenhum caminho aceita um endereço que o
/// outro recusaria.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTPS sempre.</b> O token vai em cada pedido; um HTTP de rede o
/// mandaria em claro. HTTP só no loopback, e só quando o desenvolvimento
/// pede (build Debug ou <c>Sync:AllowHttpLoopback</c>).
/// </para>
/// <para>
/// <b>Só a raiz da API.</b> Usuário e senha na URL iriam parar em log e em
/// histórico; query e fragmento não têm sentido num endereço base.
/// </para>
/// </remarks>
public static class SyncServerUrl
{
    public const int MaxLength = 2000;

    public static SyncServerUrlCheck Check(string? text, bool allowHttpLoopback)
    {
        var trimmed = text?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Invalid("Informe o endereço do servidor.");
        }

        if (trimmed.Length > MaxLength)
        {
            return Invalid("O endereço é longo demais.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return Invalid("Endereço inválido. Use o formato https://servidor.exemplo.com/.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return Invalid("O endereço precisa começar com https://.");
        }

        if (uri.UserInfo.Length > 0)
        {
            return Invalid("Não ponha usuário nem senha no endereço: o login é pelo navegador.");
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return Invalid("O endereço não pode ter ? nem #.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            return Invalid("Use https://. HTTP mandaria o login em claro pela rede.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttpLoopback)
        {
            return Invalid("HTTP só vale no desenvolvimento. Use https://.");
        }

        return new SyncServerUrlCheck(new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), null);
    }

    /// <summary>O mesmo servidor, comparando os endereços já normalizados. Nenhum dos dois é "o mesmo" que nada.</summary>
    public static bool SameServer(Uri? first, Uri? second) =>
        first is not null && second is not null
        && Uri.Compare(Normalize(first), Normalize(second), UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static Uri Normalize(Uri uri) => new(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");

    private static SyncServerUrlCheck Invalid(string error) => new(null, error);
}
