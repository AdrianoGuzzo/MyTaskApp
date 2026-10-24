# ADR-036 — "Abrir Claude Code" no menu da linha

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-25, commit `a4f1317` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Abrir Claude Code, menu da linha, StartAgentSession, FocusAgentSession, (aberto), ambiente pronto |
| **Relações** | relacionado: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-040](040-modelo-e-esforco-do-agente.md) |
| **Código** | `src/MyTaskApp.Desktop/Views/TodayView.axaml.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskRowViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TodayViewModel.cs` |

**Decisão:** o menu "⋯" da linha da tela Hoje ganha "Abrir Claude Code", que
abre o agente (ADR-030) num ambiente da tarefa sem abrir a tarefa. É o mesmo
caso de uso do card da aba Desenvolvimento (`StartAgentSession`), com os
parâmetros salvos.

**Só com ambiente pronto.** O item só aparece quando a tarefa tem worktree
`Ready`, os mesmos que o quadro já traz para a bolinha (ADR-034). Sem
ambiente não há onde abrir, e um item que só serve para dar erro não vale o
espaço no menu.

**Mais de um ambiente, a view pergunta qual.** Com um só, abre direto. Com
mais de um, abre um segundo menu com um item por ambiente
(`repositório · branch`), no molde do selo de vários agentes (ADR-031). Por
isso o item é um clique de code-behind, e não um comando: perguntar é
trabalho da view.

**Ambiente com agente já aberto traz o terminal.** São um agente por
ambiente, e o caso de uso recusaria o segundo. Quem clica quer o agente
daquele ambiente, então o item, marcado "(aberto)", faz o mesmo que o selo:
traz o terminal para a frente.
