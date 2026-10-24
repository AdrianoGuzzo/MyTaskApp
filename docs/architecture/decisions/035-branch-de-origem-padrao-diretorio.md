# ADR-035 — Branch de origem padrão no diretório da etiqueta

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-09-25, commit `d13a2f5` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TagDirectory.DefaultBranch, ListBranchesHandler, FindConfigured, Branch de origem padrão, Suggest, TagDirectoryDefaultBranch |
| **Relações** | relacionado: [ADR-026](026-diretorios-da-etiqueta-alias.md), [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-043](043-tag-opcional-no-worktree.md) |
| **Código** | `src/MyTaskApp.Domain/Tags/TagDirectory.cs`, `src/MyTaskApp.Application/Development/DevelopmentQueries.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20260925171057_TagDirectoryDefaultBranch.cs` |

**Decisão:** o diretório da etiqueta (ADR-026) ganha um campo opcional, "Branch
de origem padrão" (`TagDirectory.DefaultBranch`). Quando o repositório de
"Iniciar implementação" (ADR-027) é essa pasta, a branch já vem escolhida no
combo "Branch de origem".

**Só uma preferência, sem perguntar ao Git ao salvar.** O domínio só confere o
que dá para saber sem o repositório: sem espaços e com no máximo 255
caracteres. Se a branch existe, isso só se sabe na hora de usar. Se o
repositório não tem a branch, a escolha cai na sugestão de sempre
(`ListBranchesHandler.Suggest`), e um aviso embaixo do combo diz que a branch
padrão não foi encontrada.

**Como o nome vira branch** (`ListBranchesHandler.FindConfigured`): primeiro o
nome curto exato, e assim `develop` acha a local e `origin/develop` acha a
remota. Depois o mesmo nome sem diferenciar maiúsculas. Por último, sem local,
`develop` acha a remota, de `origin` primeiro.

**Qual diretório vale.** A tela já recebe os diretórios das etiquetas da tarefa
para o autocomplete do `@alias`. Vale o diretório com o mesmo caminho digitado
ou o do worktree principal. Com a mesma pasta em duas etiquetas, vale a
primeira que tiver uma branch padrão.

**Ordem da escolha:** a branch que já estava escolhida (recarga da mesma
pasta), depois a origem da tentativa anterior deste ambiente, depois a branch
padrão do diretório, depois a origem dos outros ambientes da tarefa (ADR-031),
depois a sugestão. A origem dos outros ambientes fica abaixo da branch padrão
porque veio de outro repositório. A padrão foi cadastrada para este.
