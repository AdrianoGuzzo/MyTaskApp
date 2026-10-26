# Testes

Estratégia, comandos e armadilhas conhecidas. Ambiente e build estão em
[`../development/README.md`](../development/README.md).

## Comandos

```bash
dotnet test MyTaskApp.slnx          # desktop, celular sem MAUI e contratos
dotnet test MyTaskApp.Server.slnx   # servidor de sincronização (+ ponta a ponta)
dotnet test tests/MyTaskApp.Domain.Tests   # uma suíte só
dotnet test MyTaskApp.slnx --filter "FullyQualifiedName~TodayClassifier"   # um recorte
```

Portão de cobertura, o mesmo do CI (mínimo de **80% de linhas** e **70% de
branches**, definidos só nos parâmetros de `scripts/coverage.ps1`):

```powershell
pwsh ./scripts/coverage.ps1 -Open   # relatório em artifacts/coverage/report/index.html
```

O script roda as duas solutions, junta os relatórios (ReportGenerator) e reprova
se algum teste falhar ou se a cobertura ficar abaixo do mínimo. Baixar o mínimo
exige um motivo no PR. O que conta para a cobertura está em `coverage.runsettings`
(migrations e `obj/` ficam fora).

## Suítes

xUnit v3, AwesomeAssertions e NSubstitute (`tests/Directory.Build.props`). Uma
suíte por projeto:

| Projeto | Cobre |
|---|---|
| `MyTaskApp.Domain.Tests` | Regras puras: recorrência, classificação de "Hoje", lembretes, prazos, ciclo de vida |
| `MyTaskApp.Application.Tests` | Casos de uso e agendadores, com `FakeTimeProvider` |
| `MyTaskApp.Infrastructure.Tests` | EF Core contra **SQLite real** (as migrations rodam), Git real, processos, upgrade de banco |
| `MyTaskApp.Mcp.Tests` | Ferramentas do servidor MCP; `DocumentationTests` exige que toda ferramenta esteja em `docs/mcp-server.md` e que a contagem "São N ferramentas." bata |
| `MyTaskApp.Desktop.Tests` | ViewModels, testes de UI **headless** (Avalonia.Headless) e a guarda de camadas (`Architecture/LayerDependencyTests`) |
| `MyTaskApp.Packaging.Tests` | Contratos entre código e instalador (mutex, `--startup`, versão × manifest do Release Please), lidos como texto, sem Inno Setup |
| `MyTaskApp.Sync.Contracts.Tests` | Contrato da sincronização: JSON, ids determinísticos, registro de campos, validação |
| `MyTaskApp.Remote.Contracts.Tests` | Contrato da orquestração remota (fase 2) |
| `MyTaskApp.Mobile.Core.Tests` | O app do celular sem MAUI: ViewModels, composição e a guarda de camadas do celular |
| `MyTaskApp.Server.Application.Tests` | Mescla por campo, push e pull do servidor, em memória |
| `MyTaskApp.Server.IntegrationTests` | O servidor contra **PostgreSQL real** (Testcontainers) e o ponta a ponta com dois desktops e um celular |

Guias com seção de testes própria: [sincronização](../sync-server.md#6-testes),
[celular](../mobile.md#testes), [MCP](../mcp-server.md#testes),
[Consulta Inteligente](../consulta-inteligente.md#testes),
[operações de banco](../database-operations.md#teste-de-integração-com-postgresql-real).

## O que testar, e onde

- **Regra de negócio no domínio, como função pura** ([ADR-010](../architecture/decisions/010-classificacao-hoje-funcao-pura.md)):
  cada borda vira um teste direto.
- **Tempo só pelo `TimeProvider`** ([ADR-002](../architecture/decisions/002-hora-de-parede-no-dominio.md), [ADR-015](../architecture/decisions/015-agendador-com-timeprovider-createtimer.md)):
  `FakeTimeProvider.Advance` dirige os agendadores sem dormir. Testes de DST usam
  `America/New_York` (São Paulo não tem mais horário de verão). Só avance o
  relógio falso com o código parado num timer pendente; avançar enquanto a
  continuação ainda espera o pool gera falha intermitente.
- **Persistência contra SQLite de verdade**, aplicando as migrations
  (`TempSqliteDatabase`), nunca `EnsureCreated` ([ADR-005](../architecture/decisions/005-persistencia-por-interfaces-estreitas.md)).
- **ViewModels com executor falso**: o teste registra *qual* caso de uso foi
  pedido; o que o handler faz já está coberto em Application e Infrastructure
  ([ADR-012](../architecture/decisions/012-iusecaserunner-um-escopo-por-operacao.md)).
- **Registro de DI**: um teste resolve cada handler do contêiner, para que
  esquecer um registro quebre a build, não a tela ([ADR-009](../architecture/decisions/009-casos-de-uso-sem-mediador.md)).

### Teste de UI: só onde paga

Binding de XAML falha em runtime, não em compilação. Há testes headless que sobem
a janela de verdade e verificam que as seções desenham e que o `Command` do
checkbox **resolve**: o binding usa um caminho com cast até o `DataContext` do
UserControl e, se quebrasse, o checkbox viraria enfeite sem ninguém avisar. Fora
isso, a UI não é testada: comportamento fica nos ViewModels.

O mesmo critério trouxe os testes da moldura do widget ([ADR-017](../architecture/decisions/017-janela-principal-como-widget.md)): o
cabeçalho liga em `#Shell.Chrome`, um caminho que o compilador aceita e que só
quebraria quando alguém abrisse o app. Foi ali que apareceu o seletor de tipo que
não casava: a build passava e o modo compacto não escondia nada. `WidgetPlacement`
é testado à parte, e sem display, porque "o painel nunca abre fora da tela" é uma
promessa que só quebra na mesa de alguém que desconectou um monitor.

## Requisitos para validar uma alteração

1. `dotnet build MyTaskApp.slnx -c Release` sem aviso (avisos são erros).
2. A suíte da área tocada passa; antes do PR, `pwsh ./scripts/coverage.ps1`.
3. Mexeu no servidor ou no contrato da sincronização: `dotnet test MyTaskApp.Server.slnx`
   com Docker ligado (sem Docker, os testes de integração **pulam**, não passam).
4. Ferramenta MCP nova ou removida: atualizar `docs/mcp-server.md` (o
   `DocumentationTests` cobra).
5. Migration nova: o upgrade de um banco existente precisa continuar passando
   (`UpgradePreservationTests` e afins na Infrastructure).

## Armadilhas conhecidas

- **App aberto derruba 4 testes.** Um `MyTaskApp.exe` vivo **na mesma sessão do
  Windows** (o instalado ou o de outro checkout) segura o mutex
  `MyTaskApp.SingleInstance` ([ADR-019](../architecture/decisions/019-instancia-unica-por-mutex-nomeado.md)), e os 4 `SingleInstanceTests` falham. Outra
  sessão do Windows não interfere. Confira com
  `Get-Process MyTaskApp | Select-Object Id, SessionId, Path` antes de culpar a
  alteração.
- **Não defina `MYTASKAPP_DATA_DIR` ao rodar `dotnet test`.** Há testes que
  verificam justamente o caminho padrão; a suíte já se isola em pasta temporária.
- **Menos testes "aprovados" do que existem, sem nenhuma falha** = o processo de
  teste caiu (stack overflow, exit code `-1073741571`). Rode com
  `--logger "console;verbosity=normal"` e procure `Stack overflow` /
  `Test process crashed`. Um caso real: `ComboBox` com `ItemTemplate` de
  `<TextBlock><Run Text="{Binding …}"/></TextBlock>` entra em ping-pong de
  binding; trocar os `Run` por `TextBlock` resolve.
- **`coverage.ps1` precisa do PowerShell 7 (`pwsh`).** No Windows PowerShell 5.1,
  a primeira linha de stderr do `dotnet test` vira exceção
  (`$ErrorActionPreference = 'Stop'`) e o script morre no meio da
  instrumentação, deixando DLL e PDB fora de par: as rodadas seguintes mostram um
  assembly com 0% (`SymbolsNotMatchingException` no `--diag`). Corrija com
  `dotnet build MyTaskApp.slnx -c Release --no-incremental`.
- **Testes intermitentes no CI** (headless Avalonia, arquivo preso em runner
  lento, processos com prazo de dreno): se o diff não toca a área, faça um rerun
  do job (`gh run rerun <id> --failed`) antes de investigar. Se repetir, é real.
- **Integração do servidor com falha em massa e rápida** costuma ser disputa de
  Docker com outra execução em paralelo (veja `docker ps`), não regressão.
  Alternativa ao Testcontainers: a variável `MYTASKAPP_TEST_SERVER_POSTGRES`
  (`tests/MyTaskApp.Server.IntegrationTests/PostgresFixture.cs`).
