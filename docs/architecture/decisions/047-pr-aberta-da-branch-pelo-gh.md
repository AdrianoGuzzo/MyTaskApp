# ADR-047 — PR aberta da branch, pelo GitHub CLI

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-10-03, commit `7ee5009` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | GitHub CLI, gh pr list, GhCliPullRequestClient, GhLocator, PR aberta, Como instalar o gh, cache |
| **Relações** | relacionado: [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-045](045-jira-issue-como-contexto-da-tarefa.md), [ADR-064](064-repositorio-git-do-diretorio-clone.md) |
| **Código** | `src/MyTaskApp.Infrastructure/GitHub/GhCliPullRequestClient.cs`, `src/MyTaskApp.Infrastructure/GitHub/GhLocator.cs`, `src/MyTaskApp.Desktop/Development/GhInstallGuide.cs` |

**Contexto:** com a issue do Jira (ADR-045), a branch da tarefa muitas vezes já
existe — alguém começou, abriu a PR, e a tarefa volta para outra pessoa. Na
aba Desenvolvimento o aviso dizia "a branch já existe", mas não que ela já tinha
PR aberta, e o usuário ia ao GitHub procurar.

**Decisão:** o app pergunta ao GitHub pela PR aberta da branch usando o
**GitHub CLI (`gh`)**, com o login que o `gh` já tem:

```
gh pr list --repo github.com/{dono}/{nome} --head {branch} --state open \
           --json number,title,url,isDraft --limit 1
```

- **Onde aparece:** no formulário de criação, embaixo do aviso de branch
  existente, um link **"PR #123 aberta ↗"** com o título ao lado. O clique abre a
  PR no navegador. A mesma informação vai para o **balão da aba do
  repositório** e para o **balão do título na lista Hoje**, no bloco WORKTREE.
  Nos balões não há link, porque eles somem quando o mouse sai.
- **Quando pergunta:** só para branch que **já existe**, local ou no remoto,
  porque branch nova não tem PR. A pergunta espera a mesma pausa da digitação
  do diretório. Com o ambiente pronto, pergunta pela branch dele. Na lista Hoje,
  pergunta depois das cores do Git (ADR-034), sem atrasá-las.
- **De onde vem o repositório:** de `git remote get-url origin`. Valem os
  formatos https, `ssh://` e `git@github.com:dono/nome.git`. **Só github.com**:
  GitHub Enterprise tem host e login próprios no `gh`, e fica para quando alguém
  precisar.
- **Cache de 2 minutos** no cliente, que é singleton. A lista Hoje recarrega a
  cada minuto, e cada recarga não pode virar uma rajada de idas ao GitHub.
  "Verificar novamente" limpa o cache. Falha (erro ou timeout) fica só **20
  segundos**: o bastante para a recarga seguinte não abrir outra leva de `gh`
  presos, e pouco para a PR voltar logo que a rede voltar.
- **Timeout de 8 segundos.** A resposta normal vem em menos de um segundo. Em
  03/10/2026 o `gh` chegou a ficar preso esperando o `api.github.com` por vários
  minutos seguidos, enquanto outras chamadas passavam, e cada consulta ocupava
  os 15 segundos de antes.
- **Nunca bloqueia nem lança.** Sem remoto do GitHub, sem rede ou com o `gh` em
  erro, criar o worktree nunca depende disso. Quando a consulta da branch falha,
  o formulário diz "Não foi possível consultar o GitHub…" com **"Tentar de
  novo"**: sem o aviso, a falta do link parecia "a branch não tem PR".

**Tutorial do `gh`:** se o repositório é do GitHub e o `gh` falta (ou está
instalado sem login), o formulário avisa e oferece **"Como instalar o gh"**. O
painel segue o modelo do Git (ADR-027):

1. o comando de instalação por sistema: `winget install --id GitHub.cli`,
   `brew install gh` ou o gerenciador da distribuição pelo `os-release`;
2. `gh auth login`;
3. `gh auth status` para conferir;
4. o link para cli.github.com e um "Verificar novamente".

Sem login, o painel pula a instalação. O app não instala nada sozinho, e o
`GhLocator` relê o PATH a cada procura, então a instalação feita com o app
aberto é encontrada sem reiniciar.

**Por que o `gh` e não um token no app:** o `gh` já resolve o login (navegador,
SSO da organização, 2FA), funciona com repositório privado e é o que quem usa
GitHub costuma ter. Um token próprio pediria mais uma tela de integração e mais
um segredo guardado no app, para uma informação que é só um aviso. A API
pública sem token não serve, porque não enxerga repositório privado e tem
limite de 60 consultas por hora.

**Limites aceitos:**

- `--head` filtra pelo nome da branch, sem o dono: uma PR de fork com a mesma
  branch também conta;
- com mais de uma PR aberta da mesma branch, só a primeira aparece.
