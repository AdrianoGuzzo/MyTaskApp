using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.FileSystem;

/// <summary>
/// O disco de verdade, fora da thread de UI: um caminho de rede desconectado
/// pode segurar a resposta por segundos (ADR-026).
/// </summary>
internal sealed class FileSystemDirectoryProbe : IDirectoryProbe
{
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Directory.Exists(path), cancellationToken);

    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Directory.Exists(path) || File.Exists(path), cancellationToken);
}
