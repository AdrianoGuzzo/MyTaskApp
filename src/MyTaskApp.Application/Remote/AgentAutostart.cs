namespace MyTaskApp.Application.Remote;

/// <summary>
/// O agente sem tela (ADR-085) subindo sozinho no logon desta conta. Só com
/// "aceitar ser controlada" ligado: quem não aceita pedidos não precisa de um
/// processo a mais rodando o dia inteiro.
/// </summary>
/// <remarks>
/// No Windows é a chave <c>Run</c> do usuário, como o próprio app (ADR-023) —
/// uma tarefa agendada no logon pediria administrador a quem não é. No Linux,
/// uma unit do <c>systemd --user</c>. Ligar também sobe o agente agora, se ele
/// não estiver rodando; desligar tira do logon, e o que roda vai até sair.
/// </remarks>
public interface IAgentAutostart
{
    /// <summary>Deixa o logon como <paramref name="wanted"/> pede. Nunca lança: é uma preferência.</summary>
    Task ApplyAsync(bool wanted, CancellationToken cancellationToken = default);
}

/// <summary>
/// Acerta o logon pelo que está gravado: o desktop chama ao abrir, e um agente
/// que caiu (ou que o instalador fechou) volta a subir.
/// </summary>
public sealed record ApplyAgentAutostart;

public sealed class ApplyAgentAutostartHandler(IRemoteSettingsStore settings, IAgentAutostart autostart)
{
    public async Task HandleAsync(ApplyAgentAutostart command, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        await autostart.ApplyAsync(current.AcceptControl, cancellationToken);
    }
}

/// <summary>Sem o agente instalado ao lado (o celular, os testes): nada a ligar.</summary>
public sealed class NoAgentAutostart : IAgentAutostart
{
    public Task ApplyAsync(bool wanted, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
