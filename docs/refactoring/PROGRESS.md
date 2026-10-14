# Refatoração sem mudança de comportamento — acompanhamento

Princípio: **refatorar a implementação, não o produto.** Cada etapa é um commit
próprio (`refactor:`, `test:`, `fix:` ou `docs:`) na branch
`feature/fatoracao-do-codigo`. Correção de defeito só entra quando autorizada, e
sempre separada da refatoração.

## Estado

| Etapa | Tipo | Situação |
|---|---|---|
| E0 — Inventário e linha de base | docs | concluída |
| E1 — Guarda de camadas | test | concluída |
| E2 — Relógio pelo `TimeProvider` | refactor | concluída |
| E3 — Portas da UI fora de `Views` | refactor | concluída |
| E4 — Caminho único de erro nos ViewModels (ADR-008) | refactor | concluída (7 de 19 telas; ver registro) |
| E5 — Regras pequenas saem dos ViewModels | refactor | 5a concluída; 5b–5e pendentes |
| E6 — Duplicação na Infrastructure | refactor | 6a descartada, 6b concluída, 6c pendente |
| E7 — Composition root | refactor | pendente |
| F6 — `ProcessRunner` preso por um neto | fix | concluída |
| F4 — Vincular a outra conta é tudo ou nada | fix | concluída |
| F5 — Perfil de anonimização não sobrescreve o MCP | fix | concluída |
| F1 — Contêiner descartado só depois do encerramento | fix | concluída |
| F2 — Seletor do sistema que falha não fecha o app | fix | concluída |
| F3 — Buscas da janela da tarefa canceladas ao fechar | fix | concluída |

## Linha de base (13/10/2026, commit `7f48341`)

**Ambiente.** SDK 10.0.402. O SDK do .NET 10 não estava instalado na máquina, e o
instalador MSI falhou ao extrair (`0x8007025d`). Por isso o SDK foi instalado por
usuário, com o `dotnet-install.ps1`, em `%LOCALAPPDATA%\Microsoft\dotnet`.

O MyTaskApp instalado estava aberto na mesma sessão do Windows. O Docker Desktop
foi ligado para os testes do servidor.

**Build.** `dotnet build MyTaskApp.slnx -c Release` e
`dotnet build MyTaskApp.Server.slnx -c Release` compilam com 0 erros. Os warnings
viram erro: `TreatWarningsAsErrors` e `EnforceCodeStyleInBuild` estão ligados.

**Testes.** `dotnet test <solution> -c Release --no-build`:

| Projeto | Aprovados | Falhas | Ignorados |
|---|---|---|---|
| Domain.Tests | 912 | 0 | 0 |
| Application.Tests | 1291 | 0 | 0 |
| Infrastructure.Tests | 877 | 0 | 7 |
| Desktop.Tests | 2301 | **4** | 2 |
| Mcp.Tests | 77 | 0 | 0 |
| Packaging.Tests | 72 | 0 | 0 |
| Sync.Contracts.Tests | 36 | 0 | 0 |
| Server.Application.Tests | 45 | 0 | 0 |
| Server.IntegrationTests (PostgreSQL real, Testcontainers) | 42 | 0 | 0 |

- **As 4 falhas são de ambiente e já existiam.** São
  `SingleInstanceTests.{TheFirstLaunchOwnsTheApplication,
  ClosingTheAppLetsTheNextLaunchIn, TheOwnerIsToldWhenSomeoneClicksTheShortcutAgain,
  ASecondLaunchDoesNotOwnIt}`. O app instalado segura o mutex da sessão.
- **Ignorados, todos por ambiente:**
  - os testes de PostgreSQL local, sem `MYTASKAPP_TEST_POSTGRES` e
    `MYTASKAPP_TEST_PG_BIN`;
  - os testes só de Linux e de Unix;
  - duas palavras em inglês do corretor do Windows.
- **Resultado que às vezes some do relatório (já existia):**
  `QuickCommandTerminalTests.WatchExitCode_ReportsTheExitCode_OfAProcessItDidNotStart`
  fica de fora do total em algumas rodadas, sem falhar.
  - Faltou na linha de base (Infrastructure com 884), apareceu depois da E2 (886, com
    1 teste novo) e faltou de novo depois da E3 (885).
  - Duas rodadas seguidas, comparadas pelo `.trx`, diferem só nele.
  - Ele roda um `cmd.exe` real e não tem relação com as etapas. Fica registrado, e
    não é investigado aqui.

**Cobertura:** veja a seção [Cobertura](#cobertura).

**Contratos registrados:**

- **Migrations do desktop:** 35, e a última é `20261013200115_DevicePlacement`.
- **Migration do servidor:** 1, `20261013055355_InitialServer`.
- **`MyTaskApp.Sync.Contracts`:** `Messages`, `SyncSchema` e as definições,
  `SyncErrorCodes`, `SyncIds` (UUIDv5), `SyncJson` e `SyncCursor`.
- **Ferramentas MCP:** 82 (`McpServerTool`).

Nada disso pode mudar sem autorização.

**Fluxos que precisam continuar iguais:** a tela Hoje e a classificação, o
ciclo da tarefa, do checklist e da ocorrência, a recorrência e a materialização,
o cronômetro e os lançamentos, os lembretes e prazos, as etiquetas e os
diretórios, a aba Desenvolvimento (worktree, comandos, banco do ambiente, PR,
agente), as operações de banco, o MCP, a sincronização (captura, outbox, push e
pull, conflito, reaplicação), os post-its, o HUD e a bandeja, a instância única,
iniciar com o Windows, e o encerramento.

## Inventário

O inventário saiu de três varreduras de leitura do código, conferidas por
amostragem. A base já é disciplinada:

- nenhum ViewModel usa `DbContext`, `Process`, `HttpClient` ou arquivo
  diretamente, e são 202 chamadas por `IUseCaseRunner`;
- o contêiner é montado com `validateScopes: true` e não há *captive
  dependency*;
- um `ProcessRunner` com `ArgumentList` e cancelamento;
- `PGPASSWORD` mascarado;
- o outbox gravado no mesmo `SaveChanges` da mudança;
- dois `HttpClient` longevos com `PooledConnectionLifetime`;
- o código específico do Windows atrás de portas.

Por isso a refatoração é pontual, e não estrutural.

### Comprovados

| # | Onde | Problema e evidência | Risco | Proposta | Preservar | Testes | Prioridade |
|---|---|---|---|---|---|---|---|
| I1 | Desktop/ViewModels (~20 classes) | `TryAsync`/`RunAsync` copiado: `DomainException` → mensagem, outra → log + fallback (ex.: `TodayViewModel.cs:1862`, `TagsViewModel.cs:852`, `ReminderSettingsViewModel.cs:160`). As variantes diferem: limpam ou não o status, cada uma tem o próprio evento de log, e `SyncViewModel` trata `SyncApiException`. | Uma correção no caminho de erro precisa ser feita 20 vezes. | E4: `ScreenOperation` parametrizado, aplicado só onde a variante bate exatamente. | O evento de log, a ordem de limpeza, `IsBusy` no `finally`, o retorno `bool`. | Teste de mensagem e de fallback por ViewModel convertido. | Média |
| I2 | Mcp, Application, Infrastructure, Desktop | Relógio lido direto: `McpGateway.cs:48,120`, `McpSecurityMiddleware.cs:55`, `RecordAgentEvent.cs:216`, `SyncHttp.cs:137`, `WindowsTerminalLauncher.cs:150`, `McpServerViewModel.cs:96`. | Não dá para testar sem esperar o relógio. Contraria o ADR-015. | E2: `TimeProvider` injetado. | Valores idênticos com `TimeProvider.System`. | Rate limit do MCP e `Retry-After` com `FakeTimeProvider`. | Alta (baixo risco) |
| I3 | `Views/ConfirmWindow.axaml.cs:36` | `IConfirmationDialog` e outras portas declaradas em `Views`. 17 ViewModels importam `MyTaskApp.Desktop.Views`. | O ViewModel depende do namespace da View. Contraria o MVVM. | E3: mover as declarações para `MyTaskApp.Desktop.Dialogs`. | As implementações e o registro no DI. | Guarda de camadas. | Média |
| I4 | (testes) | Não há teste de arquitetura. As regras de camada (ADR-005/009/012) só valem por disciplina. | Um atalho futuro passa sem ninguém ver. | E1: testes de dependência por reflexão. | — | O próprio teste. | Alta |
| I5 | `TaskDevelopmentViewModel.cs:952-961, 1024-1035` | A escolha da branch de origem (anterior → preferida → do diretório → irmã → sugerida) está no ViewModel. | Regra de negócio sem teste e fora da Application. | E5a: função pura em `Application/Development/ExistingBranches.cs`. | A ordem exata e o aviso "branch padrão do diretório". | Caracterização no ViewModel, depois na Application. | Média |
| I6 | `DevelopmentDatabaseViewModel.cs:686-687, 768, 795-819`; `DatabaseCopyViewModel.cs:199-216, 410`; `DatabaseProfilesViewModel.cs:278-292` | O pedido de cópia (`DatabaseCopyOptions`, a operação "anonimizar se exigido") é montado em três ViewModels. | Uma flag pode divergir entre as telas. | E5b: *factory* ao lado de `DatabaseCopyPlanner`. | Cada flag, por origem. | Caracterização por tela. | Média |
| I7 | `Mcp/Tools/AnonymizationProfileTools.cs:346-347`; `Desktop/ViewModels/DatabaseOperationsItems.cs:336` | "Trocar a máscara reinicia o argumento" está escrito nos dois adaptadores. | O MCP e a tela podem divergir. | E5c: um único ponto no Domain ou na Application. | Mesmo argumento padrão por máscara. | Caracterização dos dois lados. | Média |
| I8 | `SyncViewModel.cs:476-537, 587` | O ViewModel decodifica o JSON do protocolo (`SyncValue.Parse`) e usa o literal `"merged_on_join"`. | O Desktop fica acoplado ao formato do fio. | E5d: o `ListSyncConflictsHandler` entrega o texto pronto, e o literal vira constante. | Os mesmos textos na janela. O JSON não muda. | `SyncViewModelTests`, `DevicePlacementViewModelTests`. | Média |
| I9 | `Views/TaskNotesWindow.axaml.cs:184-244` | Árvore de 5 ramos decidindo se a janela da tarefa pode fechar, no code-behind. | Sem teste. | E5e: `TaskNotesViewModel.DecideClose()`, no molde de `WidgetChromeViewModel.DecideClose()`. | Os ramos e as mensagens "Aguarde…"/"Feche de novo…". | Testes dos 5 ramos. | Média |
| I10 | `Infrastructure/Processes/ProcessRunner.cs:231-242`; `ShellCommandExecutor.cs:131-142` | `Kill` da árvore de processos parecido nos dois. **Revisto na E6a: não são iguais.** O `ProcessRunner` só engole `InvalidOperationException`; o `ShellCommandExecutor` também engole `Win32Exception`. | Unificar mudaria o que o `ProcessRunner` faz quando o `Kill` falha (hoje a exceção sobe). | **Descartado**, até haver decisão sobre essa semântica. | — | — | — |
| I11 | `Infrastructure/Sync/SyncLocalStore.cs:439` | `new SyncReconciler(..., NullLogger<SyncReconciler>.Instance)`: os logs do merge no push somem. | Diagnóstico de sincronização às cegas. | E6b: logger real pelo `ILoggerFactory`. | Só muda o log, sem dado sensível. | Testes de sync existentes. | Baixa |
| I12 | `Application/DatabaseOperations/AnonymizationProfileEditing.cs:381, 421` | Handlers de conexão (`DatabaseUsageHandlers`, `DuplicateDatabaseConnectionHandler`) no arquivo de perfis. | Coesão: o handler é procurado no arquivo errado. | E6c: arquivo próprio, mesmo namespace. | Os tipos e o registro. | O teste que resolve cada handler do contêiner. | Baixa |
| I13 | `App.axaml.cs:628-670` e o bloco Show/Activate (×10) | `X.Changed += () => Dispatcher.UIThread.Post(() => _ = todayViewModel.LoadAsync(...))` repetido. | Ruído no composition root. | E7: helper local. | A ordem das assinaturas e a thread. | Testes de composição. | Baixa |
| D1 | `App.axaml.cs:854`; `Program.cs:45` | `Exit` dispara `Task.Run(Shutdown)` e o `Program` descarta o contêiner em seguida. O `Shutdown` pode resolver serviços num contêiner já descartado, e a exceção nunca é observada. | Agendador, MCP ou bandeja podem não ser liberados. | F1 (autorizado). | A janela some na hora. A ordem do `Shutdown`. | Teste da espera. | Alta |
| D2 | Views: `AgentAlertSoundsWindow:45`, `DevelopmentCommandsWindow:48`, `DirectoryCommandWindow:37`, `TimeEntryWindow:95`, `TaskNotesWindow:489, 502`, `TagsWindow:79` | `async void` sem try/catch: um seletor de arquivo que falhar derruba o processo. | Crash. | F2 (autorizado). | O resultado de sucesso. | Por handler, onde for testável. | Alta |
| D3 | `TaskDevelopmentViewModel.cs:107, 327` | `_inspection` e `_pullRequestLookup` não são cancelados ao fechar a janela. | git e gh continuam rodando para uma janela fechada. | F3 (autorizado). | Comandos do usuário (`_run`) não são cancelados. | Teste de cancelamento. | Média |
| D4 | `SyncLocalStore.cs:64-110` (`LinkAsync`) | Três `ExecuteDeleteAsync` com commit próprio, e só depois o `SaveAsync` do vínculo. | Uma queda no meio apaga o outbox e mantém o vínculo antigo. Na volta, a varredura não roda. | F4 (autorizado): uma transação. | A semântica de "outra conta". | Falha injetada no save. Ponta a ponta do servidor. | Alta |
| D5 | `ProfileHandlers.cs:85-125` | `SaveAnonymizationProfileHandler` troca todas as regras sem conferir versão. O MCP exige `ExpectedUpdatedAt` (`AnonymizationProfileEditing.cs:133-146`). | A tela aberta antes de uma edição do MCP apaga essa edição em silêncio. | F5 (autorizado): `ExpectedUpdatedAt` opcional. | A chamada interna do MCP (`:310`) sem conferência, como hoje. | Teste do conflito. | Média |
| D6 | `ProcessRunner.cs:156` | Depois de uma saída normal, `await readOutput, await readError` sem limite. O `ShellCommandExecutor` limita a 5 s. | git ou gh com um neto segurando o stdout trava a operação. | F6 (autorizado). | O resultado completo no caso normal. | Neto segurando o stdout. | Média |

### Hipóteses e defeitos só documentados

Ficam sem mudança. Decidir antes de mexer.

| # | Onde | Observação |
|---|---|---|
| H1 | `Application/Sync/SyncEngine.cs:110-111` | `SetStateAsync(NeedsReconnect)` e `RecordErrorAsync` em escopos separados. Uma queda entre os dois deixa o estado sem o erro. Não autorizado. |
| H2 | `Server.Infrastructure/.../AccountStores.cs:110` | Todo `DbUpdateException` é tratado como "dois logins ao mesmo tempo". Outra falha vira um `SingleAsync` confuso. |
| H3 | `Persistence/DatabaseOptions.cs:23` | SQLite sem WAL. Uma página longa de pull pode segurar a tela e o MCP até o timeout de 30 s. Mudar o journal muda os arquivos no disco: precisa de decisão. |
| H4 | `GitClient.cs:346-350`, `GhCliPullRequestClient.cs:154-158`, `StartDevelopment.cs:237-241`, `QuickCommandHandlers.cs:349-356` | O stderr de git e gh e a linha do comando rápido vão para o log sem máscara. O git costuma anonimizar a URL, mas um parâmetro digitado pode ser segredo. |
| H5 | `tests/.../NoBulkWritesTests.cs` | Só procura `ExecuteDelete`/`ExecuteUpdate`/`ExecuteSql*`. Um ADO cru fora de `Sync/` passaria. |
| H6 | `SyncLocalStore.cs:437` | `ChangeTracker.Clear()` no meio do laço. Hoje está correto só porque cada volta consulta de novo. |
| H7 | `Desktop/Views/StickyNoteWindow.axaml.cs:55-66, 239-240` | O *debounce* do autosave fica na Window, e o fechamento dispara `FlushAsync` sem esperar. |
| H8 | `MainWindow.axaml.cs:290-299` | `PersistNow` grava arquivo de forma síncrona na thread da UI. O arquivo é pequeno. |

### Para próximas etapas (fora do escopo desta sessão)

- **Fluxo "Iniciar implementação"** (`TaskDevelopmentViewModel.cs:1171-1338`). Validação → banco → worktree → conflito → cópia → comandos, com mensagens de falha parcial. Precisa de caracterização completa (o caminho do banco não tem teste) antes de virar coordenador na Application. Só a pergunta de conflito fica na UI.
- **Troca de cronômetro duplicada** (`TodayViewModel.StartTimerAsync` e `TaskTimeLogViewModel.ToggleTimerAsync`). Área crítica. Caracterizar a confirmação e o `replace` antes de unificar.
- **Remoção de worktrees em lote** (`TodayViewModel.cs:1400-1494`). Cada remoção roda no próprio escopo. Juntar num handler mudaria a semântica de falha parcial.
- **Mescla de perfil no `DatabaseProfilesViewModel.cs:529-620`** (sugestões, tabelas puladas, regras não confirmadas).
- **`Focus*Handler` duplicados** (`AgentSessionHandlers.cs:458` e `CommandExecutionHandlers.cs:143`), os *monitors* que divergiram (`AgentSessionMonitor` com `_gate` e `CommandExecutionMonitor` sem), e o mapeamento de erro repetido em `JiraHttp`/`SyncHttp`.
- **Vários handlers por arquivo** (`ProfileHandlers.cs` com cerca de 17), e os helpers `Replace<T>` (×4) e `OnUi` (×3) nos ViewModels.

## Registro das etapas

### E0 — Inventário e linha de base

- **O que foi feito:** este arquivo.
- **Código de produção:** nenhuma mudança.

### E1 — Guarda de camadas

- **O que foi feito:** `tests/MyTaskApp.Desktop.Tests/Architecture/LayerDependencyTests.cs`,
  por reflexão e sem pacote novo:
  - o Domain não referencia nada do app, nem EF, Npgsql ou Avalonia;
  - o Sync.Contracts não referencia nada do app;
  - a Application não referencia Infrastructure, Mcp, Desktop, EF, Npgsql nem Avalonia;
  - o Mcp não referencia Infrastructure, EF, Npgsql nem Avalonia;
  - nenhum tipo em `MyTaskApp.Desktop.ViewModels` guarda ou recebe
    `IServiceProvider`, `IServiceScopeFactory`, `DbContext`, `Process` ou `HttpClient`,
    nem embrulhado em `Func<>`, `Lazy<>` ou array;
  - nenhum ViewModel depende de tipo da Infrastructure;
  - um teste de sanidade confirma que a reflexão enxerga dependências que existem.
- **Resultado:** os 7 testes passam sobre o código atual, que portanto já cumpria
  as regras.
- **Comando:** `dotnet test tests/MyTaskApp.Desktop.Tests -c Release --filter
  FullyQualifiedName~LayerDependencyTests`.

### E2 — Relógio pelo `TimeProvider`

- **Problema (I2):** quatro componentes liam `DateTimeOffset.UtcNow` direto, apesar
  de o `TimeProvider` já estar registrado. Por isso o limite de registros de recusa
  do MCP e o `Retry-After` em forma de data só podiam ser testados esperando o
  relógio de verdade.
- **O que mudou:**
  - `Mcp/Hosting/McpGateway.cs`: as entradas de atividade, inclusive a recusa em
    somente leitura, usam o `TimeProvider` do contêiner do app.
  - `Mcp/Hosting/McpSecurityMiddleware.cs`: o limite de um registro de recusa por
    segundo usa o `TimeProvider`. O host do MCP já registrava a mesma instância
    (`McpServerManager.cs:351`).
  - `Application/Agents/RecordAgentEvent.cs`: o *fallback* de `ActivityChangedAt`
    usa o `timeProvider` que o handler já recebia.
  - `Infrastructure/Sync/Http/SyncHttp.cs`: o `Retry-After` com data calcula o tempo
    restante pelo `TimeProvider`. O construtor ganhou o parâmetro, e o registro no
    DI e os dois pontos de teste que constroem a classe foram atualizados.
- **Preservação:** em produção a instância é `TimeProvider.System` (registrada com
  `TryAddSingleton` em Application e Infrastructure), que devolve o mesmo valor de
  `DateTimeOffset.UtcNow`. A ordem das operações não mudou.
- **Caracterização antes da troca, ambos verdes sobre o código antigo:**
  - `SyncClientTests.ARetryAfterDate_BecomesTheTimeLeftUntilIt`, com o relógio real
    e tolerância de 5 s;
  - `ServerLifecycleTests.AfterTheInterval_TheNextDenial_IsRecordedWithTheSuppressedCount`,
    com espera real de 1,2 s.
- **Depois da troca:**
  - os dois testes ficaram determinísticos (`FakeTimeProvider`, e um `ManualClock`
    no MCP, que só fixa a hora e mantém os temporizadores do sistema);
  - o `McpTestHost` aceita um relógio;
  - novo teste: `AWriteRefusedWhileReadOnly_IsRecordedAtTheAppsTime`.
- **Ficaram de fora, com justificativa:**
  - `WindowsTerminalLauncher.cs:150`: um *fallback* do *fallback*, quando o sistema
    nega o `Process.StartTime`, num método estático. Levar o relógio até ali mudaria
    o construtor público usado pelo DI sem ganho de teste;
  - `McpServerViewModel.cs:96`: o valor inicial do campo é sobrescrito no próprio
    construtor por `Show(manager.Status)`.
- **Testes:**
  - Mcp.Tests: 79 aprovados;
  - Infrastructure.Tests `~Sync`: 131 aprovados;
  - Application.Tests `~Agent`: 113 aprovados;
  - suíte completa das duas solutions: só as 4 falhas de ambiente já conhecidas;
  - Desktop.Tests: 2308 aprovados;
  - Server.IntegrationTests: 42 aprovados.

### E3 — Portas da UI fora de `Views`

- **Problema (I3):** `IConfirmationDialog`, `ConfirmationRequest` e
  `IQuickCommandPrompt` eram declarados nos arquivos das janelas, no namespace
  `MyTaskApp.Desktop.Views`. Para usar a porta, 17 ViewModels importavam o namespace
  das janelas. Enquanto isso, `ITimeEntryEditor` e `IDirectoryCommandEditor` já
  moravam em `MyTaskApp.Desktop.ViewModels`.
- **O que mudou:**
  - as três declarações foram para `ViewModels/IConfirmationDialog.cs` e
    `ViewModels/IQuickCommandPrompt.cs`, sem alterar membros nem documentação;
  - as implementações (`ConfirmationDialog`, `QuickCommandPrompt`) continuam
    nas janelas;
  - os 17 `using MyTaskApp.Desktop.Views;` saíram dos ViewModels, e nenhum deles
    usava outra coisa de `Views`;
  - os dois *fakes* de teste trocaram o `using`.
  - Optei pelo namespace dos ViewModels, e não por um `Dialogs` novo, para seguir o
    precedente que já existia.
- **Preservação:** só namespace, sem mudança de assinatura, registro no DI ou XAML.
  Nenhum tipo movido é serializado nem resolvido por nome.
- **Guarda:** `LayerDependencyTests.NoViewModel_DependsOnAView`.
- **Testes:** `dotnet test MyTaskApp.slnx -c Release`.
  - Desktop.Tests: 2309 aprovados, e as 4 falhas de ambiente de sempre.
  - O resto igual à E2.

### E4 — Caminho único de erro nos ViewModels (ADR-008)

- **Problema (I1):** o `TryAsync` estava copiado em 19 telas, com variações pequenas:
  - limpar ou não o `StatusMessage`;
  - tratar ou não o cancelamento como silencioso;
  - o evento de log de cada tela.

  Uma correção no caminho de erro teria de ser feita 19 vezes, e as cópias já
  divergiam na ordem dos `catch`.
- **O que mudou:**
  - `Desktop/ViewModels/ScreenErrorPath.cs` (`readonly record struct`) tem o caminho
    numa só implementação: liga o "ocupado", limpa o erro (e o status, se pedido),
    roda, trata `DomainException` → mensagem, cancelamento → silêncio (se pedido) e
    o resto → log + fallback, e desliga o "ocupado" no `finally`;
  - 7 telas passaram a usá-lo, cada uma com o próprio evento de log: Hoje,
    Etiquetas, Comandos globais, Sons do agente, Lembretes, Integrações e Conexões
    de banco. O `TryAsync` delas virou uma linha que diz só o que é da tela;
  - nota no ADR-008.
- **Equivalência:**
  - a ordem de atribuição continua a mesma (`IsBusy`, `ErrorMessage`, `StatusMessage`);
  - `DomainException` e `OperationCanceledException` não se sobrepõem, então a ordem
    dos `catch` não muda o resultado;
  - sem `CancellationIsSilent`, o cancelamento cai no `catch (Exception)`, como
    nas cópias que não o tratavam.
- **Caracterização antes da troca:** `ScreenErrorPathTests`, 23 casos verdes sobre o
  código antigo e de novo depois da troca. Por tela, eles fixam:
  - a mensagem da regra, sem log;
  - o fallback, com o evento exato de log, nível Error e a exceção;
  - `IsBusy` falso no fim;
  - a linha de status limpa só onde já era limpa;
  - o cancelamento silencioso em Integrações e Conexões de banco.

  `CapturingLogger<T>` é novo, para conferir o evento.
- **Não convertidas:**
  - `TaskIssue`, `TaskDeadline`, `TaskTimeLog` (evento com `{OccurrenceId}`),
    `DatabaseProfiles` e `McpServer`: as variantes são idênticas às convertidas,
    mas a operação passa por pré-condições que pedem preparo próprio. Ficam para
    uma próxima rodada, com caracterização própria;
  - com passo a mais, mantêm o `TryAsync` de propósito: `DataManagement` (limpa
    também `SettingsStatus`), `DatabaseDiagnostics` (sem retorno),
    `DeadlineAlert` e `ReminderAlert` (sem "ocupado", avisam `Acted`),
    `StickyNotes` (recarrega a lista), `StickyNote` (limpa o erro só no sucesso) e
    `Sync` (`SyncApiException`, recarrega no `finally`).
- **Testes:** `dotnet test MyTaskApp.slnx -c Release`.
  - Desktop.Tests: 2332 aprovados (+23), e as 4 falhas de ambiente.
  - Infrastructure: 879 aprovados.
  - O resto igual.

### E6a — `Kill` comum aos dois executores: descartada

O inventário dizia que os dois `Kill` eram idênticos. Lendo o código, não são: o
`ProcessRunner` engole só `InvalidOperationException`, e o `ShellCommandExecutor`
também engole `Win32Exception`. Um helper comum mudaria o que acontece quando o
`Kill` do `ProcessRunner` falha, porque hoje a exceção sobe para quem chamou. Sem
decisão sobre isso, nada foi mudado. O resto (bomba de saída e texto com teto) já
é compartilhado por `ProcessOutputPump` e `CapturedOutput`.

### F6 — `ProcessRunner` preso por um neto que herdou a saída (defeito D6, autorizado)

- **Defeito comprovado.** `ProcessRunnerTests.AGrandchildHoldingTheOutput_DoesNotHoldTheResult`
  usa um `cmd` (ou `sh`) que escreve "pronto" e deixa um `ping` (ou `sleep`) de
  25 s em segundo plano. Sobre o código antigo, ele levou **24 s nos dois caminhos**
  (texto cru e ao vivo): o resultado de quem já tinha saído esperava o neto morrer.
  Isso vale para git, gh, pg_* e qualquer outro processo que deixe um filho com o
  handle.
- **Correção** (`Infrastructure/Processes/ProcessRunner.cs`):
  - depois da saída normal, a espera pelo resto da saída tem teto de **30 s**
    (`DefaultDrainTimeout`).
    - A primeira versão usava 5 s, como o `ShellCommandExecutor`. Na suíte
      inteira, com o pool de threads disputado, a leitura do pipe atrasou mais que
      isso, e o "pronto" já escrito ficou de fora (stdout vazio).
    - Aqui a saída é lida por parser (o Git), e cortá-la num momento de app
      ocupado seria pior que esperar. Com 30 s, o travamento deixa de ser "para
      sempre", e perder saída exigiria 30 s de leitura parada;
  - para o teste, um construtor `internal` recebe o prazo. O DI usa o público;
  - para devolver o que já chegou, o caminho cru troca `ReadToEndAsync` por
    `RawOutput`, que lê em blocos e concatena sem tocar no texto. O resultado é o
    mesmo texto que o `ReadToEndAsync` entregaria;
  - o caminho ao vivo passa a guardar o `CapturedOutput` para ler pela metade.
- **Preservado:**
  - com a saída fechada a tempo (o caso normal), o resultado é idêntico;
  - uma falha de leitura continua subindo para quem chamou;
  - o caminho de tempo esgotado devolve o que devolvia: o texto só se a leitura
    terminou, e vazio se não terminou.
- **Testes:**
  - `ProcessRunnerGrandchildTests`: prazo de 3 s, coleção sem paralelismo, e um
    teste que fixa os 30 s de produção. Os casos novos passam abaixo de 15 s;
  - Infrastructure.Tests completo, três rodadas seguidas: 881, 882 e 881 aprovados,
    sem falha. A variação é o resultado intermitente que já existia;
  - suíte completa das duas solutions: só as 4 falhas de ambiente.
  - Server.IntegrationTests: 42 aprovados.

### E6b — O merge na hora do push deixa de jogar o log fora

- **Problema (I11):** `SyncLocalStore.MergeIntoAsync` monta um `SyncReconciler` à mão
  (ele precisa do mesmo contexto do push) e passava
  `NullLogger<SyncReconciler>.Instance`. Por isso o `SyncScanCompleted {Operations}`
  dessa varredura nunca chegava ao log.
- **O que mudou:**
  - o `SyncLocalStore` recebe `ILogger<SyncReconciler>` pelo construtor (DI) e o
    repassa;
  - o `new` continua, pelo motivo de sempre, agora com um comentário.
- **Preservação:** só o log muda, e ele carrega uma contagem, sem dado do usuário. A
  ordem das operações e as transações não mudaram.
- **Testes:** `dotnet build MyTaskApp.slnx`. Infrastructure `~Sync`: 131 aprovados.
  A suíte completa roda junto com a F4.

### F4 — Vincular a outra conta é tudo ou nada (defeito D4, autorizado)

- **Defeito comprovado.** `SyncEngineTests.LinkingToAnotherAccount_IsAllOrNothing`:
  - liga à conta A e cria uma tarefa (2 operações no outbox);
  - liga à conta B com o `SaveChanges` final falhando (`SavingChanges` lança);
  - sobre o código antigo: **outbox 2 → 0**, com a ligação ainda na conta A. Os três
    `ExecuteDeleteAsync` (outbox, retratos, conflitos) tinham commit próprio antes da
    gravação da ligação.
- **Correção** (`Infrastructure/Sync/SyncLocalStore.cs`, `LinkAsync`):
  - tudo dentro de uma transação, no mesmo padrão de `ApplyPushResultsAsync`;
  - o `captureSwitch` e a hidratação do posicionamento só acontecem depois do
    commit;
  - o único chamador (`SyncUseCases`, pelo `IUseCaseRunner`) não abre transação,
    então não há transação aninhada.
- **Preservado:**
  - o que acontece quando dá certo é idêntico: o que é apagado, o que é zerado e a
    ordem;
  - a semântica de "outra conta" (dados locais ficam e sobem como novos) não mudou;
  - nenhum contrato ou esquema mudou.
- **Testes:**
  - Infrastructure `~Sync`: 132 aprovados, com o novo;
  - suíte completa das duas solutions (com a E6b): só as 4 falhas de ambiente;
  - Infrastructure: 882 aprovados;
  - Server.IntegrationTests: 42 aprovados, inclusive o ponta a ponta com dois
    desktops, "instalação existente" e isolamento entre contas.

### E5a — A escolha da branch de origem sai do ViewModel

- **Problema (I5):** a cadeia que decide a branch de origem marcada estava no
  `LoadBranchesAsync` do `TaskDevelopmentViewModel`. A cadeia é: escolha anterior →
  tentativa anterior → padrão do diretório → outros ambientes → sugerida →
  primeira. Junto estava a busca da branch padrão do diretório
  (`ConfiguredDefaultBranch`). São regras dos ADR-031 e 035, e só dava para testar
  pelo ViewModel inteiro.
  - Correção do inventário: havia sim testes de ViewModel para parte da cadeia
    (`TaskDevelopmentViewModelTests.cs:208-244`).
- **O que mudou:**
  - `ListBranchesHandler.ChooseSource` e `ListBranchesHandler.ConfiguredDefault`
    (`Application/Development/DevelopmentQueries.cs`), ao lado do `FindConfigured`
    que já estava lá. São funções puras com os mesmos predicados, na mesma ordem;
  - o ViewModel passa as branches escolhíveis **na ordem da tela** e marca a opção
    da branch escolhida.
- **Equivalência:**
  - o `GitBranch` é `record`, então o `==` continua sendo igualdade de valor, como
    era entre as opções;
  - a primeira opção com a branch escolhida é a mesma que o `FirstOrDefault`
    antigo achava;
  - sem candidatas, o resultado continua nulo;
  - a normalização do texto digitado (`Trim().Trim('"').Trim()`) ficou no
    ViewModel, porque é da tela.
- **Testes:**
  - caracterização nova no ViewModel, verde antes e depois da troca:
    `ReloadingTheSameFolder_KeepsTheBranchTheUserChose`;
  - na Application: `ChooseSource_FollowsThePriority_OneLinkAtATime`,
    `ChooseSource_SkipsALinkThatIsNotAmongTheCandidates` e
    `ConfiguredDefault_ComesFromTheDirectoryThatIsThisFolder`, com caminhos neutros
    de plataforma;
  - `dotnet test MyTaskApp.slnx`: Application com 1294 aprovados e Desktop com 2333,
    além das 4 falhas de ambiente.

### F5 — Salvar o perfil de anonimização não apaga a mudança do MCP (defeito D5, autorizado)

- **Defeito comprovado.** `DatabaseCatalogHandlersTests.SavingOverAProfileChangedElsewhere_IsRefused_AndNothingIsWritten`:
  sobre o código antigo, o salvar de uma versão velha **foi aceito** ("Expected a
  DomainException… but no exception was thrown") e apagaria o que o MCP gravou.
  O MCP já conferia `ExpectedUpdatedAt` (`AnonymizationProfileEditing.cs:133-146`), e
  a tela não conferia nada.
- **Correção:**
  - `SaveAnonymizationProfile` ganhou `ExpectedUpdatedAt`, uma propriedade `init`
    opcional, como `SkippedTables`. Com valor, o handler compara por `UtcTicks` (a
    mesma regra do MCP) e recusa com `DomainException` antes de mexer no perfil.
    Nada é gravado, como pede o ADR-008;
  - `SaveAnonymizationProfileHandler.SaveAsync` devolve `SavedAnonymizationProfile(Id,
    UpdatedAt)`. O `HandleAsync` continua devolvendo o `Guid`, para o MCP e para o
    duplicar;
  - o `DatabaseProfilesViewModel` guarda a versão aberta: a do perfil ao editar,
    nenhuma ao criar e a nova depois de salvar. O segundo salvar seguido não acusa
    conflito falso.
- **Preservado:**
  - as chamadas internas (`DuplicateAnonymizationProfileHandler` e a ferramenta MCP
    de criar) não passam versão, então se comportam como antes;
  - criar um perfil não confere nada;
  - nenhum esquema ou contrato do MCP mudou.
- **Mudança visível, autorizada:** quem salva na tela um perfil que o MCP alterou
  depois que ela abriu vê "O perfil … foi alterado em outro lugar…" em vez de
  sobrescrever.
- **Testes:**
  - Application: o novo teste;
  - Desktop: `SavingAProfileTheMcpChangedMeanwhile_ShowsTheRefusal` e
    `SavingTwice_ComparesWithTheVersionJustSaved`, sobre o handler real com
    repositório substituto. `Saving_SendsOnlyTheConfirmedRules` só trocou o tipo do
    resultado falso;
  - `dotnet test MyTaskApp.slnx`: Application com 1295 aprovados e Desktop com 2335,
    além das 4 falhas de ambiente.

### F1 — O contêiner só é descartado depois do encerramento (defeito D1, autorizado)

- **Defeito, comprovado pela leitura do código e não por um teste que falhasse
  antes:**
  - `App.Exit` disparava `_ = Task.Run(() => Shutdown(services))` e logo chamava
    `desktop.Shutdown()`;
  - o `StartWithClassicDesktopLifetime` voltava, e o `using var services` do
    `Program` descartava o contêiner enquanto o `Shutdown` ainda chamava
    `GetRequiredService` (agendadores, monitores, MCP com até 5 s de parada);
  - uma `ObjectDisposedException` ali sumia numa `Task` que ninguém observava, e o
    que vinha depois dela (bandeja, MCP) não era solto.
  - Reproduzir a corrida exigiria subir o app inteiro. Os testes fixam o contrato
    novo.
- **Correção:**
  - `Desktop/Composition/PendingShutdown.cs` guarda a `Task` do encerramento;
  - `App.Exit` usa `App.ShutdownInProgress.Start(...)`, no mesmo ponto e na mesma
    ordem;
  - o `Program` chama `App.ShutdownInProgress.Wait(10 s)` antes de sair do `using`.
    Uma falha vai para o log (`ShutdownFailed`), e um encerramento travado fica para
    trás depois da folga, com aviso (`ShutdownStillRunning`), como antes.
- **Preservado:**
  - a janela e o ícone somem na hora, porque o `desktop.Shutdown()` não espera;
  - a ordem interna do `Shutdown()` não mudou;
  - o `PersistNow` continua na thread da UI, antes.
- **Testes:** `PendingShutdownTests` (4 casos: espera até o fim, falha não lança,
  travado é deixado depois da folga, sem encerramento não espera). Desktop.Tests:
  2339 aprovados e as 4 falhas de ambiente.
- **Limitação:** não deu para subir o build de dev e fechar o app de verdade. O
  MyTaskApp instalado está aberto na mesma sessão e segura o mutex, então o build
  de dev só sinalizaria a instância instalada e sairia.

### F2 — Seletor do sistema que falha não fecha o app (defeito D2, autorizado)

- **Defeito, revisto ao ler cada handler.** Dos 7 `async void` apontados, 5 chamam o
  seletor de pasta ou de arquivo do sistema sem proteção:
  - `AgentAlertSoundsWindow.OnAddSoundClick`;
  - `DevelopmentCommandsWindow.OnBrowseTestDirectoryClick`;
  - `TagsWindow.OnBrowseFolderClick`;
  - `TaskNotesWindow.PickFolderAsync`, usado por dois handlers.

  O `StorageProvider` pode lançar (no Linux, sem o portal do desktop). Não há
  tratador global de exceção da thread da UI, então a exceção sobe até o `try` do
  `Program`, que registra `UnhandledException` e encerra o app.
- **Não alterados, sem risco:** `TimeEntryWindow.OnAccept` e
  `DirectoryCommandWindow.OnAccept`. O `AcceptAsync` deles chama o `SaveAsync` de
  quem pediu, e esse delegate já pega toda exceção e devolve a mensagem
  (`TaskTimeLogViewModel.SaveAsync`, `TagsViewModel.SaveCommandAsync`).
- **Correção:**
  - `Desktop/Views/SystemPickers.cs` chama o seletor. Uma falha vai para o log
    (`SystemPickerFailed {Title}`) e vira a frase "Não foi possível abrir a janela de
    escolha do sistema…" no `ErrorMessage` da janela, e nada é escolhido;
  - fechar o seletor sem escolher continua não sendo erro;
  - o cancelamento continua passando.
- **Preservado:** no caminho de sucesso, o mesmo título, o mesmo filtro e o mesmo
  "primeiro caminho local" de antes.
- **Testes:** `SystemPickersTests` (3 casos, com `IStorageProvider` substituto).
  Desktop.Tests: 2342 aprovados e as 4 falhas de ambiente.

### F3 — Fechar a janela da tarefa para as buscas que só serviam a ela (defeito D3, autorizado)

- **Defeito.** O `TaskDevelopmentViewModel` (um por ambiente, transitório) guarda três
  `CancellationTokenSource`, e nenhum era cancelado ao fechar a janela:
  - `_inspection`: a conferência atrasada da pasta digitada, que roda `git` e lista
    branches;
  - `_pullRequestLookup`: a busca da PR pelo `gh`;
  - `_run`: o "Iniciar implementação".

  O fechamento só fazia `Time?.Detach()` (`App.axaml.cs`), então git e gh
  continuavam rodando para uma janela que já não existia.
- **Correção:**
  - `TaskDevelopmentViewModel.Detach()` cancela `_inspection` e
    `_pullRequestLookup`;
  - `TaskDevelopmentsViewModel.Detach()` repassa a todos os ambientes;
  - o `Closed` da janela da tarefa chama `viewModel.Developments.Detach()` ao lado do
    `Time?.Detach()`.
- **Preservado de propósito:** o `_run` **não** é cancelado. Uma implementação em
  andamento (worktree, cópia de banco, comandos) continua, e cancelá-la é decisão do
  usuário, pelo botão "Cancelar". Os dois caminhos cancelados já tratavam o
  `OperationCanceledException` em silêncio (`InspectLaterAsync`,
  `LookUpPullRequestAsync`).
- **Testes:**
  - `ATypedFolder_IsInspectedAfterThePause`: controle, mostra que a inspeção roda
    quando o atraso vence;
  - `ClosingTheWindow_StopsTheInspectionStillWaiting`. Foi conferido que esse teste
    **falha** com o `Detach` vazio ("found True").
  - Desktop.Tests: 2344 aprovados e as 4 falhas de ambiente.

## Cobertura

**Como foi medida.** Os passos do `coverage.ps1`, rodados no Bash, porque a máquina
não tem `pwsh`:

- `dotnet test <solution> -c Release --no-build --settings coverage.runsettings
  --collect "XPlat Code Coverage"`, para as duas solutions;
- `dotnet reportgenerator -reporttypes:JsonSummary`.

| Assembly | Linha | Ramo |
|---|---|---|
| **Total (linha de base)** | **86,8%** (29418/33854) | **76,5%** (11712/15291) |
| MyTaskApp (Desktop) | 80,6% | 68,7% |
| MyTaskApp.Application | 93,7% | 85,7% |
| MyTaskApp.Domain | 98,2% | 91,8% |
| MyTaskApp.Infrastructure | 91,6% | 77,5% |
| MyTaskApp.Mcp | 71,3% | 70,4% |
| MyTaskApp.Server.Application | 95,1% | 87,8% |
| MyTaskApp.Sync.Contracts | 98,6% | 93,8% |

O gate do script é de 80% de linha e 70% de ramo, sobre o total.
