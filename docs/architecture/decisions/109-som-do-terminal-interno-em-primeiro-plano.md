# ADR-109 — Som do terminal interno em primeiro plano

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-11-20 |
| **Palavras-chave** | som, terminal integrado, primeiro plano, AgentAttentionNotifier |
| **Relações** | exceção a: [ADR-042](042-som-por-estado-do-claude-code.md) |
| **Código** | `src/MyTaskApp.Application/Agents/AgentAttentionNotifier.cs` |

**Contexto.** A regra de primeiro plano silenciava o alerta mesmo quando o usuário queria ouvir a conclusão de uma resposta no terminal interno.

**Decisão.** No Windows, a vista interna em primeiro plano toca o som configurado quando o agente entra em um estado de alerta. O aviso no canto e o piscar da janela continuam suprimidos enquanto a vista está em primeiro plano. O terminal externo segue a regra do ADR-042.

**Consequência.** O usuário pode ouvir uma mudança de estado sem precisar desviar o olhar para o terminal. A configuração por estado continua controlando o som.
