using MyTaskApp.Server.Api;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Os segredos do compose de produção (ADR-070): um arquivo por chave em
// /run/secrets, com "__" no lugar de ":" (ConnectionStrings__Sync, …).
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// Log sem segredo: o caminho da requisição (sem a query), o status e o tempo.
// O OpenIddict e o ASP.NET ficam em Warning; o detalhe deles traria
// parâmetros do protocolo para o log. Só o Serilog escreve no console; os
// provedores registrados depois (os testes) recebem os mesmos eventos. Cada
// host fica com o seu logger, e não com o Log.Logger estático: com vários
// servidores no mesmo processo (os testes em paralelo), o evento de um ia
// parar nos provedores de outro.
builder.Logging.ClearProviders();
builder.Host.UseSerilog(
    (context, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .MinimumLevel.Override("OpenIddict", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(),
    preserveStaticLogger: true,
    writeToProviders: true);

// Sem "Server: Kestrel" nas respostas (SEC-009).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
    kestrel.AddServerHeader = false;
});

builder.Services.AddSyncServer(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseMiddleware<RequestLog>();
app.UseMiddleware<SecurityHeaders>();
app.UseSyncServer();

await app.MigrateIfConfiguredAsync();
await app.RunAsync();

/// <summary>A entrada do servidor; parcial para o <c>WebApplicationFactory</c> dos testes.</summary>
public partial class Program;
