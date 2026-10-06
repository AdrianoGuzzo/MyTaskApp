using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Responde o diálogo do comando rápido sem janela (ADR-051): com os valores
/// roteirizados, ou cancelando. Guarda o que foi perguntado.
/// </summary>
internal sealed class FakeQuickCommandPrompt : IQuickCommandPrompt
{
    public List<QuickCommandPlan> Asked { get; } = [];

    /// <summary>O que o "usuário" digitou; <c>null</c> = cancelou.</summary>
    public IReadOnlyDictionary<string, string>? Answer { get; set; } = new Dictionary<string, string>();

    public Task<IReadOnlyDictionary<string, string>?> AskAsync(QuickCommandPlan plan)
    {
        Asked.Add(plan);
        return Task.FromResult(Answer);
    }
}
