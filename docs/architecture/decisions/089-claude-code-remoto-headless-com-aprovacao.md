# ADR-089 — Claude Code remoto: headless, perfil escolhido no executor e aprovação humana

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | IHeadlessAgentProvider, claude -p, stream-json, --restricted, --permission-prompt-tool, perfil do agente, ApprovalRequest |
| **Relações** | relacionado: [ADR-033](033-parametros-do-agente-lembrados.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-040](040-modelo-e-esforco-do-agente.md), [ADR-081](081-consulta-inteligente-somente-leitura.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Requests.cs`, `src/MyTaskApp.Remote.Contracts/Approvals.cs`, `src/MyTaskApp.Domain/Agents/AgentSession.cs`, `src/MyTaskApp.Application/Remote/AgentSessionExecutors.cs`, `src/MyTaskApp.Agent/Approvals/ApprovalMcpServer.cs` |

**Estado:** aceito (fase 2), implementado (T-26 a T-30). Os flags foram conferidos no 2.1.309, e a linha inteira que o app gera foi aceita pelo 2.1.310 (T-26); a versão mínima do modo é a 2.1.309. Conferido no 2.1.310: uma falha de login chega como `result` com `subtype: success` e `is_error: true`, então o leitor decide pelo `is_error`. Os testes com o Claude de verdade são opt-in (`MYTASKAPP_TEST_CLAUDE=1`) e pedem o login.

**Contexto:** hoje o Claude abre num terminal real (`WindowsTerminalLauncher`;
no Linux não abre), e o app o acompanha pelos hooks (ADR-037). O argumento
padrão do card é `--dangerously-skip-permissions` (ADR-033): faz sentido com o
usuário olhando, e não com ninguém na frente da máquina. A Consulta
Inteligente (ADR-081) mostrou o caminho: travar pelos argumentos, não pelo
texto.

Decisão do usuário: na v1, **headless com aprovação remota**.

**Decisão:**

1. **Modo headless** (`IHeadlessAgentProvider`, implementado pelo
   `ClaudeCodeCliProvider`): `claude -p` com `--output-format stream-json`,
   prompt pelo stdin (sem a limitação de `"` e `%` do `claude.cmd`),
   `--session-id` no primeiro prompt e `--resume` nos seguintes, `--model` e
   `--effort` (ADR-040), e tetos locais de `--max-turns` e `--max-budget-usd`.
   Sempre com `--restricted`, como a consulta. Cada prompt é um processo; a conversa
   é a `AgentSession`, que ganha `Mode` (Interativo/Headless), o status
   `Stopped` e o id da operação.
2. **Perfil escolhido no executor.** Perfis locais: "ler", "editar no
   worktree", "editar e rodar testes". Viram `--allowedTools`,
   `--disallowedTools` e um `--settings` com `permissions.deny` para os
   segredos do app, `~/.ssh` e afins. O controlador só escolhe um perfil do
   catálogo.
3. **Nunca pular permissões.** Os `DefaultArguments` do card não entram no modo
   headless; o agente recusa `--dangerously-skip-permissions` e
   `bypassPermissions` venham de onde vierem. O `--restricted` é a segunda
   trava: no Claude Code 2.1.309 ele recusa os dois já na leitura dos
   argumentos ("bypassPermissions not supported in restricted mode").
4. **Sem configuração do projeto.** O worktree é dado não confiável. O
   `--restricted` ignora os arquivos de configuração do usuário, do projeto e
   locais, e com eles os hooks e as permissões do repositório. As regras do
   agente entram por `--settings`, que continua valendo. O MCP é só o de
   aprovação (`--strict-mcp-config`).
5. **Aprovação humana.** Ferramenta fora do perfil vai para
   `--permission-prompt-tool`, servido por um MCP do agente em loopback, com
   segredo por sessão (o padrão do `McpSecurityMiddleware`). Vira um
   `ApprovalRequest`; sem resposta em 10 min, nega. Um perfil sem canal de
   aprovação usa `--permission-prompts none`: o que perguntaria é negado.
6. **Ambiente limpo.** Fora `PG*`, `GH_TOKEN`, `GITHUB_TOKEN` e os
   `MYTASKAPP_*` que não são da sessão.

## O worktree não é sandbox

O processo roda como o usuário e alcança tudo o que ele alcança. As travas
são as do Claude (ferramentas e permissões) e a do humano que aprova. Isolar
no sistema operacional (contêiner, AppContainer, bubblewrap) fica como perfil
futuro.

## Alternativas

- **Terminal interativo remoto.** Reaproveitaria o fluxo atual, mas exige
  sessão gráfica, só funciona no Windows e não dá para responder de longe.
- **`--permission-mode acceptEdits` sem aprovação.** Deixa o Bash de fora ou
  liberado demais; não há meio-termo sem o humano.
- **SDK do Claude em outra linguagem** (TypeScript, Python). Daria
  `canUseTool` em processo, mas poria um runtime novo nas máquinas. O CLI com
  `stream-json` cobre o mesmo, e o app já o detecta.
- **O Remote Control do próprio Claude Code** (`--remote-control`). Deixa
  continuar uma sessão pelo app do Claude, sem código nosso. Mas é uma sessão
  interativa (terminal), presa à conta do Claude e fora da tarefa, do worktree,
  da auditoria e das aprovações do MyTaskApp. Continua disponível para quem
  abre o Claude pelo card, e não substitui a operação remota.
- **Sessões em segundo plano do Claude** (`--bg`, `claude attach`, `logs`,
  `stop`). Resolveriam o processo, mas `logs` devolve a tela do terminal, e não
  eventos estruturados para repassar e aprovar.

## Consequências

- Flags conferidos no Claude Code 2.1.309 sem chamar o modelo (detalhes na
  seção 7 de [`architecture/remote-orchestration.md`](../remote-orchestration.md)).
  `--permission-prompt-tool` e `--max-turns` são aceitos mas não aparecem na
  ajuda; o formato do `stream-json` e o contrato da ferramenta de permissão
  ficam para um teste opt-in (`MYTASKAPP_TEST_CLAUDE=1`) com o modelo de
  verdade, e o `DetectAsync` passa a exigir a versão mínima conferida.
- O `-p` pula o diálogo de confiança do diretório e ignora em silêncio um
  arquivo de configuração inválido: o agente só abre sessão em worktree de
  repositório cadastrado, e um teste valida o `--settings` que ele gera.
- O acompanhamento vem do `stream-json`; os hooks continuam só no modo
  interativo.

## Fora de escopo, de propósito

- Outros provedores de agente de IA (a porta permite, nenhum na v1).
- Sandbox do sistema operacional.
