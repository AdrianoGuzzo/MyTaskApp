# ADR-033 — Parâmetros do agente: editáveis no card, lembrados por agente

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-24, commit `c06d413` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | AgentSettings, IAgentSettingsStore, AgentArguments, DefaultArguments, --dangerously-skip-permissions, Parâmetros, StartAgentSession |
| **Relações** | relacionado: [ADR-014](014-padrao-de-lembrete-no-banco.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-039](039-arroba-ambientes-e-arquivos-no-agente.md), [ADR-040](040-modelo-e-esforco-do-agente.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Application/Agents/AgentArguments.cs`, `src/MyTaskApp.Application/Agents/IAgentSettingsStore.cs`, `src/MyTaskApp.Infrastructure/Persistence/Configurations/AgentSettingsConfiguration.cs`, `src/MyTaskApp.Application/Agents/AgentSessionHandlers.cs` |

**Decisão:** o card do agente (ADR-030) ganha o campo "Parâmetros", logo acima
de "Iniciar Claude Code". Ele vem preenchido com o padrão e mostra embaixo o que
vai rodar ("Roda: claude --dangerously-skip-permissions"). O texto usado ao
iniciar vira o novo padrão, para todas as tarefas.

**Padrão de fábrica: `--dangerously-skip-permissions`.** O worktree é uma pasta
descartável e isolada, feita para o agente trabalhar sem parar a cada arquivo.
Cada `IAgentCliProvider` diz o seu em `DefaultArguments`, e só o
`ClaudeCodeCliProvider` sabe dessa flag.

**Guardado por agente, no banco.** A tabela `AgentSettings` (`ProviderId` →
`Arguments`), atrás do `IAgentSettingsStore`, segue o desenho das outras
configurações (ADR-014). Sem linha, vale o padrão do agente. Texto **vazio**
gravado é escolha do usuário (abrir o `claude` puro), e não "sem configuração".

**Texto → lista, sem shell.** `AgentArguments.Parse` separa por espaço, e as
aspas duplas juntam um argumento com espaço. A lista vai para o
`AgentCliStartContext` e daí para o `ArgumentList` do processo. `&&` ou `|`
chegam ao Claude como texto e nunca viram outro comando. Aspas sem fechar são
recusadas antes de gravar a sessão ou o padrão.

**Junto com o texto livre.** Os parâmetros vêm antes do texto da tela e do
`--permission-mode plan` (ADR-030): `claude --dangerously-skip-permissions --permission-mode plan "texto"`.

**Quem salva é o "Iniciar".** Não há botão "Salvar" próprio.
`StartAgentSession.Arguments` informado vira o padrão no mesmo `SaveChanges`
da sessão; `null` usa o salvo. A tela só manda `null` antes de a detecção
trazer o padrão, para um campo ainda vazio não apagar o que foi salvo. A
detecção periódica só repõe o campo se o usuário não o editou.

## Divergências com o código (conferido em 2026-10-24)

- O texto atribui ao ADR-030 o "texto para o agente" com `--permission-mode plan`, que o ADR-030 não descreve. O recurso existe (`ClaudeCodeCliProvider.CreateLaunch`, migration `DevelopmentAgentPrompt`), sem ADR próprio.
