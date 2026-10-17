using System.Net;
using Microsoft.Extensions.Hosting;
using MyTaskApp.Server.Api;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Development nunca serve a rede (ADR-070): com um endereço fora do loopback,
/// o servidor recusa subir, a menos que a exceção do contêiner de
/// desenvolvimento esteja ligada. Os endereços vêm do <c>urls</c>, como o
/// <c>ASPNETCORE_URLS</c> e o <c>--urls</c> fariam, sem abrir porta nenhuma.
/// </summary>
public sealed class DevelopmentLoopbackTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://0.0.0.0:5280")]
    [InlineData("http://+:8080")]
    [InlineData("http://192.168.100.8:5280")]
    [InlineData("http://localhost:5280;http://[::]:5281")]
    public async Task InDevelopment_AnAddressOnTheNetwork_RefusesToStart(string urls)
    {
        await using var api = await CreateApiAsync(Environments.Development, urls);

        var start = () => api.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("Em Development o servidor só escuta no loopback*");
        api.Logs.Text.Should().Contain("Critical").And.Contain("SyncServerDevelopmentOnNetwork");
    }

    [Fact]
    public async Task InDevelopment_OnlyOnLoopback_TheServerStarts()
    {
        await using var api = await CreateApiAsync(Environments.Development, "http://localhost:5280;http://127.0.0.1:5281;http://[::1]:5282");

        await HealthShouldAnswerAsync(api);
    }

    [Fact]
    public async Task TheDevelopmentContainer_ListensOnAllInterfacesOnlyWithTheExplicitFlag()
    {
        await using var api = await CreateApiAsync(
            Environments.Development,
            "http://+:8080",
            new Dictionary<string, string?> { [DevelopmentLoopbackGuard.AllowNonLoopbackKey] = "true" });

        await HealthShouldAnswerAsync(api);
    }

    [Fact]
    public async Task OutsideDevelopment_TheGuardDoesNotApply()
    {
        await using var api = await CreateApiAsync(ServerSetup.TestingEnvironment, "http://0.0.0.0:5280");

        await HealthShouldAnswerAsync(api);
    }

    [Theory]
    [InlineData("http://localhost:5280", true)]
    [InlineData("http://LOCALHOST:5280", true)]
    [InlineData("http://127.0.0.1:5280", true)]
    [InlineData("http://127.10.0.1:5280", true)]
    [InlineData("http://[::1]:5280", true)]
    [InlineData("http://pipe:/mytaskapp-sync", true)]
    [InlineData("http://0.0.0.0:5280", false)]
    [InlineData("http://[::]:5280", false)]
    [InlineData("http://+:8080", false)]
    [InlineData("http://*:8080", false)]
    [InlineData("http://192.168.100.8:5280", false)]
    [InlineData("https://sync.exemplo.com:443", false)]
    public void WhatCountsAsLoopback(string address, bool loopback) =>
        DevelopmentLoopbackGuard.IsLoopback(address).Should().Be(loopback);

    private async Task<ApiFactory> CreateApiAsync(string environment, string urls, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>(settings ?? []) { ["urls"] = urls };

        return new ApiFactory(await postgres.CreateDatabaseAsync(Ct), all, environment);
    }

    private static async Task HealthShouldAnswerAsync(ApiFactory api)
    {
        using var client = api.CreateClient();
        using var response = await client.GetAsync("/health", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
