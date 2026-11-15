# ADR-105 — Perguntar ao fechar o terminal local e abrir dentro do app

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | agente-ia |
| **Registrado** | 2026-11-14 |
| **Palavras-chave** | terminal, fechamento, AgentEmbedded |
| **Relações** | revisa: [ADR-098](098-terminal-do-agente-em-pseudoterminal.md), [ADR-101](101-terminal-do-claude-em-qualquer-aparelho.md); revisto por: [ADR-106](106-terminal-integrado-unico-para-agentes.md) |

**Contexto:** fechar a vista externa deixa a sessão viva e causa confusão.

**Decisão:** usar o terminal embutido por padrão e perguntar ao fechar a vista
local: encerrar, forçar com confirmação, desconectar ou cancelar. Aguardar a saída
por até cinco segundos; sem confirmação, manter a janela aberta. Migrar uma vez
as preferências existentes de Claude e Codex, sem alterar sessões em andamento.

**Por quê:** a janela do app permite cancelar o fechamento antes de desconectar.

## Alternativas

- Interceptar o fechamento do console externo: não permite cancelar de forma confiável.

## Consequências

O usuário distingue desconectar de encerrar. Pode escolher outro terminal depois.
Terminais remotos e saída global do app preservam o comportamento atual.
Validação manual com processos reais permanece necessária.
