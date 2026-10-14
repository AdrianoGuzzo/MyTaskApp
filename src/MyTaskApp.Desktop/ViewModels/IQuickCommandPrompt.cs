using MyTaskApp.Application.QuickCommands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Pergunta os parâmetros de um comando rápido e mostra a linha final antes de
/// executar (ADR-051). Porta, como <see cref="IConfirmationDialog"/>, para os
/// ViewModels serem testados sem janela.
/// </summary>
public interface IQuickCommandPrompt
{
    /// <summary>Os valores digitados, ou <c>null</c> se o usuário cancelou.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskAsync(QuickCommandPlan plan);
}
