using System.Security.Cryptography;

namespace MyTaskApp.Sync.Contracts;

/// <summary>
/// Ids que nascem das pontas, e não do acaso. O vínculo tarefa↔etiqueta não
/// tem id no domínio (a chave é o par); com um UUIDv5 do par, dois
/// dispositivos que marcam a mesma etiqueta falam do mesmo vínculo.
/// </summary>
public static class SyncIds
{
    /// <summary>O namespace do MyTaskApp para os UUIDv5. Fixo para sempre.</summary>
    public static readonly Guid Namespace = new("6f6b1a40-3d0e-5c49-9b55-2a8f3f0c7e11");

    public static Guid TaskTag(Guid taskId, Guid tagId)
    {
        Span<byte> name = stackalloc byte[32];
        taskId.TryWriteBytes(name[..16], bigEndian: true, out _);
        tagId.TryWriteBytes(name[16..], bigEndian: true, out _);

        return NameBased(name);
    }

    /// <summary>RFC 9562, versão 5 (SHA-1).</summary>
    private static Guid NameBased(ReadOnlySpan<byte> name)
    {
        Span<byte> input = stackalloc byte[16 + name.Length];
        Namespace.TryWriteBytes(input[..16], bigEndian: true, out _);
        name.CopyTo(input[16..]);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }
}
