using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// A identificação disponível num app de desktop sem login: a conta do sistema
/// operacional. É o "quando houver identificação do usuário" do §5 — honesto
/// sobre o que o app sabe, em vez de inventar um autor.
/// </summary>
/// <remarks>
/// Lido uma vez, na construção: o nome da conta não muda durante a execução do
/// processo, e tocar em <c>Environment</c> a cada operação de auditoria não
/// traria informação nova nenhuma.
/// </remarks>
internal sealed class CurrentWindowsUser : ICurrentUser
{
    public CurrentWindowsUser()
    {
        try
        {
            var name = Environment.UserName;

            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (InvalidOperationException)
        {
            // Sem nome é um resultado válido, não uma falha: a auditoria grava
            // a operação do mesmo jeito, só sem autor.
            Name = null;
        }
    }

    public string? Name { get; }
}
