namespace MyTaskApp.Desktop.Widget;

/// <summary>O que o X da janela (e o Alt+F4) significa para o usuário (ADR-047).</summary>
public enum CloseBehavior
{
    /// <summary>Encerra o processo, com lembretes e tudo.</summary>
    Exit = 0,

    /// <summary>Esconde na bandeja; o app continua lembrando (ADR-016). O padrão de sempre.</summary>
    Tray = 1,

    /// <summary>Vira HUD: o X da janela normal é "sai da frente, mas fica à vista".</summary>
    Hud = 2,
}

/// <summary>O que de fato acontece depois de um pedido de fechar.</summary>
public enum CloseAction
{
    Exit,

    HideToTray,

    EnterHud,

    /// <summary>
    /// Já no HUD com "fechar vira HUD": repetir o gesto não faria nada, então a
    /// janela pergunta — ocultar, sair, ou voltar à janela normal.
    /// </summary>
    AskInHud,
}

/// <summary>
/// A decisão do X, isolada da janela para caber num teste sem display.
/// </summary>
public static class CloseRouting
{
    /// <remarks>
    /// Sem bandeja (o ícone não subiu), "ocultar" esconderia o app sem nenhum
    /// caminho de volta: vira sair. Mesmo raciocínio do <c>ShutdownMode</c> do
    /// ADR-016.
    /// </remarks>
    public static CloseAction Decide(CloseBehavior behavior, WindowMode mode, bool trayAvailable) =>
        behavior switch
        {
            CloseBehavior.Exit => CloseAction.Exit,
            CloseBehavior.Tray => trayAvailable ? CloseAction.HideToTray : CloseAction.Exit,
            CloseBehavior.Hud when mode == WindowMode.Normal => CloseAction.EnterHud,
            CloseBehavior.Hud => CloseAction.AskInHud,
            _ => trayAvailable ? CloseAction.HideToTray : CloseAction.Exit,
        };
}
