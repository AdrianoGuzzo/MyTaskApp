# Claude Code por tarefa

| | |
|---|---|
| **Status** | Implementado no Windows. Fora dele não há terminal real do agente (`UnsupportedTerminalLauncher`) nem sons. |
| **Decisões** | [ADR-030](../architecture/decisions/030-sessoes-claude-code-por-tarefa.md) sessões · [ADR-033](../architecture/decisions/033-parametros-do-agente-lembrados.md) parâmetros · [ADR-036](../architecture/decisions/036-abrir-claude-code-menu-da-linha.md) menu da linha · [ADR-037](../architecture/decisions/037-acompanhar-claude-code-pelos-hooks.md) hooks · [ADR-039](../architecture/decisions/039-arroba-ambientes-e-arquivos-no-agente.md) `@` no texto · [ADR-040](../architecture/decisions/040-modelo-e-esforco-do-agente.md) modelo e esforço · [ADR-042](../architecture/decisions/042-som-por-estado-do-claude-code.md) sons |
| **Código** | `src/MyTaskApp.Application/Agents/`, `src/MyTaskApp.Domain/Agents/AgentSession.cs`, `src/MyTaskApp.Infrastructure/Agents/`, `src/MyTaskApp.Infrastructure/Terminals/`, `src/MyTaskApp.Desktop/ViewModels/AgentSessionViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/AgentAlertSoundsViewModel.cs` |
| **Testes** | `tests/MyTaskApp.Application.Tests/Agents/`, `tests/MyTaskApp.Infrastructure.Tests/Agents/`, `tests/MyTaskApp.Desktop.Tests/ViewModels/AgentSessionViewModelTests.cs` |

- **Iniciar Claude Code** abre o `claude` num terminal real, dentro do
  worktree. O card mostra PID, início e pasta, e **Abrir terminal do agente**
  traz a janela certa para a frente.
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
- **Um som por estado**, com sons do app ou arquivos seus, em
  *☰ → Sons do Claude Code…*.
- **Abrir Claude Code** também fica no menu `⋯` da linha, sem precisar abrir a
  tarefa.
- **Abrir Claude gerenciado**, com esta máquina aceitando controle remoto, roda
  a conversa pelo agente daqui, sem terminal, para continuar do celular ou de
  outro computador ([controle remoto](../remote.md#as-conversas-do-claude),
  ADR-092).

A **Consulta Inteligente**, que abre o Claude Code só lendo, sem os parâmetros
acima, tem guia próprio: [`../consulta-inteligente.md`](../consulta-inteligente.md).

## Lacunas de documentação (2026-10-24)

- O texto inicial para o agente com `--permission-mode plan`
  (`ClaudeCodeCliProvider.CreateLaunch`, migration `DevelopmentAgentPrompt`)
  existe no código, mas nenhum ADR o descreve; o ADR-033 e o ADR-040 o atribuem
  ao ADR-030, que não fala dele.
