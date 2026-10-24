# Desenvolvimento

Ambiente, build, convenções e contribuição. Testes estão em
[`../testing/README.md`](../testing/README.md); release e CI em
[`../release-process.md`](../release-process.md).

## Pré-requisitos

| | Versão | Para quê |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | a de `global.json` (`rollForward: latestFeature`) | tudo |
| Git | qualquer recente | aba Desenvolvimento e testes de Git |
| PowerShell 7 (`pwsh`) | 7.x | `scripts/coverage.ps1` (o CI roda com `shell: pwsh`) |
| Docker | qualquer recente | só para os testes de integração do servidor (Testcontainers); sem ele, pulam |
| Ferramentas cliente do PostgreSQL | a versão do servidor de origem, ou mais nova | só para usar *Bancos de Dados…*; os testes não precisam ([instalação](../database-operations.md#instalação)) |
| Inno Setup 6 | `winget install -e --id JRSoftware.InnoSetup` | só para gerar o instalador Windows |
| Workload `maui-android` | a do SDK | só para o app do celular ([guia](../mobile.md#ambiente-de-desenvolvimento)) |

Ferramentas locais (`dotnet-ef`, `reportgenerator`) em `.config/dotnet-tools.json`:

```bash
dotnet tool restore
```

## Estrutura do repositório

```
src/                    um projeto por camada (ver architecture/overview.md)
tests/                  uma suíte por projeto + Packaging.Tests
deploy/sync-server/     docker compose do servidor de sincronização
installer/              windows/ (Inno Setup), linux/ (tarball), macos/ (roteiro)
scripts/
  coverage.ps1                  testes + cobertura + portão mínimo (o mesmo do CI)
  check-vulnerable-packages.ps1 pacotes NuGet com vulnerabilidade conhecida
  run-android.ps1               emulador + build + app do celular aberto
  check-android-apk.sh          confere o APK (SQLite nativo incluído)
  generate-icon-font.py         fonte de ícones embutida (ADR-046)
docs/                   documentação (índice em docs/README.md)
.github/                CI, CodeQL, título de PR, Release Please, release, iOS; Dependabot
Directory.Build.props   versão do produto e regras de build
Directory.Packages.props versões dos pacotes
global.json             SDK fixado
```

## Solutions

| Solution | Projetos |
|---|---|
| `MyTaskApp.slnx` | Domain, Application, Infrastructure, Mcp, Desktop, Sync.Contracts, Remote.Contracts, Mobile.Core e os testes deles |
| `MyTaskApp.Server.slnx` | Server.Api, Server.Application, Server.Infrastructure, os dois Contracts e, numa pasta `desktop`, Domain, Application, Infrastructure e Mobile.Core (os testes ponta a ponta sobem dois desktops e um celular contra a API) |
| `MyTaskApp.Mobile.slnx` | a casca .NET MAUI (precisa do workload) e o Mobile.Core |

O `MyTaskApp.Mobile.Core` não depende de MAUI e compila sem workload.

## Compilar e rodar

```bash
dotnet build MyTaskApp.slnx
dotnet run --project src/MyTaskApp.Desktop
```

- **A build de desenvolvimento usa o mesmo banco do app instalado**
  (`%APPDATA%\MyTaskApp`). Para não tocar nos dados reais, aponte para uma pasta
  descartável (`UserDataLocation.OverrideVariable`):

  ```powershell
  $env:MYTASKAPP_DATA_DIR = "$env:TEMP\mytaskapp-dev"
  dotnet run --project src/MyTaskApp.Desktop
  ```

  Não deixe a variável definida ao rodar `dotnet test` ([por quê](../testing/README.md#armadilhas-conhecidas)).
- **Instância única** ([ADR-019](../architecture/decisions/019-instancia-unica-por-mutex-nomeado.md)): com um MyTaskApp aberto na mesma sessão do
  Windows (o instalado ou o de outro checkout), a build de dev só sinaliza esse
  processo e encerra. Feche o app pela bandeja antes.
- `MyTaskApp.exe --startup` abre direto na bandeja (o argumento da chave `Run`).

## Regras de build

- `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` (`Directory.Build.props`):
  esse é o lint do projeto, e um aviso novo reprova no CI.
- `InvariantGlobalization=false` é o que faz ids IANA de fuso resolverem no
  Windows; não "otimize" para `true` ([ADR-002](../architecture/decisions/002-hora-de-parede-no-dominio.md)).
- Versões de pacote só em `Directory.Packages.props` (Central Package Management).
- A versão do produto (`VersionPrefix` em `Directory.Build.props`) é do Release
  Please. Ninguém a edita à mão ([ADR-044](../architecture/decisions/044-versionamento-com-release-please.md)).

## Migrations

O banco evolui só por migrations do EF Core; `EnsureCreated` é proibido
([ADR-005](../architecture/decisions/005-persistencia-por-interfaces-estreitas.md)). As pendentes são aplicadas no start, antes da primeira
tela. Regras do EF que quebram em silêncio:
[visão geral da arquitetura](../architecture/overview.md#persistência-o-que-a-implementação-confirmou).

```bash
dotnet tool restore
dotnet ef migrations add <Nome> \
  --project src/MyTaskApp.Infrastructure \
  --startup-project src/MyTaskApp.Infrastructure
```

Migrations do servidor (PostgreSQL): [`../sync-server.md`](../sync-server.md#4-migrations).

## Instalador local

```powershell
.\installer\windows\build.ps1    # → artifacts/installer/MyTaskApp-<versão>-win-x64-setup.exe
```

```bash
installer/linux/build.sh         # → artifacts/installer/MyTaskApp-<versão>-linux-x64.tar.gz
```

Os mesmos scripts da pipeline. Parâmetros e teste de fumaça:
[`installer/README.md`](../../installer/README.md).

## Contribuição

1. Branch a partir do `master`: `feature/…` ou `fix/…`.
2. Commits livres. O merge é **squash**, e o **título do PR** vira o único commit
   no `master`.
3. Título do PR em [Conventional Commits](https://www.conventionalcommits.org/),
   com a descrição em português e pensando em quem usa o app (vai para o
   changelog). O workflow `PR title` reprova títulos fora do padrão.

   | Tipo | Quando | Versão |
   |---|---|---|
   | `feat:` | funcionalidade nova | MINOR |
   | `fix:` | correção de bug | PATCH |
   | `perf:` / `refactor:` / `revert:` | sem mudar o comportamento | PATCH |
   | `docs:` `test:` `build:` `ci:` `chore:` `style:` | o resto | nenhuma |
   | `feat!:` ou rodapé `BREAKING CHANGE:` | quebra de compatibilidade | MAJOR |

4. Antes do PR: `dotnet build MyTaskApp.slnx -c Release` e
   `pwsh ./scripts/coverage.ps1`.

O CI (`.github/workflows/ci.yml`) roda em todo PR: build Release das duas
solutions, testes + portão de cobertura (com comentário no PR), pacotes NuGet
vulneráveis, integração do servidor com PostgreSQL (ubuntu), build Android do
celular e um `publish` self-contained de fumaça. O CodeQL analisa o C# e os
workflows. Detalhes e como rodar o check de pacotes local:
[`../release-process.md`](../release-process.md#4-ci-githubworkflowsciyml).

## Cuidados com o repositório

- **O repositório é público.** Nada de endereço real em código, testes, docs,
  commits ou PRs: IP de rede local, domínio do servidor, host de banco, domínio
  Jira da empresa. Em testes, use IPs da RFC 5737 (`192.0.2.x`, `198.51.100.x`,
  `203.0.113.x`) e domínios `example`; em docs, `<host>`, `<dominio-publico>`.
- **Fim de linha.** Os arquivos ficam em CRLF no working tree
  (`core.autocrlf=true`). Ao editar por script, leia e grave em bytes, preserve
  CRLF e grave em UTF-8 explícito; confira com `git diff --stat` (um arquivo
  inteiro "mudado" é fim de linha convertido).
