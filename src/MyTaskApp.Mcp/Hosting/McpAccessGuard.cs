using System.Security.Cryptography;
using System.Text;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// Confere o token do <c>Authorization: Bearer</c> (ADR-059). Guarda só o hash,
/// e compara em tempo constante — o mesmo cuidado do <c>AgentEventListener</c>.
/// </summary>
/// <remarks>
/// HTTP no loopback não é autenticação: qualquer processo da máquina, e qualquer
/// página aberta no navegador, alcança <c>127.0.0.1</c>. O token é o que separa
/// o cliente que o usuário configurou de todo o resto.
/// </remarks>
public sealed class McpAccessGuard
{
    private volatile byte[]? _hash;

    /// <summary>Sem token definido, ninguém passa.</summary>
    public bool HasToken => _hash is not null;

    public void SetToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _hash = Hash(token);
    }

    public bool Accepts(string? presented)
    {
        var expected = _hash;

        if (expected is null || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, Hash(presented));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

/// <summary>
/// O que vale enquanto o servidor está no ar e pode mudar sem reiniciar:
/// hoje, o somente leitura.
/// </summary>
public sealed class McpServerRuntime
{
    private volatile bool _readOnly;

    public bool ReadOnly
    {
        get => _readOnly;
        set => _readOnly = value;
    }
}
