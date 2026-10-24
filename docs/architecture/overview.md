# Visão geral da arquitetura

Camadas, projetos, stack e as regras transversais que não pertencem a um ADR só.
As decisões, uma por arquivo, estão no [índice dos ADRs](README.md).

## Camadas

Clean Architecture: as dependências apontam para dentro. O grafo abaixo é o dos
`ProjectReference` dos `.csproj` (conferido em 2026-10-24):

```
Desktop ──► Application ──► Domain
   │            ▲   └─────► Sync.Contracts, Remote.Contracts
   ├──► Infrastructure ─┘ (também referencia os dois Contracts)
   └──► Mcp ──► Application

Mobile ──► Mobile.Core ──► Application, Infrastructure

Server.Api ──► Server.Infrastructure ──► Server.Application ──► Sync.Contracts, Remote.Contracts
Server.Api ──► Remote.Contracts
```

`tests/MyTaskApp.Desktop.Tests/Architecture/LayerDependencyTests.cs` e
`tests/MyTaskApp.Mobile.Core.Tests/Architecture/MobileLayerTests.cs` quebram a
build quando uma camada pula outra: o Domain não referencia nada do app, de
persistência nem de UI, e o desktop não conhece o celular.

| Projeto | Responsabilidade |
|---|---|
| `MyTaskApp.Domain` | Entidades e regras puras: `TaskItem` (a série) e `TaskOccurrence` (cada dia), lembretes, prazos, ciclo de vida, etiquetas, ambientes de desenvolvimento, sessões de agente. Sem dependências externas. |
| `MyTaskApp.Application` | Casos de uso (uma classe por caso de uso, sem mediador, [ADR-009](decisions/009-casos-de-uso-sem-mediador.md)), agendadores sobre `TimeProvider` e as portas (`ITaskItemRepository`, `IGitClient`, `IAgentCliProvider`, …). |
| `MyTaskApp.Infrastructure` | EF Core + SQLite, Git, GitHub CLI, Jira, processos, terminais, hooks do Claude Code, sons, cofre de segredos, PostgreSQL das operações de banco, cliente da sincronização e da orquestração remota. |
| `MyTaskApp.Mcp` | Servidor MCP local: Kestrel no loopback, ferramentas, resources e prompts sobre os casos de uso ([ADR-059](decisions/059-servidor-mcp-local-loopback.md), [guia](../mcp-server.md)). |
| `MyTaskApp.Desktop` | Avalonia + MVVM (CommunityToolkit.Mvvm), bandeja, temas, editor Markdown, corretor ortográfico e a composição (DI + Serilog). |
| `MyTaskApp.Sync.Contracts` | Contrato da sincronização entre desktop, celular e servidor ([ADR-061](decisions/061-sincronizacao-outbox-mescla-por-campo.md)). |
| `MyTaskApp.Remote.Contracts` | Contrato da orquestração remota, fase 2 ([ADR-084](decisions/084-orquestracao-remota-coordenador-e-agente.md)). |
| `MyTaskApp.Server.*` | Servidor de sincronização opcional: ASP.NET Core + PostgreSQL ([guia](../sync-server.md)). |
| `MyTaskApp.Mobile.Core` / `MyTaskApp.Mobile` | App do celular: o núcleo sem MAUI (ViewModels, composição) e a casca .NET MAUI ([ADR-067](decisions/067-celular-dispositivo-da-sincronizacao.md), [guia](../mobile.md)). |

## Stack

As versões exatas moram só em `Directory.Packages.props` (Central Package
Management) e o SDK em `global.json`. Esta tabela registra **por que** cada peça
está ali; não repita versões aqui, porque elas envelhecem.

| Item | Observação |
|---|---|
| .NET 10 (SDK fixado em `global.json`) | `rollForward: latestFeature` |
| Avalonia 12 | TFM `net10.0` nativo. `Avalonia.Diagnostics` não existe na linha 12.x (parou na 11.3.22); o DevTools deixou de ser pacote separado. |
| CommunityToolkit.Mvvm | `netstandard2.0`, compatível |
| EF Core + SQLite | Banco do desktop e do celular. Só por migrations ([ADR-005](decisions/005-persistencia-por-interfaces-estreitas.md)). |
| Serilog | Log estruturado do desktop |
| xUnit **v3** | Obrigatório: `Avalonia.Headless.XUnit` depende de `xunit.v3.extensibility.core` |
| AwesomeAssertions | No lugar do FluentAssertions, por licença ([ADR-006](decisions/006-awesomeassertions-no-lugar-de-fluentassertions.md)) |
| NSubstitute | Dublês nos testes |
| Avalonia.Controls.ColorPicker | Só o `ColorView` da janela de etiquetas ([ADR-025](decisions/025-etiquetas-n-n-bolinhas.md)) |
| Npgsql | ADO.NET das operações de banco ([ADR-056](decisions/056-operacoes-de-banco-politica-central.md)); a versão acompanha a que o provider do EF exige |
| Npgsql.EntityFrameworkCore.PostgreSQL | Só o servidor de sincronização; o desktop continua só SQLite |
| ASP.NET Core Identity + Google + DataProtection.EF | Contas do servidor de sincronização, só por login externo ([ADR-061](decisions/061-sincronizacao-outbox-mescla-por-campo.md)) |
| OpenIddict | Servidor OAuth do desktop: code + PKCE, refresh rotativo, revogação ([ADR-061](decisions/061-sincronizacao-outbox-mescla-por-campo.md)) |
| Testcontainers.PostgreSql | Testes do servidor contra PostgreSQL real; pulam sem Docker |
| Caddy (`caddy:2.10-alpine`, no compose) | Só o proxy TLS do compose de produção do servidor ([ADR-070](decisions/070-servidor-sync-production-tls.md)) |
| .NET MAUI | Só o app do celular, Android e iOS; a versão acompanha o workload `maui-android` |

## Convenções do domínio

- **`TaskItem`, não `Task`.** `Task` colide com `System.Threading.Tasks.Task` e
  envenenaria todo arquivo com `async`. Código e testes em inglês; interface do
  usuário em pt-BR.
- **Edição atômica.** `TaskItem.Update(title, description, priority)` normaliza
  e valida **tudo** antes de atribuir qualquer campo. Sem isso, um título
  recusado depois de a descrição já ter sido trocada deixaria a entidade
  meio-editada em memória, e o EF persistiria essa meia-alteração no próximo
  `SaveChanges`. Hoje só o título pode ser recusado (em branco ou acima de 200
  caracteres): a descrição não tem teto, e o SQLite guarda TEXT sem limite. A
  ordem de validação continua importando para quando outro campo ganhar regra.
- **"Agora" vem de `TimeProvider`**, nunca de `DateTime.Now`; a hora de parede
  vira instante só na borda ([ADR-002](decisions/002-hora-de-parede-no-dominio.md)).
- **`DomainException` é a mensagem que o usuário lê**; o resto vai para o log
  ([ADR-008](decisions/008-domainexception-falha-que-o-usuario-le.md)).
- **Um escopo de DI por operação** (`IUseCaseRunner`): o widget de longa duração
  não segura um `DbContext` aberto ([ADR-012](decisions/012-iusecaserunner-um-escopo-por-operacao.md)).

## Persistência: o que a implementação confirmou

Regras do EF Core + SQLite que quebram em silêncio. Valem para qualquer entidade
nova.

- **Concessão do domínio ao EF.** `TaskOccurrence` tem um construtor privado sem
  parâmetros, usado só na materialização. `TaskItem` não precisou: o EF liga o
  construtor privado existente pelos nomes dos parâmetros. A coleção
  `Occurrences` continua somente-leitura para fora; o EF escreve direto no campo
  `_occurrences` via `PropertyAccessMode.Field`.
- **Tipos no SQLite.** `DateOnly`, `TimeOnly` e `DateTimeOffset` viram TEXT por
  padrão, com teste de round-trip exato para os três. Colunas de instante usam
  ticks UTC ([ADR-011](decisions/011-datetimeoffset-em-ticks-utc.md)).
- **`TimeSpan` também vira ticks.** O padrão do EF no SQLite guardaria TEXT
  `"hh:mm:ss"`, desordenado e intraduzível em comparação (o mesmo erro do
  ADR-011 para `DateTimeOffset`). `TimeSpanTicksConverter` resolve os dois pelo
  mesmo caminho. O `[Flags] AlertChannels` vira `int`, para um futuro
  `(Channels & Sound) != 0` continuar traduzível.
- **Instância de tipo owned não pode ser compartilhada entre donos.**
  `ReminderPolicy.Default` é uma instância estática, e a captura rápida aplica a
  mesma política a várias tarefas. Com a mesma instância em dois donos, o EF
  grava uma e deixa a outra com os valores default, **em silêncio** (a primeira
  tarefa de cada captura nascia com o lembrete desligado).
  `TaskItem.ChangeReminder` copia (`policy with { }`) antes de guardar; há teste
  de regressão no domínio e contra o banco.
- **Owned type precisa de uma coluna obrigatória.** O EF não distingue "owned
  ausente" de "todas as colunas nulas": sem `Reminder_Attempt`/`Reminder_IsEnabled`
  NOT NULL mais `.Navigation(...).IsRequired()`, o modelo estoura na primeira
  consulta, não no build.
- **Upgrade não arma nada.** Uma migration que introduz comportamento novo não o
  liga nos dados existentes: toda tarefa que já existia recebeu
  `ReminderPolicy.None` na migration dos lembretes.
- **Implementações são `internal`.** O Desktop referencia a Infrastructure só
  para chamar `AddInfrastructure()`. Os testes de integração alcançam as classes
  por `InternalsVisibleTo`.
- **Migrations com tooling local.** O `dotnet-ef` precisa ser ≥ o runtime do EF;
  por isso ele está em `.config/dotnet-tools.json` (`dotnet tool restore`), sem
  depender do que estiver instalado globalmente.
- **Banco e logs na pasta de dados do usuário**, nunca no diretório de instalação,
  que pode ser somente-leitura (`UserDataLocation`, `MYTASKAPP_DATA_DIR`).
- **O quadro "Hoje" se refresca sozinho a cada 60 s.** Sem isso, "aguardando há N
  minutos" envelhecia e AGORA/ATRASADAS congelavam com a janela aberta.
