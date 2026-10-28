# ADR-093 — A pergunta do Claude (AskUserQuestion) respondida de longe

| | |
|---|---|
| **Status** | Proposto |
| **Implementação** | Planejado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | AskUserQuestion, updatedInput, answers, permission-prompt-tool, aprovação de pergunta, cartões de opção |
| **Relações** | relacionado: [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md), [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md) |
| **Código** | (planejado) `src/MyTaskApp.Application/Remote/AgentSessionExecutors.cs`, `src/MyTaskApp.Agent/Approvals/ApprovalMcpServer.cs` |

**Estado:** proposto (fase 3, E4). Nada implementado.

**Contexto:** no modo sem terminal, a ferramenta `AskUserQuestion` fica fora dos
perfis, e o Claude não tem a quem perguntar. Pela documentação oficial, a
chamada chega ao host de permissão (`--permission-prompt-tool`) como qualquer
ferramenta. A resposta volta como `allow` com
`updatedInput: { questions, answers }`.

**Decisão (proposta):**

1. `AskUserQuestion` entra nos perfis, sempre perguntada, nunca lembrada.
2. A pergunta vai como uma aprovação `ToolUse`, com `Details.kind = "question"`,
   as perguntas e as opções. O enum `ApprovalKind` é estrito: um valor novo
   pediria versão de protocolo, e uma chave nova no `Details` não pede.
3. A resposta assinada leva as respostas escolhidas. O agente as devolve ao
   Claude em `updatedInput.answers`, sem tocar no resto da entrada.
4. Desktop e celular mostram cartões com as opções, pelos mesmos textos
   compartilhados da Application. O controlador antigo vê a aprovação comum e
   pode negar.

**Por quê:** usa só a interface oficial do host de permissão, e o caminho
seguro que já existe (assinatura, prazo, uso único).

## Alternativas

- **Um tipo de aprovação novo no enum.** Quebra o controlador antigo (JSON
  estrito).
- **Responder pelo prompt seguinte.** O Claude já terminou o turno sem a
  resposta; perde o contexto da pergunta.

## Consequências

- A conversa não trava esperando alguém na frente da máquina.
- Sem resposta em 10 minutos, a pergunta é negada, como as outras.

## Fora de escopo, de propósito

- Perguntas em texto livre fora do `AskUserQuestion`.
