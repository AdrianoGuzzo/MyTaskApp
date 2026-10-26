# ADR-040 — Modelo e esforço do agente, escolhidos no card

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-28, commit `f7ff460` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | AgentCliOption, --model, --effort, IAgentCliProvider.Models, Efforts, AgentSettings, ClaudeCodeCliProvider |
| **Relações** | relacionado: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-033](033-parametros-do-agente-lembrados.md), [ADR-036](036-abrir-claude-code-menu-da-linha.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Application/Agents/IAgentCliProvider.cs`, `src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeCliProvider.cs`, `src/MyTaskApp.Application/Agents/IAgentSettingsStore.cs` |

**Contexto:** o Claude Code aceita `--model` e `--effort`, mas escolhê-los
exigia lembrar a flag e digitá-la no campo "Parâmetros" (ADR-033) antes de
cada "Iniciar".

**Decisão:** o card do agente ganha duas listas, **Modelo** e **Esforço**,
entre o texto para o agente e os parâmetros. A primeira opção de cada uma é
"Padrão", que não acrescenta nada e deixa o agente decidir. A prévia "Roda: …"
já mostra o que foi escolhido.

**O provider diz as opções.** `IAgentCliProvider.Models` e `Efforts` devolvem
`AgentCliOption(Value, Label, Arguments)`. O valor é o que se grava, o nome é o
que a tela mostra, e os argumentos são o que entra no comando. Só o
`ClaudeCodeCliProvider` sabe que é `--model opus` ou `--effort high`. Os dois
membros têm implementação padrão vazia, e um agente sem essa escolha não mostra
as listas.

**Modelos por apelido** (`fable`, `opus`, `sonnet`, `haiku`), e não pelo nome
completo. O apelido aponta para a versão mais nova da família, então a lista
não envelhece a cada modelo novo. Os esforços são os níveis do `--effort`:
`low`, `medium`, `high`, `xhigh`, `max` (Baixo … Máximo).

**Lembrado como os parâmetros.** `AgentSettings` ganhou `Model` e `Effort`, e
`StartAgentSession.Model`/`Effort` seguem a regra do ADR-033. Informado, vira
o padrão no mesmo `SaveChanges` da sessão. `null` usa o salvo, e vazio é
"Padrão". Por isso o "Abrir Claude Code" do menu da linha (ADR-036) já abre com
a última escolha. Um valor salvo que o agente deixou de oferecer volta a ser
"Padrão", em vez de travar a abertura.

**Conferido antes de gravar.** Valor fora da lista do agente é recusado
(`DomainException`). Modelo e esforço são conferidos antes dos parâmetros e
gravados depois deles, então nada recusado deixa os outros salvos.

**Ordem no comando:** os parâmetros do campo, depois modelo e esforço, depois
o `--permission-mode plan` e o texto (ADR-030):
`claude --dangerously-skip-permissions --model opus --effort high "texto"`.
Com `--model` também no campo, vale o da lista, que vem por último. Em
"Padrão", vale o do campo.

**Limites aceitos:**

- um `claude` antigo que não conheça `--effort` recusa a flag e fecha. O card
  mostra "encerrou logo ao abrir", e "Padrão" volta a abrir;
- nome completo de modelo (`claude-opus-5-5`) continua indo pelo campo
  "Parâmetros". A lista cobre o caso comum.
