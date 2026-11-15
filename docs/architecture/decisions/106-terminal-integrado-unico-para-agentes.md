# ADR-106 — Claude Code e Codex abrem no terminal integrado

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | agente-ia |
| **Registrado** | 2026-11-15 |
| **Palavras-chave** | terminal integrado, AgentEmbedded, OpenAgentSessionHandler, FocusAgentSessionHandler, HostedConsultationOpener |
| **Relações** | revisa: [ADR-098](098-terminal-do-agente-em-pseudoterminal.md), [ADR-101](101-terminal-do-claude-em-qualquer-aparelho.md), [ADR-104](104-provedores-claude-code-e-codex.md), [ADR-105](105-perguntar-ao-fechar-terminal.md), [ADR-100](100-consulta-inteligente-com-a-configuracao-do-desenvolvimento.md) |
| **Código** | `src/MyTaskApp.Application/Terminals/AgentTerminalWindows.cs`, `src/MyTaskApp.Application/Consultations/HostedConsultationOpener.cs` |

**Contexto:** preferências antigas, reabertura pelo menu da tarefa e Consulta
Inteligente ainda abriam terminais diferentes. O padrão integrado sozinho não
impedia esses desvios, tornando ambígua a relação entre janela, processo e tarefa.

**Decisão:** no Windows e Linux, novas sessões interativas de Claude Code e Codex
usam o pseudoterminal do MyTaskApp Agent e a vista dentro do app. Preferências
antigas e pedidos explícitos de janela externa são normalizados para
`AgentEmbedded`. O card deixa de oferecer os três modos e o botão duplicado
“Ver no app”. Reabrir uma sessão hospedada mostra a mesma sessão no integrado;
não inicia outro processo. Sessões antigas em terminal direto continuam acessíveis
pelo processo original até terminarem.

A Consulta Inteligente também inicia e retoma pelo canal local do agente, com
uma operação específica e versão 3 do primeiro quadro. O pedido contém dados da
consulta e o endereço local do MCP, nunca um comando arbitrário. O agente monta
a execução com o provedor original e reaplica as regras de consulta e leitura.
O segredo de consulta continua somente no ambiente do processo e seu hash no banco.
Agentes antigos recusam a nova versão antes de executar a consulta.

O estado continua vindo dos hooks e da saída do processo hospedado. Fechar só a
vista não encerra a sessão; o diálogo de fechamento do ADR-105 continua valendo.
Habilitar o terminal remoto normaliza a preferência de todos os provedores.
Pedidos remotos de desenvolvimento continuam usando o mesmo host, relay,
pareamento, assinatura e controle de entrada.

**Por quê:** uma única execução e uma única vista local tornam previsíveis a
abertura, reabertura e relação entre status do terminal e da tarefa.

## Alternativas

- **Só mudar o padrão:** preferências antigas e reaberturas continuariam divergindo.
- **Encerrar ou migrar processos ativos:** interromperia conversas em andamento.

## Consequências

- App e agente precisam ser atualizados juntos para a Consulta Inteligente integrada.
- Ausência do executável do agente é informada; não provoca troca silenciosa para outro terminal.
- Sem suporte de pseudoterminal na plataforma, permanece o terminal direto existente.
- Não há alteração de esquema nem migração de conversas entre provedores.
- Validação manual com Claude Code e Codex reais, inclusive fechamento e controle
  remoto, permanece necessária; por isso a implementação está marcada como parcial.

## Fora de escopo, de propósito

- Conversas gerenciadas sem terminal e comandos de shell de uso geral.
- Habilitar acesso remoto às Consultas Inteligentes: o contrato remoto atual exige
  ambiente de desenvolvimento; a consulta conserva sua identidade sem ambiente.
