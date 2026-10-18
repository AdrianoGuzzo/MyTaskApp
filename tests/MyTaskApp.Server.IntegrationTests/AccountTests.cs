using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using MyTaskApp.Server.Api;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// As páginas do login pelo navegador (SEC-013): a volta só vai para um caminho
/// deste servidor, e o que não for um vira <c>/</c>, sem 500. O logout, fora dos
/// testes, exige HTTPS e diz isso em vez de estourar.
/// </summary>
public sealed partial class AccountTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/\t/evil.example", "/")]
    [InlineData("/\r\n/evil.example", "/")]
    [InlineData("/ /evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("/health", "/health")]
    public async Task TheReturnUrl_OnlyLeadsToAPathOfThisServer(string returnUrl, string expected)
    {
        await using var api = await CreateApiAsync();
        using var browser = api.Browser("ana");

        // O login já normaliza: o callback que ele monta só leva o que passou.
        using var login = await browser.GetAsync($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().EndWith($"returnUrl={Uri.EscapeDataString(expected)}");

        // E o callback também, para quem chega nele direto com o cookie externo que o desafio deixou.
        using var callback = await browser.GetAsync($"/account/external-callback?returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be(expected);
        api.Logs.Text.Should().NotContain("SyncServerUnhandled");
    }

    [Fact]
    public async Task OutsideTheTests_LoggingOutOverHttp_IsAClear400_AndOverHttpsItWorks()
    {
        await using var api = await CreateApiAsync(Environments.Development);

        using (var http = Client(api, "http://localhost/"))
        {
            using var page = await http.GetAsync("/account/logout", Ct);
            using var post = await http.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

            foreach (var response in new[] { page, post })
            {
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest, response.RequestMessage!.Method.Method);
                response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
                (await DetailAsync(response)).Should().Contain("HTTPS");
            }
        }

        using var https = Client(api, "https://localhost/");
        using var form = await https.GetAsync("/account/logout", Ct);

        form.StatusCode.Should().Be(HttpStatusCode.OK);

        var field = HiddenField().Match(await form.Content.ReadAsStringAsync(Ct));
        using var logout = await https.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [field.Groups["name"].Value] = WebUtility.HtmlDecode(field.Groups["value"].Value),
        }), Ct);

        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        (await logout.Content.ReadAsStringAsync(Ct)).Should().StartWith("Você saiu.");
        api.Logs.Text.Should().NotContain("SyncServerUnhandled");
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]+)\">")]
    private static partial Regex HiddenField();

    private static HttpClient Client(ApiFactory api, string baseAddress) =>
        api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.GetProperty("detail").GetString();
    }

    private async Task<ApiFactory> CreateApiAsync(string environment = ServerSetup.TestingEnvironment) =>
        new(await postgres.CreateDatabaseAsync(Ct), environment: environment);
}
