# ADR-065 — Limpeza de worktrees: os sem tarefa e os de tarefa encerrada, com força

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-10-15, commit `b54529e` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | CleanWorktreesHandler, WorktreeRemoveForce, git worktree remove --force, WorktreeEraser, IsSettled, Gerenciamento de dados, worktree sem tarefa |
| **Relações** | exceção a: [ADR-027](027-aba-desenvolvimento-worktree-git.md); relacionado: [ADR-029](029-remover-worktree-apaga-pasta.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-060](060-banco-do-ambiente-variavel-database.md) |
| **Código** | `src/MyTaskApp.Application/Development/CleanWorktrees.cs`, `src/MyTaskApp.Application/Development/RemoveWorktree.cs`, `src/MyTaskApp.Infrastructure/Git/GitClient.cs`, `src/MyTaskApp.Desktop/Views/DataManagementWindow.axaml` |

**Contexto:** o worktree sai do disco por dois caminhos: pela aba
Desenvolvimento ou pela pergunta feita ao concluir a tarefa (ADR-034). O
resto se acumula:
- o worktree de quem respondeu "Manter";
- o de tarefa arquivada, cancelada ou na lixeira;
- o de tarefa apagada de vez. O purge (§7) apaga as linhas de
  `TaskDevelopments` em cascata, e a pasta e o registro no Git ficam sem
  ninguém que aponte para eles;
- o criado fora do app, à mão ou por um agente (`.claude/worktrees`).

O pedido foi uma faxina: apagar todo worktree que não está ligado a uma
tarefa, ou cuja tarefa terminou. Ela apaga inclusive os worktrees com
alterações pendentes.

**Decisão:** uma aba **Worktrees** em *Gerenciamento de dados*, ao lado de
Lixeira. Ela lista os candidatos, todos marcados, e **Limpar N worktrees**
apaga os marcados com `git worktree remove --force`, depois de uma confirmação
irreversível que conta quantos têm alterações a perder. A branch fica no
repositório, como em toda remoção do app.

## Onde procurar

A varredura junta duas fontes:
- os **diretórios mapeados das etiquetas** neste computador
  (`ListMappedDirectoryPathsAsync`), cada um levado à raiz do repositório
  com `rev-parse --show-toplevel`. O que não é repositório fica de fora;
- os **repositórios das tarefas** (`TaskDevelopment.RepositoryPath`).

As duas fontes são deduplicadas pelo caminho canônico. Em cada repositório
roda um `git worktree list --porcelain`. Um repositório em que o Git falha
fica fora da lista, e a aba diz qual.

## Quem entra

- **Sem tarefa:** nenhum ambiente pronto ou em criação aponta para a pasta.
- **De tarefa encerrada:** concluída, arquivada ou na lixeira, pelo
  `TaskItem.Lifecycle`. A tarefa cancelada não tem estado próprio no ciclo de
  vida: tudo cancelado não tem `ConcludedAt` e continua `Active`. O domínio
  ganhou `TaskItem.IsSettled` (nenhuma ocorrência pendente) só para esse caso,
  em vez de a consulta remontar a regra.
- **O ambiente pronto de tarefa encerrada que o Git já esqueceu** (sobra de
  pasta, ou pasta sumida) também entra. A remoção de sempre resolve os dois
  casos.

**Nunca entram:**
- o worktree principal e o repositório bare;
- o worktree de tarefa em andamento;
- o ambiente em criação;
- a pasta que é ela mesma um diretório mapeado de etiqueta. O usuário a
  cadastrou, então ela está em uso, mesmo sem tarefa.

Cada linha traz a mesma sonda da bolinha da lista (`ProbeWorktreesHandler`):
alterações não commitadas e commits sem push. No worktree sem tarefa, os
commits contam a partir da branch do worktree principal.

## Marcados, menos dois

- **Travado com `git worktree lock`:** vem desmarcado. Alguém pediu
  explicitamente que o Git não mexesse nele. Marcado, sai com
  `--force --force`, que é como o Git aceita passar por cima da trava.
- **Com agente de IA do app aberto (ADR-030):** não pode ser marcado. A
  remoção recusaria de qualquer jeito.

## A exceção ao "sem `--force`"

O ADR-027 proíbe operação destrutiva no `IGitClient`, e a remoção comum
continua recusando worktree com alterações. A limpeza é a exceção, e só ela
passa `WorktreeRemoveForce` além de `None`. Ela se apoia em três coisas, e só
nelas:
1. o usuário vê cada worktree, com as alterações de cada um;
2. ele escolhe quais saem;
3. a confirmação é irreversível e diz quantos têm trabalho a perder.

O commit sem push não se perde, porque a branch fica.

## Antes de cada um, de novo

Entre a varredura e o clique, o mundo muda. Por isso o comando confere de
novo, worktree por worktree, e o que não passa fica com o motivo, sem parar
os outros:
- **quem é o dono agora:** uma tarefa reaberta, ou que adotou o worktree sem
  dono, faz o worktree ficar. Uma tarefa apagada de vez depois da varredura
  faz o worktree ser tratado como sem tarefa;
- **se é o principal:** nunca sai, mesmo que um candidato montado à mão
  aponte para ele;
- **quem está com a pasta aberta** (`IDirectoryRemover.FindLockersAsync`, a
  mesma detecção do ADR-029). Sem força, o Git recusaria. Com força, ele
  apaga os arquivos debaixo de um terminal ou agente rodando ali e só deixa a
  pasta vazia presa. Com alguém segurando, nada é apagado, e a mensagem diz
  quem. Na limpeza em lote, nenhum processo é encerrado.

O ambiente de tarefa sai pelo mesmo `RemoveWorktreeHandler` da aba
Desenvolvimento, com `Force`: fica `Removed`, e a recusa por agente aberto
continua valendo. O worktree sem tarefa sai pelo mesmo miolo
(`WorktreeEraser`), que remove o registrado, apaga a sobra ou faz o prune. O
banco copiado do ambiente (ADR-060) não é apagado, como na remoção comum.

## A varredura não roda ao abrir a janela

Ela chama o Git em cada repositório. Por isso roda quando a aba aparece e em
**Procurar**, não a cada abertura do Gerenciamento de dados. Depois de
limpar, a lista é varrida de novo, e a lista Hoje é recarregada para a
bolinha e o "Worktree criado" sumirem na hora.

## Alternativas

- **Limpar sozinho, ao concluir ou no purge.** Apagar alterações não
  commitadas sem o usuário ver a lista é exatamente o que o ADR-027 existe
  para impedir.
- **Um item de menu que apaga tudo de uma vez.** Um worktree criado à mão
  para um experimento não teria como ser poupado.
- **Só os worktrees criados pelo app.** O pedido incluía os criados fora
  dele. E a tarefa apagada de vez já não deixa rastro de que o worktree era
  do app.
- **Apagar a branch junto.** Commit sem push se perderia de verdade. Com a
  branch, a pasta some e o trabalho fica recuperável.

## Consequências

- A janela de Gerenciamento de dados ficou mais larga (780) para as cinco abas
  caberem numa linha.
- `IGitClient.RemoveWorktreeAsync` ganhou o parâmetro `force`. O padrão é sem
  força, e a revisão de segurança do Git continua olhando um lugar só.

**Testes:**

- **Domain:** `IsSettled` com cancelar, reabrir e concluir.
- **Application:**
  - o principal e o de tarefa ativa ficam fora;
  - o motivo certo para cada estado da tarefa;
  - repositório alcançado duas vezes;
  - a pasta mapeada como diretório;
  - alterações reportadas, travado e agente aberto;
  - o ambiente que o Git esqueceu;
  - o repositório que falha;
  - a remoção forçada de worktree com alterações, e a dupla força no travado;
  - a pasta aberta pulada sem encerrar nada;
  - a tarefa reaberta depois da varredura;
  - o principal recusado.
- **Infrastructure:**
  - os argumentos `--force` e `--force --force`;
  - com o Git de verdade, um worktree criado à mão com arquivo não commitado e
    o de uma tarefa concluída saem, e as duas branches ficam.
- **Desktop:**
  - o que vem marcado;
  - os textos de cada linha;
  - marcar e desmarcar todos;
  - recusar não apaga;
  - a pergunta conta o que se perde;
  - confirmar varre de novo e avisa a lista;
  - a limpeza parcial;
  - a aba que só varre quando aparece.
