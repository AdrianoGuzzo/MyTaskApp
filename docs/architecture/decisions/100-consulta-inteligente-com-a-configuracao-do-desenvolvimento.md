# ADR-100 — Consulta Inteligente: o Claude Code com a mesma configuração do desenvolvimento

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-11-03 |
| **Palavras-chave** | Consulta Inteligente, IConsultationLaunchBuilder, ConsultationLauncher, --restricted, --strict-mcp-config, mytaskapp-consulta, Parâmetros do card |
| **Relações** | revisa: [ADR-081](081-consulta-inteligente-somente-leitura.md); relacionado: [ADR-033](033-parametros-do-agente-lembrados.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md); revisto por: [ADR-104](104-provedores-claude-code-e-codex.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeCliProvider.cs`, `src/MyTaskApp.Application/Consultations/ConsultationSessionHandlers.cs` |

**Contexto:** o item 3 do ADR-081 travava o Claude da consulta pelos
argumentos: `--restricted --tools Read,Grep,Glob --strict-mcp-config`, sem os
"Parâmetros" do card de Desenvolvimento. O `--restricted` ignora os arquivos de
configuração do usuário, do projeto e locais, e o `--strict-mcp-config`, os
servidores MCP do usuário. Na prática, a consulta abria um Claude diferente do
que a pessoa usa: sem os MCPs, sem os plugins, sem os hooks, sem as permissões
e sem as preferências que ela já tem numa sessão de desenvolvimento. O usuário
pediu que a consulta fosse **igual ao desenvolvimento**, incluindo os
Parâmetros do card.

**Decisão:**

1. **A linha do Claude perde as travas.** O `CreateConsultationLaunch` não
   passa mais `--restricted`, `--tools` nem `--strict-mcp-config`. Valem os
   settings, os plugins, os hooks, o `CLAUDE.md` e os servidores MCP do
   usuário, como no `CreateLaunch` do desenvolvimento.
2. **Os Parâmetros do card entram.** O `ConsultationLauncher` lê os Parâmetros
   salvos do agente (ADR-033; por padrão, `--dangerously-skip-permissions`) e
   os passa antes do modelo e do esforço da aba, na mesma ordem de uma sessão
   de desenvolvimento. Assim, o modelo da aba vence um `--model` vindo do card.
3. **O MCP do app é somado aos do usuário**, e se chama `mytaskapp-consulta`. Ele
   chega por `--mcp-config`, com as quatro ferramentas de consulta
   pré-aprovadas (`mcp__mytaskapp-consulta__consultation_*`). Não se chama
   `mytaskapp` porque esse é o nome que quem usa o MCP do app costuma dar a ele
   na própria configuração (o do token do servidor), e os dois colidiriam.
4. **Continua igual:** as pastas em `--add-dir`, as instruções em
   `--append-system-prompt-file`, o `--session-id`/`--resume`, o
   acompanhamento pelos hooks (ADR-037) e todo o banco do ADR-081: o usuário
   `mytaskapp_leitura`, a conferência no catálogo, a transação `READ ONLY` com
   rollback, o segredo por sessão que só alcança as quatro ferramentas, e
   produção nunca.

**Por quê:** a pessoa decide como o Claude dela trabalha, e a consulta é mais
uma sessão dela. Uma consulta sem os MCPs e as permissões de sempre responde
pior e surpreende. A garantia que importa para não estragar nada, os dados, já
estava fora do Claude: é do PostgreSQL e do servidor MCP.

## Alternativas

- **Tirar só o `--strict-mcp-config`.** Os MCPs voltariam, mas o
  `--restricted` continuaria ignorando settings, plugins e hooks. Era só metade
  do pedido.
- **Tirar `--restricted` e `--strict-mcp-config`, mas manter `--tools
  Read,Grep,Glob` e forçar um modo de permissão sem bypass.** Ficaria sem
  escrita de código, mas não seria igual ao desenvolvimento. Foi a opção
  oferecida e recusada pelo usuário.

## Consequências

- **O código deixa de ser só leitura.** Com os Parâmetros padrão
  (`--dangerously-skip-permissions`), o Claude da consulta pode editar arquivos
  e rodar comandos nas pastas da consulta sem perguntar, como no
  desenvolvimento. As instruções geradas continuam pedindo para não alterar
  nada, mas isso é orientação, não trava. Quem quiser a consulta mais contida
  ajusta os Parâmetros do card, que valem para os dois.
- As ferramentas dos MCPs do usuário (com escrita, inclusive) ficam
  disponíveis na consulta, sujeitas às permissões dele.
- O banco segue só leitura, pelas três camadas do ADR-081.
- O caminho headless/remoto (ADR-089) não muda: continua com `--restricted`.

## Fora de escopo, de propósito

- Parâmetros próprios da consulta, separados dos do card. Por ora, a consulta
  usa os mesmos do desenvolvimento.
