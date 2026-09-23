using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>Responde "existe" para os caminhos listados, e "não existe" para o resto.</summary>
internal sealed class FakeDirectoryProbe : IDirectoryProbe
{
    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Existing.Contains(path));

    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Existing.Contains(path));
}
