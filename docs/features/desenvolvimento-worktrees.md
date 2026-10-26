# Desenvolvimento: tarefa → worktree → agente

| | |
|---|---|
| **Status** | Implementado. No Linux não há indicação de quem segura a pasta nem terminal real para "Executar aplicação". |
| **Decisões** | [ADR-027](../architecture/decisions/027-aba-desenvolvimento-worktree-git.md) worktree · [ADR-028](../architecture/decisions/028-comandos-pos-worktree-e-globais.md) comandos pós-worktree · [ADR-029](../architecture/decisions/029-remover-worktree-apaga-pasta.md) remover · [ADR-031](../architecture/decisions/031-varios-ambientes-por-tarefa.md) vários ambientes · [ADR-034](../architecture/decisions/034-bolinha-worktree-e-pergunta-ao-concluir.md) bolinha · [ADR-035](../architecture/decisions/035-branch-de-origem-padrao-diretorio.md) branch padrão · [ADR-043](../architecture/decisions/043-tag-opcional-no-worktree.md) tag · [ADR-047](../architecture/decisions/047-pr-aberta-da-branch-pelo-gh.md) PR · [ADR-051](../architecture/decisions/051-comandos-rapidos-no-worktree.md) comandos rápidos · [ADR-055](../architecture/decisions/055-comando-so-do-diretorio.md) comando do diretório · [ADR-060](../architecture/decisions/060-banco-do-ambiente-variavel-database.md) banco do ambiente · [ADR-065](../architecture/decisions/065-limpeza-de-worktrees-com-forca.md) limpeza · [ADR-066](../architecture/decisions/066-comandos-globais-locais-por-padrao.md) comandos e sincronização |
| **Código** | `src/MyTaskApp.Application/Development/`, `src/MyTaskApp.Application/Commands/`, `src/MyTaskApp.Application/QuickCommands/`, `src/MyTaskApp.Domain/Commands/`, `src/MyTaskApp.Infrastructure/Git/`, `src/MyTaskApp.Infrastructure/GitHub/`, `src/MyTaskApp.Desktop/ViewModels/TaskDevelopmentViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/WorktreeCleanupViewModel.cs` |
| **Testes** | `tests/MyTaskApp.Application.Tests/Development/`, `tests/MyTaskApp.Infrastructure.Tests/Git/GitWorktreeIntegrationTests.cs`, `tests/MyTaskApp.Desktop.Tests/ViewModels/TaskDevelopmentViewModelTests.cs` |

A janela da tarefa tem a aba **Desenvolvimento**:

1. **Iniciar implementação.** Escolha o repositório (por `@alias` ou caminho),
   a branch de origem e, se quiser, uma **tag** para partir de uma versão
   antiga. O app faz o `fetch`, cria a branch (sugestão:
   `feature/{slug-do-título}`, ou a convenção do [Jira](jira.md)) e um
   **worktree** numa pasta irmã do repositório.
2. **Comandos pós-worktree.** Uma lista ordenada, como `dotnet restore` e
   `npm ci`, roda em sequência com o output ao vivo e para no primeiro erro.
   Os **comandos globais** (`@restore` → `dotnet restore`) ficam em
   *☰ → Comandos globais…*. Com a sincronização ligada, cada um fica só no
   computador onde foi criado, a menos que você marque **Compartilhar entre
   computadores**.
3. **Comandos rápidos.** Ligue um comando global a um diretório da etiqueta
   (*☰ → Etiquetas… → Comandos*) e ele vira um botão **▶** no worktree pronto
   de toda tarefa daquele repositório: "▶ Executar aplicação" abre um terminal
   no worktree rodando `dotnet run`; "▶ Testes" roda escondido e mostra o
   resultado. Variáveis como `{worktree}` e `{branch}` são preenchidas
   sozinhas, e parâmetros e confirmação são perguntados antes. Comando que só
   faz sentido num repositório não precisa ser global: **+ Novo comando** cria
   um comando só daquele diretório, que não aparece em Comandos globais nem em
   outros diretórios.
4. **Vários ambientes por tarefa**, um worktree por repositório (o app e a
   API, o front e o back). Cada ambiente pode receber uma cópia de banco
   ([operações de banco](../database-operations.md#copiar-um-banco-para-o-ambiente-da-tarefa)),
   exposta aos comandos como `{database}`.
5. **Bolinha de estado Git na linha:** âmbar quando há alteração não
   commitada, azul quando há commit sem push e verde quando tudo foi enviado.
   Ao concluir a tarefa, o app pergunta o que fazer com o worktree.
6. **PR aberta da branch.** Se a branch já existe e tem PR aberta no GitHub, o
   formulário mostra **"PR #123 aberta ↗"**, um link que abre a PR. Os balões
   da aba do repositório e da lista Hoje também mostram a PR. Usa o
   [GitHub CLI](https://cli.github.com/) (`gh`) com o seu login. Se ele faltar,
   o app mostra como instalar.
7. **Remover worktree** apaga a pasta. Se algum processo a estiver segurando,
   o app mostra qual.
8. **Limpeza de worktrees.** Em *☰ → Gerenciamento de dados… → Worktrees*, o
   app lista os worktrees que não pertencem a nenhuma tarefa em andamento:
   - os **sem tarefa**: a tarefa foi apagada de vez, ou o worktree foi criado
     à mão ou por outra ferramenta;
   - os de tarefa **concluída, cancelada, arquivada ou na lixeira**.

   A busca cobre os repositórios dos diretórios das etiquetas e os das
   tarefas. Cada linha mostra as alterações não commitadas e os commits sem
   push, e vem marcada. Desmarque o que quiser manter e clique em
   **Limpar N worktrees**. A limpeza apaga **inclusive as alterações não
   commitadas** (é a única exceção ao "sem `--force`" do ADR-027), e as
   branches continuam no repositório.

   Nunca entram na limpeza:
   - o worktree principal;
   - um worktree com um agente de IA aberto;
   - uma pasta aberta num terminal ou editor. Ela fica, com o nome de quem a
     segura;
   - a pasta mapeada num diretório de etiqueta e um worktree ainda em criação
     (`CleanWorktrees.cs`).

   Worktree travado com `git worktree lock` aparece desmarcado.

O agente de IA dentro do worktree: [Claude Code por tarefa](claude-code.md).
