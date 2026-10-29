using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// As mesmas travas do terminal direto (ADR-030), para o terminal do agente:
/// caminho absoluto e existente para o programa e para a pasta, e tamanho
/// dentro dos limites.
/// </summary>
internal static class PseudoTerminalValidation
{
    public static void Validate(PseudoTerminalStart start)
    {
        if (string.IsNullOrWhiteSpace(start.Executable)
            || !Path.IsPathFullyQualified(start.Executable)
            || !File.Exists(start.Executable))
        {
            throw new DomainException($"O executável não foi encontrado: {start.Executable}");
        }

        if (string.IsNullOrWhiteSpace(start.WorkingDirectory)
            || !Path.IsPathFullyQualified(start.WorkingDirectory)
            || !Directory.Exists(start.WorkingDirectory))
        {
            throw new DomainException($"A pasta não existe: {start.WorkingDirectory}");
        }

        if (!start.Size.IsValid)
        {
            throw new DomainException($"Tamanho de terminal fora dos limites: {start.Size}.");
        }
    }
}
