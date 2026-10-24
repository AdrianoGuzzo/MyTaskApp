# ADR-043 — Tag opcional: o worktree parte de uma versão

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-10-02, commit `8f7a371` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SourceTag, refs/tags, ListTagsAsync, for-each-ref, Tag (opcional), hotfix, versão antiga |
| **Relações** | relacionado: [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-035](035-branch-de-origem-padrao-diretorio.md), [ADR-044](044-versionamento-com-release-please.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Git/GitClient.cs`, `src/MyTaskApp.Domain/Tasks/TaskDevelopment.cs`, `src/MyTaskApp.Application/Development/PrepareDevelopment.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20261002212558_TaskDevelopmentSourceTag.cs` |

**Contexto:** "Iniciar implementação" (ADR-027) sempre parte da ponta da branch
de origem. Para depurar um cliente que está numa versão antiga, o usuário
precisava do código daquela versão, e não do que está na `main` hoje.

**Decisão:** abaixo de "Branch de origem", um combo **"Tag (opcional)"**. Ele
começa em "Nenhuma — a ponta da branch", e o fluxo é o de sempre. Com uma tag
escolhida, a branch nova nasce nela:
`git worktree add --no-track -b {nova} {caminho} refs/tags/{tag}`.

- **Sempre uma branch nova, nunca HEAD destacado.** O resto do app conta com
  uma branch no worktree: a conferência depois de criar, a bolinha da lista
  (ADR-034), o agente. E quem depura costuma querer commitar o conserto. Um
  nome como `hotfix/1.4.2-cliente-x` fica a cargo do usuário, no campo de
  sempre.
- **A tag manda no ponto de partida; a branch continua sendo a origem.**
  `SourceBranch` grava a branch escolhida, como antes. A tag vai para uma
  coluna nova, `TaskDevelopments.SourceTag` (anulável, migration
  `TaskDevelopmentSourceTag`). O cartão "Ambiente pronto" mostra "Tag de
  origem" só quando há uma.
- **Com tag, a origem não é atualizada.** O fast-forward da branch de origem
  serve para partir da ponta mais nova, e aqui ela não entra no worktree.
  Atualizá-la só arriscaria recusar por nada (divergiu, alterações locais). A
  etapa aparece como "pulada", com o nome da tag.
- **A tag é conferida depois do fetch**, na etapa da origem, e só pelo nome
  exato. Uma tag que não existe para ali, sem gravar nada.
- **Sempre `refs/tags/{tag}`, nunca o nome curto.** Uma branch `v1.0.0` ao
  lado da tag `v1.0.0` deixaria o nome curto ambíguo. Pelo mesmo motivo, a
  listagem lê `%(refname)` e não `%(refname:short)`, que vira `tags/v1.0.0`
  nesse caso.
- **Branch que já existe vence a tag.** O fluxo reaproveita a branch existente
  (ADR-027), e ela tem o código dela. O aviso da etapa diz "…e não a tag X", e
  `SourceTag` não é gravada, para o registro não dizer uma origem que não é.
- **A bolinha conta a partir da tag.** A consulta da tela Hoje entrega
  `refs/tags/{tag}` como base da comparação. Contar contra a `main` mostraria
  como "commits" tudo o que a versão não tem.

**Listagem.** `IGitClient.ListTagsAsync` roda
`git for-each-ref --sort=-v:refname --format=%(refname) refs/tags`: a versão
mais nova primeiro, em ordem de versão (`v1.10.0` antes de `v1.9.0`). Quem
procura a versão de um cliente procura pelo número. As tags chegam junto com as
branches, em `ListBranches`.

**Qual tag já vem escolhida.** Numa recarga da mesma pasta, a que estava. Num
"tentar de novo", a da tentativa anterior. Fora isso, nenhuma. Nem a branch
padrão do diretório (ADR-035) nem os outros ambientes da tarefa (ADR-031)
sugerem tag: tag é exceção, e cada repositório tem as suas versões.

**Armadilha: o fetch não ganhou `--tags`.** Com `--tags`, o fetch passa a
buscar `refs/tags/*` explicitamente. Uma tag que alguém moveu no remoto é
recusada ("would clobber existing tag"), e o fetch sai com erro. Como o fetch é
obrigatório, isso travaria todo "Iniciar implementação" daquele repositório,
com ou sem tag. O fetch de sempre já traz as tags que apontam para commits das
branches buscadas (o tag following do Git).

**Limites aceitos:**

- uma tag que só existe no remoto e aponta para um commit fora de qualquer
  branch remota não é trazida pelo fetch, então não aparece no combo;
- uma tag movida no remoto não é atualizada localmente; o Git não reescreve
  tags sem `--force`, e a porta não tem `--force` (ADR-027);
- o combo lista todas as tags, sem filtro por branch.
