using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// JWS compacto ES256 (RFC 7515), o envelope dos pedidos e das respostas de
/// aprovação (ADR-087). O cabeçalho leva a impressão digital da chave
/// (<c>kid</c>) e o tipo do que foi assinado (<c>typ</c>): uma aprovação
/// assinada nunca serve de operação, nem o contrário.
/// </summary>
/// <remarks>
/// Quem confere lê o payload dos bytes assinados, e não de uma cópia: não há
/// canonicalização a combinar entre os lados.
/// </remarks>
public static class RemoteJws
{
    /// <summary>Um <see cref="RemoteOperationRequest"/>.</summary>
    public const string OperationType = "mytaskapp-operation+jws";

    /// <summary>Uma <see cref="ApprovalResponse"/>.</summary>
    public const string ApprovalType = "mytaskapp-approval+jws";

    /// <summary>Teto do envelope: o maior pedido (um prompt de 8000 caracteres) fica muito abaixo.</summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>
    /// O que se assina: <c>base64url(cabeçalho).base64url(payload)</c>, em ASCII.
    /// Junte com <see cref="Compose"/> depois de assinar.
    /// </summary>
    public static string SigningInput<T>(T payload, string keyFingerprint, string type)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(DevicePublicKey.Es256, keyFingerprint, type), RemoteJson.Options);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, RemoteJson.Options);

        return Base64Url.EncodeToString(header) + "." + Base64Url.EncodeToString(body);
    }

    public static SignedEnvelope Compose(string signingInput, string signature) => new(signingInput + "." + signature);

    /// <summary>As três partes, se o envelope tem a forma certa. Não confere a assinatura.</summary>
    public static bool TryParse(SignedEnvelope envelope, out JwsParts parts)
    {
        parts = null!;
        var jws = envelope.Jws;

        if (string.IsNullOrEmpty(jws) || jws.Length > MaxLength)
        {
            return false;
        }

        var segments = jws.Split('.');

        if (segments.Length != 3 || segments.Any(segment => !Base64Url.IsValid(segment)))
        {
            return false;
        }

        try
        {
            var header = JsonSerializer.Deserialize<Header>(Base64Url.DecodeFromChars(segments[0]), RemoteJson.Options);

            if (header is null || header.Alg != DevicePublicKey.Es256)
            {
                return false;
            }

            parts = new JwsParts(
                header.Kid,
                header.Typ,
                Base64Url.DecodeFromChars(segments[1]),
                Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]),
                segments[2]);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// O envelope é do tipo pedido e foi assinado por <paramref name="key"/>; se
    /// for, devolve o payload lido no contrato estrito.
    /// </summary>
    public static bool TryVerify<T>(SignedEnvelope envelope, string type, DevicePublicKey key, out T payload)
        where T : class
    {
        payload = null!;

        if (!TryParse(envelope, out var parts) || parts.Type != type || parts.KeyFingerprint != key.Fingerprint
            || !key.Verifies(parts.SigningInput, parts.Signature))
        {
            return false;
        }

        return TryRead(parts, out payload);
    }

    /// <summary>O payload no contrato estrito; falso se não seguir.</summary>
    public static bool TryRead<T>(JwsParts parts, out T payload)
        where T : class
    {
        payload = null!;

        try
        {
            payload = JsonSerializer.Deserialize<T>(parts.Payload, RemoteJson.Options)!;
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Header(string Alg, string Kid, string Typ);
}

/// <param name="KeyFingerprint">O <c>kid</c>: a impressão digital da chave que diz ter assinado.</param>
/// <param name="SigningInput">Os bytes que a assinatura cobre.</param>
/// <param name="Signature">r e s, 64 bytes, em base64url.</param>
public sealed record JwsParts(string KeyFingerprint, string Type, byte[] Payload, byte[] SigningInput, string Signature);
