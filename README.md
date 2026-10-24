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
primeiro para Windows. Também roda no Linux, com algumas funções a menos. Há um
app de celular (Android; iOS configurado) e um servidor de sincronização
opcional, que você hospeda.

## Funcionalidades

| | |
|---|---|
| **Checklist do dia** | Captura rápida (cada linha vira uma tarefa), seções `ATRASADAS`, `AGORA`, `HOJE`, `PRAZOS`, `SEM HORÁRIO`, `CONCLUÍDAS` e ordem manual por arrasto. [Detalhes](docs/features/checklist.md) |
| **Lembretes e prazos** | Lembretes que repetem até alguém responder e sobrevivem ao app fechado; prazos com aviso por degrau. [Detalhes](docs/features/lembretes-e-prazos.md) |
| **Tempo trabalhado** | Cronômetro por tarefa, períodos editáveis e histórico dos últimos 7 dias. [Detalhes](docs/features/tempo-e-historico.md) |
| **Post-its** | Notas soltas em janelas próprias, com atalho global `Ctrl+Alt+N`. [Detalhes](docs/features/post-its.md) |
| **Um widget, não uma janela** | Painel sem moldura, modo HUD, bandeja, instância única, iniciar com o Windows e sete temas. [Detalhes](docs/features/janela-e-temas.md) |
| **Anotações, etiquetas e ciclo de vida** | Markdown por tarefa, etiquetas com diretórios (`@alias`), arquivo, lixeira e auditoria. [Detalhes](docs/features/anotacoes-etiquetas-ciclo-de-vida.md) |
| **Desenvolvimento** | A tarefa vira um worktree Git, com comandos pós-worktree, comandos rápidos, PR aberta e limpeza. [Detalhes](docs/features/desenvolvimento-worktrees.md) |
| **Claude Code por tarefa** | O agente num terminal real dentro do worktree, acompanhado pelos hooks oficiais. [Detalhes](docs/features/claude-code.md) |
| **Consulta Inteligente** | Perguntar sobre o sistema e receber a resposta do Claude Code, que só lê o código e uma cópia do banco. [Guia](docs/consulta-inteligente.md) |
| **Jira** | A issue vira o contexto da tarefa: busca ao digitar, chave, link e nome da branch. [Detalhes](docs/features/jira.md) |
| **Bancos de Dados** | Cópia anonimizada de PostgreSQL de produção para outros ambientes, sem tocar na produção. [Guia](docs/database-operations.md) |
| **Servidor MCP** | Um cliente de IA consulta e altera as tarefas pelas mesmas regras da tela, só no loopback. [Guia](docs/mcp-server.md) |
| **Sincronização (opcional)** | Tarefas, etiquetas, horas e post-its entre computadores, num servidor seu. [Guia](docs/sync.md) |
| **Celular** | Mais um dispositivo da mesma sincronização, que funciona sem rede. [Guia](docs/mobile.md) |

No Linux não há corretor ortográfico, sons, terminal do agente, indicação de
quem segura a pasta do worktree nem início automático com o sistema, porque
essas funções dependem de APIs do Windows.

## Instalação

Os instaladores ficam em
[**Releases**](https://github.com/AdrianoGuzzo/MyTaskApp/releases). Eles são
*self-contained* e já levam o runtime do .NET.

| Plataforma | Arquivo |
|---|---|
| Windows x64 | `MyTaskApp-<versão>-win-x64-setup.exe` |
| Linux x64 | `MyTaskApp-<versão>-linux-x64.tar.gz` |
| Android (celular) | `MyTaskApp-<versão>-android.apk` ([como instalar](docs/mobile.md#android-apk-assinado-em-cada-release)) |
| macOS | ainda não disponível ([roteiro](installer/macos/README.md)) |

Cada release publica também um `SHA256SUMS`:

```bash
sha256sum -c SHA256SUMS --ignore-missing
```

**Windows:** rode o instalador. A instalação é **por usuário**, sem UAC, em
`%LOCALAPPDATA%\Programs\MyTaskApp`, e atualizar é rodar a versão nova por cima.
O instalador ainda não é assinado digitalmente, então o SmartScreen avisa na
primeira execução.

```bat
REM Instalação silenciosa ("Iniciar com o Windows" vem ligado; /MERGETASKS="!startupicon" desliga)
MyTaskApp-<versão>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

REM Desinstalação: a primeira mantém os dados, a segunda apaga
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT /DELETEDATA=1
```

**Linux:**

```bash
tar -xzf MyTaskApp-<versão>-linux-x64.tar.gz
cd MyTaskApp-<versão>
./install.sh                                      # sem sudo, em ~/.local (respeita PREFIX=)
~/.local/share/MyTaskApp/uninstall.sh             # remove o app e mantém os dados
~/.local/share/MyTaskApp/uninstall.sh --purge     # pergunta antes de apagar os dados
```

Todos os parâmetros, a atualização e o teste de fumaça:
[`installer/README.md`](installer/README.md).

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
| Token do Jira | `%APPDATA%\MyTaskApp\secrets\jira.bin` (DPAPI) | chaveiro do sistema (`secret-tool`) |
| Senhas das conexões de banco | `%APPDATA%\MyTaskApp\secrets\postgres-<id>.bin` (DPAPI) | chaveiro do sistema (`secret-tool`) |
| Temporários das cópias de banco | `%LOCALAPPDATA%\MyTaskApp\database-operations` | `~/.local/share/MyTaskApp/database-operations` |

O banco é criado e atualizado pela própria aplicação na inicialização (migrations
do EF Core). Para fazer backup, copie o `mytaskapp.db` com o app fechado. A
variável **`MYTASKAPP_DATA_DIR`** move banco, estado e logs para outra pasta.
Configuração avançada (`appsettings.user.json`): [`docs/configuration.md`](docs/configuration.md).

## Desenvolvimento

```bash
dotnet tool restore
dotnet build MyTaskApp.slnx
dotnet run --project src/MyTaskApp.Desktop   # cuidado: usa o mesmo banco do app instalado
dotnet test MyTaskApp.slnx
```

Pré-requisitos, solutions, migrations, instalador local e como contribuir:
[`docs/development/README.md`](docs/development/README.md). Testes e cobertura:
[`docs/testing/README.md`](docs/testing/README.md). Release e versionamento
(Release Please, SemVer, tags que não se movem):
[`docs/release-process.md`](docs/release-process.md).

## Tecnologias

.NET 10, Avalonia 12 (MVVM com CommunityToolkit.Mvvm), EF Core + SQLite, Serilog,
xUnit v3 + AwesomeAssertions + NSubstitute. O servidor de sincronização usa
ASP.NET Core, PostgreSQL e OpenIddict; o celular, .NET MAUI. As versões exatas
ficam em `Directory.Packages.props` e `global.json`. Arquitetura:
[`docs/architecture/overview.md`](docs/architecture/overview.md).

## Documentação

- [`docs/README.md`](docs/README.md): índice de toda a documentação, por assunto.
- [`docs/architecture/README.md`](docs/architecture/README.md): as decisões de arquitetura (ADR-001 a ADR-090).
- [`AGENTS.md`](AGENTS.md): instruções para agentes de IA.
- [`CHANGELOG.md`](CHANGELOG.md): o que mudou em cada versão.

## Licença

Distribuído sob a licença MIT. O texto completo está em [`LICENSE`](LICENSE).

Os ícones da fonte embutida (`src/MyTaskApp.Desktop/Assets/Fonts/MyTaskAppIcons.ttf`)
são do [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons),
© Microsoft Corporation, também sob licença MIT.
