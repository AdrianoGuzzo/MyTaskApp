using MyTaskApp.Infrastructure.Storage;

namespace MyTaskApp.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string FileName { get; set; } = "mytaskapp.db";

    /// <summary>
    /// Pasta do banco. Vazio usa o diretório de dados do usuário, para que o app
    /// funcione instalado em local somente-leitura. Preenchido, separa o banco
    /// do resto — útil em teste, e o único motivo de a propriedade existir.
    /// </summary>
    public string? Directory { get; set; }

    public string ResolveFullPath() =>
        string.IsNullOrWhiteSpace(Directory)
            ? UserDataLocation.Current.DatabaseFile(FileName)
            : Path.Combine(Directory, FileName);

    public string BuildConnectionString() => $"Data Source={ResolveFullPath()}";
}
