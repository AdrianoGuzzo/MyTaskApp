using Microsoft.Extensions.Configuration;
using MyTaskApp.Infrastructure.Storage;
using Serilog;

namespace MyTaskApp.Desktop.Composition;

internal static class LoggingSetup
{
    private const string FileTemplate = "mytaskapp-.log";

    private const int RetainedFiles = 7;

    /// <summary>
    /// Antes de existir configuração. Um app que não abre porque o instalador
    /// esqueceu o <c>appsettings.json</c> falharia na primeira linha do
    /// <c>Main</c> e não deixaria rastro nenhum — este logger é o rastro.
    /// </summary>
    public static void ConfigureBootstrap()
    {
        // Primeira escrita do processo: é aqui que a pasta de dados nasce, e é
        // dela que o banco e o widget.json dependem logo em seguida.
        UserDataLocation.Current.CreateDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(UserDataLocation.Current.Logs, FileTemplate),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFiles)
            .CreateLogger();
    }

    /// <summary>
    /// Logs vão para a pasta de dados do usuário: o diretório de instalação pode
    /// ser somente-leitura. Retenção curta — é um app pessoal, não um servidor.
    /// </summary>
    /// <remarks>
    /// O <c>CloseAndFlush</c> não é zelo: o sink de arquivo abre o log do dia com
    /// trava exclusiva, então o logger de bootstrap precisa soltar o arquivo
    /// antes de o definitivo tentar abrir o mesmo caminho.
    /// </remarks>
    public static void Configure(IConfiguration configuration)
    {
        Log.CloseAndFlush();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(UserDataLocation.Current.Logs, FileTemplate),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFiles)
            .CreateLogger();
    }
}
