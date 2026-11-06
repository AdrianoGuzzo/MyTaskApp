# Claude Code por tarefa

| | |
|---|---|
| **Status** | Implementado no Windows e no Linux. No Linux, o terminal do sistema é o emulador achado (`LinuxTerminalLauncher`) e não há sons. No macOS não há terminal do agente (`UnsupportedTerminalLauncher`). |
| **Decisões** | [ADR-030](../architecture/decisions/030-sessoes-claude-code-por-tarefa.md) sessões · [ADR-033](../architecture/decisions/033-parametros-do-agente-lembrados.md) parâmetros · [ADR-036](../architecture/decisions/036-abrir-claude-code-menu-da-linha.md) menu da linha · [ADR-037](../architecture/decisions/037-acompanhar-claude-code-pelos-hooks.md) hooks · [ADR-039](../architecture/decisions/039-arroba-ambientes-e-arquivos-no-agente.md) `@` no texto · [ADR-040](../architecture/decisions/040-modelo-e-esforco-do-agente.md) modelo e esforço · [ADR-042](../architecture/decisions/042-som-por-estado-do-claude-code.md) sons · [ADR-098](../architecture/decisions/098-terminal-do-agente-em-pseudoterminal.md) terminal do agente · [ADR-099](../architecture/decisions/099-terminal-remoto-do-claude.md) terminal remoto · [ADR-101](../architecture/decisions/101-terminal-do-claude-em-qualquer-aparelho.md) em qualquer aparelho · [ADR-103](../architecture/decisions/103-estado-do-claude-de-outras-maquinas-na-tarefa.md) estado de outras máquinas |
| **Código** | `src/MyTaskApp.Application/Agents/`, `src/MyTaskApp.Domain/Agents/AgentSession.cs`, `src/MyTaskApp.Infrastructure/Agents/`, `src/MyTaskApp.Infrastructure/Terminals/`, `src/MyTaskApp.Application/Terminals/`, `src/MyTaskApp.TerminalAttach/`, `src/MyTaskApp.Desktop/ViewModels/AgentSessionViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TerminalSessionViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/AgentAlertSoundsViewModel.cs` |
| **Testes** | `tests/MyTaskApp.Application.Tests/Agents/`, `tests/MyTaskApp.Infrastructure.Tests/Agents/`, `tests/MyTaskApp.Desktop.Tests/ViewModels/AgentSessionViewModelTests.cs` |

- **Iniciar Claude Code** abre o `claude` num terminal real, dentro do
  worktree. O card mostra PID, início e pasta, e **Abrir terminal do agente**
  traz a janela certa para a frente.
- **Onde abrir**, no card, escolhe o terminal (ADR-098). Sem escolha, é o
  terminal do agente, em janela, onde ele existe; no macOS, o do sistema
  (ADR-101). A escolha feita fica:
  - **Terminal do sistema:** um terminal novo; fechar encerra o Claude.
  - **Terminal do agente, em janela:** o Claude roda no `MyTaskApp.Agent`, num
    pseudoterminal (ConPTY no Windows, `openpty` no Linux), e aparece numa
    janela de terminal. Fechar a janela não encerra o Claude; **Abrir terminal
    do agente** mostra de novo.
  - **Terminal do agente, dentro do app:** a mesma sessão numa janela do
    MyTaskApp; **Ver no app** mostra de novo. O texto sai na Cascadia Mono,
    a fonte do Windows Terminal, que vem embutida no app (o Windows pode não
    tê-la instalada). Se ela faltar, vale a primeira monoespaçada do sistema.

  As duas vistas do agente são a mesma sessão, e outra máquina pareada ou o
  celular podem segui-la ([o terminal do Claude](../remote.md#o-terminal-do-claude)).
  Com "Aceitar ser controlado" (ou "Aceitar o terminal remoto") ligado e o
  terminal do sistema escolhido, o card avisa que a sessão, e o estado dela,
  não aparecem nos outros aparelhos, e oferece **Usar o terminal do agente**. A
  sessão do agente sempre recebe `--session-id`, e vive enquanto o agente
  vive.
- **Na vista dentro do app**, o teclado vai inteiro para o Claude. Arrastar
  seleciona, Ctrl+C com seleção copia, Ctrl+Shift+V cola, e a roda rola o
  histórico. Os botões **Interromper (Esc)**, **Permitir**/**Negar** (só no
  pedido de permissão), **Encerrar** e **Forçar o fim** usam o estado dos
  hooks; sem acompanhamento, só o teclado vale.
- **Parâmetros, modelo e esforço** escolhidos no card. O padrão é
  `--dangerously-skip-permissions`, já que o worktree é isolado e descartável.
- **Texto para o agente com `@`:** completa os caminhos dos ambientes da
  tarefa e dos arquivos dentro deles.
- **Acompanhamento pelos hooks oficiais** do Claude Code. O selo da linha e um
  aviso no canto da tela mostram **Trabalhando**, **Aguardando você**,
  **Aguardando revisão** ou **Erro na resposta**. Os hooks são passados com
  `claude --settings <arquivo do app>`, e o `~/.claude/settings.json` do
  usuário não é alterado. Eles chegam pela porta local
  `Application:AgentEventsPort` (47831, [configuração](../configuration.md)).
- **O Claude de outra máquina** aparece na linha da tarefa com um selo
  próprio ("⚠ Claude em Notebook · aguardando você"), e o pedido novo pulsa a
  linha e abre o aviso do canto com **Seguir**
  ([o estado do Claude na tarefa](../remote.md#o-estado-do-claude-na-tarefa),
  ADR-103).
- **Um som por estado**, com sons do app ou arquivos seus, em
  *☰ → Sons do Claude Code…*.
- **Abrir Claude Code** também fica no menu `⋯` da linha, sem precisar abrir a
  tarefa.
- **Abrir Claude gerenciado**, com esta máquina aceitando controle remoto, roda
  a conversa pelo agente daqui, sem terminal, para continuar do celular ou de
  outro computador ([controle remoto](../remote.md#as-conversas-do-claude),
  ADR-092).
- **A conversa aberta de longe** aparece no card como "Claude gerenciado",
  sem PID nem "Abrir terminal". O card diz de onde veio, mostra o estado e
  oferece **Abrir conversa** ([controle remoto](../remote.md#as-conversas-do-claude)).

A **Consulta Inteligente**, que abre o Claude Code com os mesmos parâmetros e
configurações daqui (ADR-100), e com o banco só leitura, tem guia próprio: [`../consulta-inteligente.md`](../consulta-inteligente.md).

## Lacunas de documentação (2026-10-24)

- O texto inicial para o agente com `--permission-mode plan`
  (`ClaudeCodeCliProvider.CreateLaunch`, migration `DevelopmentAgentPrompt`)
  existe no código, mas nenhum ADR o descreve; o ADR-033 e o ADR-040 o atribuem
  ao ADR-030, que não fala dele.
