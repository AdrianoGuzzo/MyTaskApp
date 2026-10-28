# ADR-092 — Sessão gerenciada a partir do desktop: o agente da própria máquina roda o Claude

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | sessão gerenciada, Abrir Claude gerenciado, executor confia na própria chave, desktop para celular, AgentSessionMode.Headless |
| **Relações** | relacionado: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md), [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md) |
| **Código** | `src/MyTaskApp.Application/Remote/AdmitRemoteOperation.cs` (`RemoteTrust`), `src/MyTaskApp.Application/Remote/RemoteOperationDispatcher.cs`, `src/MyTaskApp.Desktop/ViewModels/RemotePrepareViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskDevelopmentsViewModel.cs` |

**Estado:** implementado (fase 3, E5). A decisão de seguir por aqui é do
usuário: o caminho desktop → celular é a sessão gerenciada.

**Contexto:** o Claude aberto pelo card da tarefa roda num terminal interativo
(ADR-030). O app o acompanha pelos hooks (ADR-037), que morrem com o desktop e
não chegam ao servidor. Começar no computador e continuar no celular exige
que a conversa seja de um processo que o agente controla, a sessão sem
terminal do ADR-089.

A documentação oficial do Claude Code não oferece uma saída para terceiros:

- o **Remote Control** nativo só funciona com o claude.ai e o app do Claude;
- os **Channels** estão em prévia, pedem
  `--dangerously-load-development-channels` com diálogo interativo e são
  ignorados em `-p`.

Depender deles seria depender de um protocolo privado ou instável.

**Decisão:**

1. O card ganha **"Abrir Claude gerenciado"**, ao lado do terminal, quando
   esta máquina aceita controle. Ele abre a tela *Controlar* desta própria
   máquina, já no ambiente da tarefa: o perfil, o prompt, a linha do tempo e
   as aprovações são os de sempre. O pedido (`agent.start`, depois
   `agent.prompt`) vai assinado com a chave do próprio dispositivo, pelo
   mesmo servidor.
2. O executor confia na chave do próprio dispositivo como controlador, além
   dos pareados (`RemoteTrust`). A regra é explícita e restrita: o mesmo
   `DeviceId` e a chave do cofre, a mesma do registro. Não é um pareamento:
   não fica no banco, não aparece em Máquinas e não se revoga; vale enquanto
   a máquina aceita controle. Vale também para as respostas de aprovação:
   quem está na frente da máquina responde pelo desktop dela.
3. A conversa é a sessão sem terminal do ADR-089, com perfil, aprovação e
   limites iguais. O desktop vê a linha do tempo pela janela *Controlar*
   (ADR-091), e o celular continua de onde parou.
4. **O terminal interativo continua como está.** Ele é o modo "estou na
   frente da máquina"; não vira sessão gerenciada nem aparece de longe.

**Por quê:** reaproveita tudo o que já protege o caminho remoto (assinatura,
catálogo, perfil, aprovação, diário) sem abrir uma segunda porta. A sessão do
terminal seguiria um caminho que a ferramenta oficial não expõe.

## Alternativas

- **Relatar a sessão do terminal pelos hooks.** Os hooks dão o estado, não um
  canal para mandar o próximo prompt. O celular veria, mas não continuaria.
- **Remote Control nativo ou Channels.** Fora da interface oficial para
  terceiros (veja o contexto).
- **O desktop rodar o Claude headless sozinho, sem o agente.** Fecha o desktop,
  morre a conversa, e duplica o executor.

## Consequências

- A conversa começada no computador aparece no celular como qualquer outra.
- Uma exceção controlada à regra "o executor só aceita controlador pareado",
  que entra no modelo de ameaças antes de implementar.
- Sem o agente rodando, a tela *Controlar* diz "agente parado desde…: os
  pedidos esperam na fila", e o pedido espera até ele voltar ou vencer.

## Fora de escopo, de propósito

- Transformar uma sessão de terminal já aberta em gerenciada.
- O mesmo no Linux, onde o terminal não abre hoje.
