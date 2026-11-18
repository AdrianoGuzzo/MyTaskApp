using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using MyTaskApp.Server.Infrastructure.Persistence;
namespace MyTaskApp.Server.Infrastructure;

internal interface IFcmAuthorization
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

internal sealed class FcmAuthorization(IOptions<RemotePushOptions> options) : IFcmAuthorization
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceAccountCredential? _credential;
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_credential is null)
            {
                // Somente o arquivo privado configurado pelo administrador; nunca JSON enviado pelo aparelho.
                await using var file = File.OpenRead(options.Value.FcmServiceAccountFile);
                var source = ServiceAccountCredential.FromServiceAccountData(file);
                _credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(source.Id)
                {
                    Scopes = ["https://www.googleapis.com/auth/firebase.messaging"],
                    Key = source.Key,
                });
            }
            return await _credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
