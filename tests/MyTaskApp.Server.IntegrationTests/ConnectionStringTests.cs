namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Nenhuma connection string vem do repositório (SEC-012): o
/// <c>appsettings.json</c> só tem a chave vazia, e quem a preenche é a
/// variável de ambiente, o <c>/run/secrets</c> ou, em Development, o
/// user-secrets. Sem ela, o servidor recusa subir com mensagem clara, e não
/// com o erro do Npgsql na primeira consulta.
/// </summary>
public sealed class ConnectionStringTests
{
    [Theory]
    [InlineData("Development", "")]
    [InlineData("Development", "   ")]
    [InlineData("Production", "")]
    public async Task WithoutTheConnectionString_TheServerRefusesToStart(string environment, string connectionString)
    {
        await using var api = new ApiFactory(connectionString, environment: environment);

        var start = () => api.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("Falta ConnectionStrings:Sync*user-secrets*");
    }
}
