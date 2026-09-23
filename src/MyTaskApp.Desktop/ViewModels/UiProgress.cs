namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// <see cref="IProgress{T}"/> que entrega na thread de quem o criou (ADR-028).
/// </summary>
/// <remarks>
/// <para>
/// Diferente do <c>InlineProgress</c> do worktree: as linhas de um comando
/// chegam da thread que lê o pipe, e mexer numa coleção amarrada à tela fora
/// da thread de UI derruba o app. Essas são postadas.
/// </para>
/// <para>
/// O que já chega na thread de UI (começou, terminou) é aplicado na hora, como
/// o <c>InlineProgress</c> — o <see cref="Progress{T}"/> do BCL postaria também
/// esses, e o "terminou" poderia ser aplicado depois do resultado final. Linhas
/// atrasadas não fazem mal: cada uma sabe de qual etapa é.
/// </para>
/// <para>
/// Sem contexto de sincronização (testes), entrega na hora.
/// </para>
/// </remarks>
public sealed class UiProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Report(T value)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            report(value);
            return;
        }

        _context.Post(state => report((T)state!), value);
    }
}
