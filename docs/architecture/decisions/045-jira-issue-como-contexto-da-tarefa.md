# ADR-045 — Jira: a issue vira contexto da tarefa, sem virar um clone do Jira

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | integracoes |
| **Registrado** | 2026-10-03, commit `f2857d7` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ExternalLink, IssueKey, IExternalTaskProvider, ExternalTaskSearch, OAuth 3LO, DpapiSecretStore, convenção de branch, autocomplete |
| **Relações** | revisto por: [ADR-056](056-operacoes-de-banco-politica-central.md); relacionado: [ADR-013](013-captura-rapida-uma-linha-uma-tarefa.md), [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-047](047-pr-aberta-da-branch-pelo-gh.md) |
| **Código** | `src/MyTaskApp.Domain/External/ExternalLink.cs`, `src/MyTaskApp.Application/External/ExternalTaskSearch.cs`, `src/MyTaskApp.Application/External/BranchNaming.cs`, `src/MyTaskApp.Infrastructure/Jira/JiraAuthenticationService.cs` |

**Contexto:** quem trabalha em várias tarefas ao mesmo tempo precisa, ao
voltar a uma delas, saber em segundos o que ela é, abrir a issue certa e cair
no ambiente certo. Até aqui a tarefa só tinha um título livre, e o nome da
branch saía do título (`feature/{slug}`, ADR-027). A chave do Jira morava na
memória do usuário.

**Decisão:** o Jira é **fonte externa**, e o MyTaskApp guarda só um
**retrato** da issue na tarefa. A tarefa nasce vinculada pelo autocomplete da
própria captura rápida: digitar parte do título traz as issues parecidas, e
escolher uma reescreve a linha como `GAECO-1234 título`. O vínculo dá a branch
pela convenção do tipo (`bug/GAECO-1234`), a chave em destaque na lista e o
cartão da issue na janela da tarefa. Boards, backlog, comentários, anexos e
workflow continuam no Jira.

```
Domain       ExternalLink (retrato: provider, chave, título, tipo, status, URL, lido em)
             IssueKey (GAECO-1234), TaskItem.LinkExternal/UnlinkExternal/RefreshExternal
Application  ExternalTask · IExternalTaskProvider · IExternalTaskSearchProvider
             ExternalTaskSearch (cache) · IBranchNameStrategy + BranchConventions
             IJiraAuthenticationService · casos de uso de vínculo, conexão e convenções
Infra        JiraHttp · JiraClient · JiraTaskProvider/SearchProvider
             JiraAuthenticationService (OAuth 3LO + API token) · DpapiSecretStore
Desktop      Integrações… · IssueSuggestionsViewModel (captura) · TaskIssueViewModel (cartão)
```

## O retrato, e por que ele basta

- **Colunas em `Tasks`, owned e opcional** (`External_*`). O vínculo é 1:0..1,
  é lido pela tela Hoje a cada carga e nunca é consultado sozinho, então não
  merece tabela. Quem diz "existe" é o `Provider`, obrigatório no objeto: tudo
  nulo é ausência. Ao contrário do lembrete (que usa `IsRequired`), aqui a
  ausência é o caso comum.
- **O domínio não sabe o que é Jira.** `Provider` é texto. Azure DevOps,
  GitHub Issues ou Linear entram como mais um `IExternalTaskProvider` e mais um
  `IExternalTaskSearchProvider`, sem mexer no domínio nem nos casos de uso.
- **Offline primeiro.** A lista e o cartão desenham do retrato, que vem com a
  linha (`TodayTask.External`, na mesma consulta, sem ida extra ao banco). Sem
  rede, a tarefa abre, e o worktree, o terminal e o Claude funcionam; só
  "Atualizar do Jira" e a busca ficam indisponíveis.
- **A URL só pode ser `http(s)`.** Ela vira um clique que o sistema abre, e um
  `file:` ou `javascript:` vindo de fora não pode virar execução local.
- **O título local é do usuário.** Vincular uma tarefa que já existe não troca
  o título. "Atualizar do Jira" troca o retrato inteiro e só leva o título junto
  enquanto o usuário não o tiver mudado (o título ainda é igual ao do retrato
  anterior). A descrição da issue não é guardada: a anotação da tarefa
  (ADR-024) é do usuário, e a descrição está a um clique.

## Autocomplete: dentro da captura, e Enter continua sendo "capturar"

- **Uma busca por pausa.** 350 ms sem digitar; cada tecla cancela a espera e a
  busca anterior, e a resposta de uma busca superada é descartada (a lista
  nunca mostra o resultado de um texto que já saiu da caixa). Abaixo de três
  letras não pergunta, a não ser que seja uma chave.
- **Cache compartilhado** (`ExternalTaskSearch`, singleton): consulta
  normalizada (minúsculas, espaços colapsados), TTL de 2 min, no máximo 50
  consultas, 8 resultados. Falha não entra no cache: a próxima tecla tenta de
  novo. Conectar, desconectar ou trocar o projeto padrão esvazia o cache.
- **Chave primeiro.** `GAECO-1234` é lida direto; só se não existir cai para
  texto, porque `COVID-19` também tem forma de chave. Com projeto padrão, o
  número sozinho (`1234`) vira chave.
- **A lista abre sem escolha.** Quem digita "comprar pão" e aperta Enter cria a
  tarefa, mesmo que o Jira tenha sugerido algo. Vincular é um gesto: ↓/↑ e
  Enter, Tab (que pega a primeira) ou clique. A exceção é a chave inteira com
  uma resposta só, que já vem marcada. Esc fecha, e aquela consulta não reabre
  sozinha. Ctrl+Espaço busca na hora — ou, sem Jira, ensina onde conectar.
- **Na caixa, e não num popup.** A lista fica dentro do cartão de captura: não
  cobre a lista, não rouba o foco e some junto com a captura nos modos compacto
  e discreto (ADR-017), sem estilo nenhum a mais.
- **A chave fica no texto.** Escolher a issue troca a linha por `CHAVE título`
  e mostra embaixo "será vinculada ao Jira". `QuickCapture` recebe as issues
  escolhidas e vincula a linha que começa com a chave de uma delas, tirando a
  chave do título. Apagar a chave é desfazer o vínculo, sem botão; editar o
  título depois da chave não perde nada. Uma chave que ninguém escolheu
  continua texto: a captura não vai à rede. A atomicidade do ADR-013 vale para
  o vínculo também — uma issue inválida recusa a captura inteira.

## Branch: convenção do tipo, e nunca duplicada

- `IBranchNameStrategy` / `ConventionBranchNameStrategy`: `Bug = bug/{id}`,
  `Story = feature/{id}`, `Task = task/{id}`, `Improvement = improvement/{id}`,
  `Hotfix = hotfix/{id}`. Os moldes aceitam `{id}` (obrigatório), `{type}` e
  `{slug}`. Tipo desconhecido vira o próprio prefixo (`spike/…`).
- **A API devolve o nome do tipo na língua do usuário.** "História", "Tarefa",
  "Subtarefa", "Melhoria" e "Épico" seguem as linhas em inglês sem o usuário
  repetir a convenção; um tipo localizado escrito à mão ganha do apelido.
- **As convenções são dado do usuário** (ADR-014): linha única
  `BranchSettings`, editadas como texto em Integrações, com prévia ao vivo e
  recusa por linha antes de gravar. Linha corrompida degrada para o padrão.
- **A aba Desenvolvimento só troca a sugestão se o campo não foi tocado.** O
  nome que o usuário escreveu vale mais; desvincular volta para
  `feature/{slug}` pelo mesmo critério.
- **Branch existente:** `ExistingBranches` é a regra que o pipeline já seguia
  (ADR-027) — a local ganha checkout, a só remota vira local acompanhando, sem
  diferenciar maiúsculas —, agora extraída e usada também pela tela, que avisa
  **antes** do clique. "Criar/Checkout branch" segue pelo worktree: o
  repositório principal continua sem checkout nem operação destrutiva.

## Conexão: um clique, e o segredo longe de disco legível

- **OAuth 2.0 (3LO) da Atlassian como caminho principal.** "Conectar ao Jira"
  abre o navegador, o usuário autoriza, a volta chega em
  `http://localhost:47832/callback` e o app fica conectado. Com mais de um site
  autorizado, o usuário escolhe. Escopos: `read:jira-work read:jira-user
  offline_access`. O `state` é conferido, e o PKCE (S256) vai junto.
- **O 3LO exige client secret**: não há cliente público nem PKCE sem secret
  (pedido ECO-283 da Atlassian, ainda aberto). O client id e o secret **não
  estão no repositório**. Eles entram no build como propriedades MSBuild
  (`JiraClientId`, `JiraClientSecret`), viram `AssemblyMetadata` e são lidos
  por `JiraOptions.FromBuild()`. No CI vêm dos secrets do GitHub, só no build
  do Windows. Sem eles, a build oferece só o API token, que então já aparece
  aberto. Passo a passo para registrar o app: `docs/jira-oauth-app.md`.
- **API token como caminho avançado**, atrás de um link: site (aceita
  `empresa`, `empresa.atlassian.net` ou o link de uma issue), e-mail e token,
  conferidos no `/myself` antes de gravar qualquer coisa.
- **Onde fica cada coisa.** O refresh token ou o API token vão para o DPAPI,
  no escopo do usuário (`secrets/jira.bin`), com entropia do app — e não para o
  Credential Manager, que limita o segredo a 2.560 bytes. Site, conta e projeto
  padrão vão para o `jira.json`, sem segredo, ao lado do `widget.json` e fora
  do banco: a conexão vale para este usuário nesta máquina e não deve viajar
  num backup. O access token fica só na memória. Um `jira.json` sem segredo
  legível é "conecte de novo".
- **A Atlassian gira o refresh token** a cada uso. A renovação passa por um
  `SemaphoreSlim`, e o novo é gravado antes de o access ser usado: duas
  renovações em paralelo derrubariam a conexão. Um 401 numa chamada esquece o
  access e tenta de novo uma vez.
- **Log sem segredo.** `JiraHttp` é o único lugar que fala HTTP: uma linha por
  chamada com método, **caminho** (sem a query, que leva o que o usuário
  digitou), status e tempo. Nunca cabeçalho, corpo, código de autorização nem
  token. O `HttpClient` é cru, sem o `IHttpClientFactory`, cujo log de
  depuração registra cabeçalhos. Mensagem de exceção leva só o tipo de falha, e
  há teste que procura cada segredo no log.
- **Timeout não é cancelamento.** Cancelar (outra tecla) sobe como
  `OperationCanceledException`; o Jira demorar mais de 8 s vira "indisponível".

## Tela

- **Lista:** selo do tipo e chave acima do título, a chave na cor de destaque.
  O clique abre a issue, e o balão diz tipo, status e título. O menu da linha
  ganha abrir no Jira, copiar chave, link e nome da branch, e abrir terminal e
  pasta do worktree (com escolha de ambiente, no molde do ADR-036).
- **Janela da tarefa:** o cartão da issue entre o título e as abas — tipo,
  chave em destaque, status, título, branch da convenção, "lido do Jira há…" e
  as ações. Concluída, a tarefa só abre e copia. Uma tarefa local ganha
  "Vincular ao Jira…", com a mesma busca da captura.
- **Integrações…** no menu ☰: o estado da conexão, o projeto padrão (lista do
  próprio Jira) e as convenções de branch com prévia.

**Interfaces que o pedido citou e não existem com esse nome.**
`IJiraIssueSearchService` é o `IExternalTaskSearchProvider`: uma segunda
interface com a mesma assinatura seria uma arquitetura paralela.
`IJiraIssueKeyParser` é `IssueKey.TryParse`, função pura no domínio, no molde
de `AliasRule` e `GitBranchName` (interface só quando agrega valor, §24).
`IJiraClient` existe, interno à Infrastructure.

**Armadilhas:**

- **`localhost` resolve para `::1` antes de `127.0.0.1`** em algumas máquinas;
  o listener da volta escuta os dois.
- **A Atlassian só aceita o redirect exato que foi registrado**, então a porta é
  fixa (`Jira:CallbackPort`). Ocupada, a conexão recusa e diz o que fazer.
- **`/rest/api/3/search` foi removido em 2025.** A busca usa
  `/rest/api/3/search/jql`, que devolve só ids se não pedir `fields`.
- **O texto do usuário nunca vira JQL.** Só letras e dígitos sobrevivem; aspas,
  barras e operadores do Lucene viram espaço. Há teste de injeção.

**Limites aceitos:**

- só Jira Cloud; Data Center/Server ficam para um provedor próprio, com PAT;
- o client secret embarcado é extraível por quem tem o executável. Ele
  identifica o app, e não o usuário — sem o consentimento no navegador não abre
  conta de ninguém —, mas um abuso pode fazer a Atlassian revogar o app. A
  saída, se precisar, é um broker que guarde o secret;
- sem cofre fora do Windows: lá a conexão é recusada em vez de gravar o token
  em texto;
- a busca procura no **resumo** (`summary ~`), e não na descrição nem nos
  comentários, e ordena por atualização, e não por relevância;
- Ctrl+Espaço com a caixa vazia não lista "minhas issues". É o próximo passo
  óbvio, e pede um método a mais no provedor;
- sem polling: o status na lista é o da última leitura, e o cartão diz há
  quanto tempo foi.
