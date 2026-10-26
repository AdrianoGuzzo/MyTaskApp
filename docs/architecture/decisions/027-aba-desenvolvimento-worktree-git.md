# ADR-027 — Aba Desenvolvimento: a tarefa vira um worktree Git

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-09-23, commit `57bb5ab` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskDevelopment, IGitClient, GitClient, ProcessRunner, WorktreePathPlanner, GitLocator, Iniciar implementação, --no-track |
| **Relações** | revisto por: [ADR-031](031-varios-ambientes-por-tarefa.md); exceção aberta por: [ADR-065](065-limpeza-de-worktrees-com-forca.md); relacionado: [ADR-026](026-diretorios-da-etiqueta-alias.md), [ADR-028](028-comandos-pos-worktree-e-globais.md), [ADR-029](029-remover-worktree-apaga-pasta.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-035](035-branch-de-origem-padrao-diretorio.md), [ADR-043](043-tag-opcional-no-worktree.md), [ADR-045](045-jira-issue-como-contexto-da-tarefa.md), [ADR-047](047-pr-aberta-da-branch-pelo-gh.md), [ADR-060](060-banco-do-ambiente-variavel-database.md), [ADR-064](064-repositorio-git-do-diretorio-clone.md), [ADR-083](083-diagnostico-instalar-e-banco-docker.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskDevelopment.cs`, `src/MyTaskApp.Application/Development/IGitClient.cs`, `src/MyTaskApp.Infrastructure/Git/GitClient.cs`, `src/MyTaskApp.Application/Development/WorktreePathPlanner.cs` |

**Decisão:** a janela da tarefa ganhou abas, **Anotação** e **Desenvolvimento**.
Na segunda, o usuário:

1. escolhe um repositório, pelo `@alias` das etiquetas da tarefa (ADR-026) ou
   digitando o caminho;
2. escolhe a branch de origem, local ou remota, carregada do próprio repositório;
3. dá nome à branch nova. A sugestão é `feature/{slug-do-título}`, porque a
   tarefa só tem Guid, e um pedaço de Guid no nome da branch ninguém lê.

"Iniciar implementação" faz o fetch, atualiza a origem só por fast-forward, cria
a branch e o worktree numa **pasta irmã** do repositório
(`../{projeto}-{branch-sanitizada}`) e grava o ambiente na tarefa. Pronto, a aba
oferece abrir a pasta, abrir um terminal nela, copiar o caminho e remover o
worktree.

## O ambiente na tarefa: tabela própria, 1:0..1, dentro do agregado

> Desde o ADR-031, a relação é 1:N, com um ambiente por repositório. O índice
> em `TaskItemId` deixou de ser único, e o acesso é `TaskItem.Developments`. O
> resto desta seção continua valendo para cada ambiente.

`TaskDevelopment` guarda `RepositoryPath`, `SourceBranch`, `Branch`,
`WorktreePath`, `Status`, `CreatedAt`, `StatusChangedAt` e `FailureReason`. A
tabela é `TaskDevelopments`, com índice único em `TaskItemId`, e o acesso é
`TaskItem.Development`.

- **Não é owned.** Um owned opcional tem o mesmo "ausente ou tudo nulo?" que o
  lembrete já resolveu com `IsRequired` (ver `TaskItemConfiguration`). Além
  disso, a tabela `Tasks` é varrida pela tela Hoje e pelas varreduras, e o
  escopo futuro (commit, push, PR) cresce aqui sem mover dado de coluna.
- **Guarda cópias dos caminhos, e não o id do `TagDirectory`.** O alias é atalho
  (ADR-026). Renomear ou excluir o diretório da etiqueta não pode deixar um
  worktree em uso sem endereço.
- **Não guarda o que o Git sabe dizer:** alterações, commits, se a branch
  existe. Tudo isso é perguntado na hora.
- **"NotStarted" não é um status.** É `Development == null`.
- **Começar de novo muta a mesma linha.** Isso vale depois de Error, de
  Removed ou de um Creating órfão. Trocar a instância faria o EF inserir a nova
  antes de apagar a velha, e o índice único recusaria.
- **Guarda de estado.** Só `BeginDevelopment` passa por
  `RefuseWhenOutOfTheMainList`. Pronto, falha e remoção registram fatos do
  disco: a varredura pode arquivar o checklist no meio de um `worktree add`, e
  um checklist arquivado ainda precisa poder limpar o seu worktree.

## Git atrás de uma porta, e processo atrás de outra

```
Desktop ─ IUseCaseRunner ─► PrepareDevelopment / StartDevelopment / RemoveWorktree …
                                   │
                              IGitClient (Application)
                                   │
                              GitClient (Infrastructure) ─► IProcessRunner ─► git
```

- **`IGitClient` tem um método por pergunta ou operação**, nunca "rode estes
  argumentos". Montar linha de comando é assunto de um arquivo só, e é ele que
  se revisa quando o assunto é segurança.
- **Não existe operação destrutiva na porta.** Não há reset, clean, stash,
  checkout forçado nem `--force` em lugar nenhum. A origem só anda por
  fast-forward (`merge --ff-only` ou `fetch . upstream:refs/heads/b` sem `+`).
  A remoção é `git worktree remove` sem força, e o Git recusa se houver
  alterações. O que o Git deixa na pasta quando algo a segura fica com o
  ADR-029, fora desta porta.
- **`ProcessRunner` é a única porta do app para processos.** Ela usa
  `ArgumentList` (nunca uma string montada), sem shell, sem janela e com a
  entrada fechada. Lê stdout e stderr ao mesmo tempo, tem timeout e, ao
  cancelar, mata a árvore inteira. Um caminho com espaço ou uma branch com `&`
  não têm como virar outro comando.
- **O terminal e a pasta** ficam em `IShellLauncher` (Desktop):
  - a pasta abre pelo `Launcher` do Avalonia (Explorer, xdg-open, Finder);
  - o terminal tenta, em ordem: `wt -d` e depois o PowerShell com
    `WorkingDirectory` no Windows; gnome-terminal, konsole, xfce4-terminal,
    x-terminal-emulator e xterm no Linux; `open -a Terminal` no macOS.

## Duas metades, e o ViewModel no meio

- **`PrepareDevelopment`** valida Git, pasta e repositório, faz o fetch, valida
  a origem, olha as alterações, atualiza a origem, valida o nome e calcula o
  caminho.
  - Não grava nada e pode ser cancelado.
  - Devolve um `DevelopmentPlan`.
- **`StartDevelopment`** cria o worktree e grava o resultado.
- **Por que duas.** Entre as duas pode haver uma pergunta: a pasta calculada já
  existe. As opções são "usar o worktree existente" (só se for um worktree
  deste repositório), "escolher outro caminho" (já sugerindo `-2`) ou cancelar.
  Um caso de uso só teria de segurar escopo e DbContext abertos esperando um
  clique.
- **A ordem do Start é a do rastro:**
  1. grava `Creating`;
  2. roda `git worktree add --no-track -b nova caminho origem`. Se a branch
     já existe, reaproveita em vez de recusar: a local ganha checkout
     (`git worktree add caminho branch`); sem local, a remota vira local
     acompanhando-a (`git worktree add --track -b branch caminho remoto/branch`,
     preferindo o remoto da origem, depois `origin`);
  3. confere a branch em checkout;
  4. grava `Ready`.

  Falhou, grava `Error` com o motivo e relança. Se o app cai no meio, a tarefa
  diz "criação interrompida". Depois de gravar `Creating` não há cancelamento:
  matar o `worktree add` no meio deixa meio worktree registrado.
- **Progresso chega por `IProgress<DevelopmentProgress>` síncrono.** Os casos
  de uso não usam `ConfigureAwait(false)`, então o aviso já chega na thread de
  UI. O `Progress<T>` do BCL postaria para depois, e a lista andaria fora de
  ordem com o resultado.
- **Falha é `DevelopmentStepException : DomainException`.** Ela traz a etapa, o
  `GitCommandResult` (comando, exit code, stdout, stderr) e as alterações
  locais. A tela mostra a mensagem em pt-BR e deixa o erro real do Git em "Ver
  detalhes" e "Copiar detalhes".

## Alterações locais: bloqueiam só quando atualizar mexeria nelas

- **Bloqueia** quando a origem é uma branch local em checkout, atrás do
  upstream, e o worktree em que ela está tem alterações. Atualizá-la mexeria
  nesses arquivos. A tela lista as alterações e oferece abrir um terminal no
  repositório.
- **Divergiu** (commits dos dois lados): para e explica, sem tocar em nada.
- **Nos outros casos** (origem remota, ou branch local fora de checkout), o
  `worktree add` não toca no working tree principal. As alterações viram só um
  aviso: "N alterações não vão para o novo worktree".

## O caminho do worktree

`WorktreePathPlanner.Plan(repo, branch)` =
`Path.Combine(pai, $"{projeto}-{SanitizeSegment(branch)}")`.

- O projeto é o nome do **worktree principal** (o primeiro de
  `git worktree list`), mesmo que o usuário tenha escolhido uma subpasta.
- A sanitização:
  - troca `/`, `\`, `<>:"|?*`, espaços e caracteres de controle por `-`;
  - reduz `..` a `.` e junta hífens repetidos;
  - apara ponto, espaço e hífen nas pontas;
  - põe `-wt` em nome reservado do Windows;
  - limita a 80 caracteres.

  As regras são as do Windows em qualquer sistema, para o mesmo repositório dar
  a mesma pasta em qualquer máquina. Nada existente é sobrescrito nem apagado.

## Git ausente

`git --version` sem resposta mostra as instruções do sistema. O app não instala
nada.

- **Windows:** `winget install --id Git.Git -e --source winget`.
- **Linux:** apt, dnf ou pacman, escolhido pelo `ID`/`ID_LIKE` de
  `/etc/os-release`. Sem reconhecer a distribuição, mostra os três.
- **macOS:** `xcode-select --install` ou `brew install git`.

Cada comando tem "Copiar comando". Há também o link para git-scm.com e
"Verificar novamente". A versão mínima é 2.17, a que trouxe
`git worktree remove`.

**Armadilhas:**

- **PATH velho.** O PATH de um processo é copiado quando ele nasce. Quem
  instala o Git com o app aberto continuaria vendo "não encontrado" até
  reiniciar. `GitLocator` relê o PATH de máquina e de usuário do registro a cada
  procura, olha as pastas de instalação conhecidas e executa sempre por
  **caminho absoluto**.
- **Credenciais.** `GIT_TERMINAL_PROMPT=0` sozinho não impede o Git Credential
  Manager de esperar; `GCM_INTERACTIVE=Never` também é preciso. Sem os dois, um
  fetch que pede senha fica parado até o timeout.
- **`--no-track`.** Partindo de `origin/develop`, o `worktree add -b` faria a
  branch nova acompanhar `origin/develop`, e o primeiro `push` iria para lá. O
  teste de integração com Git real confere que o upstream fica vazio.
- **Formato dos caminhos.** O Git escreve `C:/x/y`, o Windows `C:\x\y`, e no
  Windows maiúscula não importa. Toda comparação passa por
  `WorktreePathPlanner.SamePath`.
- **Branch já existente.** No Windows as refs soltas são arquivos, então
  `Feature/X` e `feature/x` disputam o mesmo nome. E `feature` impede
  `feature/x`. Por isso a checagem ignora maiúsculas e olha o conflito
  arquivo/pasta, em vez de um `show-ref` exato. A existente é reaproveitada na
  grafia dela; só é recusada se já estiver aberta em outro worktree (o Git
  recusaria o checkout). Aberta no próprio caminho planejado, vira o conflito
  de caminho, que oferece "Usar Worktree existente".
- **`fetch . a:b`** é recusado se `b` estiver em checkout em **qualquer**
  worktree. Quem decide entre ele e `merge --ff-only` é o `worktree list`, e
  não só o worktree principal.
- **`GIT_OPTIONAL_LOCKS=0`**, para o `status` não disputar o `index.lock` com a
  IDE aberta no mesmo repositório. **`LC_ALL=C`**, para o stderr chegar em
  inglês, que é o que a tradução reconhece. O texto original vai sempre para os
  detalhes.
- **Linhas no checkout.** O teste de integração não compara fim de linha: o
  checkout segue o `core.autocrlf` de quem roda.

**Limites aceitos:**

- sem rede, não há worktree, porque o fetch é obrigatório;
- sem submódulos e sem LFS;
- o worktree é sempre pasta irmã;
- a detecção de terminal no Linux é heurística;
- remover o worktree não apaga a branch, e isso é de propósito;
- Git 2.17 ou mais novo.

O escopo futuro (abrir na IDE, status, commit, push, PR) entra como métodos
novos no `IGitClient` e colunas novas em `TaskDevelopments`. A relação 1:N,
com um ambiente por repositório, veio com o ADR-031.

## Divergências com o código (conferido em 2026-10-24)

- 'Não existe operação destrutiva na porta... nem --force em lugar nenhum': o GitClient hoje tem WorktreeRemoveForce.Force/ForceLocked com 'worktree remove --force [--force]' (src/MyTaskApp.Infrastructure/Git/GitClient.cs), exceção aberta pelo ADR-065 para a limpeza de worktrees.
