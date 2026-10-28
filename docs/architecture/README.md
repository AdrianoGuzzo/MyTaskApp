# Decisões de arquitetura (ADRs)

Um arquivo por decisão em [`decisions/`](decisions/). Este índice serve para
**achar** a decisão sem abrir os arquivos: procure pela palavra-chave
(`rg -n -i "<termo>" docs/architecture/README.md`) e abra só o ADR da linha.

- Camadas, projetos, stack e regras do EF Core: [`overview.md`](overview.md).
- Desenho detalhado da fase 2: [`remote-orchestration.md`](remote-orchestration.md)
  e [`remote-security-threat-model.md`](remote-security-threat-model.md).
- O ID `ADR-NNN` é estável e é o que o código cita; o arquivo é `decisions/NNN-*.md`.

## Como ler o cabeçalho de um ADR

- **Status:** `Aceito` (vale hoje, mesmo com partes revistas depois),
  `Proposto` (decidido no papel, ainda não adotado), `Substituído`, `Obsoleto`,
  `Rejeitado`.
- **Implementação:** `Implementado`, `Parcial`, `Planejado` ou `Não confirmado`,
  conferido no código em 2026-10-24.
- **Relações:** *revisto por* e *exceção aberta por* mudam parte da decisão;
  leia esses ADRs junto. *Relacionado* é só contexto.
- **Registrado:** data do primeiro commit em que o ADR aparece no repositório.
  Os ADR-001 a ADR-019 já estavam no primeiro commit do documento, então a
  decisão pode ser anterior.
- O corpo de cada ADR é o texto original (antes num único `docs/ARCHITECTURE.md`),
  sem reescrita. Quando o código diverge do texto, a seção
  "Divergências com o código" no fim do ADR diz onde.

## Por categoria

Números dos ADRs; o link está na linha do índice abaixo.

- **dominio** (modelo de tarefas, tempo, recorrência, regras puras): 001, 002, 003, 007, 010
- **aplicacao** (casos de uso, DI, erros): 008, 009, 012, 015
- **persistencia** (SQLite, EF Core, migrations): 005, 011
- **ui-desktop** (janela, widget, HUD, temas, editor, ícones, post-its): 013, 016, 017, 022, 024, 032, 038, 041, 046, 048, 054
- **lembretes-prazos** (lembretes, prazos, avisos): 004, 014, 050
- **ciclo-de-vida** (conclusão, arquivamento, lixeira, auditoria, histórico, tempo trabalhado): 020, 021, 052, 053
- **etiquetas** (etiquetas, diretórios, aliases): 025, 026, 064, 077
- **git-worktree** (worktrees, comandos, branches, PR, limpeza): 027, 028, 029, 031, 034, 035, 043, 047, 051, 055, 065
- **agente-ia** (Claude Code, hooks, sessões, sons, Consulta Inteligente): 030, 033, 036, 037, 039, 040, 042, 081
- **integracoes** (Jira, MCP): 045, 059
- **banco-de-dados** (operações de banco PostgreSQL, cópia, anonimização): 056, 057, 058, 060, 080, 082, 083
- **sincronizacao** (sincronização, servidor, autenticação e segurança do servidor): 061, 062, 063, 066, 068, 069, 070, 071, 072, 073, 074, 078
- **mobile** (app do celular): 067, 075, 076, 079
- **distribuicao** (instalador, versionamento, release, instância única, iniciar com o sistema): 018, 019, 023, 044, 049
- **testes** (estratégia e ferramentas de teste): 006
- **remoto** (orquestração remota (fases 2 e 3)): 084, 085, 086, 087, 088, 089, 090, 091, 092, 093, 094, 095, 096

## Índice

| ID | Decisão | Status | Impl. | Categoria | Palavras-chave |
|---|---|---|---|---|---|
| [ADR-001](decisions/001-tarefa-serie-e-ocorrencia.md) | `TaskItem` (série) + `TaskOccurrence` (unidade de trabalho) | Aceito | Implementado | dominio | TaskItem, TaskOccurrence, ocorrência, série, modelo uniforme |
| [ADR-002](decisions/002-hora-de-parede-no-dominio.md) | Tempo: parede (wall-clock) no domínio, instante só na borda | Aceito | Implementado | dominio | TimeProvider, IUserClock, ToInstant, DateOnly, TimeOnly |
| [ADR-003](decisions/003-materializacao-de-ocorrencias-por-horizonte.md) | Materialização de ocorrências por horizonte | Proposto | Parcial | dominio | recorrência, materialização, horizonte, MaterializedThrough, marca-d'água |
| [ADR-004](decisions/004-lembretes-sobrevivem-ao-processo.md) | Lembretes sobrevivem ao processo | Aceito | Implementado | lembretes-prazos | ReminderPolicy, ReminderState, NextFireAtUtc, ReminderScheduler, coalescência |
| [ADR-005](decisions/005-persistencia-por-interfaces-estreitas.md) | Persistência isolada por interfaces estreitas | Aceito | Implementado | persistencia | ITaskItemRepository, ITodayQuery, IUnitOfWork, repositório genérico, migrations |
| [ADR-006](decisions/006-awesomeassertions-no-lugar-de-fluentassertions.md) | AwesomeAssertions no lugar de FluentAssertions | Aceito | Implementado | testes | AwesomeAssertions, FluentAssertions, licença, Xceed, asserções |
| [ADR-007](decisions/007-inbox-e-estado-nao-entidade.md) | Inbox é estado, não entidade | Aceito | Parcial | dominio | Inbox, TriagedAt, triagem, InboxItem, tarefa sem data |
| [ADR-008](decisions/008-domainexception-falha-que-o-usuario-le.md) | `DomainException` é a falha que o usuário lê | Aceito | Implementado | aplicacao | DomainException, ScreenErrorPath, tratamento de erro, Tarefa não encontrada, Result<T> |
| [ADR-009](decisions/009-casos-de-uso-sem-mediador.md) | Casos de uso sem mediador | Aceito | Implementado | aplicacao | MediatR, HandleAsync, AddApplication, handler, caso de uso |
| [ADR-010](decisions/010-classificacao-hoje-funcao-pura.md) | Classificação da tela "Hoje" é função pura no domínio | Aceito | Implementado | dominio | TodayClassifier, Classify, tela Hoje, ATRASADAS, AGORA |
| [ADR-011](decisions/011-datetimeoffset-em-ticks-utc.md) | `DateTimeOffset` vira ticks UTC no SQLite | Aceito | Implementado | persistencia | DateTimeOffset, ticks UTC, value converter, UtcInstantConverter, SQLite |
| [ADR-012](decisions/012-iusecaserunner-um-escopo-por-operacao.md) | `IUseCaseRunner`: um escopo por operação | Aceito | Implementado | aplicacao | IUseCaseRunner, ScopedUseCaseRunner, escopo, DbContext, service locator |
| [ADR-013](decisions/013-captura-rapida-uma-linha-uma-tarefa.md) | Captura rápida: uma linha, uma tarefa | Aceito | Implementado | ui-desktop | QuickCapture, captura rápida, Enter, Shift+Enter, MaxLines |
| [ADR-014](decisions/014-padrao-de-lembrete-no-banco.md) | Configuração de lembrete é dado do usuário, não do `appsettings` | Aceito | Implementado | lembretes-prazos | ReminderSettings, IReminderSettingsStore, ReminderPolicy.Default, appsettings, ReminderTickSeconds |
| [ADR-015](decisions/015-agendador-com-timeprovider-createtimer.md) | Agendador com `TimeProvider.CreateTimer`, não `IHostedService` | Aceito | Implementado | aplicacao | ReminderScheduler, TimeProvider.CreateTimer, IHostedService, FakeTimeProvider, SemaphoreSlim |
| [ADR-016](decisions/016-bandeja-e-fechar-para-esconder.md) | Bandeja, `OnExplicitShutdown` e fechar-para-esconder | Aceito | Implementado | ui-desktop | bandeja, TrayIcon, OnExplicitShutdown, fechar para esconder, TrayIconUnavailable |
| [ADR-017](decisions/017-janela-principal-como-widget.md) | A janela principal é um widget, não uma aplicação | Aceito | Implementado | ui-desktop | MainWindow, widget, WidgetMode, widget.json, IWidgetStateStore |
| [ADR-018](decisions/018-instalador-inno-setup-e-dados-do-usuario.md) | Distribuição: Inno Setup, e uma linha divisória entre aplicação e dados | Aceito | Implementado | distribuicao | Inno Setup, instalador, UserDataLocation, MYTASKAPP_DATA_DIR, appsettings.user.json |
| [ADR-019](decisions/019-instancia-unica-por-mutex-nomeado.md) | Instância única, e o clique no atalho que traz a janela de volta | Aceito | Implementado | distribuicao | SingleInstance, Mutex, EventWaitHandle, instância única, AppMutex |
| [ADR-020](decisions/020-ciclo-de-vida-estado-derivado.md) | Ciclo de vida do checklist: estado derivado, não coluna de estado | Aceito | Implementado | ciclo-de-vida | TaskLifecycle, ArchivedAt, DeletedAt, ConcludedAt, TaskAuditEntries |
| [ADR-021](decisions/021-agendador-de-manutencao-do-ciclo-de-vida.md) | Um segundo agendador, e não um segundo passo do tique dos lembretes | Aceito | Implementado | ciclo-de-vida | LifecycleMaintenanceScheduler, RunLifecycleMaintenance, varredura, idempotência, BatchSize |
| [ADR-022](decisions/022-ordem-manual-por-arrasto.md) | Ordem manual dentro da seção, e um item que descola da lista | Aceito | Implementado | ui-desktop | Position, reordenar, arrastar, ReorderOccurrences, ReorderDrag |
| [ADR-023](decisions/023-iniciar-com-windows-chave-run.md) | Iniciar com o Windows, e a chave `Run` como única verdade | Aceito | Implementado | distribuicao | --startup, HKCU Run, IStartupRegistration, WindowsStartupRegistration, startupicon |
| [ADR-024](decisions/024-anotacao-markdown-proprio.md) | Anotação do item: Markdown próprio, porque o editor pronto é pago | Aceito | Implementado | ui-desktop | MarkdownView, TaskItem.Description, TaskNotesWindow, barra de formatação, UnboundedDescription |
| [ADR-025](decisions/025-etiquetas-n-n-bolinhas.md) | Etiquetas: N:N com o checklist, bolinhas na linha | Aceito | Implementado | etiquetas | Tag, TaskItemTags, TagColor, SetTaskTags, TagPicker |
| [ADR-026](decisions/026-diretorios-da-etiqueta-alias.md) | Diretórios da etiqueta: o alias é atalho de digitação, não referência | Aceito | Implementado | etiquetas | TagDirectory, TagDirectories, @alias, AliasCompletion, IDirectoryProbe |
| [ADR-027](decisions/027-aba-desenvolvimento-worktree-git.md) | Aba Desenvolvimento: a tarefa vira um worktree Git | Aceito | Implementado | git-worktree | TaskDevelopment, IGitClient, GitClient, ProcessRunner, WorktreePathPlanner |
| [ADR-028](decisions/028-comandos-pos-worktree-e-globais.md) | Comandos pós-Worktree e comandos globais por `@alias` | Aceito | Implementado | git-worktree | TaskDevelopmentCommands, DevelopmentCommands, CommandAliasResolver, ShellCommandExecutor, CommandParameters |
| [ADR-029](decisions/029-remover-worktree-apaga-pasta.md) | Remover o worktree apaga a pasta, e mostra quem a segura | Aceito | Implementado | git-worktree | IDirectoryRemover, WindowsDirectoryLockFinder, Restart Manager, NtQuerySystemInformation, IsLeftover |
| [ADR-030](decisions/030-sessoes-claude-code-por-tarefa.md) | Sessões de agente de IA (Claude Code) por tarefa | Aceito | Implementado | agente-ia | AgentSession, IAgentCliProvider, ClaudeCodeCliProvider, WindowsTerminalLauncher, AgentSessionMonitor |
| [ADR-031](decisions/031-varios-ambientes-por-tarefa.md) | Vários ambientes por tarefa: um worktree por repositório | Aceito | Implementado | git-worktree | TaskItem.Developments, EnsureDevelopmentCanBegin, ForgetDevelopment, TaskDevelopmentsViewModel, MultipleDevelopments |
| [ADR-032](decisions/032-correcao-ortografica-do-sistema.md) | Correção ortográfica: o corretor do sistema, desenhado por cima da caixa | Aceito | Implementado | ui-desktop | ISpellChecker, WindowsSpellChecker, SpellCheck.IsEnabled, SpellingTokenizer, SpellingSquiggles |
| [ADR-033](decisions/033-parametros-do-agente-lembrados.md) | Parâmetros do agente: editáveis no card, lembrados por agente | Aceito | Implementado | agente-ia | AgentSettings, IAgentSettingsStore, AgentArguments, DefaultArguments, --dangerously-skip-permissions |
| [ADR-034](decisions/034-bolinha-worktree-e-pergunta-ao-concluir.md) | Bolinha de worktree na lista, e a pergunta ao concluir | Aceito | Implementado | git-worktree | ProbeWorktreesHandler, GetBranchStatusAsync, worktreeDot, commit sem push, Remover o worktree desta tarefa? |
| [ADR-035](decisions/035-branch-de-origem-padrao-diretorio.md) | Branch de origem padrão no diretório da etiqueta | Aceito | Implementado | git-worktree | TagDirectory.DefaultBranch, ListBranchesHandler, FindConfigured, Branch de origem padrão, Suggest |
| [ADR-036](decisions/036-abrir-claude-code-menu-da-linha.md) | "Abrir Claude Code" no menu da linha | Aceito | Implementado | agente-ia | Abrir Claude Code, menu da linha, StartAgentSession, FocusAgentSession, (aberto) |
| [ADR-037](decisions/037-acompanhar-claude-code-pelos-hooks.md) | Acompanhar o Claude Code pelos hooks dele | Aceito | Implementado | agente-ia | AgentEventListener, ClaudeCodeHookEvents, AgentActivity, RecordAgentEventHandler, hooks |
| [ADR-038](decisions/038-anotacao-markdown-vscode-e-previa.md) | Anotação: Markdown do VS Code e pré-visualização enquanto escreve | Aceito | Implementado | ui-desktop | MarkdownDocument, MarkdownView, pré-visualização, lado a lado, Ctrl+Shift+V |
| [ADR-039](decisions/039-arroba-ambientes-e-arquivos-no-agente.md) | `@` no texto do agente: os ambientes da tarefa e os arquivos deles | Aceito | Implementado | agente-ia | @, ListEnvironmentFiles, PathReference, ReferenceCompletionViewModel, git ls-files |
| [ADR-040](decisions/040-modelo-e-esforco-do-agente.md) | Modelo e esforço do agente, escolhidos no card | Aceito | Implementado | agente-ia | AgentCliOption, --model, --effort, IAgentCliProvider.Models, Efforts |
| [ADR-041](decisions/041-temas-sete-paletas-e-automatico.md) | Temas: sete paletas, "Automático" segue o Windows | Aceito | Implementado | ui-desktop | ThemeCatalog, ThemeController, ThemeResources, Automático, alto contraste |
| [ADR-042](decisions/042-som-por-estado-do-claude-code.md) | Um som por estado do Claude Code, com sons do app e do usuário | Aceito | Implementado | agente-ia | SoundSynthesizer, WindowsAudioPlayer, MCI, AgentAlertSounds, sons do usuário |
| [ADR-043](decisions/043-tag-opcional-no-worktree.md) | Tag opcional: o worktree parte de uma versão | Aceito | Implementado | git-worktree | SourceTag, refs/tags, ListTagsAsync, for-each-ref, Tag (opcional) |
| [ADR-044](decisions/044-versionamento-com-release-please.md) | Versionamento: o Release Please sobe a versão, a tag não se move | Aceito | Implementado | distribuicao | Release Please, Conventional Commits, CHANGELOG, VersionPrefix, SHA256SUMS |
| [ADR-045](decisions/045-jira-issue-como-contexto-da-tarefa.md) | Jira: a issue vira contexto da tarefa, sem virar um clone do Jira | Aceito | Implementado | integracoes | ExternalLink, IssueKey, IExternalTaskProvider, ExternalTaskSearch, OAuth 3LO |
| [ADR-046](decisions/046-fonte-de-icones-embutida.md) | Fonte de ícones embutida: o cabeçalho também tem ícones no Linux | Aceito | Implementado | ui-desktop | MyTaskAppIcons.ttf, WidgetIconFont, Segoe Fluent Icons, Fluent UI System Icons, Linux |
| [ADR-047](decisions/047-pr-aberta-da-branch-pelo-gh.md) | PR aberta da branch, pelo GitHub CLI | Aceito | Implementado | git-worktree | GitHub CLI, gh pr list, GhCliPullRequestClient, GhLocator, PR aberta |
| [ADR-048](decisions/048-hud-visivel-sem-atrapalhar.md) | HUD: o pino deixa de ser "transparente" e vira "visível sem atrapalhar" | Aceito | Implementado | ui-desktop | HUD, WindowMode, SetWindowRgn, CloseBehavior, HudPlacement |
| [ADR-049](decisions/049-instalador-fecha-o-app-aberto.md) | Instalador fecha o app aberto, em vez de pedir que alguém o feche | Aceito | Implementado | distribuicao | Inno Setup, PrepareToInstall, taskkill, WMI, CloseApplications |
| [ADR-050](decisions/050-prazo-com-aviso-por-degrau.md) | Prazo: responsabilidade contínua, aviso por degrau | Aceito | Implementado | lembretes-prazos | TaskDeadline, DeadlineAlerting, DeadlineAlertStage, PRAZOS, DispatchDeadlineAlertsHandler |
| [ADR-051](decisions/051-comandos-rapidos-no-worktree.md) | Comandos rápidos: o comando global vira botão no worktree | Aceito | Implementado | git-worktree | QuickCommandCatalog, TagDirectoryCommand, CommandExecution, RunQuickCommand, variáveis de contexto |
| [ADR-052](decisions/052-tempo-trabalhado-por-periodo.md) | Tempo trabalhado: o período é a verdade, o relógio é desenho | Aceito | Implementado | ciclo-de-vida | TimeEntry, cronômetro, TimeLog.FindOverlap, IX_TimeEntries_SingleActive, ActiveTimerViewModel |
| [ADR-053](decisions/053-historico-sete-dias-projecao.md) | Histórico de 7 dias: uma projeção, e não uma tabela | Aceito | Implementado | ciclo-de-vida | ActivityHistoryBuilder, ActivityHistoryQuery, GetActivityHistory, histórico, últimos 7 dias |
| [ADR-054](decisions/054-post-it-entidade-propria.md) | Post-it: o que ainda não merece virar tarefa | Aceito | Implementado | ui-desktop | StickyNote, post-it, StickyNoteTint, StickyNoteWindowManager, Transformar em tarefa |
| [ADR-055](decisions/055-comando-so-do-diretorio.md) | Comando só do diretório: o botão sem passar por Comandos globais | Aceito | Implementado | git-worktree | DevelopmentCommand.TagDirectoryId, CreateForDirectory, DirectoryCommandWindow, comando do diretório, Alias anulável |
| [ADR-056](decisions/056-operacoes-de-banco-politica-central.md) | Operações de banco: cópia de produção só por dump anônimo, e uma política que a tela não contorna | Aceito | Implementado | banco-de-dados | IDatabaseSecurityPolicy, PostgresProcessGuard, EnvironmentPolicy, Bancos de Dados, produção somente leitura |
| [ADR-057](decisions/057-conexao-por-servidor-e-apelidos.md) | Conexão por servidor, banco escolhido na cópia, e apelidos | Aceito | Implementado | banco-de-dados | SavedDatabase, apelido, DatabaseCopyPlanner, WithDatabase, IsAmong |
| [ADR-058](decisions/058-anonimizar-na-consulta.md) | Anonimizar na consulta, sem instalar nada no banco de origem | Aceito | Implementado | banco-de-dados | MaskingPlanner, MaskedSelectBuilder, MaskingMethod, COPY mascarado, pg_export_snapshot |
| [ADR-059](decisions/059-servidor-mcp-local-loopback.md) | Servidor MCP local: Kestrel só no loopback, e os casos de uso da tela | Aceito | Implementado | integracoes | MCP, McpGateway, McpSecurityMiddleware, McpServerManager, Kestrel |
| [ADR-060](decisions/060-banco-do-ambiente-variavel-database.md) | Banco do ambiente: a cópia na aba Desenvolvimento e o `{database}` dos comandos | Aceito | Implementado | banco-de-dados | TaskDevelopmentDatabase, {database}, CommandContext, DatabaseAliasVariable, CopyDevelopmentDatabaseHandler |
| [ADR-061](decisions/061-sincronizacao-outbox-mescla-por-campo.md) | Sincronização opcional: outbox local, mescla por campo no servidor | Aceito | Implementado | sincronizacao | SyncOutbox, SyncCaptureInterceptor, SqlRowWriter, OpenIddict, PKCE |
| [ADR-062](decisions/062-lista-por-computador-onde-aparece.md) | Cada computador com a sua lista: o "onde aparece" da tarefa | Aceito | Implementado | sincronizacao | DevicePlacement, PlacementKind, DevicePlacementState, PlacementPredicates, Onde aparece |
| [ADR-063](decisions/063-indicador-sincronizacao-no-cabecalho.md) | A bolinha da sincronização no cabeçalho | Aceito | Implementado | sincronizacao | SyncIndicatorViewModel, bolinha, SyncIndicatorClassifier, CycleStarted, ChangeQueued |
| [ADR-064](decisions/064-repositorio-git-do-diretorio-clone.md) | O repositório Git do diretório: a URL acompanha a pasta, e clonar dali | Aceito | Implementado | etiquetas | RepositoryUrl, GitRemoteUrl, CloneAsync, git clone, origin |
| [ADR-065](decisions/065-limpeza-de-worktrees-com-forca.md) | Limpeza de worktrees: os sem tarefa e os de tarefa encerrada, com força | Aceito | Implementado | git-worktree | CleanWorktreesHandler, WorktreeRemoveForce, git worktree remove --force, WorktreeEraser, IsSettled |
| [ADR-066](decisions/066-comandos-globais-locais-por-padrao.md) | Comandos globais só deste computador, a menos que compartilhados | Aceito | Implementado | sincronizacao | IsShared, IsDetached, Compartilhar entre computadores, DevelopmentCommand, CommandSharing |
| [ADR-067](decisions/067-celular-dispositivo-da-sincronizacao.md) | O celular como mais um dispositivo da sincronização (.NET MAUI) | Aceito | Implementado | mobile | .NET MAUI, MyTaskApp.Mobile.Core, AddLocalData, AppLinkAuthorizationRedirect, ISyncAuthorizationRedirect |
| [ADR-068](decisions/068-servidor-sync-lista-de-acesso.md) | Servidor de sincronização fechado: lista de quem pode entrar | Aceito | Implementado | sincronizacao | RegistrationOptions, AllowedEmails, AllowedSubjects, registration_closed, email_verified |
| [ADR-069](decisions/069-rate-limit-por-conta.md) | Rate limit da API por conta, de verdade | Aceito | Implementado | sincronizacao | UseRateLimiter, IdentifyApiCaller, AnonymousPerMinute, GlobalPerMinute, AuthPerMinute |
| [ADR-070](decisions/070-servidor-sync-production-tls.md) | O servidor de sincronização só vai para a rede em Production, atrás de TLS | Aceito | Implementado | sincronizacao | docker-compose.prod.yml, Caddy, DevelopmentLoopbackGuard, AddKeyPerFile, KnownProxies |
| [ADR-071](decisions/071-pull-com-teto-por-pagina.md) | O pull com teto por página, sem reenviar a família da tarefa | Aceito | Implementado | sincronizacao | PullPageBuilder, PullLimits, MaxEntities, MaxBytes, TravelsWithParent |
| [ADR-072](decisions/072-push-so-guarda-o-contrato.md) | O push só guarda o contrato, com teto por entidade e cota por workspace | Aceito | Implementado | sincronizacao | unknown_field, entity_too_large, quota_exceeded, SyncFieldValidator.FindUnknown, PushLimits |
| [ADR-073](decisions/073-chaves-data-protection-cifradas-com-pfx.md) | As chaves do Data Protection cifradas no banco, com um PFX fora dele | Aceito | Implementado | sincronizacao | Data Protection, ProtectKeysWithCertificate, PlaintextKeyRevoker, DataProtectionCertificate, DataProtectionKeys |
| [ADR-074](decisions/074-consentimento-authorize-e-porta-efemera.md) | Consentimento no `/connect/authorize` fora do login recém-feito, e loopback com porta efêmera | Aceito | Implementado | sincronizacao | /connect/authorize, consentimento, mytaskapp_authorize_flow, porta efêmera, loopback |
| [ADR-075](decisions/075-celular-temas-concluir-e-teclado.md) | Celular: os temas do desktop, concluir sem acidente e o teclado que fecha | Aceito | Implementado | mobile | MobileThemeCatalog, MauiThemeService, ThemeParityTests, SwipeView Reveal, Desfazer |
| [ADR-076](decisions/076-celular-pendentes-historico-dispositivo-lembrado.md) | Celular: pendentes na frente, Histórico numa aba e o dispositivo lembrado | Aceito | Implementado | mobile | HistoryViewModel, TaskListViewModelBase, DeviceFocus, task-list-device, Histórico |
| [ADR-077](decisions/077-importar-etiqueta-de-outro-computador.md) | Importar etiqueta de outro computador: a mesma etiqueta, sem o caminho | Aceito | Implementado | etiquetas | importar etiqueta, PlaceTag, DevicePlacement.Everywhere, IsVisibleHere, TagsViewModel |
| [ADR-078](decisions/078-servidor-de-sync-do-usuario.md) | O servidor de sincronização é do usuário: nenhum embutido, e a troca é explícita | Aceito | Implementado | sincronizacao | SyncServerUrl, ISyncServerAddress, SwitchServerAsync, server_changed, ISyncServerProbe |
| [ADR-079](decisions/079-celular-aba-tarefas-enxuta.md) | Celular: a aba Tarefas enxuta, o dispositivo no título e a Nova tarefa discreta | Aceito | Implementado | mobile | DeviceTitle, IDialogService.ChooseAsync, HasSheetFilters, TaskFilter.OverdueOnly, Só as vencidas |
| [ADR-080](decisions/080-linhas-sem-mascara-condicao-sql.md) | Linhas sem máscara: uma condição SQL do usuário, por tabela, só na leitura da origem | Aceito | Implementado | banco-de-dados | RowFilters, RowFilterCondition, AnonymizationRowFilters, MaskedSelectBuilder, CASE WHEN |
| [ADR-081](decisions/081-consulta-inteligente-somente-leitura.md) | Consulta Inteligente: o Claude Code só lendo, e o banco recusando escrita no servidor | Aceito | Implementado | agente-ia | TaskConsultation, Consulta Inteligente, mytaskapp_leitura, IConsultationLaunchBuilder, NpgsqlConsultationDatabase |
| [ADR-082](decisions/082-script-depois-da-copia.md) | Script depois da cópia: SQL do usuário no destino, no fim, numa transação | Aceito | Implementado | banco-de-dados | PostCopyScript, PostCopyScriptText, IPostgresScriptRunner, NpgsqlScriptRunner, Executar SQL |
| [ADR-083](decisions/083-diagnostico-instalar-e-banco-docker.md) | Diagnóstico que ensina a instalar tudo, e um banco local no Docker sempre ligado | Aceito | Implementado | banco-de-dados | ToolInstallGuide, InstallStep, IDockerInspector, DockerInspector, LocalPostgresContainer |
| [ADR-084](decisions/084-orquestracao-remota-coordenador-e-agente.md) | Orquestração remota: o servidor do usuário coordena, e cada máquina tem um agente que só disca para fora | Aceito | Implementado | remoto | MyTaskApp.Remote.Contracts, controlador, coordenador, agente, conexão só de saída |
| [ADR-085](decisions/085-agente-sem-tela-sobre-o-mesmo-sqlite.md) | O agente: um processo sem tela, do usuário, sobre o mesmo SQLite | Aceito | Implementado | remoto | MyTaskApp.Agent, ScopedUseCaseRunner, WAL, ProcessLeases, named pipe |
| [ADR-086](decisions/086-operacao-remota-registro-diario-e-signalr.md) | Operação remota: registro no PostgreSQL, diário no agente, SignalR só como campainha | Aceito | Implementado | remoto | RemoteOperations, RemoteOperationEvents, RemoteOperationJournal, RemoteOperationStatus, Seq |
| [ADR-087](decisions/087-pareamento-chave-por-dispositivo-operacao-assinada.md) | Confiança: pareamento na máquina executora, chave por dispositivo e operação assinada | Aceito | Parcial | remoto | DeviceKeyStore, device-key, ES256, PairingCode, ConfirmPairingHandler |
| [ADR-088](decisions/088-catalogo-fechado-de-operacoes-remotas.md) | Catálogo fechado de operações: nenhum texto de shell vem de fora | Aceito | Implementado | remoto | catálogo de operações, RemoteAllowed, command.run, RequiresConfirmation, RunQuickCommand.Confirmed |
| [ADR-089](decisions/089-claude-code-remoto-headless-com-aprovacao.md) | Claude Code remoto: headless, perfil escolhido no executor e aprovação humana | Aceito | Implementado | remoto | IHeadlessAgentProvider, claude -p, stream-json, --restricted, --permission-prompt-tool |
| [ADR-090](decisions/090-copia-de-banco-remota-com-aprovacao.md) | Cópia de banco de longe: as mesmas barreiras, e produção com aprovação digitada | Aceito | Implementado | remoto | database.copy, DatabaseCopyRequest, ApprovalKind.ProductionDatabase, TypedConfirmation, CriticalProduction |
| [ADR-091](decisions/091-conversa-do-claude-como-sessao-no-servidor.md) | A conversa do Claude como sessão no servidor, relatada pelo agente | Aceito | Implementado | remoto | RemoteAgentSessions, RemoteSessionPublisher, AgentSeenAt, SessionChanged, linha do tempo |
| [ADR-092](decisions/092-sessao-gerenciada-a-partir-do-desktop.md) | Sessão gerenciada a partir do desktop: o agente da própria máquina roda o Claude | Aceito | Implementado | remoto | sessão gerenciada, Abrir Claude gerenciado, desktop para celular |
| [ADR-093](decisions/093-pergunta-do-claude-respondida-de-longe.md) | A pergunta do Claude (AskUserQuestion) respondida de longe | Aceito | Implementado | remoto | AskUserQuestion, updatedInput, answers, permission-prompt-tool |
| [ADR-094](decisions/094-controladores-ouvem-o-hub.md) | Os controladores ouvem o hub, com a leitura por tempo como reserva | Aceito | Implementado | remoto | SignalR, EventsAppended, SessionChanged, polling, reconexão |
| [ADR-095](decisions/095-fila-de-prompts-por-conversa.md) | A instrução com o Claude respondendo espera a vez na fila da conversa | Aceito | Implementado | remoto | fila por conversa, MaxWaiting, processo vivo, stream-json, replay-user-messages |
| [ADR-096](decisions/096-verificacao-periodica-no-celular.md) | Com o app fechado, o celular verifica o controle remoto de tempos em tempos e avisa | Aceito | Implementado | remoto | verificação periódica, JobScheduler, notificação local, POST_NOTIFICATIONS, push, FCM |

## Novo ADR

1. Confira o próximo número livre **em todas as branches**, não só no `master`:
   branches paralelas também criam ADRs, e duas com o mesmo número geram
   conflito e referências cruzadas erradas.

   ```bash
   git fetch origin
   for b in $(git for-each-ref --format='%(refname:short)' refs/heads refs/remotes/origin); do
     git ls-tree --name-only "$b" docs/architecture/decisions/ 2>/dev/null
     git grep -h "^## ADR-" "$b" -- docs/ARCHITECTURE.md 2>/dev/null   # branches de antes da divisão
   done | grep -oE "(decisions/|ADR-)[0-9]{3}" | grep -oE "[0-9]{3}$" | sort -u | tail -1
   ```

   Confira de novo antes do commit: outro PR pode ter levado o número.
2. Copie [`decisions/_template.md`](decisions/_template.md) para
   `decisions/NNN-slug-curto.md` e preencha. Um ADR, uma decisão.
3. Acrescente a linha no índice acima e o número na categoria.
4. Se a decisão muda outra: não reescreva o ADR antigo. Atualize nele só o
   **Status** (`Substituído`, se a decisão central caiu) e as **Relações**
   (`revisto por` / `substituído por`), e faça o mesmo no índice.
