using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MyTaskApp.Infrastructure.Secrets;

/// <summary>
/// Segredos cifrados pelo DPAPI do Windows, no escopo do usuário (ADR-045).
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que DPAPI, e não o Credential Manager.</b> O cofre de credenciais
/// limita o segredo a 2.560 bytes, e o refresh token da Atlassian pode chegar
/// perto disso. O DPAPI é o mesmo mecanismo que o Credential Manager usa por
/// baixo: a chave é derivada do login do Windows, então só o mesmo usuário na
/// mesma máquina decifra. Outro usuário, um backup restaurado em outro PC ou
/// alguém que copie o arquivo leem bytes sem sentido.
/// </para>
/// <para>
/// O arquivo fica fora do banco (<c>secrets/{nome}.bin</c>), de propósito: o
/// banco pode ir para backup e suporte; o token não. Escrever é atômico
/// (arquivo temporário + troca), para uma queda não deixar meio segredo.
/// </para>
/// <para>
/// A chamada é a <c>CryptProtectData</c> direta, por <c>LibraryImport</c>,
/// como o resto do P/Invoke do app: o pacote <c>ProtectedData</c> seria uma
/// dependência só para estas duas funções.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class DpapiSecretStore(string directory, ILogger<DpapiSecretStore> logger) : ISecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>Amarra o segredo ao app: outro programa do mesmo usuário não decifra sem saber isto.</summary>
    private static readonly byte[] Entropy = "MyTaskApp.Secrets.v1"u8.ToArray();

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);

        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);

        try
        {
            return Encoding.UTF8.GetString(Unprotect(protectedBytes));
        }
        catch (CryptographicException exception)
        {
            // Outro usuário, outra máquina ou arquivo corrompido. Sem segredo,
            // a integração pede para conectar de novo — sem derrubar nada.
            logger.LogWarning("SecretUnreadable {Name} {Error}", name, exception.HResult);
            return null;
        }
    }

    public async Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);

        var path = PathFor(name);
        var temporary = path + ".tmp";
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(value));

        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(name));
        return Task.CompletedTask;
    }

    private string PathFor(string name) => Path.Combine(directory, SecretName.Validate(name) + ".bin");

    private static unsafe byte[] Protect(byte[] plain)
    {
        fixed (byte* plainPointer = plain)
        fixed (byte* entropyPointer = Entropy)
        {
            var input = new DataBlob(plain.Length, plainPointer);
            var entropy = new DataBlob(Entropy.Length, entropyPointer);

            if (!CryptProtectData(ref input, null, ref entropy, 0, 0, CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return TakeAndFree(output);
        }
    }

    private static unsafe byte[] Unprotect(byte[] protectedBytes)
    {
        fixed (byte* inputPointer = protectedBytes)
        fixed (byte* entropyPointer = Entropy)
        {
            var input = new DataBlob(protectedBytes.Length, inputPointer);
            var entropy = new DataBlob(Entropy.Length, entropyPointer);

            if (!CryptUnprotectData(ref input, 0, ref entropy, 0, 0, CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return TakeAndFree(output);
        }
    }

    private static unsafe byte[] TakeAndFree(DataBlob blob)
    {
        try
        {
            return new ReadOnlySpan<byte>(blob.Data, blob.Size).ToArray();
        }
        finally
        {
            // O texto decifrado não fica esperando o coletor na memória nativa.
            new Span<byte>(blob.Data, blob.Size).Clear();
            LocalFree((nint)blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DataBlob(int size, byte* data)
    {
        public int Size = size;
        public byte* Data = data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob entropy,
        nint reserved,
        nint prompt,
        int flags,
        out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn,
        nint description,
        ref DataBlob entropy,
        nint reserved,
        nint prompt,
        int flags,
        out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
