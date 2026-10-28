# ADR-093 — A pergunta do Claude (AskUserQuestion) respondida de longe

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | AskUserQuestion, updatedInput, answers, permission-prompt-tool, aprovação de pergunta, cartões de opção |
| **Relações** | relacionado: [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md), [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md) |
| **Código** | `src/MyTaskApp.Application/Agents/HeadlessQuestions.cs`, `src/MyTaskApp.Application/Remote/AgentSessionExecutors.cs`, `src/MyTaskApp.Application/Remote/RemoteApprovals.cs`, `src/MyTaskApp.Agent/Approvals/ApprovalMcpServer.cs`, `src/MyTaskApp.Desktop/ViewModels/RemoteControlViewModel.cs`, `src/MyTaskApp.Mobile.Core/Remote/ApprovalAnswers.cs` |

**Estado:** implementado (fase 3, E4). O caminho do `--permission-prompt-tool`
com o `AskUserQuestion` não está na documentação oficial do Claude Code (ela
descreve o `canUseTool` do SDK); o teste opcional
`ApprovalClaudeTests.TheQuestionOfClaude_ArrivesAtTheApprovalMcp_AndTheAnswerGoesOnInTheUpdatedInput`
confere com o Claude de verdade (`MYTASKAPP_TEST_CLAUDE=1`, com login).

**Contexto:** no modo sem terminal, a ferramenta `AskUserQuestion` fica fora dos
perfis, e o Claude não tem a quem perguntar. Pela documentação oficial, a
chamada chega ao host de permissão (`--permission-prompt-tool`) como qualquer
ferramenta. A resposta volta como `allow` com
`updatedInput: { questions, answers }`.

**Decisão:**

1. `AskUserQuestion` entra nas ferramentas dos três perfis, nunca nas
   liberadas: é sempre perguntada e nunca lembrada (`canRemember = false`).
2. A pergunta vai como uma aprovação `ToolUse`, com `Details.kind = "question"`,
   as perguntas e as opções. O enum `ApprovalKind` é estrito: um valor novo
   pediria versão de protocolo, e uma chave nova no `Details` não pede.
3. A resposta assinada leva as respostas no `TypedConfirmation`, que já é
   assinado: um array JSON, uma resposta por pergunta, na ordem delas. A ordem,
   e não o texto, porque a pergunta vai mascarada ao controlador; o agente
   devolve ao Claude o texto original da pergunta e da opção em
   `updatedInput.answers`, sem tocar no resto da entrada. Mais de uma opção
   vai junta por ", "; a resposta escrita vai como foi escrita (até 500
   caracteres).
4. Negar, deixar vencer ou permitir sem respostas válidas (um controlador
   antigo só sabe permitir) vira negação com "Siga pelo seu julgamento e diga
   o que escolheu." O Claude segue, e a escolha fica na conversa.
5. Desktop e celular mostram as opções, com "Responder" e "Deixar o Claude
   decidir", pelos mesmos textos da Application (`RemoteApprovalTexts`). No
   desktop, opções marcáveis e uma caixa para escrever; no celular, uma folha
   de opções por pergunta, com "Outra resposta…". O controlador antigo vê uma
   aprovação comum.

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
