using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

/// <summary>
/// A chave com que esta instalação assina na orquestração remota (fase 2,
/// T-03; ADR-087): ECDSA P-256, criada na primeira vez e guardada no cofre do
/// sistema como <c>device-key</c>, ao lado dos outros segredos. Nunca no banco:
/// o banco vai para backup e para outro computador; a chave não.
/// </summary>
/// <remarks>
/// Nada fica em memória entre uma chamada e outra: o desktop e o agente leem o
/// mesmo cofre, e cada um usa a chave que está lá. Criar é serializado neste
/// processo. Se dois processos criarem ao mesmo tempo (só na primeira vez de
/// todas), vale a última gravação, e quem gravou relê antes de devolver.
/// </remarks>
internal sealed class DeviceKeyStore(ISecretStore secrets, ILogger<DeviceKeyStore> logger)
{
    internal const string SecretName = "device-key";

    private readonly SemaphoreSlim _creating = new(1, 1);

    /// <summary>A chave desta instalação. Na primeira vez, cria e guarda.</summary>
    /// <remarks>
    /// Uma chave guardada que não se lê mais (outro usuário, outra máquina,
    /// arquivo estragado) é trocada por uma nova: a instalação muda de
    /// identidade, e os pareamentos dela precisam ser refeitos.
    /// </remarks>
    public async Task<DeviceKey> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(cancellationToken) is { } existing)
        {
            return existing;
        }

        await _creating.WaitAsync(cancellationToken);

        try
        {
            if (await ReadAsync(cancellationToken) is { } createdMeanwhile)
            {
                return createdMeanwhile;
            }

            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                await secrets.WriteAsync(SecretName, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), cancellationToken);
            }

            logger.LogInformation("DeviceKeyCreated");

            return await ReadAsync(cancellationToken)
                ?? throw new InvalidOperationException("A chave do dispositivo foi guardada, mas não pôde ser lida de volta.");
        }
        finally
        {
            _creating.Release();
        }
    }

    private async Task<DeviceKey?> ReadAsync(CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(SecretName, cancellationToken) is not { } stored)
        {
            return null;
        }

        try
        {
            return DeviceKey.FromPkcs8(Convert.FromBase64String(stored));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            logger.LogWarning("DeviceKeyUnreadable {Error}", exception.GetType().Name);
            return null;
        }
    }
}

/// <summary>A chave desta instalação, aberta: a parte pública para registrar e a assinatura.</summary>
internal sealed class DeviceKey : IDisposable
{
    private readonly ECDsa _key;

    private DeviceKey(ECDsa key)
    {
        _key = key;
        PublicKey = DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());
    }

    public DevicePublicKey PublicKey { get; }

    /// <summary>
    /// ES256 como o JWS pede (RFC 7518, seção 3.4): <c>r</c> e <c>s</c> lado a
    /// lado, 64 bytes, e não o DER que o .NET daria por padrão.
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();

    /// <summary>Só P-256: uma chave de outra curva no cofre não assina ES256, e conta como ilegível.</summary>
    internal static DeviceKey FromPkcs8(byte[] pkcs8)
    {
        var key = ECDsa.Create();

        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);

            if (key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new CryptographicException("A chave do dispositivo não é P-256.");
            }

            return new DeviceKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
