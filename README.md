# MyTaskApp

[![CI](https://github.com/AdrianoGuzzo/MyTaskApp/actions/workflows/ci.yml/badge.svg)](https://github.com/AdrianoGuzzo/MyTaskApp/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/AdrianoGuzzo/MyTaskApp)](https://github.com/AdrianoGuzzo/MyTaskApp/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Widget de tarefas que não deixa você esquecer.**

O MyTaskApp é um painel pequeno de checklist que fica na área de trabalho, mora
na bandeja do sistema e insiste até você dar atenção. Registrar uma tarefa custa
uma linha de texto. A partir daí, o app se encarrega de lembrar. Para quem
desenvolve, a tarefa também pode virar um **worktree Git** com um **Claude Code**
trabalhando nele, e o painel avisa quando o agente precisa de você.

Aplicativo desktop em **.NET 10** + **Avalonia 12**, com SQLite local. Feito
primeiro para Windows. Também roda no Linux, com algumas funções a menos.

---

## Sumário

- [Funcionalidades](#funcionalidades)
- [Instalação](#instalação)
- [Onde ficam os dados](#onde-ficam-os-dados)
- [Configuração](#configuração)
- [Desenvolvimento](#desenvolvimento)
- [Arquitetura](#arquitetura)
- [Estrutura do repositório](#estrutura-do-repositório)
- [Contribuindo](#contribuindo)
- [Releases e versionamento](#releases-e-versionamento)
- [Documentação](#documentação)
- [Licença](#licença)

---

## Funcionalidades

### Checklist do dia

- **Captura rápida.** A tela abre com uma caixa já focada. Cada linha vira uma
  tarefa para hoje. `Enter` registra a lista inteira, `Shift+Enter` quebra a
  linha. A captura é atômica: ou entra tudo, ou nada (até 100 linhas).
- **Seções do dia.** `ATRASADAS`, `AGORA`, `HOJE`, `SEM HORÁRIO` e `CONCLUÍDAS`.
  A janela "agora" vai de 15 min antes a 60 min depois do horário, e é
  configurável. O quadro se atualiza sozinho a cada minuto.
- **Ordem manual.** Arraste as linhas para reordenar dentro da seção.
- **Concluir e reabrir** pelo checkbox. Título e prioridade são editados na
  janela da tarefa.

### Lembretes que insistem

- Lembrete por tarefa, contado **a partir da criação** ou **antes do horário**,
  com repetição até alguém responder ("a cada 15 min até eu dar atenção").
- Escalonamento por canais: notificação, som e trazer a janela para a frente.
- No aviso: **adiar** ou **marcar como visto**. Na bandeja: **pausar os
  lembretes** por uma hora.
- Os lembretes sobrevivem ao fechamento do app: o próximo disparo fica gravado
  no banco. Ao abrir, o app avisa o que deveria ter avisado enquanto estava
  fechado.
- O padrão de lembrete é ajustado em *☰ → Configuração de lembretes…*.

### Um widget, não uma janela

- Painel de 360×560 sem moldura, arrastável e redimensionável, com três modos:
  **Painel completo**, **Modo compacto** e **Recolher**. A posição e o tamanho
  ficam salvos.
- **Modo HUD** (o alfinete do cabeçalho). O painel vira um cartão compacto,
  sempre visível sobre as outras janelas num canto da tela, só com o que falta
  fazer. Fora do cartão, o clique vai para o app de trás. Posição (seis cantos ou
  onde você arrastar), tamanho (Compacto, Normal, Expandido), opacidade do fundo
  e "HUD recolhido" (uma pílula que abre ao passar o mouse) ficam em
  *☰ → Janela e comportamento…*. Para voltar, clique no alfinete aceso.
- **Sempre no topo** (no menu) vale para a janela normal e não muda posição nem
  tamanho.
- **Ao fechar a janela:** fechar o aplicativo, minimizar para a bandeja (o
  padrão; os lembretes continuam rodando) ou entrar no modo HUD. Dá para
  **iniciar no modo HUD** e, no Windows, ligar **Ctrl+Shift+Espaço** para
  alternar entre janela e HUD. O atalho vem desligado porque o Visual Studio usa
  a mesma combinação.
- **Instância única.** Abrir o atalho de novo traz a janela que já existe e não
  cria um segundo processo.
- **Iniciar com o Windows** (ligado por padrão no instalador). O app sobe
  direto na bandeja. Dá para mudar em *☰ → Iniciar com o Windows*.
- **Temas:** Papel, Sépia, Carvão, Nórdico, Ameixa, Ponta e Alto contraste,
  além de **Automático**, que segue o tema claro, escuro ou de alto contraste
  do Windows.

### Anotações, etiquetas e ciclo de vida

- **Anotação em Markdown** por tarefa, com barra de formatação e os modos
  **Escrever**, **Visualizar** e **Lado a lado** (`Ctrl+Shift+V`, como no VS
  Code). A renderização segue o preview do VS Code: títulos, listas aninhadas,
  `- [ ]`, código, citação, tabela e links.
- **Correção ortográfica** com o corretor do Windows: sublinhado vermelho e
  sugestões no clique direito.
- **Etiquetas** com nome e cor, quantas quiser por tarefa. Na linha elas
  aparecem como bolinhas coloridas.
- **Diretórios da etiqueta:** um `@alias` aponta para uma pasta, que pode ter
  uma branch de origem padrão. Digitar `@` na anotação troca o alias pelo
  caminho real.
- **Arquivar e mover para a lixeira** pelo menu `⋯` da linha. Em
  *☰ → Gerenciamento de dados…* ficam as abas Concluídos, Arquivados e Lixeira,
  com restauração, exclusão definitiva, histórico de auditoria e a política de
  arquivamento automático.

### Desenvolvimento: tarefa → worktree → agente

A janela da tarefa tem a aba **Desenvolvimento**:

1. **Iniciar implementação.** Escolha o repositório (por `@alias` ou caminho),
   a branch de origem e, se quiser, uma **tag** para partir de uma versão
   antiga. O app faz o `fetch`, cria a branch (sugestão:
   `feature/{slug-do-título}`) e um **worktree** numa pasta irmã do
   repositório.
2. **Comandos pós-worktree.** Uma lista ordenada, como `dotnet restore` e
   `npm ci`, roda em sequência com o output ao vivo e para no primeiro erro.
   Os **comandos globais** (`@restore` → `dotnet restore`) ficam em
   *☰ → Comandos globais…*.
3. **Vários ambientes por tarefa**, um worktree por repositório (o app e a
   API, o front e o back).
4. **Bolinha de estado Git na linha:** âmbar quando há alteração não
   commitada, azul quando há commit sem push e verde quando tudo foi enviado.
   Ao concluir a tarefa, o app pergunta o que fazer com o worktree.
5. **PR aberta da branch.** Se a branch já existe e tem PR aberta no GitHub, o
   formulário mostra **"PR #123 aberta ↗"**, um link que abre a PR. Os balões
   da aba do repositório e da lista Hoje também mostram a PR. Usa o
   [GitHub CLI](https://cli.github.com/) (`gh`) com o seu login. Se ele faltar,
   o app mostra como instalar (ADR-047).
6. **Remover worktree** apaga a pasta. Se algum processo a estiver segurando,
   o app mostra qual.

### Claude Code por tarefa

- **Iniciar Claude Code** abre o `claude` num terminal real, dentro do
  worktree. O card mostra PID, início e pasta, e **Abrir terminal do agente**
  traz a janela certa para a frente.
- **Parâmetros, modelo e esforço** escolhidos no card. O padrão é
  `--dangerously-skip-permissions`, já que o worktree é isolado e descartável.
- **Texto para o agente com `@`:** completa os caminhos dos ambientes da
  tarefa e dos arquivos dentro deles.
- **Acompanhamento pelos hooks oficiais** do Claude Code. O selo da linha e um
  aviso no canto da tela mostram **Trabalhando**, **Aguardando você**,
  **Aguardando revisão** ou **Erro na resposta**. Os hooks são passados com
  `claude --settings <arquivo do app>`, e o `~/.claude/settings.json` do
  usuário não é alterado.
- **Um som por estado**, com sons do app ou arquivos seus, em
  *☰ → Sons do Claude Code…*.
- **Abrir Claude Code** também fica no menu `⋯` da linha, sem precisar abrir a
  tarefa.

### Jira: a issue vira o contexto da tarefa

O Jira continua sendo onde a issue mora. O MyTaskApp busca a issue enquanto
você escreve, guarda a chave, o título e o link na tarefa e dá o nome da
branch. Detalhes em [ADR-045](docs/ARCHITECTURE.md).

- **Conectar com um clique** em *☰ → Integrações…*: o navegador abre na
  Atlassian, você autoriza e volta conectado. E-mail + API token fica como
  caminho avançado.
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

---

## Instalação

Os instaladores ficam em
[**Releases**](https://github.com/AdrianoGuzzo/MyTaskApp/releases). Eles são
*self-contained* e já levam o runtime do .NET, então não é preciso instalar
nada antes.

| Plataforma | Arquivo |
|---|---|
| Windows x64 | `MyTaskApp-<versão>-win-x64-setup.exe` |
| Linux x64 | `MyTaskApp-<versão>-linux-x64.tar.gz` |
| macOS | ainda não disponível ([roteiro](installer/macos/README.md)) |

Cada release publica também um `SHA256SUMS`, para conferir o download:

```bash
sha256sum -c SHA256SUMS --ignore-missing
```

### Windows

Rode o instalador. A instalação é **por usuário**, sem UAC, em
`%LOCALAPPDATA%\Programs\MyTaskApp`. Para atualizar, basta rodar o instalador
da versão nova por cima: os dados e os atalhos são mantidos.

> O instalador ainda não é assinado digitalmente, então o SmartScreen avisa na
> primeira execução.

Instalação silenciosa:

```bat
MyTaskApp-1.1.0-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

> No modo silencioso, "Iniciar com o Windows" também vem ligado. Para
> desligar, acrescente `/MERGETASKS="!startupicon"`.

Desinstalação (os dados ficam, a não ser que você peça para apagar):

```bat
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT /DELETEDATA=1
```

### Linux

```bash
tar -xzf MyTaskApp-1.1.0-linux-x64.tar.gz
cd MyTaskApp-1.1.0
./install.sh                                      # sem sudo, em ~/.local (respeita PREFIX=)

~/.local/share/MyTaskApp/uninstall.sh             # remove o app e mantém os dados
~/.local/share/MyTaskApp/uninstall.sh --purge     # pergunta antes de apagar os dados
```

No Linux não há corretor ortográfico, sons, terminal do agente, indicação de
quem segura a pasta do worktree nem início automático com o sistema, porque
essas funções dependem de APIs do Windows.

Todos os parâmetros, o comportamento de atualização e o teste de fumaça estão
em [`installer/README.md`](installer/README.md).

---

## Onde ficam os dados

A aplicação e os dados ficam em lugares separados. Atualizar ou desinstalar o
app **nunca** apaga suas tarefas.

| | Windows | Linux |
|---|---|---|
| Aplicação | `%LOCALAPPDATA%\Programs\MyTaskApp` | `~/.local/share/MyTaskApp` |
| Banco SQLite | `%APPDATA%\MyTaskApp\mytaskapp.db` | `~/.config/MyTaskApp/mytaskapp.db` |
| Estado da janela | `%APPDATA%\MyTaskApp\widget.json` | `~/.config/MyTaskApp/widget.json` |
| Config do usuário | `%APPDATA%\MyTaskApp\appsettings.user.json` | `~/.config/MyTaskApp/appsettings.user.json` |
| Logs | `%APPDATA%\MyTaskApp\logs` | `~/.config/MyTaskApp/logs` |
| Conexão com o Jira | `%APPDATA%\MyTaskApp\jira.json` (sem segredo) | — |
| Token do Jira | `%APPDATA%\MyTaskApp\secrets\jira.bin` (DPAPI) | — |

O banco é criado e atualizado pela própria aplicação na inicialização (migrations
do EF Core). O instalador não mexe nele. Para fazer backup, copie o
`mytaskapp.db` com o app fechado.

A variável de ambiente **`MYTASKAPP_DATA_DIR`** move banco, estado e logs para
outra pasta, para uma instalação portátil ou para testes.

---

## Configuração

Quase tudo é configurado na própria interface, e as escolhas ficam salvas no
banco. O `appsettings.json` guarda só os parâmetros de funcionamento do app. Para
mudar algum deles sem editar o diretório de instalação, crie um
`appsettings.user.json` na pasta de dados com as chaves que quer sobrescrever:

```json
{
  "Application": {
    "TimeZoneId": "America/Sao_Paulo",
    "NowWindowBeforeMinutes": 15,
    "NowWindowAfterMinutes": 60,
    "ReminderTickSeconds": 30,
    "LifecycleSweepMinutes": 360,
    "AgentSessionReconcileSeconds": 60,
    "AgentEventsPort": 47831
  },
  "Jira": {
    "CallbackPort": 47832,
    "RequestTimeoutSeconds": 8
  }
}
```

| Chave | Padrão | O que faz |
|---|---|---|
| `TimeZoneId` | `America/Sao_Paulo` | fuso IANA usado para "hoje" e para os horários |
| `NowWindowBeforeMinutes` / `AfterMinutes` | 15 / 60 | janela da seção `AGORA` |
| `ReminderTickSeconds` | 30 | intervalo do agendador de lembretes |
| `LifecycleSweepMinutes` | 360 | intervalo da varredura de arquivamento e lixeira |
| `AgentSessionReconcileSeconds` | 60 | intervalo da conferência de processos do agente |
| `AgentEventsPort` | 47831 | porta local (`127.0.0.1`) que recebe os hooks do Claude Code |
| `Jira:CallbackPort` | 47832 | porta da volta do login do Jira; precisa ser a registrada no app OAuth |
| `Jira:RequestTimeoutSeconds` | 8 | quanto uma chamada ao Jira espera antes de desistir |
| `Jira:ClientId` / `ClientSecret` | do build | o app OAuth da Atlassian ([como registrar](docs/jira-oauth-app.md)) |

Linha de comando: `MyTaskApp.exe --startup` abre direto na bandeja. É o
argumento que o instalador grava na chave `Run` do Windows.

---

## Desenvolvimento

### Pré-requisitos

| | Versão | Para quê |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0.401** (fixado em `global.json`) | tudo |
| Git | qualquer recente | aba Desenvolvimento e testes de Git |
| Inno Setup 6 | `winget install -e --id JRSoftware.InnoSetup` | só para gerar o instalador Windows |

As ferramentas locais (`dotnet-ef` e `reportgenerator`) ficam em
`.config/dotnet-tools.json`:

```bash
dotnet tool restore
```

### Compilar e rodar

```bash
dotnet build MyTaskApp.slnx
dotnet run --project src/MyTaskApp.Desktop
```

> **Atenção:** a build de desenvolvimento usa **o mesmo banco do app
> instalado** (`%APPDATA%\MyTaskApp`). Para trabalhar sem tocar nos seus dados
> reais, aponte para uma pasta descartável:
>
> ```powershell
> $env:MYTASKAPP_DATA_DIR = "$env:TEMP\mytaskapp-dev"
> dotnet run --project src/MyTaskApp.Desktop
> ```
>
> Não deixe essa variável definida ao rodar `dotnet test`: há testes que
> verificam justamente o caminho padrão, e a suíte já se isola sozinha.

> **Instância única:** se um MyTaskApp estiver aberto na mesma sessão do
> Windows, seja o instalado ou o de outro checkout, a build de dev só sinaliza
> esse processo e encerra. Os 4 testes de `SingleInstanceTests` também falham.
> Feche o app pela bandeja antes de rodar.

A build trata avisos como erros (`TreatWarningsAsErrors` +
`EnforceCodeStyleInBuild`). Esse é o lint do projeto: um aviso novo reprova no
CI.

### Testes

```bash
dotnet test MyTaskApp.slnx
```

São cinco suítes com xUnit v3, AwesomeAssertions e NSubstitute:

| Projeto | Cobre |
|---|---|
| `MyTaskApp.Domain.Tests` | regras puras: recorrência, classificação de "Hoje", lembretes, ciclo de vida |
| `MyTaskApp.Application.Tests` | casos de uso e agendadores, com `FakeTimeProvider` |
| `MyTaskApp.Infrastructure.Tests` | EF Core contra **SQLite real**, Git real, processos, migrations e upgrade |
| `MyTaskApp.Desktop.Tests` | ViewModels e testes de UI **headless** (Avalonia.Headless), onde um binding quebrado passaria despercebido |
| `MyTaskApp.Packaging.Tests` | contratos entre código e instalador (mutex, `--startup`, versão), lidos como texto e sem precisar do Inno Setup |

### Cobertura

O mesmo portão do CI, com mínimo de **80% de linhas** e **70% de branches**:

```powershell
.\scripts\coverage.ps1 -Open
```

O relatório HTML vai para `artifacts/coverage/report/index.html`, e o script
falha se algum teste falhar ou se a cobertura ficar abaixo do mínimo.

### Migrations

O banco evolui só por migrations do EF Core. `EnsureCreated` é proibido.

```bash
dotnet tool restore
dotnet ef migrations add <Nome> \
  --project src/MyTaskApp.Infrastructure \
  --startup-project src/MyTaskApp.Infrastructure
```

As migrations pendentes são aplicadas no próximo start, antes da primeira tela.

### Gerar o instalador localmente

```powershell
.\installer\windows\build.ps1    # → artifacts/installer/MyTaskApp-<versão>-win-x64-setup.exe
```

```bash
installer/linux/build.sh         # → artifacts/installer/MyTaskApp-<versão>-linux-x64.tar.gz
```

São os mesmos scripts que a pipeline roda: versão, testes, `publish`
self-contained e empacotamento.

---

## Arquitetura

Clean Architecture em quatro projetos, com dependências apontando para dentro:

```
Desktop ─────────────► Application ──► Domain
   │                       ▲
   └──► Infrastructure ────┘
```

| Projeto | Responsabilidade |
|---|---|
| **Domain** | Entidades e regras puras: `TaskItem` (a série) e `TaskOccurrence` (cada dia), lembretes, ciclo de vida, etiquetas, ambientes de desenvolvimento e sessões de agente. Sem dependências externas. |
| **Application** | Casos de uso (sem mediador), agendadores sobre `TimeProvider` e portas (`ITaskItemRepository`, `IGitClient`, `IAgentCliProvider`, …). |
| **Infrastructure** | EF Core + SQLite, Git, processos, terminal do Windows, listener dos hooks do Claude Code e sons. |
| **Desktop** | Avalonia + MVVM (CommunityToolkit.Mvvm), bandeja, temas, editor Markdown, corretor ortográfico e composição (DI + Serilog). |

Algumas decisões que moldam o código:

- **Série e ocorrência:** toda tarefa tem pelo menos uma ocorrência, inclusive
  as que não se repetem. O histórico fica preservado (cada dia é um registro), e
  a recorrência não se espalha em `if`s pelo código.
- **Tempo:** o domínio usa hora local (`DateOnly` + `TimeOnly` + fuso), e o
  instante UTC só é calculado na borda. "Agora" sempre vem de `TimeProvider`,
  nunca de `DateTime.Now`.
- **Lembretes persistidos:** o próximo disparo é uma coluna no banco, não um
  timer em memória.
- **Um escopo de DI por operação** (`IUseCaseRunner`), para o widget de longa
  duração não segurar um `DbContext` aberto.
- **`DomainException` é a mensagem que o usuário lê.** Stack trace vai só para
  o log.

Cada decisão, com o motivo e as alternativas descartadas, está registrada como
ADR em [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

### Stack

| | Versão |
|---|---|
| .NET | 10.0.401 |
| Avalonia | 12.1.2 |
| CommunityToolkit.Mvvm | 8.4.2 |
| EF Core + SQLite | 10.0.12 |
| Serilog | 4.4.0 |
| xUnit | v3 (3.2.2) |
| AwesomeAssertions | 9.6.0 |
| NSubstitute | 6.2.0 |

As versões ficam centralizadas em `Directory.Packages.props` (Central Package
Management).

---

## Estrutura do repositório

```
.
├── src/
│   ├── MyTaskApp.Domain/            entidades e regras
│   ├── MyTaskApp.Application/       casos de uso, agendadores, portas
│   ├── MyTaskApp.Infrastructure/    EF Core/SQLite, Git, processos, Claude Code
│   └── MyTaskApp.Desktop/           app Avalonia (views, view models, temas)
├── tests/                           uma suíte por projeto + Packaging.Tests
├── installer/
│   ├── windows/                     Inno Setup 6 (MyTaskApp.iss, build.ps1)
│   ├── linux/                       tarball + install.sh/uninstall.sh
│   └── macos/                       roteiro (não implementado)
├── scripts/
│   ├── coverage.ps1                 testes + cobertura + portão mínimo
│   └── generate-icon-font.py        fonte de ícones embutida (ADR-046)
├── docs/
│   ├── ARCHITECTURE.md              ADRs
│   └── release-process.md           versionamento e release
├── .github/workflows/               CI, título de PR, Release Please, release
├── Directory.Build.props            versão do produto e regras de build
├── Directory.Packages.props         versões dos pacotes
└── global.json                      SDK fixado
```

---

## Contribuindo

1. Crie uma branch a partir do `master`: `feature/…` ou `fix/…`.
2. Escreva os commits como preferir. O merge é **squash**, e o **título do PR**
   vira o único commit no `master`.
3. Use no título do PR o formato
   [Conventional Commits](https://www.conventionalcommits.org/), com a
   descrição em português. Ela vai para o changelog, então escreva pensando em
   quem usa o app:

   | Tipo | Quando | Versão |
   |---|---|---|
   | `feat:` | funcionalidade nova | MINOR |
   | `fix:` | correção de bug | PATCH |
   | `perf:` / `refactor:` / `revert:` | sem mudar o comportamento | PATCH |
   | `docs:` `test:` `build:` `ci:` `chore:` `style:` | o resto | nenhuma |
   | `feat!:` ou rodapé `BREAKING CHANGE:` | quebra de compatibilidade | MAJOR |

   O workflow `PR title` reprova títulos fora do padrão.
4. Antes de abrir o PR:

   ```powershell
   dotnet build MyTaskApp.slnx -c Release
   .\scripts\coverage.ps1
   ```

O CI roda em todo PR: build Release, as cinco suítes, o portão de cobertura
(com comentário no PR) e um `publish` self-contained para `win-x64` e
`linux-x64`.

---

## Releases e versionamento

Ninguém edita número de versão à mão. O fluxo é este:

```
PR com título convencional → squash no master
  → Release Please mantém o PR "chore(master): release X.Y.Z"
  → merge desse PR → tag vX.Y.Z + release em rascunho
  → release.yml: confere tag × código → testes → instaladores → SHA256SUMS
  → teste de fumaça manual → publicar o rascunho
```

- Versão em [SemVer](https://semver.org/lang/pt-BR/), com tag `vX.Y.Z`. A
  versão fica em `Directory.Build.props` e é mantida pelo Release Please.
- Tags nunca são movidas e releases publicadas não são refeitas. Uma correção
  sai como versão nova.
- A versão instalada aparece na última linha do menu ☰ (`MyTaskApp 1.1.0 ·
  <commit> · <data>`). Clicar nela copia esses dados para colar num relato de
  problema.

O processo completo, com hotfix em branch de manutenção, configuração do
repositório e assinatura digital, está em
[`docs/release-process.md`](docs/release-process.md). O histórico de versões
fica em [`CHANGELOG.md`](CHANGELOG.md).

---

## Documentação

| Documento | Conteúdo |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | decisões de arquitetura (ADR-001 a ADR-048), com o motivo de cada uma |
| [`docs/jira-oauth-app.md`](docs/jira-oauth-app.md) | registrar o app OAuth do Jira e pôr as credenciais no build |
| [`docs/release-process.md`](docs/release-process.md) | Conventional Commits, SemVer, pipeline de release, hotfix, verificação de versão |
| [`installer/README.md`](installer/README.md) | instaladores Windows e Linux, parâmetros, atualização, teste de fumaça |
| [`installer/macos/README.md`](installer/macos/README.md) | roteiro para o empacotamento macOS |
| [`CHANGELOG.md`](CHANGELOG.md) | o que mudou em cada versão |

---

## Licença

Distribuído sob a licença MIT. O texto completo está em [`LICENSE`](LICENSE).

Os ícones da fonte embutida (`src/MyTaskApp.Desktop/Assets/Fonts/MyTaskAppIcons.ttf`)
são do [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons),
© Microsoft Corporation, também sob licença MIT.
