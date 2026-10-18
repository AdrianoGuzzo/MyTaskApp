using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// As chaves do Data Protection em repouso (SEC-011, ADR-073): com o
/// certificado, cada chave nova vai cifrada para o banco, a gravada antes em
/// claro é revogada ao subir, e os certificados anteriores ainda decifram as
/// chaves deles. Em Production, sem o certificado, o servidor não sobe.
/// </summary>
public sealed partial class DataProtectionTests(PostgresFixture postgres)
{
    private const string Password = "senha-do-pfx";

    private const string Purpose = "SEC-011";

    private const string Current = "Sync:DataProtectionCertificate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithTheCertificate_TheKeysAreEncrypted_AndLoginAndLogoutWork()
    {
        var database = await postgres.CreateDatabaseAsync(Ct);

        await using (var api = new ApiFactory(database, Certificate("Atual")))
        {
            await LoginAndLogoutAsync(api);
        }

        var keys = Keys(await StoredAsync(database));

        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(key => IsEncrypted(key), "o XML da chave leva o encryptedSecret, e não o segredo");
    }

    [Fact]
    public async Task WithoutTheCertificate_OutsideProduction_TheKeyStaysInTheClear()
    {
        var database = await postgres.CreateDatabaseAsync(Ct);

        await using (var api = new ApiFactory(database))
        {
            await LoginAndLogoutAsync(api);
        }

        Keys(await StoredAsync(database)).Should().NotBeEmpty().And.OnlyContain(key => !IsEncrypted(key));
    }

    [Fact]
    public async Task AKeyStoredInTheClear_IsRevokedOnce_WhenTheCertificateArrives()
    {
        var database = await postgres.CreateDatabaseAsync(Ct);
        var certificate = Certificate("Atual");

        await using (var before = new ApiFactory(database))
        {
            await LoginAndLogoutAsync(before);
        }

        var plaintext = Keys(await StoredAsync(database)).Select(KeyId).Should().ContainSingle().Subject;

        await using (var after = new ApiFactory(database, certificate))
        {
            // Logo ao subir: o ring carregado antes da revogação não volta com ela de padrão.
            ProtectedWith(Protector(after).Protect("o primeiro cookie")).Should().NotBe(plaintext);
            await LoginAndLogoutAsync(after);
        }

        var stored = await StoredAsync(database);

        Revoked(stored).Should().Equal(plaintext);
        Keys(stored).Where(key => KeyId(key) != plaintext).Should().NotBeEmpty().And.OnlyContain(key => IsEncrypted(key));

        // Subir de novo não revoga outra vez: as linhas não crescem a cada subida.
        await using (var again = new ApiFactory(database, certificate))
        {
            await LoginAndLogoutAsync(again);
        }

        Revoked(await StoredAsync(database)).Should().Equal(plaintext);
    }

    [Fact]
    public async Task OnRotation_ThePreviousCertificateStillDecryptsItsKeys()
    {
        var database = await postgres.CreateDatabaseAsync(Ct);
        var old = Certificate("Antigo");
        string payload;

        await using (var before = new ApiFactory(database, old))
        {
            payload = Protector(before).Protect("cookie de antes");
        }

        await using var after = new ApiFactory(database, Merge(Certificate("Novo"), AsPrevious(old)));

        Protector(after).Unprotect(payload).Should().Be("cookie de antes");
        await LoginAndLogoutAsync(after);
    }

    [Fact]
    public async Task WithoutThePreviousCertificate_TheOldKeysAreUnreadable_AndANewOneTakesOver()
    {
        var database = await postgres.CreateDatabaseAsync(Ct);
        string payload;

        await using (var before = new ApiFactory(database, Certificate("Vazado")))
        {
            payload = Protector(before).Protect("cookie de antes");
        }

        await using (var after = new ApiFactory(database, Certificate("Novo")))
        {
            var unprotect = () => Protector(after).Unprotect(payload);

            unprotect.Should().Throw<CryptographicException>("a chave antiga só abre com o certificado que a cifrou");
            await LoginAndLogoutAsync(after);
        }

        Keys(await StoredAsync(database)).Should().HaveCountGreaterThan(1).And.OnlyContain(key => IsEncrypted(key));
    }

    [Fact]
    public async Task InProduction_TheCertificateIsRequired()
    {
        var address = new Dictionary<string, string?> { ["Sync:Issuer"] = "https://sync.test/", ["AllowedHosts"] = "sync.test" };
        var database = await postgres.CreateDatabaseAsync(Ct);

        await using (var without = new ApiFactory(database, address, Environments.Production))
        {
            var start = () => without.CreateClient();

            start.Should().Throw<InvalidOperationException>().WithMessage("Falta o certificado do Data Protection*");
        }

        // Com ele, o próximo a faltar é o do OpenIddict: a conferência do Data Protection passou.
        await using var with = new ApiFactory(database, Merge(address, Certificate("Atual")), Environments.Production);
        var next = () => with.CreateClient();

        next.Should().Throw<InvalidOperationException>().WithMessage("Falta o certificado de assinatura*");
    }

    /// <summary>
    /// O login inteiro (o cookie do Identity é o que o Data Protection
    /// protege) e o logout, que valida o token antiforgery com a mesma chave.
    /// </summary>
    private static async Task LoginAndLogoutAsync(ApiFactory api)
    {
        using var browser = api.Browser("ana");
        var (code, verifier) = await api.AuthorizeAsync("ana", Guid.CreateVersion7(), browser: browser);
        await api.ExchangeAsync(code, verifier);

        var page = await browser.GetStringAsync("/account/logout", Ct);
        var field = AntiforgeryField().Match(page);

        field.Success.Should().BeTrue("a página do logout traz o token antiforgery");

        using var logout = await browser.PostAsync(
            "/account/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { [field.Groups["name"].Value] = WebUtility.HtmlDecode(field.Groups["value"].Value) }),
            Ct);

        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        (await logout.Content.ReadAsStringAsync(Ct)).Should().StartWith("Você saiu");
    }

    private static IDataProtector Protector(ApiFactory api) =>
        api.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    /// <summary>Um PFX autoassinado, como o do openssl de docs/sync-server.md, em base64 na configuração.</summary>
    private static Dictionary<string, string?> Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=MyTaskApp Sync Data Protection {name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        return new()
        {
            [$"{Current}:Base64"] = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, Password)),
            [$"{Current}:Password"] = Password,
        };
    }

    /// <summary>O mesmo PFX, agora na lista dos anteriores.</summary>
    private static Dictionary<string, string?> AsPrevious(Dictionary<string, string?> certificate) =>
        certificate.ToDictionary(pair => pair.Key.Replace(Current, "Sync:DataProtectionPreviousCertificates:0", StringComparison.Ordinal), pair => pair.Value);

    private static Dictionary<string, string?> Merge(params Dictionary<string, string?>[] settings) =>
        settings.SelectMany(pair => pair).ToDictionary(pair => pair.Key, pair => pair.Value);

    private static async Task<List<XElement>> StoredAsync(string connectionString)
    {
        await using var context = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(connectionString).Options);
        var rows = await context.DataProtectionKeys.AsNoTracking().Select(row => row.Xml).ToListAsync(Ct);

        return rows.Select(xml => XElement.Parse(xml!)).ToList();
    }

    private static List<XElement> Keys(IEnumerable<XElement> stored) =>
        stored.Where(element => element.Name.LocalName == "key").ToList();

    private static List<Guid> Revoked(IEnumerable<XElement> stored) =>
        stored.Where(element => element.Name.LocalName == "revocation")
            .Select(element => KeyId(element.Elements().Single(child => child.Name.LocalName == "key")))
            .ToList();

    private static Guid KeyId(XElement key) => Guid.Parse((string)key.Attribute("id")!);

    /// <summary>A chave que protegeu o payload: o id vem logo depois dos 4 bytes do cabeçalho.</summary>
    private static Guid ProtectedWith(string payload) => new(WebEncoders.Base64UrlDecode(payload).AsSpan(4, 16));

    private static bool IsEncrypted(XElement key) =>
        key.Descendants().Any(child => child.Name.LocalName == "encryptedSecret");

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]+)\">")]
    private static partial Regex AntiforgeryField();
}
