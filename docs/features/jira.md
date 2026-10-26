# Jira: a issue vira o contexto da tarefa

| | |
|---|---|
| **Status** | Implementado. Precisa do app OAuth registrado e das credenciais no build ([como registrar](../jira-oauth-app.md)). |
| **Decisões** | [ADR-045](../architecture/decisions/045-jira-issue-como-contexto-da-tarefa.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Jira/`, `src/MyTaskApp.Application/External/`, `src/MyTaskApp.Domain/External/`, `src/MyTaskApp.Desktop/ViewModels/IssueSuggestionsViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskIssueViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/IntegrationsViewModel.cs` |
| **Testes** | `tests/MyTaskApp.Infrastructure.Tests/Jira/`, `tests/MyTaskApp.Application.Tests/External/`, `tests/MyTaskApp.Desktop.Tests/ViewModels/TodayIssueCaptureTests.cs` |

O Jira continua sendo onde a issue mora. O MyTaskApp busca a issue enquanto
você escreve, guarda a chave, o título e o link na tarefa e dá o nome da
branch.

- **Conectar com um clique** em *☰ → Integrações…*: o navegador abre na
  Atlassian, você autoriza e volta conectado. E-mail + API token fica como
  caminho avançado. O token vai para o cofre do sistema (DPAPI no Windows,
  `secret-tool` no Linux); a conexão sem segredo fica em `jira.json`, na pasta
  de dados.
- **Buscar ao digitar o título.** Escreva parte do título na caixa de captura,
  e as issues parecidas aparecem embaixo: tipo, chave, título e status. ↓/↑ e
  Enter escolhem, Tab pega a primeira, Esc fecha e Ctrl+Espaço busca na hora.
  Digitar a chave (`GAECO-1234`) também funciona, e com projeto padrão basta o
  número. A lista nunca vem escolhida: Enter sem escolha continua criando a
  tarefa sem vínculo.
- **A linha vira `GAECO-1234 título`** e a tarefa nasce vinculada. Apagar a
  chave desfaz o vínculo.
- **Na lista**, o tipo e a chave aparecem em destaque acima do título. Um
  clique abre a issue. O menu `⋯` copia a chave, o link e o nome da branch, e
  abre o terminal e a pasta do worktree.
- **Na tarefa**, um cartão com tipo, chave, status, título, branch e "lido do
  Jira há…", mais **Atualizar do Jira**, **Copiar** e **Desvincular**. Uma
  tarefa sem issue ganha **Vincular ao Jira…**.
- **Branch pela convenção do tipo:** `Bug → bug/GAECO-1234`,
  `Story → feature/…`, `Task → task/…`, `Improvement → improvement/…`,
  `Hotfix → hotfix/…`. É editável em Integrações, e "História", "Tarefa" e
  "Melhoria" seguem as mesmas linhas. A aba Desenvolvimento avisa quando a
  branch já existe, local ou no remoto, e a usa em vez de criar outra.
- **Funciona sem rede.** A chave, o título e o link ficam no banco. Sem Jira, só
  a busca e o "Atualizar" ficam indisponíveis.
- Pelo MCP: `jira_search`, `task_link_jira`, `task_refresh_jira` ([guia](../mcp-server.md)).
