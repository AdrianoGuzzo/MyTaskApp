# ADR-031 — Vários ambientes por tarefa: um worktree por repositório

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-09-24, commit `04521cb` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskItem.Developments, EnsureDevelopmentCanBegin, ForgetDevelopment, TaskDevelopmentsViewModel, MultipleDevelopments, TaskDevelopmentId, Adicionar repositório |
| **Relações** | revisa: [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md); relacionado: [ADR-029](029-remover-worktree-apaga-pasta.md), [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-035](035-branch-de-origem-padrao-diretorio.md), [ADR-036](036-abrir-claude-code-menu-da-linha.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-039](039-arroba-ambientes-e-arquivos-no-agente.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskItem.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskDevelopmentsViewModel.cs`, `src/MyTaskApp.Infrastructure/Persistence/Configurations/AgentSessionConfiguration.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20260924144354_MultipleDevelopments.cs` |

**Contexto:** o fluxo completo de uma tarefa costuma atravessar mais de um
repositório (o app e a API, o front e o back). Até aqui a tarefa tinha no máximo
um ambiente (`TaskItem.Development`, 1:0..1, ADR-027) e uma sessão de agente
ativa (ADR-030). Para implementar o resto, era preciso criar outra tarefa só
para ter outro worktree.

**Decisão:** a tarefa passa a ter **N ambientes**, um por repositório
(`TaskItem.Developments`). Cada um é o `TaskDevelopment` de sempre: tem worktree,
branch, comandos pós-Worktree e o **seu próprio agente**.

**1:N, e a regra do repositório no domínio.** Em `TaskDevelopments`, o índice
em `TaskItemId` deixou de ser único. A regra "um ambiente por repositório" mora
em `TaskItem.EnsureDevelopmentCanBegin`, e não num índice. No Windows,
`C:\x\Repo` e `c:/x/repo/` são a mesma pasta, e o banco compararia texto. A
regra também evita um erro certo do Git: dois worktrees da mesma tarefa no
mesmo repositório disputariam a mesma branch. A preparação confere a regra
logo depois de validar o repositório (é ali que se sabe qual é) e antes do
fetch, que demora. A recusa aparece como falha dessa etapa.

**Criar, tentar de novo, esquecer.**

- `BeginDevelopment(developmentId, …)` **com id** tenta de novo aquele
  ambiente. O repositório pode mudar, se a pasta estava errada, desde que não
  seja o de outro ambiente.
- **Sem id**, o ambiente que já existe no mesmo repositório (Erro, Removido ou
  Criando) é reaproveitado. Se ele estiver Pronto, é recusa. Se não houver
  nenhum, entra um novo na lista.
- `ForgetDevelopment` tira da lista um ambiente sem worktree (Erro ou
  Removido). A linha e os comandos dele são apagados. A branch fica no
  repositório.
- Como a remoção (ADR-029), esquecer não passa pela guarda da lista principal:
  um checklist arquivado ainda arruma a sua lista.

**Todo caso de uso diz qual ambiente.** `PrepareDevelopment` recebe um
`DevelopmentId` opcional. Os seguintes recebem um obrigatório:
`SetDevelopmentCommands`, `RunDevelopmentCommands`, `InspectWorktree`,
`RemoveWorktree`, `StartAgentSession` e `GetTaskAgentSession`.
`GetTaskDevelopment` virou `GetTaskDevelopments`, que devolve a lista em ordem
de criação (id v7). `TaskDevelopmentView` ganhou `Id` e `TaskId`.

**Um agente por ambiente.** `AgentSession` ganhou `TaskDevelopmentId`, com FK
`SET NULL`: tirar o ambiente da lista não apaga o histórico. Desde este ADR, o
índice parcial único é `IX_AgentSessions_TaskDevelopmentId_Active`. Antes era
por tarefa. Com isso, dois repositórios da mesma tarefa podem ter o Claude
aberto ao mesmo tempo. A remoção do worktree só é segurada pelo agente
**daquele** ambiente. `FocusAgentSession` aceita o ambiente. Sem ele, usa a
sessão ativa mais recente da tarefa.

**Migração.** A migration `MultipleDevelopments` troca os índices, cria a
coluna e liga as sessões antigas ao único ambiente que a tarefa tinha. Esse
`UPDATE` é a última instrução do `Up()`, porque vem depois de o SQLite
reconstruir a tabela para a FK. O `Down()` só funciona enquanto nenhuma tarefa
tiver dois ambientes.

**Tela.**

- A aba Desenvolvimento ganha uma faixa de "abas" de repositório
  (`TaskDevelopmentsViewModel`). Cada aba mostra ícone de estado, nome da pasta
  e branch, e uma bolinha quando o agente daquele ambiente está aberto. Ao lado
  fica "+ Adicionar repositório".
- O painel de antes, formulário, pronto e agente, continua igual e mostra o
  ambiente escolhido. Cada aba é um `TaskDevelopmentViewModel` inteiro.
- A faixa só aparece com algum ambiente gravado: o primeiro repositório é só o
  formulário, como antes.
- O repositório novo é um rascunho. Ele já vem com a **branch e a origem dos
  outros ambientes**: o fluxo que atravessa repositórios costuma usar a mesma
  branch em todos. A pasta fica vazia.
- Se a criação falha e fica gravada, o rascunho vira aquele ambiente, com a
  falha na tela, em vez de ganhar uma aba gêmea.
- Fechar a janela confere todos os ambientes, e não só o da frente: uma
  criação ou comandos em andamento em qualquer um seguram o fechamento.

**Selo da lista.** A linha mostra "● Claude Code ×2" quando há mais de um. O
clique com um agente só foca direto. Com vários, abre um menu "Abrir
terminal: repositório · branch", um item por ambiente.

**Limites aceitos:**

- sem reordenar as abas: a ordem é a de criação;
- um rascunho de repositório por vez;
- a regra do repositório compara o worktree principal, então duas pastas do
  mesmo repositório (dois worktrees dele) contam como um só.
