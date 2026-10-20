using MyTaskApp.Application.Sync;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// O servidor de sincronização e os limites das chamadas (ADR-061).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nenhum segredo, e nenhum servidor embutido.</b> O desktop é cliente
/// público do OAuth: sem client secret, com PKCE. O endereço não vem da build
/// (ADR-078): no desktop, a seção <c>Sync</c> do <c>appsettings.user.json</c>;
/// no celular, o que o usuário salvou na tela, aplicado por
/// <see cref="ISyncServerAddress"/>.
/// </para>
/// <para>
/// Sem endereço, a sincronização não aparece como opção: o app continua
/// inteiro, só local.
/// </para>
/// </remarks>
internal sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public const string SecretName = "sync";

    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Aceita <c>http://</c> no loopback (o servidor de desenvolvimento). Só a
    /// build Debug liga por padrão; fora dela, é <c>Sync:AllowHttpLoopback</c>
    /// explícito no <c>appsettings.user.json</c> (ADR-078).
    /// </summary>
    public bool AllowHttpLoopback { get; set; }

    /// <summary>
    /// A porta da volta do login, em <c>http://127.0.0.1:{porta}/callback</c>.
    /// 0 (o padrão) é uma porta efêmera a cada login: o servidor aceita
    /// qualquer porta no loopback (RFC 8252 §7.3, ADR-074).
    /// </summary>
    public int CallbackPort { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int AuthorizationTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// O endereço do servidor, se configurado e seguro (<see cref="SyncServerUrl"/>):
    /// HTTPS, ou HTTP só no loopback e só no desenvolvimento. Um HTTP de rede
    /// mandaria o token em claro, e é recusado.
    /// </summary>
    public Uri? BaseUri => SyncServerUrl.Check(ApiBaseUrl, AllowHttpLoopback).Uri;

    public bool IsAvailable => BaseUri is not null;

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 5, 120));

    public TimeSpan AuthorizationTimeout => TimeSpan.FromMinutes(Math.Clamp(AuthorizationTimeoutMinutes, 1, 30));
}
