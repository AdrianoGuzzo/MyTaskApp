using System.Security.Cryptography;
using System.Text;

namespace MyTaskApp.Infrastructure.OAuth;

/// <summary>
/// O PKCE (RFC 7636, S256) e o <c>state</c> de uma autorização pelo navegador:
/// um código interceptado na volta pelo <c>localhost</c> não serve para mais
/// ninguém sem o verificador, que nunca sai deste processo.
/// </summary>
internal static class Pkce
{
    /// <summary>Um <c>state</c> ou <c>code_verifier</c>: 32 bytes aleatórios em base64url.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
