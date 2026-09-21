namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Quem está usando o app, <b>quando há identificação</b> (§5, §8). Este é um
/// app desktop de usuário único, sem login: o nome é o da conta do sistema
/// operacional, e é legítimo não haver nenhum.
/// </summary>
/// <remarks>
/// Existe como porta, e não como <c>Environment.UserName</c> espalhado pelos
/// casos de uso, por dois motivos: a auditoria precisa ser testável com um nome
/// conhecido, e no dia em que houver contas de verdade muda-se uma
/// implementação em vez de caçar chamadas.
/// </remarks>
public interface ICurrentUser
{
    /// <summary><c>null</c> quando não há identificação disponível.</summary>
    string? Name { get; }
}

/// <summary>
/// O padrão quando o host não identifica ninguém. Degrada em vez de derrubar:
/// a operação é auditada do mesmo jeito, só sem autor — que é bem melhor do que
/// o app não subir porque uma composição esqueceu de registrar um nome.
/// </summary>
/// <remarks>
/// Registrado com <c>TryAddSingleton</c> em <c>AddApplication</c>, então quem
/// souber o nome de verdade — o Desktop, com a conta do sistema — registra
/// antes e ganha. Mesmo desenho do <c>TimeProvider.System</c>.
/// </remarks>
public sealed class UnknownUser : ICurrentUser
{
    public string? Name => null;
}
