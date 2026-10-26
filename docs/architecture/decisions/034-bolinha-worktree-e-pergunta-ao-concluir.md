# ADR-034 — Bolinha de worktree na lista, e a pergunta ao concluir

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-09-24, commit `eaa4ceb` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ProbeWorktreesHandler, GetBranchStatusAsync, worktreeDot, commit sem push, Remover o worktree desta tarefa?, Manter worktree, status --porcelain=v2 |
| **Relações** | relacionado: [ADR-029](029-remover-worktree-apaga-pasta.md), [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-036](036-abrir-claude-code-menu-da-linha.md), [ADR-043](043-tag-opcional-no-worktree.md), [ADR-047](047-pr-aberta-da-branch-pelo-gh.md), [ADR-063](063-indicador-sincronizacao-no-cabecalho.md), [ADR-065](065-limpeza-de-worktrees-com-forca.md) |
| **Código** | `src/MyTaskApp.Application/Development/WorktreeSync.cs`, `src/MyTaskApp.Desktop/ViewModels/TodayViewModel.cs`, `src/MyTaskApp.Desktop/Views/TodayView.axaml` |

**Contexto:** na lista de hoje, só dava para saber que uma tarefa tinha
worktree abrindo a tarefa. E concluir a tarefa deixava a pasta no disco sem
ninguém perceber. Muitas vezes ela ainda tinha alteração não commitada ou
commit que nunca foi enviado.

**Decisão:** a linha ganha uma **bolinha à esquerda do título** para cada
tarefa com worktree pronto. A cor diz o estado no Git:

| Estado | Cor | Token |
|---|---|---|
| Alteração não commitada | âmbar | `WidgetCaution` |
| Commit sem push | azul | `WidgetInfo` |
| Commits, todos enviados | verde | `WidgetSuccess` |
| Sem commits ainda | cinza | `WidgetTextMid` |
| Não verificado / pasta sumiu / Git recusou | anel vazio | `WidgetTextLow` |

Com vários repositórios (ADR-031), a bolinha mostra o pior estado. A cor nunca
é o único sinal: o balão do título ganha um bloco "WORKTREE" com uma linha por
repositório ("3 alterações não commitadas · 2 commits sem push"), e a bolinha
tem balão próprio.

**O estado vem do Git, nunca do banco.** O banco só diz quais worktrees
existem: uma consulta a mais em `TodayQuery`, feita de uma vez, nunca uma por
linha. A cor vem de `ProbeWorktreesHandler`, que faz até quatro conferências
em paralelo. Cada conferência é:

- `git status --porcelain=v2 --branch` (`IGitClient.GetBranchStatusAsync`):
  as alterações e a distância até o upstream, numa ida só;
- `rev-list --count` contra a origem da branch: quantos commits ela tem;
- sem upstream, uma comparação com `refs/remotes/origin/{branch}`, porque
  `git push origin x` sem `-u` também é enviar.

A conferência nunca lança. Um repositório com problema vira anel vazio e vai
para o log, não para a faixa de erro.

**Sem piscar.** O quadro carrega sem esperar o Git, e a conferência roda
depois, sem `await`. O `TodayViewModel` guarda a última resposta, e as linhas
novas do refresh de 60 s já nascem com ela. Um contador descarta a resposta de
uma conferência que outra mais nova já superou.

**Concluir pergunta.** Concluir uma tarefa com worktree conclui primeiro,
porque concluir não pode depender do Git. Depois vem a pergunta "Remover o
worktree desta tarefa?", com os botões "Remover" e "Manter worktree". Antes de
perguntar, o app confere de novo, e a pergunta diz o que existe agora:

- alteração não commitada impede a remoção, e isso é avisado antes;
- commit sem push não impede, mas só existe nesta máquina, e isso também é
  avisado.

"Remover" chama o `RemoveWorktreeHandler` de sempre (ADR-029), com as mesmas
travas. O que for recusado aparece na faixa de erro, e a tarefa continua
concluída.

**"Manter" não grava nada.** "Concluída com worktree pronto" já é a resposta. A
linha concluída fica cinza, mas a bolinha não: ela cresce, ganha um halo da
mesma cor e vem com o texto "● Worktree criado · repositório · branch" embaixo
do título. Um clique nesse texto abre a tarefa, onde o worktree se remove.

**Limites aceitos:**

- o remoto comparado sem upstream é sempre `origin`;
- a cor tem até 60 s de atraso para mudanças feitas fora do app.
