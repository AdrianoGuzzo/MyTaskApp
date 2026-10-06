# MyTaskApp — Decisões de Arquitetura

Documento vivo. Registra **o que foi decidido e por quê**, especialmente onde
havia mais de um caminho razoável.

## Stack validada

| Item | Versão | Observação |
|---|---|---|
| .NET | 10.0.401 (LTS) | pinado em `global.json` |
| Avalonia | 12.1.2 | possui TFM `net10.0` nativo |
| CommunityToolkit.Mvvm | 8.4.2 | `netstandard2.0`, compatível |
| EF Core + SQLite | 10.0.12 | provider com TFM `net10.0` |
| Serilog | 4.4.0 | |
| xUnit | **v3** (3.2.2) | obrigatório: `Avalonia.Headless.XUnit` depende de `xunit.v3.extensibility.core` |
| Asserts | AwesomeAssertions 9.6.0 | ver ADR-006 |
| Avalonia.Controls.ColorPicker | 12.1.2 | só o `ColorView` da janela de etiquetas (ADR-025) |

Versões centralizadas em `Directory.Packages.props` (Central Package Management).
`Avalonia.Diagnostics` **não existe** na linha 12.x (parou na 11.3.22); DevTools
deixou de ser pacote separado.

---

## ADR-001 — `TaskItem` (série) + `TaskOccurrence` (unidade de trabalho)

**Decisão:** modelo uniforme. *Toda* tarefa tem pelo menos uma ocorrência —
inclusive as não recorrentes.

```
TaskItem          → o QUE é a tarefa (título, descrição, prioridade, categoria,
                    projeto, regra de recorrência, lembretes)
TaskOccurrence    → o QUANDO + o ESTADO (data/hora agendada, status, concluída em)
```

**Alternativa rejeitada:** tarefa simples guarda status/data nela mesma, e só
tarefas recorrentes geram ocorrências.

**Por quê:** a alternativa cria um `if (isRecurring)` que vaza para *todas* as
consultas ("Hoje", histórico, atrasadas), para a conclusão e para os lembretes.
O briefing pede explicitamente (§11) que a recorrência não se espalhe pelo
código. O modelo uniforme paga um custo pequeno — uma linha extra por tarefa
simples — e em troca todo o app lê e escreve **um único conceito**: ocorrência.

**Custo aceito:** 2 inserts na criação de uma tarefa simples; o agregado
`TaskItem` garante por invariante que uma tarefa não recorrente tem
exatamente 1 ocorrência.

**Consequência desejada (§34):** o histórico é preservado por natureza.
`17/09 concluída · 18/09 concluída · 19/09 pendente` são três linhas distintas,
não uma linha que muda de data.

## ADR-002 — Tempo: parede (wall-clock) no domínio, instante só na borda

**Decisão:**
- Agendamento no domínio usa `DateOnly` + `TimeOnly?` (hora de parede) + o
  fuso configurado pelo usuário.
- O instante absoluto (`DateTimeOffset` UTC) é **derivado** apenas quando se
  precisa disparar algo (lembretes).
- "Agora" vem sempre de `TimeProvider` (BCL) — nunca de `DateTime.Now`.

**Por quê:** "verificar e-mails todo dia às 08:30" significa 08:30 no relógio
do usuário, em qualquer dia do ano. Se as ocorrências fossem materializadas em
UTC, o horário de verão deslocaria a tarefa para 07:30 ou 09:30. Guardar a hora
de parede e converter na borda é a única forma correta.

**Por que `TimeProvider` e não um `IClock` próprio:** já existe no .NET 8+, tem
`FakeTimeProvider` oficial e evita mais uma interface caseira (§24: interfaces
só quando agregam valor).

**Horário inexistente/ambíguo no DST:** regra explícita e testada — hora que
não existe avança para o primeiro instante válido; hora ambígua usa a primeira
ocorrência (offset de verão).

**A conversão parede → instante mora em `IUserClock.ToInstant(date, time)`.** É a
única ponte entre o agendamento do domínio e o instante que dispara um lembrete,
e é onde as duas regras acima estão implementadas. Não há sobrecarga para hora
opcional: ocorrência sem horário não tem instante, e quem chama precisa decidir o
que fazer com isso em vez de receber um palpite.

**Os testes de DST usam `America/New_York`, não São Paulo.** O Brasil acabou com
o horário de verão em 2019, então São Paulo não tem transição futura para
exercitar. `InvariantGlobalization=false` no `Directory.Build.props` é o que faz
ids IANA resolverem no Windows — não pode ser "otimizado" para `true`.

**Limite aceito:** a hora ambígua escolhe o maior offset, que é a primeira
ocorrência em todo fuso de DST positivo. Em fusos de DST *negativo* (Irlanda,
Lord Howe) a escolha seria a segunda. Não é o caso de nenhum usuário previsto.

## ADR-003 — Materialização de ocorrências por horizonte

**Decisão:** ocorrências não são infinitas nem criadas na leitura. Um serviço
materializa até um **horizonte** (ex.: hoje + 60 dias) e grava uma marca-d'água
(`MaterializedThrough`) na série.

**Por quê:** recorrência sem data final é infinita. Gerar sob demanda na
consulta tornaria a tela "Hoje" não determinística e impediria marcar uma
ocorrência futura como concluída. A marca-d'água torna a operação
**idempotente** (rodar duas vezes não duplica), garantida também por índice
único `(TaskItemId, ScheduledDate, ScheduledTime)`.

**Edição de série x ocorrência:** uma ocorrência editada vira *exceção*
(`IsDetachedFromSeries`) e deixa de ser sobrescrita por edições da série.
Ocorrências **passadas ou concluídas nunca são reescritas** por edição de série.

**Dívida já contratada com os lembretes (ADR-004).** Quando o materializador for
escrito, ele deve dois ganchos:

1. chamar `ReminderArming.Arm` em cada ocorrência nova, para ela nascer com o
   lembrete da série armado — é isso que faz "no dia seguinte recomeça" não
   precisar de caso especial;
2. desarmar ou atender as ocorrências **superadas**, senão o 09:00 de ontem que
   ninguém atendeu continua tocando junto com o de hoje.

Nenhum dos dois é necessário hoje; ambos estão escritos aqui para o futuro
implementador herdar a obrigação em vez de descobri-la em produção.

## ADR-004 — Lembretes sobrevivem ao processo

**Decisão:** o lembrete tem duas camadas:
- `ReminderPolicy` — a **política**, da série (`TaskItem`): "1 hora depois de
  criar", "a cada 15 min até eu dar atenção", e por quais canais avisar.
- `ReminderState` — o **estado por ocorrência** (`TaskOccurrence`):
  `NextFireAtUtc`, `Attempt`, `WaitingSinceUtc`, `LastFiredAtUtc`,
  `AcknowledgedAtUtc`, `AcknowledgedBy`.

`ReminderScheduler` acorda a cada 30 s (via `TimeProvider`), busca as ocorrências
com `NextFireAtUtc <= agora` e avisa.

**Por quê (§13):** o app desktop não fica ligado para sempre. Timer por lembrete
não sobrevive a reinício e não escala. A coluna persistida é a única forma de
responder "o que eu devia ter avisado enquanto estava fechado?".

**Revisão do desenho original — um disparo rolante, não N linhas materializadas.**
A primeira versão deste ADR previa uma tabela `ReminderFire`, uma linha por
disparo, com chave única `(ReminderId, OccurrenceId, ScheduledFireAtUtc)`. Foi
trocada por **um único `NextFireAtUtc` por ocorrência**, que rola para frente a
cada disparo. Razões:

1. **A durabilidade já está satisfeita** — `Reminder_NextFireAtUtc` é instante
   absoluto em disco. Era esse o objetivo do ADR.
2. **A coalescência vira estrutural.** Com N linhas seria preciso *encontrar* 144
   disparos vencidos e então decidir colapsá-los; com uma linha rolante é
   impossível acumular 144.
3. **Disparo materializado é dado derivado** — função pura de `(política, último
   disparo, agora)`. Persistir N vezes pagaria uma segunda tabela, um segundo
   aparato de horizonte/marca-d'água espelhando o ADR-003, um segundo índice
   único e limpeza de órfãos em todo reagendamento.
4. **A consulta quente vira um predicado indexado** numa tabela que já lemos
   (índice parcial `Reminder_NextFireAtUtc`), em vez de um join.
5. O único ganho das N linhas — trilha de auditoria — já é atendido pela linha de
   log estruturado (`RemindersDispatched`) mais `LastFiredAtUtc`/`Attempt`.

**Coalescência de atrasados (agora aritmética):** `ReminderScheduling.NextFireAfter`
avança intervalos inteiros de uma vez (`piso((agora − último) / intervalo) + 1`),
então 3 dias fechado produzem **um** aviso e o próximo a ≤ 15 min. Continua sendo
regra de negócio testada — contra o SQLite de verdade — só não depende mais de
alguém lembrar de aplicá-la. A grade original é preservada: um lembrete das 15:00
continua caindo em 15:15 e 15:30 mesmo que o tique tenha chegado atrasado.

**Coalescência entre tarefas:** acima de 5 lembretes vencidos no mesmo tique, o
despacho vira **um único aviso agregado** ("12 checklists estão esperando sua
atenção"). Sem isso, abrir o app depois de um mês jogaria dezenas de janelas na
tela.

**Marcar antes de enviar, não na mesma transação.** O envio é uma janela da
Avalonia em outra thread e não se alista numa transação do EF — "na mesma
transação" não é alcançável. A escolha real é entre dois modos de falha, e
escolhemos **marcar → salvar → apresentar**: uma queda no meio perde *um*
disparo, que volta um intervalo depois porque o lembrete repete, enquanto
enviar-e-depois-marcar duplicaria um aviso que o usuário não tem como desfazer.
Perda que se cura sozinha ganha de duplicata irreversível. Apresentação que lança
é registrada em log e **não** desfaz a marcação: reverter faria o agendador
martelar um apresentador quebrado a cada 30 s.

**Notificado ≠ atendido.** `PresentAsync` retorna quando o aviso está na tela, não
quando o usuário reage. O lembrete só termina em `AcknowledgeReminder` — abrir,
concluir ou marcar como visto. **Adiar não atende**: adia, e zera a insistência,
mas o lembrete continua vivo. Essa separação é estrutural, não convenção, e tem
teste dedicado nas três camadas.

**Escada de insistência fixa, canais opcionais.** `ReminderEscalation.LevelFor` é
função pura no estilo do ADR-010: 1ª e 2ª tentativa notificam; a 3ª acrescenta
som; a 4ª fica evidente; da 5ª em diante traz a janela para a frente. O usuário
liga e desliga *canais*, não reordena degraus — um canal desligado apenas pula o
degrau. Foi a forma de atender "que seja insistente" sem virar uma tela de
configuração que ninguém preenche.

**Fora de escopo, e de propósito:** horário silencioso ("não me incomode das 22h
às 8h"). É a próxima funcionalidade óbvia; as saídas hoje são pausar pela bandeja
e atender o lembrete. Registrado aqui para a omissão ler como decisão.

## ADR-005 — Persistência isolada por interfaces estreitas

**Decisão:** `Application` define interfaces por necessidade
(`ITaskItemRepository`, `ITodayQuery`, `IUnitOfWork`); `Infrastructure`
implementa com EF Core. **Sem** `IRepository<T>` genérico.

**Por quê:** o repositório genérico só reembala o `DbSet` e obriga a vazar
`IQueryable` — acoplamento sem benefício. Consultas de leitura ("Hoje",
histórico, busca) vão por interfaces de *query* que devolvem DTOs, sem passar
por agregados.

**Migrations:** obrigatórias desde a primeira versão. `EnsureCreated()` não é
usado em lugar nenhum (§19). Testes de integração rodam **as migrations** contra
SQLite em arquivo temporário — assim a migration também é testada.

## ADR-006 — AwesomeAssertions no lugar de FluentAssertions

**Decisão:** `AwesomeAssertions` 9.6.0 (Apache-2.0).

**Por quê:** FluentAssertions 8.x passou a usar a *Xceed Community License*,
que autoriza apenas uso **não comercial** (verificado no texto publicado no
NuGet). AwesomeAssertions é fork com API idêntica e licença permissiva, o que
mantém o projeto livre de dúvida jurídica caso ele cresça.

## ADR-007 — Inbox é estado, não entidade

**Decisão:** Inbox = `TaskItem` ainda não triado (`TriagedAt == null`).

**Por quê:** uma entidade `InboxItem` separada exigiria conversão, duplicação de
campos e um segundo modelo. O fluxo do §36 ("pensei → registrei → organizei
quando necessário") é exatamente uma tarefa que ainda não recebeu classificação.

## Nomenclatura

`TaskItem`, não `Task` — colide com `System.Threading.Tasks.Task` e envenenaria
todo arquivo com `async`. Código e testes em inglês; interface do usuário em
pt-BR.

## ADR-008 — `DomainException` é a falha que o usuário lê

**Decisão:** uma única exceção representa "falha esperada, com mensagem
apresentável": `DomainException`. A Application também a lança quando o
agregado pedido não existe ("Tarefa não encontrada.").

A UI então tem **um** caminho de tratamento:

```
DomainException  → mostra a mensagem como está
qualquer outra   → log completo + "Não foi possível salvar. [Tentar novamente]"
```

**Por quê:** a alternativa (`Result<T>` em todo caso de uso) obrigaria a
encanar resultado por handler, ViewModel e binding, para um app cujas regras
já param a operação no agregado. Um segundo tipo (`NotFoundException`) só
dobraria os `catch` sem ganho — "não encontrada" é falha de negócio tanto
quanto "já foi concluída".

**Consequência testada:** nenhum caso de uso deixa vazar
`NullReferenceException` para a borda, e nada é persistido quando a regra
recusa a operação.

## ADR-009 — Casos de uso sem mediador

**Decisão:** uma classe por caso de uso, com `HandleAsync`. Sem MediatR.

**Por quê:** MediatR resolveria acoplamento entre módulos que este app não tem
— a UI chama o handler direto, por injeção. Acrescentaria indireção em runtime,
pipeline behaviors e uma dependência, sem eliminar nenhuma linha de código.
`AddApplication()` registra todos os handlers, e um teste resolve cada um do
contêiner para que esquecer um registro quebre a build, não a tela.

## Edição atômica

`TaskItem.Update(title, description, priority)` normaliza e valida **tudo**
antes de atribuir qualquer campo. Sem isso, um título recusado depois de a
descrição já ter sido trocada deixaria a entidade meio-editada em memória — e o
EF persistiria essa meia-alteração no próximo `SaveChanges`. Hoje só o título
pode ser recusado (em branco ou acima de 200 caracteres): a descrição não tem
teto — é onde cabe o que não coube no título, e o SQLite guarda TEXT sem limite.
A ordem de validação continua importando para quando outro campo ganhar regra.

## Persistência — o que a implementação confirmou

**Concessão do domínio ao EF:** `TaskOccurrence` ganhou um construtor privado
sem parâmetros, usado só na materialização. `TaskItem` não precisou de nenhum:
o EF liga o construtor privado existente pelos nomes dos parâmetros. A coleção
`Occurrences` continua somente-leitura para fora — o EF escreve direto no campo
`_occurrences` via `PropertyAccessMode.Field`.

**Tipos no SQLite:** `DateOnly`, `TimeOnly` e `DateTimeOffset` viram TEXT.
Há teste de round-trip exato para os três, porque é o tipo de coisa que quebra
silenciosamente.

**Implementações são `internal`.** O Desktop referencia a Infrastructure só para
chamar `AddInfrastructure()`; `TaskItemRepository` e `EfUnitOfWork` não são
visíveis para ele. Os testes de integração alcançam essas classes por
`InternalsVisibleTo`, não abrindo mão do encapsulamento.

**Migrations com tooling local.** O `dotnet-ef` global da máquina é 9.0.9 e as
tools precisam ser ≥ runtime (10.0.12). Por isso há `.config/dotnet-tools.json`
no repositório: `dotnet tool restore` garante a versão certa para quem clonar,
sem depender do que está instalado globalmente.

**Banco e logs na pasta de dados do usuário.** O diretório de instalação pode ser
somente-leitura; `%APPDATA%/MyTaskApp` não é. Vale para o `.db` e para os logs.

## ADR-010 — Classificação da tela "Hoje" é função pura no domínio

**Decisão:** `TodayClassifier.Classify(candidato, hoje, agora, janela)` decide a
seção. Não lê relógio nem banco; recebe tudo por parâmetro.

**Por quê:** "o que está atrasado" é regra de negócio, não detalhe de consulta.
Como função pura, cada linha do §9 vira um teste direto — inclusive as bordas que
passariam despercebidas, como a janela de "agora" dando a volta na meia-noite.

**Interpretação registrada do §9:** o exemplo do briefing mostra `Daily 09:00`
sob **HOJE** enquanto **AGORA** marca 14:00. Ou seja, tarefa de hoje com horário
vencido **não** migra para ATRASADAS — lá ficam só as de dias anteriores. Seguimos
o exemplo e sinalizamos essas com `IsLate`, para a UI destacar sem trocar de
seção. Mudar de ideia é alterar uma linha do classificador e os testes que a
fixam.

**"Agora" é configurável:** `NowWindowBeforeMinutes`/`NowWindowAfterMinutes` no
appsettings, padrão −15/+60. Assimétrico de propósito: o que está chegando pesa
mais que o que acabou de vencer.

## ADR-011 — `DateTimeOffset` vira ticks UTC no SQLite

**Decisão:** value converter de `DateTimeOffset` para `long` (ticks em UTC) nas
colunas de instante.

**Por quê:** o SQLite não tem tipo de data nativo. Como TEXT com offset embutido,
o EF Core **se recusa a traduzir comparações** — a consulta da tela "Hoje" falhava
com "could not be translated". Ticks em UTC são ordenáveis, indexáveis e sem
ambiguidade de fuso.

**Preço aceito:** abrir o banco à mão mostra número em vez de data legível.

## ADR-012 — `IUseCaseRunner`: um escopo por operação

**Decisão:** ViewModels não recebem contêiner nem `IServiceScopeFactory`. Recebem
`IUseCaseRunner`, que abre um escopo, resolve o handler e o executa.

**Por quê:** os handlers dependem de `DbContext`, que é *scoped*, enquanto um
ViewModel vive enquanto a janela existir. Um `DbContext` longevo acumula
entidades rastreadas e serve dado velho. A alternativa — injetar
`IServiceProvider` no ViewModel — é service locator disfarçado e impede testar
sem levantar DI.

**Consequência:** os testes de ViewModel usam um executor falso que só registra
*qual* caso de uso foi pedido. O que cada handler faz já está coberto em
Application e Infrastructure; repetir ali seria teste frágil e duplicado (§6).

## ADR-013 — Captura rápida: uma linha, uma tarefa

**Decisão:** a tela "Hoje" abre com uma caixa de texto já focada. Cada linha
escrita vira um `TaskItem` agendado para **hoje, sem horário**. Enter registra a
lista inteira; Shift+Enter quebra linha.

**Por quê:** registrar precisa custar menos esforço do que a própria tarefa, ou o
usuário não registra (§36). Um formulário com título, data, hora e prioridade
transforma "comprar pão" em seis interações — e o app perde justamente as coisas
pequenas, que são as que se esquece.

**Alternativa rejeitada — sintaxe na linha** (`ligar dentista 14:30`, `!alta`):
economiza cliques de quem decora a sintaxe e confunde todo o resto, que fica com
uma tarefa chamada "ligar dentista 14:30". A linha é um título e nada mais.

**Alternativa rejeitada — capturar sem data (Inbox puro, ADR-007):** seria mais
fiel ao fluxo "pensei → registrei → organizei depois", mas o `TodayQuery` só traz
ocorrências com data — o que fosse escrito sumiria da única tela que existe. Datar
em hoje mantém o que foi escrito à vista. Quando houver tela de Inbox, o padrão
muda em uma linha do handler.

**Atomicidade:** a lista inteira é construída — e validada — antes de qualquer
`AddAsync`, e um único `SaveChanges` fecha a captura. Uma linha longa demais no
meio não pode deixar metade do checklist gravada: o usuário não teria como saber
onde parou para reescrever o resto. Testado contra o SQLite de verdade.

**Teto de 100 linhas por captura:** um Ctrl+V no documento errado criaria
centenas de tarefas que ninguém desfaz uma a uma. Recusar é mais barato do que
limpar.

**Enter precisa ser tratado no túnel:** a caixa aceita quebra de linha
(`AcceptsReturn`), então o próprio `TextBox` marca o Enter como tratado antes de a
tecla subir — um `KeyBinding` nunca a veria. Há teste headless que digita e aperta
Enter de verdade, porque esse é o tipo de fio que se rompe sem quebrar a build.

**Limitação conhecida:** a seção SEM HORÁRIO ordena por prioridade e depois por
título, então um checklist recém-escrito aparece em ordem alfabética, não na ordem
em que foi digitado.

## Teste de UI: só onde paga

Binding de XAML falha em runtime, não em compilação. Há testes headless que sobem
a janela de verdade e verificam que as seções desenham e que o `Command` do
checkbox **resolve** — o binding usa um caminho com cast até o `DataContext` do
UserControl e, se quebrasse, o checkbox viraria enfeite sem ninguém avisar.
Fora isso, a UI não é testada: comportamento fica nos ViewModels.

O mesmo critério trouxe os testes da moldura do widget (ADR-017): o cabeçalho
liga em `#Shell.Chrome`, um caminho que o compilador aceita e que só quebraria
quando alguém abrisse o app. Foi ali que apareceu o seletor de tipo que não
casava — a build passava e o modo compacto não escondia nada. `WidgetPlacement`
é testado à parte, e sem display, porque "o painel nunca abre fora da tela" é
uma promessa que só quebra na mesa de alguém que desconectou um monitor.

## ADR-014 — Configuração de lembrete é dado do usuário, não do `appsettings`

**Decisão:** o padrão de lembrete mora numa **linha única** da tabela
`ReminderSettings` no SQLite, atrás de `IReminderSettingsStore`. O
`appsettings.json` guarda apenas a cadência do agendador
(`ReminderTickSeconds`).

**Por quê:** o `appsettings` é lido uma vez, com `reloadOnChange: false`, e não é
gravável — o diretório de instalação pode ser somente-leitura. "Configuração
padrão" é algo que o usuário muda na tela, então é dado, e dado mora no banco:
um artefato só para backup, transacional com o resto e testável com o
`TempSqliteDatabase` que já existe. A cadência do tique é botão de implantação,
não preferência, e por isso ficou do outro lado da linha.

**Sem `HasData`.** Instalação nova não semeia linha nenhuma: `GetAsync` devolve
`ReminderPolicy.Default` quando a linha falta. Semear um singleton mutável por
migration vira armadilha no dia em que o padrão mudar — a linha semeada com o
valor velho continuaria lá. Consequência boa: "Restaurar padrão" é literalmente
gravar `ReminderPolicy.Default`, e há **uma** fonte da verdade.

**Linha corrompida degrada, não derruba.** Um `DomainException` ao reconstruir a
política vira log `InvalidReminderDefaultsStored` e o padrão de fábrica — mesma
degradação que o `UserClock` já aplica a um fuso inválido.

**O padrão não é reaplicado retroativamente.** Ele vale na criação. Rearmar todas
as tarefas do banco porque o usuário mexeu num combo seria uma surpresa
genuinamente alarmante; há teste fixando isso.

## ADR-015 — Agendador com `TimeProvider.CreateTimer`, não `IHostedService`

**Decisão:** `ReminderScheduler` é uma classe comum que cria um `ITimer` pelo
`TimeProvider`, iniciada em `App.OnFrameworkInitializationCompleted`. **Não** foi
adicionado o Generic Host.

**Por quê — testabilidade pesa mais que tudo aqui:** o `FakeTimeProvider` dirige
`CreateTimer` deterministicamente; um `Advance(30s)` produz exatamente um tique,
em processo, sem dormir. Um `BackgroundService` espera internamente em
`Task.Delay`, e torná-lo testável significaria reimplementar o laço sobre
`TimeProvider` de qualquer forma. Como o ADR-002 já obriga `TimeProvider` e o
`Microsoft.Extensions.TimeProvider.Testing` já está referenciado, a decisão se
resolve sozinha.

**Alternativa rejeitada — introduzir o Generic Host:** obrigaria a reestruturar o
`Program.Main` em torno de `IHost` e a conciliar dois ciclos de vida concorrentes
(`IHost.StopAsync` × `StartWithClassicDesktopLifetime`), para um laço de fundo. E
não ajudaria na injeção: `IHostedService` é singleton e precisaria do
`IUseCaseRunner` do mesmo jeito.

**Mora na Application, não no Desktop:** não toca em nenhum tipo de UI, então é
testável sem levantar o host headless da Avalonia.

**Detalhes que são decisão, não acaso:**
- primeiro tique **imediato** (`dueTime: Zero`) — é ele que recupera o que venceu
  com o app fechado, sem caminho de código separado para "catch-up";
- `SemaphoreSlim(1,1)` com espera zero: tique sobreposto é **pulado, não
  enfileirado**;
- o corpo do tique é `try/catch` de ponta a ponta — um tique que lança nunca pode
  matar o timer, que seria o app parar de lembrar em silêncio;
- salto de relógio e volta da suspensão não exigem nada: o disparo é instante
  absoluto e o tique é um poll de "o que venceu". O buraco vira `ReminderTickSkew`
  no log, que é onde se procura depois;
- é `IDisposable` **e** `IAsyncDisposable`: um singleton que só fosse
  `IAsyncDisposable` faria o `Dispose()` do contêiner **lançar**, e o composition
  root descarta o contêiner com um `using` comum no fim do `Main`.

## ADR-016 — Bandeja, `OnExplicitShutdown` e fechar-para-esconder

**Decisão:** o app vive na bandeja do Windows. Fechar a janela a **esconde**;
sair mesmo é pelo menu da bandeja. `ShutdownMode` vira `OnExplicitShutdown`.

**Por quê:** "o sistema fica responsável por me lembrar" só é verdade se o
processo estiver vivo às 15:00. Com o padrão `OnLastWindowClose`, fechar a janela
mataria o app e todo lembrete pendente junto.

**A saída de emergência é parte da decisão:** se o `TrayIcon` falhar ao subir,
`TryInstall` devolve `false`, o log recebe `TrayIconUnavailable` e o
`ShutdownMode` **volta** para `OnMainWindowClose`. Sem isso um app sem bandeja
ficaria sem nenhuma forma de ser encerrado.

**Duas armadilhas da API, registradas porque falham em silêncio:** a coleção
`TrayIcons` precisa ficar em campo (coletada, o ícone some da bandeja sem erro
nenhum) e o `TrayIcon` precisa ser descartado na saída (senão fica ícone fantasma
até o usuário passar o mouse por cima).

**Limite aceito:** o Windows bloqueia roubo de foreground por processo em segundo
plano, então o último degrau da escada pode apenas piscar na barra de tarefas em
vez de trazer a janela à frente. Quem realmente chama atenção é a janela de
aviso; o degrau 5 é reforço, não garantia.

**Fora de escopo:** iniciar com o Windows, e instância única. Nada impede hoje um
segundo processo, e a bandeja torna relançar mais provável (a janela está
escondida, o usuário acha que fechou). Dois agendadores no mesmo SQLite significam
aviso dobrado e `SQLITE_BUSY`; um `Mutex` nomeado no `Program.Main` é a correção
barata no dia em que isso morder.

> **Revisto.** Instância única entrou no ADR-019; iniciar com o Windows, no
> ADR-023 — e foi justamente a premissa deste ADR ("o processo precisa estar
> vivo às 15:00") que acabou forçando a segunda.

## Lembretes — o que a implementação confirmou

**Instância de tipo owned não pode ser compartilhada entre donos.**
`ReminderPolicy.Default` é uma instância estática, e a captura rápida aplica a
mesma política a várias tarefas de uma vez. O EF trata um tipo owned como parte do
dono e, com a mesma instância em dois donos, grava uma e deixa a outra com os
valores default — **em silêncio**. O sintoma era a primeira tarefa de cada captura
nascer com o lembrete desligado. `TaskItem.ChangeReminder` copia (`policy with { }`)
antes de guardar, o que protege todos os chamadores de uma vez; há teste de
regressão no domínio e contra o banco.

**`TimeSpan` também vira ticks.** O padrão do EF no SQLite guardaria TEXT
`"hh:mm:ss"` — desordenado e intraduzível em comparação, exatamente o erro que o
ADR-011 documenta para `DateTimeOffset`. `TimeSpanTicksConverter` resolve os dois
pelo mesmo caminho. O `[Flags] AlertChannels` vira `int`, para um futuro
`(Channels & Sound) != 0` continuar traduzível.

**Owned type precisa de uma coluna obrigatória.** O EF não distingue "owned
ausente" de "todas as colunas nulas": sem `Reminder_Attempt`/`Reminder_IsEnabled`
NOT NULL mais `.Navigation(...).IsRequired()`, o modelo estoura na primeira
consulta, não no build.

**Upgrade não arma nada.** Toda tarefa que já existia recebe
`ReminderPolicy.None` na migration. Armar lembrete silenciosamente no banco
inteiro de quem atualiza seria a pior primeira impressão possível da
funcionalidade.

**O quadro "Hoje" passou a se refrescar sozinho (60 s).** Veio junto porque
"aguardando há N minutos" envelhece; de quebra corrigiu um problema que já
existia — sem isso, AGORA e ATRASADAS congelavam enquanto a janela ficasse aberta.

## ADR-017 — A janela principal é um widget, não uma aplicação

**Decisão:** a `MainWindow` deixou de ser uma janela de 900x700 com decoração do
sistema e virou um painel de 360x560 sem decoração, arrastável, com três modos
de exibição (`Expanded`, `Compact`, `Collapsed`) e memória de posição e tamanho.
O tema passou a ser **escuro por decisão** (o ADR-041 trouxe depois o claro e
"seguir o Windows"), com paleta própria em
`Styles/Tokens.axaml`.

**Por quê:** `RequestedThemeVariant="Default"` fazia o app seguir o modo escuro
do Windows, e `DockPanel Margin="40,32"` dentro de 900x700 dava o resto. O
resultado era uma chapa preta enorme na área de trabalho — o oposto de algo que
se deixa aberto enquanto se trabalha. A variante agora é fixa (`Dark`) e a cor
de fundo é carvão (`#17181C`), não preto: `#000` puro num painel pequeno e
sempre visível vira um buraco, e a borda arredondada some contra monitores
escuros.

**A paleta é reaproveitamento, não substituição.** `Tokens.axaml` reaponta as
chaves do Fluent que as telas já pediam (`SystemFillColorCautionBrush`,
`ControlStrokeColorDefaultBrush`, `AccentFillColorDefaultBrush`, …). Vestir
botão, campo e checkbox assim custa um dicionário; reescrever `ControlTheme`
custaria centenas de linhas e um template para manter a cada atualização do
Avalonia.

**Arrastar e redimensionar são declarativos.** No Avalonia 12,
`ExtendClientAreaChromeHints` não existe mais: o equivalente é
`WindowDecorations="None"` + `ExtendClientAreaToDecorationsHint="True"` e marcar
elementos com `WindowDecorationProperties.ElementRole` (`TitleBar`, `ResizeSE`,
…). Quem move a janela continua sendo o sistema. Os manipuladores de
`PointerPressed` com `BeginMoveDrag`/`BeginResizeDrag` ficaram como rede de
segurança para o caso de a plataforma ignorar o papel — se ela honrar, o
ponteiro nem chega até eles.

**O pino não mexe em geometria.** "Sempre no topo" não entra em nenhum caminho
de posição ou tamanho: `ApplyMode()` e `ClampToScreen()` só correm quando `Mode`
muda. Com o pino ligado o painel continua sendo arrastado, redimensionado,
movido entre monitores e clicado como antes, e nada reaplica o `Topmost` ao
ganhar foco. O erro tentador é implementar o pino como "travar o painel onde
está" — dois gestos diferentes no mesmo botão, e silencioso: o botão acende
igual, e só quem tenta arrastar descobre. `WidgetPinTests` guarda isso pelo lado
estrutural: alternar o pino não pode mexer em `Position`, `Width`, `Height`,
`CanResize` ou `WindowState`, e o estado gravado é comparado campo a campo.

**O que o pino leva junto é o modo discreto.** Fixar, na prática, quer dizer
"deixa isso aí sem me atrapalhar", então `ToggleTopmost` liga `IsGhost` com ele —
um clique entrega os dois. Os dois continuam **separáveis**: "Modo discreto" no
menu e na bandeja devolve a moldura sem soltar o pino. Isto revisa o "somente" do
item 8 da especificação; o que o item pedia de fato — a janela continuar móvel —
segue valendo e testado.

**Fixado, a lista mostra só o que falta.** O pino também tira a seção
`CONCLUÍDAS` do quadro (`TodayViewModel.HideCompleted`). O motivo é o mesmo do
modo discreto: fixado, o painel fica num canto sobre as outras janelas, e ali
altura é o recurso escasso — uma tarefa riscada empurra para fora da vista uma
que ainda espera. Três limites, e cada um tem teste:

- **Esconder não é desfazer.** `TotalCount`, `CompletedCount`, `ProgressLabel` e
  o balão da bandeja continuam contando tudo; só a lista encolhe. Marcar e
  desmarcar segue funcionando pelo checkbox das linhas que restaram.
- **Não custa consulta.** O quadro carregado fica em `_board`, e trocar o modo
  remonta as seções a partir dele. Recarregar seria pior que lento: apagaria a
  mensagem de erro que estivesse à vista, pelo simples gesto de fixar o painel.
- **Dia terminado não vira painel em branco.** Sem as concluídas, um dia todo
  feito esvazia a lista — e no modo discreto não sobra nem cabeçalho para
  explicar o vazio. Daí `EmptyMessage`: "Tudo concluído. Aproveite." quando
  houve trabalho, "Nada para hoje. Aproveite." quando não houve.

Quem decide é a moldura, mas quem monta a lista é o quadro de hoje, então a
`MainWindow` carrega a decisão de um para o outro (`ShowPendingOnly`) — no
`IsTopmost` e também no `DataContextChanged`, porque o pino restaurado do disco
chega sem passar por clique nenhum.

**Modo discreto é a ausência da moldura, não um quarto modo.** `IsGhost` é
preferência (mora no `widget.json`), e não um valor de `WidgetMode`: ele se
combina com Completo e Compacto em vez de competir com eles. No modo discreto o
fundo do painel vira `Transparent`, a borda e a sombra somem e o cabeçalho sai de
cena. Três consequências tiveram de ser compensadas, e cada uma tem teste:

1. **Sem cabeçalho não há barra de arrasto.** O `Border.widgetShell` assume
   `ElementRole="TitleBar"`, e o fundo é `Transparent` — e não nulo — porque
   fundo nulo não recebe ponteiro e arrastar por área vazia deixaria de existir.
   O custo aceito: o retângulo do painel captura cliques que iriam para a área de
   trabalho atrás dele.
1b. **Sem cabeçalho também não há porta de volta.** Por isso o alfinete flutua
   ao lado do "+", e não só o "+": ele é o único caminho *na janela* para
   desfazer o modo. A bandeja continua existindo, mas é um caminho que ninguém
   encontra sozinho — e um widget sem moldura de onde não se sai é um widget
   preso na frente da tela.
2. **A lista está dentro da área de arrasto.** `Border.row`, o `CaptureCard` e o
   "+" saem dela com `ElementRole="User"`. Sem isso, marcar uma tarefa viraria um
   empurrão na janela — a mesma armadilha nº 4, agora do outro lado.
3. **Sem fundo, texto claro sobre janela clara some.** Cada linha ganha a própria
   pílula translúcida em vez de um scrim no painel inteiro: entre as linhas fica
   a área de trabalho limpa, que é o ponto do modo.

`IsGhostActive` é o que a tela usa, e não `IsGhost`: **recolhido o modo discreto
não vale**, porque ali o cabeçalho é a única coisa desenhada e escondê-lo apagaria
o widget da tela. A captura não ocupa espaço até o "+" pedir (`IsCaptureOpen`, de
sessão — não vai para o disco), e voltar para a moldura fecha a que ele abriu,
senão sobrariam duas capturas na tela.

**A moldura não é o `DataContext`.** O `DataContext` da janela continua sendo o
`TodayViewModel`; o estado do widget mora numa propriedade `Chrome` da própria
janela (`WidgetChromeViewModel`), ligada por `{Binding #Shell.Chrome.X}`. Trocar
o `DataContext` por um shell view model quebraria a tela de hoje e os testes
headless que a sobem.

**Geometria é JSON, não tabela.** `%APPDATA%/MyTaskApp/widget.json`, via
`IWidgetStateStore`. Posição de janela não é dado do usuário, não entra em
backup e não merece migração; um arquivo perdido custa um arrastar de painel.
Arquivo corrompido ou editado à mão degrada para o padrão, e `Sanitized()`
impede painel de tamanho zero.

**Cinco armadilhas, registradas porque falham em silêncio:**

1. **Pixel físico contra unidade de DPI.** `Window.Position` é `PixelPoint`
   físico; `Width`/`Height` são independentes de DPI. Misturar os dois faz o
   painel encolher a cada reabertura num monitor a 150%. A conversão passa pelo
   `Screen.Scaling` da tela onde ele vai pousar.
2. **Posicionar cedo demais não funciona.** Definir `Position` antes de a janela
   ter handle — e mesmo dentro de `OnOpened` — é sobrescrito: o Avalonia ainda
   aplica o próprio posicionamento de abertura depois do evento. A colocação
   acontece num `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)`, e
   nada é gravado antes disso (`_placed`), senão o app salvaria a posição de
   cascata do Windows por cima da escolha do usuário.
3. **Seletor de tipo casa exato.** `UserControl.compact Border#CaptureCard` não
   alcança o `TodayView`, que *deriva* de `UserControl` — o seletor precisa do
   tipo concreto (`views|TodayView.compact`). Isto passou pelo compilador e só
   apareceu porque havia teste: o modo compacto simplesmente não escondia a
   captura.
4. **`ElementRole="None"` não é "sem papel": é "invisível ao hit-test do
   chrome".** Os botões do cabeçalho ficam *dentro* do `Border` marcado como
   `TitleBar`. Com `None` o clique não encontra o botão, sobe para a área de
   arrasto e vira movimento de janela — o alfinete só sacode o painel. Quem
   precisa receber clique dentro da barra é `User` (`DecorationsElement` é o
   equivalente, reservado para temas). Esta passou pelo compilador **e pelos
   testes headless**, porque o headless não faz hit-test não-cliente: lá o papel
   não tem efeito nenhum, e um clique simulado passa com os dois valores. Só
   quebra na máquina do usuário. Por isso o teste que a guarda
   (`TheChromeButtons_ReceiveInputInsideTheTitleBar`) afirma o **papel
   declarado**, e não o clique.
5. **`InputHitTest` responde pela árvore de composição, não pelo layout.** Depois
   de trocar de modo, `Bounds` já está atualizado e o hit-test ainda aponta para
   onde os elementos estavam — um teste de clique falha sem que haja nada errado
   no app. Nos testes headless quem fecha essa distância é
   `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`, no `Settle`. Pela mesma
   razão, asserção sobre propriedade com `Transitions` (o `Background` da linha
   tem `BrushTransition` de 120ms) lê um valor no meio da animação: o que vale
   comparar é `GetBaseValue`.

**Ícone é fonte, não emoji.** `📌` e `🔔` vêm com cor própria e ignoram
`Foreground`, o que estoura uma paleta contida. Os glifos vêm de
`Segoe Fluent Icons, Segoe MDL2 Assets` (`WidgetIconFont`), que são
monocromáticos e herdam a cor do botão. A Segoe só existe no Windows; fora dele
quem responde é a fonte embutida do ADR-046.

**Alerta ficou discreto, e do lado certo.** O `AlertWindow` encolheu para 336 e
recebeu a mesma casca do painel. O `AlertPresenter` passou a empilhar na tela
**onde o widget está** (`ScreenFromPoint(MainWindow.Position)`) em vez de sempre
na primária: com dois monitores, o aviso aparecia do outro lado da mesa. O
contador de pendências virou o balão do ícone da bandeja — o aviso mais discreto
que existe, porque só aparece se o usuário for olhar.

**Contratos que a redesenhada teve de preservar** (e que os testes headless
guardam): `Title` continua `"MyTaskApp"`, o `DataContext` continua sendo
`TodayViewModel`, existe exatamente **um** `TextBox` e **um** `CheckBox` por
linha na janela, o botão de captura continua com `Content="Adicionar"`, o rótulo
`HOJE — dd/MM/yyyy` continua desenhado num `TextBlock` (agora como linha de data
do cabeçalho) e o `AlertWindow` continua com exatamente cinco botões e as
classes `card`/`prominent`.

**Limites aceitos:** sem decoração do sistema não há *snap* do Windows (arrastar
para a borda não ancora) — troca consciente por canto arredondado e sombra
próprios. "Iniciar recolhido" esconde a janela no `Opened`, então há um piscar
de um quadro. E o modo compacto redimensiona, mas não lembra: arrastar a borda
vale pela sessão, e a próxima troca de modo volta ao teto de 420px. É de
propósito — `WidgetState` tem um par `Width`/`Height` só, então gravar a altura
do compacto apagaria para sempre o tamanho que o usuário escolheu no painel
inteiro. Nada disso vem do pino, que continua sendo apenas ordem Z.

> **Revisto pelo ADR-048.** O modo discreto saiu, e o alfinete do cabeçalho
> virou "Fixar como HUD", que muda a geometria de propósito. "Sempre no topo"
> continua no menu, só para a janela normal, e continua sendo apenas ordem Z.
> O que este ADR diz sobre o modo discreto e o pino levando-o junto fica como
> registro.

## ADR-018 — Distribuição: Inno Setup, e uma linha divisória entre aplicação e dados

**Decisão:** o MyTaskApp é distribuído como instalador. No Windows, **Inno
Setup 6** gerando um `.exe` por usuário; no Linux, tarball com `install.sh`;
macOS fica documentado e não implementado. Publicação **self-contained**, sem
trimming. Tudo vive em `installer/`, fora de `src/`.

**Por que Inno Setup e não WiX/MSI:** o MSI paga um preço alto — XML verboso,
harvest de centenas de arquivos, UI datada — por benefícios que este app não
usa (GPO, rollback transacional do Windows Installer, patching). O Inno entrega
`.exe` pequeno, instalação por usuário **sem UAC**, `/VERYSILENT`, detecção de
upgrade por `AppId` e log próprio, com um arquivo de script legível. É o que
VS Code, Docker Desktop e Git for Windows usam.

**Por que não Velopack/Squirrel:** trazem updater embutido e acoplam o
`Program.Main` ao framework de atualização. O briefing pede explicitamente para
não construir updater complexo antes de ter base sólida de instalação.

**A regra que organiza tudo:**

```
Aplicação → diretório de instalação   (substituído a cada atualização)
Dados     → pasta de dados do usuário (nunca tocada)
```

Antes deste ADR, `%APPDATA%/MyTaskApp` era calculado **três vezes** — em
`LoggingSetup`, `WidgetStateStore` e `DatabaseOptions`. Três cópias da mesma
regra é como uma delas acaba diferente. `UserDataLocation`
(`Infrastructure/Storage/`) passou a ser a única a saber, e `MYTASKAPP_DATA_DIR`
move as três juntas.

**Classe comum, sem interface.** Não há segunda implementação nem nada a
substituir em teste além da raiz, que já entra por parâmetro (ADR-005). O
método de criação chama-se `CreateDirectories()`, e não `EnsureCreated()`, para
que o nome proibido pelo §19 não apareça nem por coincidência num grep.

**O instalador não conhece o banco.** Não o copia, não o lê, não o apaga e não
roda migration nenhuma. A aplicação continua dona da evolução do schema
(`Program.PrepareDatabase` → `MigrateAsync`). Há teste de packaging que falha
se a string `.db` ou `sqlite` aparecer no `.iss`.

**Desinstalar preserva.** `[UninstallDelete]` está **vazio**, e há exatamente um
`DelTree` no arquivo inteiro, dentro de `RemoveUserData`, alcançável só depois
de uma confirmação cujo botão padrão é **Não**. Em modo silencioso nada é
apagado sem `/DELETEDATA=1` explícito. Três testes cercam isso, porque é a
única falha desta entrega que o usuário não teria como desfazer.

**Configuração do usuário sobrevive ao upgrade.** `appsettings.json` mora no
diretório de instalação e é sobrescrito a cada atualização — então
`BuildConfiguration` ganhou uma segunda camada opcional,
`<dados>/appsettings.user.json`, que o instalador nunca toca. Era o caminho
por onde um `TimeZoneId` ajustado à mão se perdia.

**Logs separados por subárvore, não por nome:** instalador em
`%LOCALAPPDATA%\MyTaskApp\installer\logs`, aplicação em `%APPDATA%\MyTaskApp\logs`.
Quem procura "por que não instalou" não pode esbarrar em "por que o lembrete não
tocou". O Inno grava log **sempre** (`SetupLogging=yes`), não só com `/LOG` —
ninguém liga o log antes de o problema acontecer.

**Logger de bootstrap.** `appsettings.json` é carregado com `optional: false` e
ficava **fora** do `try` do `Main`: uma instalação sem esse arquivo matava o app
antes de existir log, o sintoma mais difícil de diagnosticar à distância. Agora
um logger de arquivo sobe primeiro e a leitura da configuração acontece dentro
do `try`. O `Log.CloseAndFlush()` antes de trocar o logger não é zelo: o sink de
arquivo abre o log do dia com trava exclusiva, e sem soltar primeiro o logger
definitivo não conseguiria abrir o mesmo caminho.

**Self-contained, ~53 MB.** A alternativa de ~8 MB exigiria o .NET Desktop
Runtime na máquina — mais uma tela, mais um download, mais um pedido de
elevação e um modo de falha ("instalou e não abre") caro de diagnosticar.
Trimming fica desligado (EF Core e Avalonia dependem de reflexão) e
`InvariantGlobalization` continua `false`, senão `America/Sao_Paulo` deixa de
resolver e o ADR-002 quebra.

**Versão com fonte única.** `VersionPrefix` em `Directory.Build.props` alimenta
o assembly, o instalador, a entrada em "Aplicativos Instalados" e o `.desktop`
do Linux. Os scripts de packaging leem com `dotnet msbuild -getProperty:Version`
em vez de repetir o número, e um teste quebra a build se alguém escrever versão
em outro lugar. Quem sobe o número é o Release Please, não uma pessoa (ADR-044).

**O assembly passou a se chamar `MyTaskApp`**, não `MyTaskApp.Desktop` — é o
nome do `.exe` que o usuário vê instalado. Custou atualizar três URIs
`avares://`, que usam o nome do assembly; os testes headless guardam isso,
porque um `avares://` errado só falharia ao abrir o app.

**Limite aceito:** o instalador não é assinado. O gancho está pronto (`SignTool`
no `.iss`, `-Sign` no `build.ps1`) e sem certificado o SmartScreen avisa na
primeira execução. Assinar é a correção; desligar o aviso não é.

## ADR-019 — Instância única, e o clique no atalho que traz a janela de volta

**Decisão:** `Program.Main` adquire um `Mutex` nomeado antes de qualquer outra
coisa. O segundo lançamento **não abre nada**: sinaliza o primeiro e encerra
com 0.

**Por que agora:** o ADR-016 registrou isto como "fora de escopo — um `Mutex`
nomeado no `Program.Main` é a correção barata no dia em que isso morder". O
instalador é esse dia. Um atalho no Menu Iniciar torna relançar trivial, e a
bandeja torna relançar **provável**: a janela está escondida, o usuário acha que
fechou o app. Dois processos no mesmo SQLite significam aviso dobrado,
`SQLITE_BUSY` e dois `widget.json` disputando a mesma posição.

**O nome do mutex é contrato com o instalador.** `SingleInstance.MutexName` é o
mesmo valor que o `.iss` usa em `AppMutex`, que é como o Inno descobre que o app
está aberto antes de trocar binários — detecção de arquivo em uso não basta,
porque o processo pode estar ocioso na bandeja. Há teste de packaging comparando
os dois lados: renomear um sem o outro quebra a build, não a atualização de
alguém.

> **Revisto pelo ADR-049.** O `.iss` não usa mais `AppMutex`: o instalador
> fecha o app em vez de pedir que o usuário o feche. O mutex continua contrato
> (`CheckForMutexes`), como detecção de reserva.

**Encerrar calado seria pior do que não fazer nada.** Como o app vive na
bandeja, o segundo lançamento que apenas morresse deixaria o usuário clicando no
atalho sem ver resposta. Então o não-dono sinaliza um `EventWaitHandle` nomeado
e o dono revela a janela pelo `Reveal` que já existia em `App.axaml.cs`.

**Evento nomeado é API só do Windows** — no Linux e no macOS ele lança
`PlatformNotSupportedException`, então é criado sob `OperatingSystem.IsWindows()`
e lá o mutex sozinho resolve o que importa: o segundo processo encerra em vez de
duplicar o agendador. O `Mutex` nomeado, esse, funciona nos três.

**Detalhes que são decisão, não acaso:**
- o portão vem **antes** do logger: dois processos abrindo o mesmo arquivo de log
  do dia disputariam a trava exclusiva do sink, e o segundo nem precisa de log —
  ele só sinaliza e sai;
- o listener é `Thread` de fundo, não `Task`: a espera é bloqueante e dura toda a
  vida do app, e prender um thread do pool nisso seria pior;
- `ReleaseMutex` só no dono, dentro de `try`: soltar mutex que não se possui
  lança, e isso aconteceria justamente no caminho de encerramento;
- um mutex abandonado por processo que morreu volta a ser adquirível — uma queda
  não tranca o app para fora de si mesmo.

**Fora de escopo, e de propósito:** iniciar com o Windows. É a próxima
funcionalidade óbvia para um app de bandeja, seria uma linha no `[Registry]` do
instalador, e continua fora porque o ADR-016 a listou como fora — mudar isso
merece decisão própria, não uma carona no packaging.

> **Revisto pelo ADR-023**, que é a decisão própria que este parágrafo pediu. Não
> saiu por uma linha no `[Registry]`: o argumento `--startup`, o toggle no menu e
> a caixa que consulta o registro numa atualização vieram junto.

## ADR-020 — Ciclo de vida do checklist: estado derivado, não coluna de estado

**Decisão:** arquivar, lixeira e exclusão definitiva entram como **três marcas
de tempo no `TaskItem`** — `ArchivedAt`, `DeletedAt` (com `DeletedBy`) e
`ConcludedAt` — e o estado do §9 é **derivado** delas em `TaskItem.Lifecycle`:

```
DeletedAt   != null → Lixeira      (vence tudo)
ArchivedAt  != null → Arquivado
ConcludedAt != null → Concluído
senão               → Ativo
```

**Por que não uma coluna `Status` com o enum:** seriam duas fontes da verdade
para a mesma pergunta. "Arquivado" e "arquivado em 12/09" teriam de concordar
para sempre, e no dia em que discordassem não haveria como saber qual das duas
está certa. Derivar custa uma expressão; guardar custaria mais um invariante a
manter em cada caminho de escrita.

**As duas marcas são independentes de propósito.** Um checklist arquivado que
vai para a lixeira conserva `ArchivedAt`, então restaurá-lo da lixeira o devolve
ao **arquivo** — o estado em que ele estava — sem nenhum campo "estado
anterior". A precedência da lixeira sobre o arquivo é o que faz o usuário agir
onde o prazo está correndo.

**Não existe valor de enum para "excluído definitivamente".** É o estado
terminal do §9, e ele não é um estado da entidade: é a ausência dela. Um valor
que nenhuma linha do banco pode carregar seria código morto com cara de regra. O
que sobrevive é a trilha, e lá ele tem nome:
`TaskAuditOperation.PermanentlyDeleted`.

### `ConcludedAt` é derivado, mas persistido — e mantido pela raiz

O §2 manda contar o prazo da **data de conclusão**, não da criação. Isso exigiu
que as transições de ocorrência passassem a entrar pela raiz
(`TaskItem.CompleteOccurrence` e irmãs, no lugar de
`task.GetOccurrence(id).Complete(...)`): é a raiz que recalcula `ConcludedAt` na
mesma operação, então o campo não tem como divergir das ocorrências.

Persistir o derivado paga uma coisa concreta: a varredura vira um predicado
sobre índice parcial (`IX_Tasks_ReadyToArchive`) em vez de um `GROUP BY` sobre
`TaskOccurrences` a cada tique. **Cancelada não conta como concluída** — um
checklist do qual se desistiu não tem data de conclusão e portanto nunca é
arquivado sozinho.

**A migration faz backfill.** Sem ele, todo checklist concluído antes da
atualização ficaria com a coluna nula e jamais entraria no arquivamento
automático. Fazer isso na atualização só é seguro porque o arquivamento
automático **nasce desligado** (abaixo).

### Auditoria sem chave estrangeira — a decisão central do §8

`TaskAuditEntries` **não** tem FK para `Tasks`, e isso não é esquecimento: com
FK, o `DELETE` da exclusão definitiva levaria junto o registro dessa mesma
exclusão (cascata) ou passaria a falhar (sem cascata). O preço é que `TaskId`
pode apontar para nada — que é exatamente o que se quer dizer depois de um
`PermanentlyDeleted`. Pela mesma razão o **título é copiado**, não juntado: uma
trilha que só mostra GUIDs não serve para investigar nada.

A linha entra na **mesma unidade de trabalho** da operação auditada
(`RecordAsync` só rastreia; quem grava é o `SaveChanges` do caso de uso). Não
existe arquivamento sem registro, nem registro de algo que a regra recusou.

**Quem fez** é `AuditActor` (`User`/`System`) mais um nome opcional. É por isso
que "exclusão automática realizada pelo sistema" não precisou de operação
própria — e é `ICurrentUser` que responde o nome, com `UnknownUser` como padrão
degradado (`TryAddSingleton`, como o `TimeProvider.System`) e a conta do Windows
registrada pelo Desktop por cima.

### Arquivamento automático nasce desligado

Mesma razão do ADR-014 ("Upgrade não arma nada"): ligar sozinho faria a primeira
abertura depois da atualização varrer o histórico inteiro do usuário para fora
da lista, sem ninguém ter pedido. A lixeira não corre esse risco — numa
atualização ela está vazia —, então lá o prazo de 30 dias já vale. Consequência
boa: uma linha de configuração corrompida degrada para o padrão de fábrica, e o
padrão de fábrica **não varre nada**.

## ADR-021 — Um segundo agendador, e não um segundo passo do tique dos lembretes

**Decisão:** `LifecycleMaintenanceScheduler`, classe própria, mesmo desenho do
`ReminderScheduler` (ADR-015): `TimeProvider.CreateTimer`, `IUseCaseRunner`,
`SemaphoreSlim(1,1)` com espera zero, primeiro tique imediato, `IDisposable` e
`IAsyncDisposable`.

**Por que não pendurar a varredura no tique dos lembretes:** as cadências são de
ordens de grandeza diferentes — 30 s contra 6 h. Uma varredura de banco no laço
quente rodaria 720 vezes por hora para não achar nada.

**Por que não extrair uma base comum agora:** extrair mexeria numa classe
documentada e testada para servir um segundo caso. O custo aceito é a mecânica
do timer aparecer duas vezes; extrair fica para quando houver um terceiro laço.

**O primeiro tique imediato é o que faz a rotina funcionar num app de desktop,**
que passa mais tempo fechado do que ligado: sem ele, a lixeira de quem abre o
app uma vez por semana nunca seria esvaziada.

### Idempotência em três camadas

Rodar a varredura duas vezes não reprocessa nada, e isso não depende de
marca-d'água nem de tabela de controle:

1. **o predicado da consulta** já exclui o que foi processado (arquivado deixa
   de ser candidato a arquivamento);
2. **a reconferência em memória** depois de carregar o agregado — entre a
   consulta e a escrita o usuário pode ter reaberto, restaurado ou excluído o
   item na tela, e nesses casos a tela ganha;
3. **o próprio agregado**, que recusa arquivar o que já está arquivado.

A camada 2 é a que mais importa na exclusão definitiva, a única operação do app
sem volta. O `Mutex` nomeado do ADR-019 garante que não exista um segundo
processo varrendo em paralelo, e o semáforo do agendador que um tique não
atropele o anterior.

**Um único `SaveChanges` por tique**, com teto de 100 itens: arquivar 40 e
apagar 12 é uma transação só, e abrir o app depois de meses não vira uma
transação de milhares de linhas.
**Limite aceito, e medido:** não há token de concorrência em `Tasks`. O
`SaveChanges` da varredura emite `DELETE FROM Tasks WHERE Id = @p0`, sem
condição, então existe uma janela — entre a reconferência em memória e a
gravação — em que um "Restaurar" clicado pelo usuário poderia ser perdido. Ela
é de microssegundos, vale só dentro de um processo (ADR-019) e o SQLite
serializa as escritas. A correção honesta seria uma coluna de versão conferida
no `WHERE`; a tentadora seria `ExecuteDeleteAsync` com predicado, que roda fora
do `SaveChanges` e **quebraria a transacionalidade da auditoria** — o preço
errado para fechar esta fresta.


### Arquivado e na lixeira somem da lista principal — por filtro explícito

`TodayQuery` e `DueReminderQuery` filtram `ArchivedAt IS NULL AND DeletedAt IS
NULL`. **Não** foi usado `HasQueryFilter` global: as áreas de arquivados e
lixeira precisam justamente do que ele excluiria, e um filtro global obrigaria
`IgnoreQueryFilters()` espalhado — inclusive no caminho de **restaurar**, que
passaria a não encontrar o registro.

No `DueReminderQuery` o filtro é cinto e suspensório: arquivar e excluir já
desarmam os lembretes no agregado, mas um checklist guardado não pode voltar a
tocar nem por uma linha que tenha escapado. Restaurar rearma **a partir de
agora** — ressuscitar um horário vencido avisaria na hora, do nada.

### O que a interface assumiu (§12)

A lista principal não ganhou nenhum botão novo. Arquivar e excluir vivem no menu
de contexto da linha, e o `⋯` que aparece no hover **abre o mesmo
`ContextFlyout`** em vez de ter um menu próprio — duas cópias em XAML
divergiriam no primeiro item novo. Arquivados, Lixeira e a seção "Gerenciamento
de dados" ficam numa janela à parte, aberta pelo menu do painel.

**Concluídos ganhou uma aba própria, antes de Arquivados.** A tela "Hoje" só
mostra o que terminou no dia (`TodayClassifier`), então na virada o concluído
some sem deixar onde olhar — e com o arquivamento automático desligado de
fábrica ele nunca chegaria aos Arquivados. A aba é um terceiro
`ChecklistScope.Concluded` na mesma consulta: `ConcludedAt` preenchido, fora do
arquivo e da lixeira — exatamente o predicado do `IX_Tasks_ReadyToArchive`, que
serve o filtro e a ordenação sem índice novo. Série recorrente com ocorrência
pendente não tem `ConcludedAt` e por isso não aparece ali: o histórico por
ocorrência ficou fora deste passo.

**Arquivar não pergunta; excluir pergunta duas vezes, de formas diferentes.**
Arquivar não perde nada e se desfaz em dois cliques — confirmar ali só treinaria
o usuário a clicar "Sim" sem ler, encarecendo a pergunta que importa. A
confirmação da lixeira diz o prazo **lido do banco**, não um "30 dias" fixo que
mentiria para quem mudou a configuração. A confirmação da exclusão definitiva
tem faixa de aviso, botão de perigo e o foco em **Cancelar** — o Enter reflexo
tem de cair no botão que não faz nada.

**Armadilha registrada:** a janela de gerenciamento é singleton no contêiner, e
uma janela do Avalonia realmente fechada não pode ser mostrada de novo. O "X"
**esconde** — cancela o fechamento só quando o motivo é `WindowClosing`, para
não pendurar o encerramento do app. Sem isso, o segundo "Gerenciamento de
dados…" do menu lançaria, e só na máquina de quem usa. `ReminderSettingsWindow`
tem hoje a mesma forma sem a mesma proteção: ela some pelo botão Salvar, que
chama `Hide`, mas o "X" a fecha de verdade.

## ADR-022 — Ordem manual dentro da seção, e um item que descola da lista

**Decisão:** `int? Position` em **`TaskOccurrence`**, numeração **densa por
seção** reescrita inteira a cada solta, e ordenação por
`Position ?? int.MaxValue` como primeiro critério nas quatro seções pendentes.
`null` = nunca foi arrastada.

**Por que na ocorrência e não na série:** a linha que o usuário arrasta é uma
ocorrência, e duas ocorrências da mesma série podem cair em seções diferentes no
mesmo dia. Posição na série ordenaria as duas juntas — ou nenhuma.

**Por que densa e não fracionária:** rebalanceamento e deriva de ponto flutuante
são preços de listas que crescem sem teto. Uma seção de um painel de 360px tem
dezenas de linhas; reescrever todas num `SaveChanges` é determinístico, cabe numa
transação e dispensa manutenção para sempre.

**Quem nunca foi arrastado fica no fim, na ordem de sempre.** É o que encerra a
"Limitação conhecida" do ADR-013 — a captura rápida saindo em ordem alfabética —
**para quem arrastar**, sem mudar nada para quem não arrastar.

**A migration não faz backfill.** Eco do ADR-014 ("Upgrade não arma nada") e do
ADR-020 (arquivamento automático nasce desligado): a atualização não pode
renumerar a lista de ninguém sozinha. `null` em todo mundo significa exatamente
"a ordem de hoje continua valendo", e semear qualquer critério — alfabético, por
exemplo — congelaria para sempre algo que hoje é só o desempate. Há teste em
`UpgradePreservationTests`, contra um banco migrado **até a versão anterior** e
populado por SQL cru, que é a única forma de exercitar o que uma migration faz
com dados que já existiam.

**`Position` não ganha índice.** A ordenação é em memória, no handler (ADR-010),
e a coluna nunca é predicado nem `ORDER BY` em SQL. Um índice aqui seria simetria
com os três vizinhos da migration anterior, não necessidade — e custaria escrita
em todo arrasto.

**O comando carrega a seção inteira**, e não "moveu da casa 3 para a 1": a lista
completa é idempotente (reenviá-la não produz deriva, e a tela grava depois de já
ter movido) e dispensa quem chama de calcular delta nenhum.

**Validar tudo antes de mexer em qualquer coisa.** Reordenar é escrita em dezenas
de agregados de uma vez, então o handler faz duas passadas: a primeira confere
cada ocorrência por `TaskItem.EnsureOccurrenceCanBePlaced` — na forma de
`EnsurePermanentDeletionIsAllowed`, com a regra morando no agregado —, e só a
segunda numera. Sem isso, uma recusa no meio da seção deixaria metade da lista
renumerada em memória, e um `SaveChanges` posterior persistiria a meia-alteração.
É a armadilha que a "Edição atômica" de `TaskItem.Update` evita, agora espalhada
por vários agregados.

**Não há entrada de auditoria.** A trilha do ADR-020 existe para investigar o que
o usuário não desfaz sozinho — arquivar, lixeira, exclusão. Arrastar se desfaz
arrastando de volta, e registrar cada arrasto a afogaria em ruído.

### O que acontece com a posição quando a linha muda de seção

A seção é **derivada em leitura** (`TodayClassifier`), então `Position` é um
ordinal de seção guardado sem a seção. Três respostas, e só a primeira exigiu
código:

1. **CONCLUÍDAS ignora `Position` por completo** — lá a ordem é a da conclusão, e
   nada mais. E `PlaceAt` **recusa ocorrência que não esteja pendente**, o que
   congela a posição de quem concluiu em vez de deixá-la editável e
   silenciosamente descartada pela leitura. O ganho vem de graça e é o melhor
   comportamento possível: **concluir não perde o lugar, e reabrir devolve a
   linha exatamente onde ela estava.** A alça não aparece em CONCLUÍDAS — alça
   que não faz nada é pior do que alça nenhuma.
2. **Reagendar zera a posição**, pela mesma razão pela qual `Reschedule` já
   desarma o lembrete: o lugar era numa fila de outro dia.
3. **Entre ATRASADAS, AGORA e HOJE a posição viaja junto, e isso é aceito.** As
   três são a mesma fila partida pelo relógio. O preço é que o relógio pode pôr
   duas linhas na mesma casa; o desempate de sempre (data/hora) resolve de forma
   determinística, e há teste fixando isso. Limite medido, não descuido.

**Alternativa rejeitada — limpar a posição em `Complete`/`Reopen`:** explicável,
mas faria um clique errado no checkbox apagar o lugar que o usuário acabou de
escolher à mão.

**Revisão do escopo pedido.** A decisão original era "reordenar dentro de
qualquer seção", CONCLUÍDAS inclusive. O que a mudou foi perceber que arrastar
ali faria a lista mentir sobre a ordem em que as coisas foram feitas — e que
recusar comprava, de graça, o "reabrir devolve o lugar" do item 1.

### O item levantado é desenho, não janela

A linha descola da lista: cresce 4%, inclina 2°, ganha sombra e segue o cursor; o
vão é a própria linha, apagada onde estava. A pega é uma alça que aparece no
hover, como o `⋯` — a lista não pode virar barra de botões (§12), mas reordenar
também não pode ser um gesto que ninguém descobre.

**Por que não `DragDrop.DoDragDrop`,** e o motivo é estrutural, não estético: ela
entrega o gesto ao laço de arrasto do SO, **bloqueia** até a solta, dá `DragOver`
em vez de posição por quadro, troca o cursor por um bitmap do sistema e é
cross-process por desenho (`DataObject`) — justamente o que disputaria com o
`ElementRole="TitleBar"` do modo discreto. Captura manual de ponteiro mantém tudo
em processo, por quadro, e **testável**: nada do laço do SO chegaria ao
`AvaloniaHeadlessPlatform`.

**Camada local, e não `OverlayLayer`.** Um `Canvas` dentro do próprio
`TodayView`: as coordenadas do item, as das linhas medidas e as de
`e.GetPosition(this)` viram **um espaço só**, e o item continua dentro do canto
arredondado do painel em vez de pairar sobre a moldura e as alças de
redimensionamento.

**Uma definição de linha, dois desenhos.** O `DataTemplate` saiu para os recursos
e é usado pelo `ItemsControl` e pelo `ContentControl` do item levantado — mesma
razão pela qual o `⋯` abre o `ContextFlyout` da linha em vez de ter menu próprio:
duas cópias em XAML divergiriam no primeiro campo novo.

**Um passo por quadro.** `ReorderDrag.TargetIndex` compara só com o centro dos
vizinhos imediatos. Não oscila quando o cursor para numa divisa, mantém a
animação legível como uma sequência de trocas simples, e num flique rápido
recupera em poucos quadros — quem segue o cursor de verdade é o item levantado; a
lista embaixo só precisa chegar lá antes de o usuário soltar. De quebra, isso
reduz o deslizamento a **um vizinho por vez**, em lugar de medir a lista inteira
antes e depois.

**A solta não recarrega.** Atualização otimista: a coleção já se moveu e o item
ainda está pousando, e um `LoadAsync` recriaria todos os `TaskRowViewModel` no
meio da animação. Falhou, desfaz o movimento e mostra a mensagem — e **não**
recarrega, o que apagaria a mensagem no mesmo gesto que a produziu. O quadro em
memória (`_board`) é atualizado junto; sem isso, fixar o painel (`HideCompleted`,
ADR-017) remonta a lista a partir do quadro velho e ressuscita a ordem antiga.

**Cinco armadilhas, registradas porque falham em silêncio:**

1. **`IsVisible="False"` na linha de origem mata o vão.** O `StackPanel` a tira
   do fluxo, a lista sobe um degrau e o buraco deixa de existir. Tem de ser
   `Opacity = 0`.
2. **`TransformOperationsTransition` só interpola `TransformOperations`.**
   Atribuir um `TranslateTransform` faz a transição não acontecer, sem avisar. E
   a transição precisa ficar **desligada** enquanto o item segue o cursor —
   ligada, ele anda 180ms atrás da mão. Some-se a isso que
   `TransformOperations.Parse` lê números: em pt-BR, `"1,04"` quebraria o parse,
   então a formatação é `CultureInfo.InvariantCulture` (o app roda com
   `InvariantGlobalization=false`, ADR-002).
3. **Captura na alça se perde.** O container é reposicionado pelo `Move` da
   coleção, e container destacado da árvore derruba a captura. A captura é no
   `TodayView`, que nunca se move — e `OnPointerCaptureLost` cancela o arrasto,
   senão uma captura perdida deixaria o item levantado na tela para sempre.
4. **O refresh de 60 s do ADR-017 atropela o arrasto.** `LoadAsync` faz
   `Sections.Clear()`, e um arrasto atravessa a fronteira do tique com
   facilidade: os containers sumiriam debaixo do ponteiro. `IsReordering` é o que
   faz o tique passar direto.
5. **Esc não chega sozinho.** O foco está na caixa de captura (ADR-013), então o
   cancelamento escuta `KeyDown` no `TopLevel`, em **túnel**, e só durante o
   arrasto — handler esquecido lá é vazamento de sintoma mudo.

**`ElementRole="User"` é declarado na alça**, e não herdado da linha: é a
armadilha nº 4 do ADR-017, que decide no hit-test **não-cliente**, antes de o
ponteiro chegar ao Avalonia — `e.Handled = true` não a impede. O `Handled`
continua lá como rede de segurança para o `BeginMoveDrag` de
`MainWindow.axaml.cs`. O teste que guarda isso afirma o **papel declarado**, e
não o clique, porque no headless não há hit-test não-cliente e um clique simulado
passaria com qualquer valor.

**A alça é um `Border`, não um `Button`:** botão por linha herdaria foco e estado
`:pressed` que atrapalham o gesto, e há testes headless que contam botões por
janela.

**Limites aceitos:**

- se a gravação falhar **depois** do pouso, a lista volta sozinha para a ordem
  anterior enquanto a faixa de erro aparece — um pulo visível. Segurar a animação
  esperando o banco faria o gesto parecer travado em todo arrasto, para evitar um
  susto que quase nunca acontece;
- as seções com horário podem ficar fora de ordem cronológica. Foi escolha
  explícita: o painel deixa de responder "o que vem a seguir" pela posição, e
  passa a responder pelo rótulo de hora, que continua na linha.

**Fora de escopo, e de propósito: Alt+↑/Alt+↓ na linha focada.** O gesto é barato
e reusaria o mesmo comando; o que não é barato é o **modelo de foco** que a lista
precisaria — linha focável, visual de foco, ordem de Tab por dezenas de linhas e
convivência com a captura que toma o foco na abertura (ADR-013). Fica registrado
aqui para a omissão ler como decisão.

## ADR-023 — Iniciar com o Windows, e a chave `Run` como única verdade

**Decisão:** o instalador oferece **"Iniciar o MyTaskApp com o Windows"**,
**marcada por padrão**, e grava
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MyTaskApp` com
`"<exe>" --startup`. O app aprende esse argumento e, quando lançado por ele,
sobe **direto para a bandeja**. O menu ☰ do painel liga e desliga a mesma
entrada depois, sem reinstalar.

**Por que agora:** ADR-016 e ADR-019 listaram isto como fora de escopo, e o
ADR-019 pediu explicitamente que mudar de ideia viesse como decisão própria —
é este ADR. O que força a mão é a premissa do próprio ADR-016: *"o sistema fica
responsável por me lembrar" só é verdade se o processo estiver vivo às 15:00*.
Sem início automático, todo reinício da máquina desarma o produto inteiro, e
justamente para o usuário que mais precisa dele — o que já esqueceu da tarefa
não vai lembrar de abrir o app que lembra por ele.

**A regra que organiza tudo:**

```
Iniciar com o Windows  →  a chave Run, e mais nada
```

Nem `widget.json`, nem o banco, nem um marcador próprio do instalador. O mesmo
raciocínio do `UserDataLocation` no ADR-018: três cópias da mesma regra é como
uma delas acaba diferente. Aqui a segunda cópia teria um sintoma concreto — o
usuário desliga pelo menu, atualiza o app, e o início automático volta sozinho.

**Por que `HKCU`, e nunca `HKA`:** o resto do `[Registry]` usa `HKA`, que numa
instalação `/ALLUSERS` vira `HKLM`. Ali seria fatal: o app roda **sem elevação**
e conseguiria ler a entrada mas nunca apagá-la — a opção do menu viraria um
interruptor que só liga. Quem liga o início automático é uma pessoa, não uma
máquina.

**Por que `--startup` e não a preferência que já existia.** "Abrir recolhido da
próxima vez" (`WidgetState.StartHidden`) responde outra pergunta: como o app
abre quando **o usuário** o abre. O login é diferente — ninguém clicou em nada,
e um painel pulando na frente de quem acabou de ligar a máquina é exatamente o
oposto do que a opção promete. As duas origens convivem em `StartHiddenIfAsked`
sem uma mandar na outra. Pelo mesmo motivo, um segundo lançamento com
`--startup` **não** revela a janela do primeiro, ao contrário do clique no
atalho do ADR-019.

**Por que marcada, se o atalho da área de trabalho é desmarcado.** Não é
incoerência: o atalho é conveniência que o usuário não pediu — ruído. Iniciar
com o Windows é a entrega da promessa central do produto. Quem não quer desmarca
numa caixa visível, e em `/VERYSILENT` a opção vem ligada (`/MERGETASKS`
`"!startupicon"` desliga).

**A atualização pergunta ao registro.** `UsePreviousTasks` (ligado por padrão)
restauraria a escolha gravada pelo instalador anterior, desfazendo **em
silêncio** um "desliga isso" feito pelo menu do app. Então `InitializeWizard`
pré-marca a caixa pelo estado real da chave numa atualização, e deixa o padrão
marcado valer só na instalação nova. O item é localizado pelo texto da
descrição, mas os dois lados saem do mesmo `{cm:StartupTask}` — não há como
divergirem.

**A desinstalação apaga o valor sempre**, e não só via `uninsdeletevalue`: se a
entrada foi criada pelo menu do app, não existe registro de desinstalação para
o Inno reverter, e sobraria no `Run` um caminho para um `.exe` que não existe
mais — falhando calado a cada login. É registro de aplicação, não dado do
usuário, então sai sem pergunta; o `[UninstallDelete]` continua vazio e o único
`DelTree` continua sendo o de `RemoveUserData`.

**Interface, contra o que o ADR-018 prescreve.** Lá a regra é "classe comum, sem
interface, porque não há segunda implementação". Aqui há: o Windows e o objeto
nulo que responde `IsSupported = false` no Linux e nos testes headless —
escrever no registro de verdade durante `dotnet test` não é opção. As guardas
de plataforma ficam nos métodos públicos e os privados são
`[SupportedOSPlatform("windows")]`; sem isso o CA1416 quebra a build, que tem
`TreatWarningsAsErrors`. O helper de escrita não usa lambda de propósito: o
analisador examina o corpo de uma lambda como se ele pudesse rodar em qualquer
plataforma, e a guarda do método que a criou não vale lá dentro.

**O menu usa comando, não `Mode=TwoWay`** — ao contrário do "abrir recolhido"
que está uma linha acima dele. A escrita no registro pode falhar (política de
grupo, perfil em rede), e um visto marcado sobre uma escrita que não aconteceu
prometeria um app que não vai subir no próximo login. O comando relê o estado e
mostra a verdade; o porquê fica no log.

**Limites aceitos:**

- o Gerenciador de Tarefas > *Inicializar* pode **desabilitar** a entrada sem
  apagar o valor (`StartupApproved`). Nesse caso o visto do app discorda do
  Windows, e quem manda é o Windows. Ler `StartupApproved` seria passar a
  depender de um detalhe não documentado para responder uma pergunta que o
  próprio sistema já responde melhor;
- `/ALLUSERS` registra o início automático só para quem instalou — os demais
  ligam pelo menu do app;
- o toggle grava `Environment.ProcessPath`. Ligado a partir de um build de
  desenvolvimento, ele aponta o `Run` para o binário de `bin`/`artifacts`, e o
  Windows passa a subir esse. `MYTASKAPP_DATA_DIR` isola banco e logs, mas não
  isola o registro;
- o menu da bandeja ficou de fora: já tem nove itens, e "Abrir" está a um clique
  do menu do painel.

**Fora de escopo, e de propósito: o equivalente no Linux.** Seria um `.desktop`
em `~/.config/autostart`, espelhando `mytaskapp.desktop.in`. O pedido era
Windows, e o Linux não tem a bandeja como centro da experiência que torna isto
necessário lá.

## ADR-024 — Anotação do item: Markdown próprio, porque o editor pronto é pago

**Decisão:** cada item do checklist ganha um texto livre com formatação, aberto
por um **ícone na linha** com clique simples. O texto é **Markdown**, escrito
numa `TextBox` com barra de formatação e lido, depois que a tarefa é concluída,
por um renderizador próprio (`MarkdownView`) que monta `Inline`s do core do
Avalonia. Ele mora em `TaskItem.Description`, que já existia.

**Por que não o `RichTextEditor`:** o pedido dizia "usar o RichTextEditor". O
`Avalonia.Controls.RichTextEditor` e o `Avalonia.Controls.Markdown` são oficiais
e resolveriam a feature inteira, mas os dois exigem **licença Avalonia Pro
paga** — e o app não tem nenhuma dependência paga hoje. O Avalonia 12 não traz
nenhum editor de texto formatado gratuito: o core tem `TextBox` e `TextBlock`, e
só. O custo aceito são os dois arquivos de `Notes/`; o que se ganha é que a
anotação continua sendo **texto puro no banco**, legível por qualquer coisa que
abra o SQLite, em vez de RTF ou de uma árvore serializada presa a um pacote.

**Por que campo nenhum foi criado.** `TaskItem.Description` já existia no
domínio, no schema e no `UpdateTaskHandler` — faltava só a tela; nenhuma
migration. (O campo tinha teto de 4000 caracteres; depois caiu — ver "Limites
aceitos".) Um campo novo custaria duas colunas com o mesmo
significado e duas respostas para "onde fica o texto deste checklist". Como
`UpdateTask` é edição **atômica** de título, descrição e prioridade, a tela
guarda os outros dois na carga e os devolve inalterados: o erro tentador seria
mandar título vazio e apagar o nome da tarefa pelo gesto de anotar algo nela.

**Ícone, e não duplo clique** — o pedido original era duplo clique. Não dá: o
`Tapped` do título já copia o texto (§12), e no Avalonia o primeiro clique de um
duplo clique dispara o `Tapped` **antes** do `DoubleTapped`. O gesto duplo
copiaria e abriria de uma vez, com o balão "Texto copiado." aparecendo por cima.
As saídas eram adiar a cópia pelo intervalo de duplo clique do sistema — meio
segundo de atraso num gesto que hoje é instantâneo — ou tirar a cópia do clique
simples, desfazendo uma feature recente. O ícone não disputa com nada.

**O ícone é o indicador.** Ele segue a discrição do sino (`Opacity=0`, aparece no
`:pointerover` da linha), mas com anotação escrita fica aceso em
`WidgetAccentBrush` mesmo sem o mouse. Sem isso, descobrir onde há texto custaria
abrir item por item. O glifo muda com o estado — "abrir em janela" em
aberto, olho concluída —, que é a única pista, antes do clique, de que a janela
vai abrir só para ler. Já foi um lápis; deixou de ser quando a janela passou a
ter título editável e a aba Desenvolvimento, e "escrever" virou só uma das coisas
que se faz nela.

**Concluída vira leitura, e a barra some.** É a regra do pedido: terminada a
tarefa, a anotação é registro. A barra de formatação **desaparece** em vez de
ficar desabilitada — botão apagado ainda convida ao clique. Reabrir a tarefa
devolve a edição, porque quem desmarcou voltou a trabalhar nela. O leitor usa
`SelectableTextBlock`, e não `TextBlock`: sem a caixa de texto, o usuário
perderia junto a capacidade de copiar um pedaço da própria anotação.

**A janela é por item, e não singleton.** Duas anotações diferentes podem estar
abertas ao mesmo tempo, então o truque do "X que esconde" do ADR-020 não se
aplica — ele existe para quem tem uma instância só. Quem impede duas janelas do
*mesmo* item é o `App`, que guarda as abertas num dicionário por `TaskId`: duas
telas do mesmo texto teriam duas versões dele, e a última a salvar apagaria a
outra.

**Quatro armadilhas, três delas descobertas por teste:**

1. **Botão focável rouba a seleção.** Clicar num botão da barra tira o foco da
   `TextBox`, e com ele a seleção — o clique em **B** formatava o vazio onde o
   cursor caía. `Focusable="False"` em `Button.tool` é o que faz a barra
   funcionar.
2. **`*` dentro de `**` é metade de outro marcador.** Conferir os caracteres
   colados na seleção faz o botão de itálico *desfazer* o negrito. O que vale é
   o tamanho do bando de asteriscos, pela convenção do próprio Markdown: um é
   itálico, dois são negrito, três são os dois. Daí `***` ser um marcador de
   verdade no parser — é o que a barra escreve quando alguém clica em **B** e
   depois em **I**.
3. **Marcador mais longo primeiro.** Com `*` tentado antes de `**`, todo negrito
   vira um itálico vazio seguido de lixo. Marcador sem par volta a ser literal:
   quem digitou "2 * 3 = 6" não pediu formatação nenhuma.
4. **`IsVisible` não é herdado.** Um botão dentro de um pai escondido continua
   se declarando visível; o teste da barra que some tem de olhar
   `IsEffectivelyVisible`.

**Limites aceitos:** a anotação não tem teto de tamanho. Nasceu com 4000
caracteres, mas anotação é justamente onde cabe o que não coube no título, e
cortá-la seria perder o detalhe; o SQLite guarda TEXT sem limite, então a
migration `UnboundedDescription` só atualiza o snapshot do EF. O rodapé mostra
a contagem sem fração, para não sugerir um limite que não existe. A janela de
gerenciamento de dados mostra `Description` como texto cru, então lá a anotação
aparece com os asteriscos à mostra. E o parser não resolve ênfase aninhada que
encosta no marcador de fora (`**muito *mesmo***`): o par de dentro sai literal
(resolvido no ADR-038, junto com o resto do Markdown do VS Code).
Nenhum dos três aparece pelo caminho que a barra de formatação escreve.

## ADR-025 — Etiquetas: N:N com o checklist, bolinhas na linha

**Decisão:** o usuário cria etiquetas (nome + cor) e marca quantas quiser em
cada checklist. `Tag` é um agregado próprio (`Domain/Tags`), e o vínculo é a
tabela `TaskItemTags (TaskItemId, TagId)`, mapeada como coleção de
`TaskItem` (`TaskItem.Tags`, campo `_tags`), no mesmo molde das ocorrências. Na
lista de hoje a linha mostra **só bolinhas coloridas**, com o nome no balão; o
nome por extenso aparece no seletor e na janela "Etiquetas…".

**Ligado ao `TaskItem`, e não à ocorrência.** A etiqueta diz o que a tarefa
*é* ("Financeiro"), não o que aconteceu num dia — o mesmo raciocínio que pôs a
anotação na série (ADR-024).

**Nome e cor moram só em `Tags`.** O vínculo guarda as duas chaves e mais nada:
renomear ou recolorir aparece em todos os checklists na próxima carga do quadro,
sem sincronizar cópia nenhuma. A cor é sempre `#RRGGBB` em maiúsculas
(`TagColor.Normalize`); alfa é recusado porque uma bolinha translúcida some no
fundo escuro.

**Integridade no banco, não só no domínio:**

- a **PK composta** `(TaskItemId, TagId)` é a regra "a mesma etiqueta uma vez
  por checklist". `TaskItem.SetTags` já garante isso, e a chave impede que outro
  caminho escape;
- **cascata dos dois lados.** Excluir uma etiqueta ou expurgar um checklist leva
  os vínculos junto e nunca sobra referência órfã. Excluir não carrega cada
  checklist para tirar a etiqueta: seria uma ida ao banco por checklist;
- `Name` com collation **`NOCASE`** e índice único: "Urgente" e "urgente" são a
  mesma etiqueta. O handler confere antes (`EnsureNameIsFreeAsync`) só para
  devolver uma mensagem legível em vez de erro de constraint. O `NOCASE` do
  SQLite só dobra ASCII, então "Ágil" e "ágil" passam como nomes diferentes.
  Limite aceito.

**`SetTaskTags` recebe o conjunto final, não "adicione esta".** Um handler só
associa e remove, e repetir a chamada não muda nada. O seletor grava **a cada
marca**, e não ao fechar: o refresh de 60 s recria as linhas e fecharia o
seletor com as escolhas pendentes. Por isso o refresh também espera enquanto
um seletor está aberto (`TodayViewModel.IsPickingTags`). Duas marcas rápidas
passam por um `SemaphoreSlim`, porque conjuntos inteiros gravados fora de ordem
fariam o primeiro sobrescrever o segundo. A tela aplica a marca antes de gravar
e desfaz se a gravação falhar. O erro aparece **dentro do seletor**, que é para
onde o usuário está olhando.

**Sem N+1.** `TodayQuery` ganha uma terceira consulta em lote (vínculos + etiquetas
de todos os `taskIds` de uma vez, agrupados em memória). São três idas ao banco
qualquer que seja o tamanho da lista. `TagQuery` traz a contagem de uso como
subconsulta, servida pelo índice em `TaskItemTags.TagId`.

**Na linha, bolinha e não pílula.** Uma pílula com nome por etiqueta
competiria com o título pela largura de 360px. Aparecem **no máximo cinco
bolinhas**, e o resto vira um "+N" com os nomes no balão. O balão da bolinha é
o `ToolTip` nativo, mas **na cor da própria etiqueta** (`ToolTip.tagTip` em
`Widget.axaml`), com `ShowDelay` de 200 ms. `BetweenShowDelay` negativo impede
que correr o mouse pela lista vá abrindo um balão atrás do outro. Duas
armadilhas:

1. **O `DataContext` não chega ao balão.** Um `ToolTip` explícito em
   `ToolTip.Tip` é valor de propriedade, não filho. Um `{Binding Name}` nele
   abria um balão vazio. As amarrações apontam para a bolinha pelo nome
   (`#Dot.((vm:TagChipViewModel)DataContext)`). Só um teste que abre o balão
   pega isso.
2. **O deslocamento padrão é de 20px para baixo.** Ele foi feito para o balão
   que nasce no ponteiro. Com `Placement="Top"` ele empurrava o balão de volta
   por cima da bolinha, e por isso `VerticalOffset` é -4. Sem espaço acima, o
   popup vira para baixo sozinho. Onde o nome aparece sobre a cor (pílulas do seletor e
prévia), o texto é preto ou branco pelo critério de contraste do WCAG
(`TagColor.PrefersDarkText`), não por um limiar de brilho no olho.

**Cor: paleta primeiro, `ColorView` depois.** Doze cores curadas resolvem com
um clique. "Personalizar cor…" abre o `ColorView` do
`Avalonia.Controls.ColorPicker`, que é **oficial e gratuito** (ao contrário do
editor do ADR-024) e só traz controle, sem tema: o `StyleInclude` do tema
Fluent dele está no `App.axaml`. Sem ele, o controle não se desenha.

**Etiquetar ao criar.** A caixa de captura tem o mesmo botão de etiqueta, com
o mesmo seletor (template `TagPicker`, um só para os dois lugares). Lá ele
trabalha em **rascunho** (`TaskTagsViewModel.IsDraft`): marcar só guarda a
escolha, e quem grava é o `QuickCapture`, que recebe as etiquetas e as aplica a
**todas as linhas**. A gravação sai no mesmo `SaveChanges` das tarefas, e uma
etiqueta excluída nesse meio-tempo recusa a captura inteira, sem criar metade.
Escolha na tela, e não `#etiqueta` no texto: seria sintaxe a decorar (§36), e
um "#1" num título viraria etiqueta sem ninguém pedir. Depois de capturar, só o
texto se esvazia: **as etiquetas continuam marcadas** para a próxima captura,
porque quem registra uma leva de "Financeiro" costuma registrar a seguinte com
a mesma etiqueta. Desmarcar é com o próprio seletor. Como a escolha atravessa
capturas, o `Changed` da janela "Etiquetas…" também relê a lista para ela
(`TodayViewModel.RefreshCaptureTagsAsync`): uma etiqueta excluída sai das
bolinhas em vez de recusar a próxima captura, e uma renomeada troca de nome e
cor. Se a captura falhar, texto e etiquetas ficam, para tentar de novo.

**Janela singleton, com o "X" que esconde** (ADR-020), aberta pelo menu ⋯ do
cabeçalho ou pelo link do seletor vazio. Ela recarrega a cada abertura porque a
contagem de uso muda enquanto está escondida. Cada mudança dispara `Changed`, e
o `App` recarrega o painel para as bolinhas seguirem.

## ADR-026 — Diretórios da etiqueta: o alias é atalho de digitação, não referência

**Decisão:** cada etiqueta tem N **diretórios** (`TagDirectory`: alias, path,
nome e descrição opcionais), cadastrados no próprio cartão da etiqueta na janela
"Etiquetas…". Na anotação de uma tarefa (ADR-024), digitar `@` abre uma lista com
os aliases das etiquetas **daquela tarefa**. Escolher um item troca o `@texto`
pelo **path real**, e o alias não fica no texto.

**Por que não guardar `@alias` no texto.** Uma referência viva faria cada
anotação depender do cadastro: mudar o path reescreveria o passado, e excluir o
diretório deixaria um `@` órfão que o leitor Markdown não saberia desenhar. O
pedido é explícito: o alias é um *snippet*, e mudar `C:\` para `D:\` depois
**não** altera anotações antigas. A anotação continua sendo texto puro no
banco, como o ADR-024 quis.

**Entidade, e não uma lista de strings na etiqueta.** O diretório tem identidade
(`Id` v7, tabela `TagDirectories`) para ser editado e removido um a um. A
integração com Git (ADR-027) **não** se prende a ele: a tarefa guarda uma cópia
do caminho, pelo mesmo motivo de a anotação guardar o path, e não o alias. Ele é
parte do agregado `Tag`: `AddDirectory`,
`UpdateDirectory` e `RemoveDirectory` passam pela raiz, e excluir a etiqueta leva
as pastas junto (cascata).

**Alias único por etiqueta, não global.** Duas etiquetas podem ter um `@api` cada.
A regra mora no agregado (compara sem maiúsculas) e o índice único
`(TagId, Alias)` com `NOCASE` impede outro caminho. Numa tarefa com as duas
etiquetas, a lista mostra os dois `@api`, cada um com a bolinha e o nome da sua
etiqueta. O alias começa com `@`, e o `@` é posto se faltar. Depois dele vêm só
letras ASCII, dígitos, `.`, `-` e `_`, que são os mesmos caracteres que o
autocomplete reconhece no texto. O path precisa ser absoluto
(`IsPathFullyQualified`), vem sem aspas e sem barra final, e **pode não
existir**.

**A pasta é conferida na exibição, nunca no cadastro.** `IDirectoryProbe` (porta
da Application desde o ADR-027, implementada na Infrastructure; com
`Directory.Exists` sob `Task.Run`, porque um caminho de rede desconectado segura a
resposta) marca ✓/⚠ na lista da etiqueta e "pasta não encontrada" no
autocomplete. A lista aparece antes da conferência.

**Quando a lista abre** (`Notes/AliasCompletion`, puro e testado sem janela):

- o `@` precisa abrir o texto ou vir depois de espaço/pontuação. Em
  `fulano@empresa.com` ele é de um e-mail, e abrir a lista ali atrapalharia;
- o filtro é por **trecho** do alias (e do nome), e não só pelo começo: o
  exemplo do pedido mostra `@eco` achando `@scripts-eco`. O que começa com o
  digitado vem primeiro;
- Escape, clique fora ou aceitar "dispensam" aquele `@`. A lista não reabre
  sobre ele, e um `@` novo reabre. Sem isso, um path com `@` recém-inserido
  reabriria a lista sozinho.

**Teclado antes de tudo.** Com a lista aberta, ↑/↓, Enter, Tab e Escape são
tratados no handler de **túnel** da janela, antes da `TextBox` (Enter quebraria a
linha, Tab tiraria o foco) e antes do Escape que fecha a janela. Os itens não
recebem foco, pelo mesmo motivo do `Button.tool`: o cursor precisa ficar no
texto. O clique aceita no `PointerPressed`. O popup se posiciona no cursor via
`TextLayout.HitTestTextPosition` e `PlacementRect`.

**Os atalhos vêm pela tarefa, a cada ativação da janela.** `ListDirectoriesForTaskAsync`
junta `TaskItemTags → Tags → TagDirectories` numa consulta. Consultar pela
tarefa, e não pelo instantâneo da linha, acompanha etiquetas e diretórios
mudados enquanto a anotação estava aberta. Se a consulta falha, a anotação
continua funcionando, só que sem lista.

**Armadilha:** o `Id` nasce no domínio. Sem `ValueGeneratedNever`, um diretório
acrescentado a uma etiqueta já rastreada chega com chave preenchida, o EF o
toma por existente e gera `UPDATE`, e o `SaveChanges` falha por concorrência.
Só o teste de ida e volta contra SQLite pegou.

**Armadilha — o Popup fecha na hora.** O `IsOpen` do Popup é amarrado nos dois
sentidos. Fechar a lista dispara o `Closed` **dentro** da atribuição, e o
handler de "fechou por fora, então dispensa" rodava enquanto o token ainda
estava lá. Todo fechamento virava dispensa: digitar `@ecx` (sem resultado) e
apagar o `x` não reabria a lista. Por isso `CloseCompletion` zera o token
**antes** de fechar. Há teste de ViewModel que confere a ordem e teste headless
com o Backspace.

---

## ADR-027 — Aba Desenvolvimento: a tarefa vira um worktree Git

**Decisão:** a janela da tarefa ganhou abas, **Anotação** e **Desenvolvimento**.
Na segunda, o usuário:

1. escolhe um repositório, pelo `@alias` das etiquetas da tarefa (ADR-026) ou
   digitando o caminho;
2. escolhe a branch de origem, local ou remota, carregada do próprio repositório;
3. dá nome à branch nova. A sugestão é `feature/{slug-do-título}`, porque a
   tarefa só tem Guid, e um pedaço de Guid no nome da branch ninguém lê.

"Iniciar implementação" faz o fetch, atualiza a origem só por fast-forward, cria
a branch e o worktree numa **pasta irmã** do repositório
(`../{projeto}-{branch-sanitizada}`) e grava o ambiente na tarefa. Pronto, a aba
oferece abrir a pasta, abrir um terminal nela, copiar o caminho e remover o
worktree.

### O ambiente na tarefa: tabela própria, 1:0..1, dentro do agregado

> Desde o ADR-031, a relação é 1:N, com um ambiente por repositório. O índice
> em `TaskItemId` deixou de ser único, e o acesso é `TaskItem.Developments`. O
> resto desta seção continua valendo para cada ambiente.

`TaskDevelopment` guarda `RepositoryPath`, `SourceBranch`, `Branch`,
`WorktreePath`, `Status`, `CreatedAt`, `StatusChangedAt` e `FailureReason`. A
tabela é `TaskDevelopments`, com índice único em `TaskItemId`, e o acesso é
`TaskItem.Development`.

- **Não é owned.** Um owned opcional tem o mesmo "ausente ou tudo nulo?" que o
  lembrete já resolveu com `IsRequired` (ver `TaskItemConfiguration`). Além
  disso, a tabela `Tasks` é varrida pela tela Hoje e pelas varreduras, e o
  escopo futuro (commit, push, PR) cresce aqui sem mover dado de coluna.
- **Guarda cópias dos caminhos, e não o id do `TagDirectory`.** O alias é atalho
  (ADR-026). Renomear ou excluir o diretório da etiqueta não pode deixar um
  worktree em uso sem endereço.
- **Não guarda o que o Git sabe dizer:** alterações, commits, se a branch
  existe. Tudo isso é perguntado na hora.
- **"NotStarted" não é um status.** É `Development == null`.
- **Começar de novo muta a mesma linha.** Isso vale depois de Error, de
  Removed ou de um Creating órfão. Trocar a instância faria o EF inserir a nova
  antes de apagar a velha, e o índice único recusaria.
- **Guarda de estado.** Só `BeginDevelopment` passa por
  `RefuseWhenOutOfTheMainList`. Pronto, falha e remoção registram fatos do
  disco: a varredura pode arquivar o checklist no meio de um `worktree add`, e
  um checklist arquivado ainda precisa poder limpar o seu worktree.

### Git atrás de uma porta, e processo atrás de outra

```
Desktop ─ IUseCaseRunner ─► PrepareDevelopment / StartDevelopment / RemoveWorktree …
                                   │
                              IGitClient (Application)
                                   │
                              GitClient (Infrastructure) ─► IProcessRunner ─► git
```

- **`IGitClient` tem um método por pergunta ou operação**, nunca "rode estes
  argumentos". Montar linha de comando é assunto de um arquivo só, e é ele que
  se revisa quando o assunto é segurança.
- **Não existe operação destrutiva na porta.** Não há reset, clean, stash,
  checkout forçado nem `--force` em lugar nenhum. A origem só anda por
  fast-forward (`merge --ff-only` ou `fetch . upstream:refs/heads/b` sem `+`).
  A remoção é `git worktree remove` sem força, e o Git recusa se houver
  alterações. O que o Git deixa na pasta quando algo a segura fica com o
  ADR-029, fora desta porta.
- **`ProcessRunner` é a única porta do app para processos.** Ela usa
  `ArgumentList` (nunca uma string montada), sem shell, sem janela e com a
  entrada fechada. Lê stdout e stderr ao mesmo tempo, tem timeout e, ao
  cancelar, mata a árvore inteira. Um caminho com espaço ou uma branch com `&`
  não têm como virar outro comando.
- **O terminal e a pasta** ficam em `IShellLauncher` (Desktop):
  - a pasta abre pelo `Launcher` do Avalonia (Explorer, xdg-open, Finder);
  - o terminal tenta, em ordem: `wt -d` e depois o PowerShell com
    `WorkingDirectory` no Windows; gnome-terminal, konsole, xfce4-terminal,
    x-terminal-emulator e xterm no Linux; `open -a Terminal` no macOS.

### Duas metades, e o ViewModel no meio

- **`PrepareDevelopment`** valida Git, pasta e repositório, faz o fetch, valida
  a origem, olha as alterações, atualiza a origem, valida o nome e calcula o
  caminho.
  - Não grava nada e pode ser cancelado.
  - Devolve um `DevelopmentPlan`.
- **`StartDevelopment`** cria o worktree e grava o resultado.
- **Por que duas.** Entre as duas pode haver uma pergunta: a pasta calculada já
  existe. As opções são "usar o worktree existente" (só se for um worktree
  deste repositório), "escolher outro caminho" (já sugerindo `-2`) ou cancelar.
  Um caso de uso só teria de segurar escopo e DbContext abertos esperando um
  clique.
- **A ordem do Start é a do rastro:**
  1. grava `Creating`;
  2. roda `git worktree add --no-track -b nova caminho origem`. Se a branch
     já existe, reaproveita em vez de recusar: a local ganha checkout
     (`git worktree add caminho branch`); sem local, a remota vira local
     acompanhando-a (`git worktree add --track -b branch caminho remoto/branch`,
     preferindo o remoto da origem, depois `origin`);
  3. confere a branch em checkout;
  4. grava `Ready`.

  Falhou, grava `Error` com o motivo e relança. Se o app cai no meio, a tarefa
  diz "criação interrompida". Depois de gravar `Creating` não há cancelamento:
  matar o `worktree add` no meio deixa meio worktree registrado.
- **Progresso chega por `IProgress<DevelopmentProgress>` síncrono.** Os casos
  de uso não usam `ConfigureAwait(false)`, então o aviso já chega na thread de
  UI. O `Progress<T>` do BCL postaria para depois, e a lista andaria fora de
  ordem com o resultado.
- **Falha é `DevelopmentStepException : DomainException`.** Ela traz a etapa, o
  `GitCommandResult` (comando, exit code, stdout, stderr) e as alterações
  locais. A tela mostra a mensagem em pt-BR e deixa o erro real do Git em "Ver
  detalhes" e "Copiar detalhes".

### Alterações locais: bloqueiam só quando atualizar mexeria nelas

- **Bloqueia** quando a origem é uma branch local em checkout, atrás do
  upstream, e o worktree em que ela está tem alterações. Atualizá-la mexeria
  nesses arquivos. A tela lista as alterações e oferece abrir um terminal no
  repositório.
- **Divergiu** (commits dos dois lados): para e explica, sem tocar em nada.
- **Nos outros casos** (origem remota, ou branch local fora de checkout), o
  `worktree add` não toca no working tree principal. As alterações viram só um
  aviso: "N alterações não vão para o novo worktree".

### O caminho do worktree

`WorktreePathPlanner.Plan(repo, branch)` =
`Path.Combine(pai, $"{projeto}-{SanitizeSegment(branch)}")`.

- O projeto é o nome do **worktree principal** (o primeiro de
  `git worktree list`), mesmo que o usuário tenha escolhido uma subpasta.
- A sanitização:
  - troca `/`, `\`, `<>:"|?*`, espaços e caracteres de controle por `-`;
  - reduz `..` a `.` e junta hífens repetidos;
  - apara ponto, espaço e hífen nas pontas;
  - põe `-wt` em nome reservado do Windows;
  - limita a 80 caracteres.

  As regras são as do Windows em qualquer sistema, para o mesmo repositório dar
  a mesma pasta em qualquer máquina. Nada existente é sobrescrito nem apagado.

### Git ausente

`git --version` sem resposta mostra as instruções do sistema. O app não instala
nada.

- **Windows:** `winget install --id Git.Git -e --source winget`.
- **Linux:** apt, dnf ou pacman, escolhido pelo `ID`/`ID_LIKE` de
  `/etc/os-release`. Sem reconhecer a distribuição, mostra os três.
- **macOS:** `xcode-select --install` ou `brew install git`.

Cada comando tem "Copiar comando". Há também o link para git-scm.com e
"Verificar novamente". A versão mínima é 2.17, a que trouxe
`git worktree remove`.

**Armadilhas:**

- **PATH velho.** O PATH de um processo é copiado quando ele nasce. Quem
  instala o Git com o app aberto continuaria vendo "não encontrado" até
  reiniciar. `GitLocator` relê o PATH de máquina e de usuário do registro a cada
  procura, olha as pastas de instalação conhecidas e executa sempre por
  **caminho absoluto**.
- **Credenciais.** `GIT_TERMINAL_PROMPT=0` sozinho não impede o Git Credential
  Manager de esperar; `GCM_INTERACTIVE=Never` também é preciso. Sem os dois, um
  fetch que pede senha fica parado até o timeout.
- **`--no-track`.** Partindo de `origin/develop`, o `worktree add -b` faria a
  branch nova acompanhar `origin/develop`, e o primeiro `push` iria para lá. O
  teste de integração com Git real confere que o upstream fica vazio.
- **Formato dos caminhos.** O Git escreve `C:/x/y`, o Windows `C:\x\y`, e no
  Windows maiúscula não importa. Toda comparação passa por
  `WorktreePathPlanner.SamePath`.
- **Branch já existente.** No Windows as refs soltas são arquivos, então
  `Feature/X` e `feature/x` disputam o mesmo nome. E `feature` impede
  `feature/x`. Por isso a checagem ignora maiúsculas e olha o conflito
  arquivo/pasta, em vez de um `show-ref` exato. A existente é reaproveitada na
  grafia dela; só é recusada se já estiver aberta em outro worktree (o Git
  recusaria o checkout). Aberta no próprio caminho planejado, vira o conflito
  de caminho, que oferece "Usar Worktree existente".
- **`fetch . a:b`** é recusado se `b` estiver em checkout em **qualquer**
  worktree. Quem decide entre ele e `merge --ff-only` é o `worktree list`, e
  não só o worktree principal.
- **`GIT_OPTIONAL_LOCKS=0`**, para o `status` não disputar o `index.lock` com a
  IDE aberta no mesmo repositório. **`LC_ALL=C`**, para o stderr chegar em
  inglês, que é o que a tradução reconhece. O texto original vai sempre para os
  detalhes.
- **Linhas no checkout.** O teste de integração não compara fim de linha: o
  checkout segue o `core.autocrlf` de quem roda.

**Limites aceitos:**

- sem rede, não há worktree, porque o fetch é obrigatório;
- sem submódulos e sem LFS;
- o worktree é sempre pasta irmã;
- a detecção de terminal no Linux é heurística;
- remover o worktree não apaga a branch, e isso é de propósito;
- Git 2.17 ou mais novo.

O escopo futuro (abrir na IDE, status, commit, push, PR) entra como métodos
novos no `IGitClient` e colunas novas em `TaskDevelopments`. A relação 1:N,
com um ambiente por repositório, veio com o ADR-031.

---

## ADR-028 — Comandos pós-Worktree e comandos globais por `@alias`

**Decisão:** o ambiente de desenvolvimento (ADR-027) ganha uma **lista ordenada
de comandos** (`TaskDevelopmentCommands`, 1:N com `TaskDevelopments`, coluna
`Order`). Quando o worktree fica pronto, a aba roda a lista **em sequência**, com
o diretório do worktree como pasta de trabalho, mostra o output ao vivo e para no
primeiro comando que não der certo. Os seguintes ficam "não executado". Também há
uma biblioteca de **comandos globais** (`DevelopmentCommands`): um apelido único
(`@restore`) que aponta para uma linha (`dotnet restore`). Ela é administrada
numa janela própria e oferecida por autocomplete ao digitar `@`.

**Um comando por linha, e não um texto com `&&`.** Assim há status, output e
falha por etapa, e a parada na primeira falha é regra do app, não do shell.
Quebra de linha dentro de um comando é recusada no domínio.

**O alias de comando é referência, não atalho.** Aqui o contrário do ADR-026: a
tarefa guarda `@restore` como foi digitado, e a resolução acontece **na hora de
executar** (`CommandAliasResolver`, puro). Editar o global vale para todas as
tarefas. Só o primeiro termo é olhado: `@build -c Release` vira
`dotnet build -c Release`. Global não chama global, então não há recursão. Um
termo com forma de alias que não existe é **erro** da etapa, e não vai ao shell
como literal. Antes de criar o worktree, "Iniciar implementação" confere todos os
apelidos (`ValidateCommandEntries`) e recusa com a lista dos que faltam: é melhor
do que descobrir com o ambiente já criado. A regra de forma do alias
(`AliasRule`) é a mesma dos diretórios, e o autocomplete reaproveita
`AliasCompletion` e o `AliasCompletionBinder`, agora sobre a interface
`IAliasCompletionSource`. Diferenças do lado do comando: a lista só abre no
**primeiro** termo, e aceitar insere o apelido, e não o comando.

**Parâmetros.** O comando global pode ter `{nome}` (`CommandParameters`, no
domínio): `eco-sync {nomebanco} -Dev`. Quem chama preenche na própria linha,
`@eco-sync nomebanco=MeuBanco`, e o valor fica gravado com a tarefa. A lista
roda sem perguntar nada. O valor entra como foi escrito, com as aspas. O que
não é `nome=valor` de um parâmetro continua sendo acrescentado no fim. O nome
começa com letra ou sublinhado e não tem espaço, então `{ $_ }`, `${env:X}` e
`HEAD@{1}` continuam literais. Parâmetro sem valor é erro da etapa, como alias
que não existe: `ValidateCommandEntries` recusa antes de criar o worktree, e o
shell nunca recebe `{nome}`. Nada muda no banco, porque os parâmetros saem do
texto do comando. A janela de globais explica a sintaxe, mostra os parâmetros
enquanto o usuário digita e mostra o "Uso: @alias nome=…" na lista. "Testar"
tem um campo para os valores. O autocomplete insere `@alias nome=`.

**Quando roda.** Só depois de `StartDevelopment` terminar com o ambiente
`Ready`. Isso vale para a criação, para "usar Worktree existente" e para
"caminho alternativo". Falha na criação retorna antes. Rodar de novo é pelo botão
"Executar comandos", com o ambiente pronto, e grava a lista antes se ela foi
alterada. "Testar", na janela de globais, roda numa pasta escolhida. Nada roda
sem uma dessas ações explícitas. `RunDevelopmentCommands` confere o status e a
existência da pasta antes da primeira etapa.

**Execução (`ICommandExecutor` → `ShellCommandExecutor`):**

- **Windows:** `%ComSpec% /d /s /c "chcp 65001>nul & <linha>"`, com a linha crua
  em `Arguments`. É a receita de Node/libuv: `/s` tira só as aspas externas e
  preserva aspas internas, `&&` e pipes. É o cmd, e não o PowerShell 5.1, porque
  o 5.1 não conhece `&&`. O `chcp 65001` põe o console escondido do processo em
  UTF-8. Sem isso, o output chega na página OEM, com acento quebrado. `&` tem a
  menor precedência, então o exit code é o da linha do usuário.
- **Linux/macOS:** `/bin/sh -c <linha>`, com a linha como **um** argumento de
  `ArgumentList`. Nada é concatenado nem escapado pelo app.
- stdin fechado logo no início, porque um prompt interativo receberia EOF em vez
  de travar. stdout e stderr são lidos linha a linha, ao mesmo tempo, e cada
  linha vai ao `IProgress` marcada com o stream. O texto guardado tem teto de
  1 MB por stream.
- Cancelar ou estourar o timeout (padrão de 30 min) faz
  `Kill(entireProcessTree: true)` e devolve **resultado** (`Canceled` ou
  `TimedOut`), com o output de até ali. Só a falha em iniciar o shell vira
  exceção (`CommandStartException`). Exit code diferente de zero é resultado,
  não erro.
- Depois do exit, o executor espera no máximo 5 s pelo fim dos pipes: um
  processo deixado em segundo plano herda a saída e a seguraria aberta para
  sempre.

**Thread de UI.** As linhas saem da thread que lê o pipe (`ConfigureAwait(false)`
de propósito). `UiProgress<T>` posta essas linhas no contexto da UI e aplica na
hora o que já chega nele (começou/terminou). O resumo final do caso de uso é a
fonte da verdade dos status, porque uma linha atrasada só acrescenta texto à
etapa dela. O terminal (`CommandOutputView`) é uma `ListBox` virtualizada com
altura fixa e as últimas 5 000 linhas, e segue o fim a menos que o usuário
tenha subido.

**Segurança.** O output não vai para o log, porque pode ter segredo. Vão o
comando, a etapa e o exit code. Fechar a janela com comandos rodando cancela
(mata a árvore) em vez de deixar processos órfãos. O primeiro "X" cancela e
mostra o que houve, e o segundo fecha.

**Limites aceitos:**

- sem "continuar mesmo com falha" por etapa, porque a regra é fixa nesta versão;
- o resultado da execução não é gravado: sobrevive enquanto a janela está aberta;
- sem terminal interativo, já que stdin está fechado;
- sem variáveis de ambiente por comando.

---

## ADR-029 — Remover o worktree apaga a pasta, e mostra quem a segura

**Contexto:** com um terminal, a IDE ou um executável aberto dentro do
worktree, o `git worktree remove` apaga os arquivos, **esquece o worktree**
(ele some da `worktree list`) e só então sai com erro 255
(`failed to delete '…': Permission denied`), deixando as pastas vazias. O app
tratava o exit code como "o Git não removeu", e a tarefa ficava presa: a pasta
existia, mas o Git já não a reconhecia.

**Decisão:**

- Depois de uma falha do `worktree remove`, a pergunta é **"o Git ainda lista
  o worktree?"**, e não o exit code. Se lista, é recusa de verdade (worktree
  trancado, alterações) e nada muda. Se não lista, só falta a pasta.
- A pasta que sobrou é apagada por `IDirectoryRemover` (Application,
  implementado em `Infrastructure/FileSystem`). É a única porta do app que
  apaga pasta. Ela tenta algumas vezes com pausa curta (antivírus e indexador
  soltam logo), tira o somente-leitura sem entrar em junction nem symlink e
  usa `Directory.Delete` recursivo.
- Uma pasta que existe e o Git não conhece é tratada como **sobra**: a
  inspeção devolve `IsLeftover`, a confirmação diz que o Git já a esqueceu, e o
  caso de uso apaga direto. A exceção é a pasta ter virado um repositório
  próprio (`rev-parse --show-toplevel` igual ao caminho): aí não é mais o
  worktree da tarefa, e nada é apagado.
- **Quem segura a pasta** (`WindowsDirectoryLockFinder`), por duas perguntas,
  porque cada uma vê o que a outra não vê:
  - **Restart Manager** sobre os arquivos que sobraram (no máximo 1 000): acha
    executável rodando dali e DLL carregada dali, que são imagem mapeada, e não
    handle aberto;
  - **tabela de handles do sistema** (`NtQuerySystemInformation`, classe 64),
    filtrada pelo tipo "File" (o índice sai de um handle que o próprio app abre
    na pasta, porque muda entre versões do Windows). Cada handle é duplicado e,
    só se `GetFileType` disser disco, o caminho é lido com
    `GetFinalPathNameByHandle` e comparado com o da pasta. Acha o terminal cuja
    pasta de trabalho está lá dentro, que é o caso mais comum, e que o Restart
    Manager não enxerga, porque ele só aceita arquivo.
- **Forçar é um segundo clique, sobre a lista que o usuário viu.** A primeira
  tentativa nunca encerra nada: a tela mostra nome, PID e executável de cada
  processo e oferece "Encerrar processos e remover". O comando leva essa lista,
  e o removedor só encerra quem nela ainda estiver segurando a pasta, conferindo
  PID **e** nome (PID é reaproveitado). Processo que apareceu depois volta para
  a tela. Encerra só o processo, não a árvore: um filho com a pasta aberta
  aparece na lista por conta própria.
- **Nunca encerrados:** o próprio app e o `explorer`. Aparecem na lista com
  "não será encerrado"; se só eles seguram, o botão de forçar não aparece.
- A tarefa só vira `Removed` quando a pasta sumiu de fato. Cada processo
  encerrado vai para o log (`WorktreeLockerTerminated`).

**Armadilhas:**

- **Pipe síncrono trava** quem pergunta o nome dele. Por isso o caminho só é
  lido de handle de disco, e a varredura roda numa thread própria com prazo de
  10 s. Uma exceção nessa thread é capturada: solta, derrubaria o app.
- `FILETIME` dentro de `RM_UNIQUE_PROCESS` tem alinhamento 4: são dois `uint`,
  e não um `long`, senão o struct desalinha.
- Só dá para duplicar handle de processo do mesmo usuário e não elevado. Um
  processo de administrador segurando a pasta não aparece, e a tela diz que o
  Windows não informou quem é.

**Limites aceitos:**

- só Windows. Fora dele a pasta aberta não impede apagar, e o localizador
  devolve lista vazia;
- encerrar é `Kill`: o que não estiver salvo no processo se perde, e a tela
  avisa disso antes do clique.

---

## ADR-030 — Sessões de agente de IA (Claude Code) por tarefa

**Decisão:** com o worktree pronto (ADR-027), a aba Desenvolvimento ganha um
card do agente de IA. "Iniciar Claude Code" abre o `claude` num **terminal real
do sistema**, com o worktree como pasta de trabalho. O app grava uma
**sessão** (`AgentSessions`) que liga a tarefa ao processo: `Id`,
`TaskItemId`, `ProviderId`, `Command` (o executável, em caminho absoluto),
`WorkingDirectory`, `ProcessId`, `ProcessStartedAt`, `StartedAt`, `EndedAt`,
`Status` (`Starting` → `Running` → `Exited`, ou `Starting` → `Failed`) e
`FailureReason`. O card mostra PID, início e a pasta. "Abrir terminal do agente"
traz aquela janela para a frente. A linha da lista ganha um selo discreto,
"● Claude Code", enquanto a sessão está em execução. Um clique no selo faz o
mesmo que "Abrir terminal do agente", sem abrir a tarefa: é o mesmo caso de uso
(`FocusAgentSession`). Se o processo já acabou, o quadro é recarregado, e o selo
some com um aviso. O objetivo é um só: não se perder entre vários terminais
abertos.

**Só manual.** Nada abre sozinho ao fim de "Iniciar implementação": o botão
fica no card do estado Pronto. O app não captura, não lê e não escreve na
sessão. O usuário conversa com o Claude no terminal, como sempre.

**Um agente é uma porta, não o fluxo.** `IAgentCliProvider` (Application) diz
`Id`, `Name`, `Command`, como detectar (`DetectAsync`, que devolve caminho e
versão), como instalar por sistema (`InstallGuideFor`) e **o que** executar
(`CreateLaunch`). Hoje há um só, o `ClaudeCodeCliProvider` (Infrastructure).
Ele procura `claude.exe` e `claude.cmd` no PATH, relido do registro como o do
Git, e em `%USERPROFILE%\.local\bin` (instalador nativo) e `%APPDATA%\npm`. A
versão sai de `claude --version`, que responde e sai, sem sessão interativa.
Sem versão, ele continua "instalado". As instruções moram em
`WindowsInstallationGuide` e `LinuxInstallationGuide`, e em nenhum outro lugar.
Codex, Gemini e outros entram como mais um `IAgentCliProvider` registrado. O
catálogo (`IAgentCliProviders`) escolhe pelo id gravado na sessão, e nada de
"Claude" aparece no domínio nem nos casos de uso. A busca no PATH saiu do
`GitLocator` para o `ExecutableLocator`, compartilhado pelos dois.

**Por que abrir o programa direto, e não pelo `wt.exe`.** O `wt.exe` é um
lançador: entrega o pedido ao Windows Terminal e sai na hora. O PID dele morre
em milissegundos, e o shell de verdade nasce filho do `WindowsTerminal.exe`,
sem ligação que se possa seguir. O `WindowsTerminalLauncher` inicia o próprio
`claude` com `UseShellExecute = false`, `CreateNoWindow = false`, `ArgumentList`
com os parâmetros escolhidos (ADR-032) e `WorkingDirectory` = worktree. O MyTaskApp é um app gráfico, sem
console, então o Windows cria um **console novo** para o filho, hospedado pelo
**terminal padrão do usuário** (Windows Terminal, se estiver configurado como
padrão; senão o console clássico). O PID é o do próprio agente e vive exatamente
enquanto ele vive. Instalado pelo npm, o `claude.cmd` é aberto pelo Windows via
`cmd.exe`, e o PID é o desse `cmd`, que também vive enquanto o Claude vive. Por
isso o terminal **fecha junto** com o Claude (`/exit`): "Finalizado" quer dizer
o fim do Claude, e não o de um shell em volta. Sem `cmd /c <linha>`, sem
concatenação: executável e pasta precisam ser caminhos absolutos que existem, e
o launcher confere antes de iniciar.

**PID + horário de início.** O Windows reaproveita PIDs. A sessão guarda também
o `StartTime` do processo, e `IAgentProcessTracker.IsAlive(pid, início)` só
aceita o processo se o início bater (com folga de 1 s). PID igual com início
diferente é outro programa, e a sessão é dada como encerrada. Acesso negado
conta como "não é a sessão".

**Achar a janela pelo processo, nunca pelo título.** Um programa de console não
é dono da janela em que aparece. O `WindowsTerminalWindowManager` pergunta ao
próprio console: `AttachConsole(pid)` → `GetConsoleWindow()` → `FreeConsole()`,
num `lock`, porque o console é um por processo. No console clássico, essa já é a
janela visível. No Windows Terminal, é uma `PseudoConsoleWindow` cuja **dona** é
a janela do Terminal: `GetAncestor(GA_ROOTOWNER)` chega nela. Depois vêm
`ShowWindow(SW_RESTORE)`, se estiver minimizada, e `SetForegroundWindow`. Se o
Windows recusar o foco (o app não estava na frente), há um fallback com
`AttachThreadInput` à thread da janela em foco. A `Process.MainWindowHandle`
fica como último recurso. Verificado de ponta a ponta no Windows 11 com o
Windows Terminal como padrão. **Limitação:** se o Terminal juntar vários
consoles como abas de uma mesma janela, a janela vem para a frente, mas a aba
certa pode não ser selecionada.

**O banco não é a verdade; o processo é.** Toda leitura para a tela
(`GetTaskAgentSession`, `FocusAgentSession`) passa antes por
`AgentSessionReconciler.EndIfGone`: sessão ativa cujo processo não existe mais
vira `Exited` e é gravada antes de voltar. "Abrir terminal" nunca abre um agente
novo nem mira num PID reaproveitado. Uma sessão `Starting` sem PID só é dada
como perdida depois de 2 minutos, para a reconciliação não encerrar uma sessão
que outra operação está terminando de abrir.

**Monitor por evento, não por polling (`AgentSessionMonitor`).** Segue o molde
de ADR-015 e ADR-021. Singleton na Application, `IUseCaseRunner` para o escopo,
`TimeProvider.CreateTimer`, primeiro tique imediato. O tique roda
`ReconcileAgentSessions`: processo vivo volta a ser vigiado, processo sumido vira
`Exited`, e **nenhuma sessão é criada**. É assim que o app reencontra o Claude
depois de reaberto. Cada sessão viva tem um vigia `Process.Exited`
(`EnableRaisingEvents`, que funciona também para processos que o app não
iniciou), e o fim do Claude chega na hora. O timer é só rede de segurança:
`Application:AgentSessionReconcileSeconds`, padrão 60 s, com limites de 10 s a
1 h. Os casos de uso avisam o monitor por `IAgentSessionWatcher` (o mesmo
objeto). O `SessionsChanged(taskId)` chega de qualquer thread, e a `App` posta na
thread de UI, recarrega a lista e atualiza o card da janela aberta daquela
tarefa. A janela é avisada pela `App`, e não assinando o monitor: um singleton
segurando o evento de um ViewModel transitório o manteria vivo depois de a
janela fechar.

**Fechar o app não encerra o Claude.** Descartar o monitor só solta os vigias.
O processo continua no terminal, e a próxima abertura o reencontra pelo PID e
pelo início.

**Uma sessão ativa por tarefa** (por ambiente desde o ADR-031, com o índice
`IX_AgentSessions_TaskDevelopmentId_Active`). O caso de uso recusa a segunda ("use Abrir
terminal do agente"). Se a anterior já morreu, ela é encerrada e gravada
**antes** da nova ser inserida. Um índice único parcial
(`IX_AgentSessions_TaskItemId_Active`, `"Status" IN (1, 2)`) impede o banco de
aceitar duas. A sessão é gravada `Starting` **antes** de o terminal abrir: se o
app cair no meio, sobra uma sessão sem PID que a reconciliação encerra, e não
um terminal órfão sem registro. Falha ao abrir vira sessão `Failed`, com o
motivo, e nunca `Running`. Agente que sai na hora vira `Exited`. Agente não
instalado é recusado **sem** criar sessão, e o card troca o botão pelas
instruções. Remover o worktree com o agente aberto é recusado
(`RemoveWorktreeHandler`), porque o processo está com a pasta aberta.

**Camadas.**

- Domínio: `AgentSession` é um aggregate próprio, e não filho de `TaskItem`.
  Quem muda o estado é um processo de fora, e carregar a tarefa inteira para
  trocar um status seria desperdício. A FK para `Tasks` tem cascade: excluir a
  tarefa de vez leva o histórico.
- Application: portas `IAgentCliProvider`, `ITerminalLauncher`,
  `ITerminalWindowManager`, `IAgentProcessTracker` e `IAgentSessionRepository`,
  mais os casos de uso e o monitor.
- Infrastructure: o provider, o tracker (.NET puro e multiplataforma) e, só no
  Windows, `WindowsTerminalLauncher` e `WindowsTerminalWindowManager`
  (`[SupportedOSPlatform]`, `LibraryImport`). Em outros sistemas, o registro
  escolhe `UnsupportedTerminalLauncher` e `UnsupportedTerminalWindowManager`,
  que respondem "ainda não suportado". O Linux entra trocando essas duas
  classes.

**Limites aceitos:**

- só o Claude Code, e sem escolher agente pela UI;
- sem terminal embutido e sem ler o que se passa na sessão (o ADR-037
  acompanha a atividade pelos hooks, sem ler o terminal);
- sem "Parar" (não há status `Stopped`): quem encerra é o usuário, no terminal;
- a aba do Windows Terminal pode não ser a selecionada (ver acima);
- Linux e macOS: abstrações prontas, sem implementação de terminal.

---

## ADR-031 — Vários ambientes por tarefa: um worktree por repositório

**Contexto:** o fluxo completo de uma tarefa costuma atravessar mais de um
repositório (o app e a API, o front e o back). Até aqui a tarefa tinha no máximo
um ambiente (`TaskItem.Development`, 1:0..1, ADR-027) e uma sessão de agente
ativa (ADR-030). Para implementar o resto, era preciso criar outra tarefa só
para ter outro worktree.

**Decisão:** a tarefa passa a ter **N ambientes**, um por repositório
(`TaskItem.Developments`). Cada um é o `TaskDevelopment` de sempre: tem worktree,
branch, comandos pós-Worktree e o **seu próprio agente**.

**1:N, e a regra do repositório no domínio.** Em `TaskDevelopments`, o índice
em `TaskItemId` deixou de ser único. A regra "um ambiente por repositório" mora
em `TaskItem.EnsureDevelopmentCanBegin`, e não num índice. No Windows,
`C:\x\Repo` e `c:/x/repo/` são a mesma pasta, e o banco compararia texto. A
regra também evita um erro certo do Git: dois worktrees da mesma tarefa no
mesmo repositório disputariam a mesma branch. A preparação confere a regra
logo depois de validar o repositório (é ali que se sabe qual é) e antes do
fetch, que demora. A recusa aparece como falha dessa etapa.

**Criar, tentar de novo, esquecer.**

- `BeginDevelopment(developmentId, …)` **com id** tenta de novo aquele
  ambiente. O repositório pode mudar, se a pasta estava errada, desde que não
  seja o de outro ambiente.
- **Sem id**, o ambiente que já existe no mesmo repositório (Erro, Removido ou
  Criando) é reaproveitado. Se ele estiver Pronto, é recusa. Se não houver
  nenhum, entra um novo na lista.
- `ForgetDevelopment` tira da lista um ambiente sem worktree (Erro ou
  Removido). A linha e os comandos dele são apagados. A branch fica no
  repositório.
- Como a remoção (ADR-029), esquecer não passa pela guarda da lista principal:
  um checklist arquivado ainda arruma a sua lista.

**Todo caso de uso diz qual ambiente.** `PrepareDevelopment` recebe um
`DevelopmentId` opcional. Os seguintes recebem um obrigatório:
`SetDevelopmentCommands`, `RunDevelopmentCommands`, `InspectWorktree`,
`RemoveWorktree`, `StartAgentSession` e `GetTaskAgentSession`.
`GetTaskDevelopment` virou `GetTaskDevelopments`, que devolve a lista em ordem
de criação (id v7). `TaskDevelopmentView` ganhou `Id` e `TaskId`.

**Um agente por ambiente.** `AgentSession` ganhou `TaskDevelopmentId`, com FK
`SET NULL`: tirar o ambiente da lista não apaga o histórico. Desde este ADR, o
índice parcial único é `IX_AgentSessions_TaskDevelopmentId_Active`. Antes era
por tarefa. Com isso, dois repositórios da mesma tarefa podem ter o Claude
aberto ao mesmo tempo. A remoção do worktree só é segurada pelo agente
**daquele** ambiente. `FocusAgentSession` aceita o ambiente. Sem ele, usa a
sessão ativa mais recente da tarefa.

**Migração.** A migration `MultipleDevelopments` troca os índices, cria a
coluna e liga as sessões antigas ao único ambiente que a tarefa tinha. Esse
`UPDATE` é a última instrução do `Up()`, porque vem depois de o SQLite
reconstruir a tabela para a FK. O `Down()` só funciona enquanto nenhuma tarefa
tiver dois ambientes.

**Tela.**

- A aba Desenvolvimento ganha uma faixa de "abas" de repositório
  (`TaskDevelopmentsViewModel`). Cada aba mostra ícone de estado, nome da pasta
  e branch, e uma bolinha quando o agente daquele ambiente está aberto. Ao lado
  fica "+ Adicionar repositório".
- O painel de antes, formulário, pronto e agente, continua igual e mostra o
  ambiente escolhido. Cada aba é um `TaskDevelopmentViewModel` inteiro.
- A faixa só aparece com algum ambiente gravado: o primeiro repositório é só o
  formulário, como antes.
- O repositório novo é um rascunho. Ele já vem com a **branch e a origem dos
  outros ambientes**: o fluxo que atravessa repositórios costuma usar a mesma
  branch em todos. A pasta fica vazia.
- Se a criação falha e fica gravada, o rascunho vira aquele ambiente, com a
  falha na tela, em vez de ganhar uma aba gêmea.
- Fechar a janela confere todos os ambientes, e não só o da frente: uma
  criação ou comandos em andamento em qualquer um seguram o fechamento.

**Selo da lista.** A linha mostra "● Claude Code ×2" quando há mais de um. O
clique com um agente só foca direto. Com vários, abre um menu "Abrir
terminal: repositório · branch", um item por ambiente.

**Limites aceitos:**

- sem reordenar as abas: a ordem é a de criação;
- um rascunho de repositório por vez;
- a regra do repositório compara o worktree principal, então duas pastas do
  mesmo repositório (dois worktrees dele) contam como um só.

---

## ADR-032 — Correção ortográfica: o corretor do sistema, desenhado por cima da caixa

**Contexto:** as anotações, os títulos e a captura rápida são texto livre em
português, e a `TextBox` da Avalonia não tem corretor ortográfico. O pedido era
o sublinhado vermelho e as sugestões no clique direito, como em qualquer editor.

**Decisão:** usar o **corretor do próprio sistema** atrás de uma porta
(`ISpellChecker`, em `Desktop/SpellChecking`) e ligá-lo por uma attached
property: `spell:SpellCheck.IsEnabled="True"`.

```
TextBox ─ SpellCheck.IsEnabled ─► SpellCheckBinder ─► SpellCheckSession ─► ISpellChecker
                                  (adorner, menu)     (tokens, cache)      ├─ WindowsSpellChecker (COM)
                                                                           └─ NoSpellChecker
```

**Por que o do sistema, e não um dicionário embarcado.** O Windows já traz o
dicionário pt-BR, as sugestões e o dicionário do usuário, que é compartilhado
com os outros apps. Um Hunspell embarcado traria vários megabytes de dicionário
para manter atualizados.

**A API.** Windows Spell Checking API (`spellcheck.h`, Windows 8+), com COM
gerado em compilação (`[GeneratedComInterface]`), o mesmo caminho do
`[LibraryImport]` do resto do app. A vtable é declarada à mão, e os métodos que
não usamos ficam como marcadores (`...Slot`) só para segurar a posição. Um
teste de integração com o COM real existe porque um método fora de ordem
compila e chama outro método.

**Português e inglês.** Uma palavra só está errada se estiver errada em todos
os idiomas carregados: pt-BR e, quando existir, en-US. O Windows só tem o
dicionário de um idioma instalado, e numa máquina só em português o inglês não
está. Por isso existe `DeveloperTerms`, uma lista curta do jargão que as
anotações usam como português (branch, merge, deploy, commit…). Ela vale em
qualquer máquina.

**O que não é prosa não é verificado** (`SpellingTokenizer`, função pura):

- código Markdown (entre crases e em bloco ```` ``` ````);
- URL, e-mail, `@alias`, `#tag` e path;
- palavra grudada em dígito ou em `_` (`v2`, `snake_case`); o `_` na ponta é
  itálico e não conta;
- sigla, camelCase e palavra de uma letra.

Sublinhado em cada path ensinaria o usuário a não olhar para o sublinhado.

**O desenho é um adorner.** A `TextBox` não tem decoração por trecho de texto,
e trocar o template das caixas para enfiar uma camada seria caro de manter. O
`SpellingSquiggles` fica na `AdornerLayer`, sobre o `TextPresenter`: acompanha
a rolagem, é recortado pela área visível e usa as coordenadas do `TextLayout`,
as mesmas do cursor (`AliasCompletionBinder`, ADR-026).

**Quando verifica.**

- A cada tecla roda só o cache, para os sublinhados andarem junto com o texto.
- Depois de 400 ms parado, pergunta ao sistema as palavras novas.
- A palavra sob o cursor não é marcada enquanto se digita. Ela é julgada no
  espaço, quando o cursor sai dela ou quando a caixa perde o foco.
- O COM só é chamado da thread de UI.

**Menu.** O clique direito numa palavra errada abre, em vez do flyout padrão,
as sugestões, "Adicionar ao dicionário" (o dicionário do usuário do Windows,
persiste), "Ignorar" (até fechar o app), Recortar, Copiar e Colar. A troca
passa pela seleção (`SelectedText`), então Ctrl+Z desfaz e o binding é avisado.
Adicionar ou ignorar avisa todas as caixas abertas (`DictionaryChanged`).

**Onde está ligado.** Anotação, título da tarefa, captura rápida e as
descrições de comando global e de diretório. Paths, aliases, comandos e buscas
ficam de fora.

**Limites aceitos:**

- Linux e macOS usam o `NoSpellChecker`. O próximo passo é o
  WeCantSpell.Hunspell com `/usr/share/hunspell/pt_BR.*`, sem mexer na UI;
- sem dicionário pt-BR nem en-US no Windows, o corretor fica desligado (log
  `SpellCheckerUnavailable`);
- "Ignorar" não sobrevive a reiniciar o app.

---

## ADR-033 — Parâmetros do agente: editáveis no card, lembrados por agente

**Decisão:** o card do agente (ADR-030) ganha o campo "Parâmetros", logo acima
de "Iniciar Claude Code". Ele vem preenchido com o padrão e mostra embaixo o que
vai rodar ("Roda: claude --dangerously-skip-permissions"). O texto usado ao
iniciar vira o novo padrão, para todas as tarefas.

**Padrão de fábrica: `--dangerously-skip-permissions`.** O worktree é uma pasta
descartável e isolada, feita para o agente trabalhar sem parar a cada arquivo.
Cada `IAgentCliProvider` diz o seu em `DefaultArguments`, e só o
`ClaudeCodeCliProvider` sabe dessa flag.

**Guardado por agente, no banco.** A tabela `AgentSettings` (`ProviderId` →
`Arguments`), atrás do `IAgentSettingsStore`, segue o desenho das outras
configurações (ADR-014). Sem linha, vale o padrão do agente. Texto **vazio**
gravado é escolha do usuário (abrir o `claude` puro), e não "sem configuração".

**Texto → lista, sem shell.** `AgentArguments.Parse` separa por espaço, e as
aspas duplas juntam um argumento com espaço. A lista vai para o
`AgentCliStartContext` e daí para o `ArgumentList` do processo. `&&` ou `|`
chegam ao Claude como texto e nunca viram outro comando. Aspas sem fechar são
recusadas antes de gravar a sessão ou o padrão.

**Junto com o texto livre.** Os parâmetros vêm antes do texto da tela e do
`--permission-mode plan` (ADR-030): `claude --dangerously-skip-permissions --permission-mode plan "texto"`.

**Quem salva é o "Iniciar".** Não há botão "Salvar" próprio.
`StartAgentSession.Arguments` informado vira o padrão no mesmo `SaveChanges`
da sessão; `null` usa o salvo. A tela só manda `null` antes de a detecção
trazer o padrão, para um campo ainda vazio não apagar o que foi salvo. A
detecção periódica só repõe o campo se o usuário não o editou.

---

## ADR-034 — Bolinha de worktree na lista, e a pergunta ao concluir

**Contexto:** na lista de hoje, só dava para saber que uma tarefa tinha
worktree abrindo a tarefa. E concluir a tarefa deixava a pasta no disco sem
ninguém perceber. Muitas vezes ela ainda tinha alteração não commitada ou
commit que nunca foi enviado.

**Decisão:** a linha ganha uma **bolinha à esquerda do título** para cada
tarefa com worktree pronto. A cor diz o estado no Git:

| Estado | Cor | Token |
|---|---|---|
| Alteração não commitada | âmbar | `WidgetCaution` |
| Commit sem push | azul | `WidgetInfo` |
| Commits, todos enviados | verde | `WidgetSuccess` |
| Sem commits ainda | cinza | `WidgetTextMid` |
| Não verificado / pasta sumiu / Git recusou | anel vazio | `WidgetTextLow` |

Com vários repositórios (ADR-031), a bolinha mostra o pior estado. A cor nunca
é o único sinal: o balão do título ganha um bloco "WORKTREE" com uma linha por
repositório ("3 alterações não commitadas · 2 commits sem push"), e a bolinha
tem balão próprio.

**O estado vem do Git, nunca do banco.** O banco só diz quais worktrees
existem: uma consulta a mais em `TodayQuery`, feita de uma vez, nunca uma por
linha. A cor vem de `ProbeWorktreesHandler`, que faz até quatro conferências
em paralelo. Cada conferência é:

- `git status --porcelain=v2 --branch` (`IGitClient.GetBranchStatusAsync`):
  as alterações e a distância até o upstream, numa ida só;
- `rev-list --count` contra a origem da branch: quantos commits ela tem;
- sem upstream, uma comparação com `refs/remotes/origin/{branch}`, porque
  `git push origin x` sem `-u` também é enviar.

A conferência nunca lança. Um repositório com problema vira anel vazio e vai
para o log, não para a faixa de erro.

**Sem piscar.** O quadro carrega sem esperar o Git, e a conferência roda
depois, sem `await`. O `TodayViewModel` guarda a última resposta, e as linhas
novas do refresh de 60 s já nascem com ela. Um contador descarta a resposta de
uma conferência que outra mais nova já superou.

**Concluir pergunta.** Concluir uma tarefa com worktree conclui primeiro,
porque concluir não pode depender do Git. Depois vem a pergunta "Remover o
worktree desta tarefa?", com os botões "Remover" e "Manter worktree". Antes de
perguntar, o app confere de novo, e a pergunta diz o que existe agora:

- alteração não commitada impede a remoção, e isso é avisado antes;
- commit sem push não impede, mas só existe nesta máquina, e isso também é
  avisado.

"Remover" chama o `RemoveWorktreeHandler` de sempre (ADR-029), com as mesmas
travas. O que for recusado aparece na faixa de erro, e a tarefa continua
concluída.

**"Manter" não grava nada.** "Concluída com worktree pronto" já é a resposta. A
linha concluída fica cinza, mas a bolinha não: ela cresce, ganha um halo da
mesma cor e vem com o texto "● Worktree criado · repositório · branch" embaixo
do título. Um clique nesse texto abre a tarefa, onde o worktree se remove.

**Limites aceitos:**

- o remoto comparado sem upstream é sempre `origin`;
- a cor tem até 60 s de atraso para mudanças feitas fora do app.

---

## ADR-035 — Branch de origem padrão no diretório da etiqueta

**Decisão:** o diretório da etiqueta (ADR-026) ganha um campo opcional, "Branch
de origem padrão" (`TagDirectory.DefaultBranch`). Quando o repositório de
"Iniciar implementação" (ADR-027) é essa pasta, a branch já vem escolhida no
combo "Branch de origem".

**Só uma preferência, sem perguntar ao Git ao salvar.** O domínio só confere o
que dá para saber sem o repositório: sem espaços e com no máximo 255
caracteres. Se a branch existe, isso só se sabe na hora de usar. Se o
repositório não tem a branch, a escolha cai na sugestão de sempre
(`ListBranchesHandler.Suggest`), e um aviso embaixo do combo diz que a branch
padrão não foi encontrada.

**Como o nome vira branch** (`ListBranchesHandler.FindConfigured`): primeiro o
nome curto exato, e assim `develop` acha a local e `origin/develop` acha a
remota. Depois o mesmo nome sem diferenciar maiúsculas. Por último, sem local,
`develop` acha a remota, de `origin` primeiro.

**Qual diretório vale.** A tela já recebe os diretórios das etiquetas da tarefa
para o autocomplete do `@alias`. Vale o diretório com o mesmo caminho digitado
ou o do worktree principal. Com a mesma pasta em duas etiquetas, vale a
primeira que tiver uma branch padrão.

**Ordem da escolha:** a branch que já estava escolhida (recarga da mesma
pasta), depois a origem da tentativa anterior deste ambiente, depois a branch
padrão do diretório, depois a origem dos outros ambientes da tarefa (ADR-031),
depois a sugestão. A origem dos outros ambientes fica abaixo da branch padrão
porque veio de outro repositório. A padrão foi cadastrada para este.

---

## ADR-036 — "Abrir Claude Code" no menu da linha

**Decisão:** o menu "⋯" da linha da tela Hoje ganha "Abrir Claude Code", que
abre o agente (ADR-030) num ambiente da tarefa sem abrir a tarefa. É o mesmo
caso de uso do card da aba Desenvolvimento (`StartAgentSession`), com os
parâmetros salvos.

**Só com ambiente pronto.** O item só aparece quando a tarefa tem worktree
`Ready`, os mesmos que o quadro já traz para a bolinha (ADR-034). Sem
ambiente não há onde abrir, e um item que só serve para dar erro não vale o
espaço no menu.

**Mais de um ambiente, a view pergunta qual.** Com um só, abre direto. Com
mais de um, abre um segundo menu com um item por ambiente
(`repositório · branch`), no molde do selo de vários agentes (ADR-031). Por
isso o item é um clique de code-behind, e não um comando: perguntar é
trabalho da view.

**Ambiente com agente já aberto traz o terminal.** São um agente por
ambiente, e o caso de uso recusaria o segundo. Quem clica quer o agente
daquele ambiente, então o item, marcado "(aberto)", faz o mesmo que o selo:
traz o terminal para a frente.

---

## ADR-037 — Acompanhar o Claude Code pelos hooks dele

**Contexto:** o ADR-030 liga a tarefa ao **processo** do Claude: o app sabe se
ele está aberto, mas não o que ele está fazendo. Quem deixa o Claude
trabalhando e vai fazer outra coisa só descobre que ele parou numa pergunta, ou
que terminou, voltando ao terminal. O pedido: avisar quando o Claude precisar
do usuário ou terminar e estiver aguardando revisão.

**Decisão:** o app acompanha a sessão pelos **hooks oficiais** do Claude Code.
Cada evento que interessa vira um POST para uma porta local do MyTaskApp, e a
sessão ganha uma **atividade**: `Working`, `WaitingForUser`, `WaitingReview` ou
`Failed`. Quando ela passa a esperar o usuário, aparece um aviso no canto da
tela e o selo da linha muda.

```
MyTaskApp ── abre ──► claude --settings <hooks do app> …   (env: MYTASKAPP_*)
                         │ hook http (evento em JSON)
                         ▼
          127.0.0.1:47831/api/claude/events  (AgentEventListener)
                         │ fila, na ordem de chegada
                         ▼
          RecordAgentEventHandler ─► AgentSession.Activity ─► selo, card, aviso
```

**Nunca pelo texto do terminal.** O estado sai só de campos estruturados do
hook (`hook_event_name`, `notification_type`, `tool_name`). "Terminou" ou um
"?" no fim da resposta não decidem nada. O texto (a pergunta, a última
resposta) vai junto só para o aviso mostrar. A tradução mora num lugar só,
`ClaudeCodeHookEvents`, e daí para dentro ninguém conhece nome de evento do
Claude.

| Hook do Claude | Evento (`AgentEventType`) | Atividade |
|---|---|---|
| `UserPromptSubmit`, `PostToolUse` | `Working` | Trabalhando |
| `PreToolUse` de `AskUserQuestion` / `ExitPlanMode` | `NeedsUserInput` | Aguardando você |
| `Notification` `permission_prompt`, `elicitation_*`, `agent_needs_input` | `NeedsUserInput` | Aguardando você |
| `Stop` | `ResponseCompleted` | Aguardando revisão |
| `StopFailure` | `SessionFailed` | Erro na resposta |
| `Notification` (os outros tipos) | `Notification` | não muda |
| `TaskCompleted` | `TaskCompleted` | não muda |
| `SessionEnd` | `SessionStopped` | não muda |

**`Stop` não é o fim da sessão.** É o fim de uma resposta: o Claude continua
aberto esperando a próxima mensagem. Por isso vira "aguardando revisão", e não
"concluída". O fim da sessão continua sendo o fim do **processo** (ADR-030). O
`SessionEnd` não encerra nada: o `/clear` também manda um, e o Claude continua
aberto. `SubagentStop` fica de fora: um subagente terminar não termina a
resposta, e o `Stop` do agente principal vem depois. `SessionStart` também: o
Claude não dispara hook HTTP nele. No teste com o Claude real, o hook de
comando disparou e o HTTP não. O `session_id` vem em todo evento, então nada
se perde. O `SessionEnd` HTTP chega quando cabe no orçamento de 1,5 s do
encerramento.

**Os estados pedidos, e onde cada um mora.** Não há um enum novo de status.
São duas dimensões que já existiam separadas: o processo (`AgentSessionStatus`,
ADR-030) e a atividade (`AgentActivity`, este ADR).

| Estado | Aqui |
|---|---|
| NotStarted | sem sessão (card "Idle") |
| Starting | `Status = Starting` |
| Running | `Running` + `Working` (ou `Unknown`, sem aviso ainda) |
| WaitingForUser | `Running` + `WaitingForUser` |
| WaitingReview | `Running` + `WaitingReview` |
| Completed / Stopped | `Exited` — o processo acabou (ADR-030 continua sem "Parar") |
| Failed | `Failed` (o terminal não abriu) ou atividade `Failed` (a resposta falhou) |

**Hooks por execução, e não na configuração do usuário.** O app não edita o
`~/.claude/settings.json`. Os hooks moram num arquivo do próprio app,
`%APPDATA%\MyTaskApp\agents\claude-code-hooks.json`, passado com
`claude --settings <arquivo>` só aos Claude que o app abre. O Claude **soma**
os hooks de todas as fontes (usuário, projeto, local e `--settings`), então os
do usuário continuam rodando junto. Isso foi conferido com o Claude real: um
hook de projeto e os hooks do app dispararam na mesma resposta. Assim:

- nenhum arquivo do usuário é escrito, e não há merge para errar nem hook dele
  para sobrescrever;
- não há o que desinstalar: desligar o acompanhamento é não passar o arquivo;
- um Claude aberto à mão, fora do app, nem fica sabendo que o MyTaskApp existe.
  Um hook global dispararia em toda sessão do usuário, e mandaria prompts de
  sessões alheias para uma porta local;
- o arquivo tem **só** a chave `hooks`: o `--settings` sobrepõe chave a chave,
  e qualquer outra chave passaria por cima da escolha do usuário.

O `--settings` vem antes dos parâmetros do usuário. Se ele passar o próprio
`--settings`, o dele vale: perde-se o acompanhamento, e não a configuração dele.

**A configuração do usuário é lida, nunca escrita.** Antes de abrir,
`ClaudeCodeHooks.UnavailableReason` confere, na ordem de precedência do Claude
(gerenciada, usuário, projeto, local), o que faria os hooks não rodarem:
`disableAllHooks`, `allowManagedHooksOnly` na política gerenciada e
`allowedHttpHookUrls` sem a URL do app. Nesses casos o Claude abre do mesmo
jeito, sem acompanhamento, e o card diz o motivo.

**Hook HTTP, direto para a porta local.** Não há processo intermediário. A
URL fica fixa no arquivo. O que identifica a sessão vem nos cabeçalhos, a
partir do ambiente do processo (`allowedEnvVars`). O Claude só interpola
variáveis nos cabeçalhos, não na URL. O arquivo é um só para todas as sessões
e não guarda segredo nenhum.

**A associação é explícita, e conferida.** O app põe no ambiente do processo
`MYTASKAPP_TASK_ID`, `MYTASKAPP_DEVELOPMENT_ID` (o ambiente/repositório,
ADR-031), `MYTASKAPP_AGENT_SESSION_ID`, `MYTASKAPP_WORKTREE_PATH`,
`MYTASKAPP_BRANCH`, `MYTASKAPP_EVENTS_URL` e `MYTASKAPP_HOOK_TOKEN`. O segredo
é aleatório (32 bytes) e **por sessão**. O banco guarda só o SHA-256
(`AgentSessions.HookTokenHash`), comparado em tempo constante. Aviso sem o
segredo daquela sessão, de sessão desconhecida, de sessão sem acompanhamento
ou com tarefa que não bate é recusado. Qualquer processo do computador alcança
a porta, e é o segredo que separa o Claude que o app abriu de quem só sabe o
endereço. A pasta (`cwd`) não decide nada; fora do worktree, só vai para o
log.

**A porta (`AgentEventListener`).**

- `TcpListener` em `IPAddress.Loopback`, com HTTP mínimo: POST com
  `Content-Length` ou em pedaços, uma requisição por conexão, corpo até 1 MB.
  O `HttpListener` passaria pelo http.sys, que pede reserva de URL. O Kestrel
  traria o ASP.NET inteiro para um app de bandeja.
- Pedido com `Origin` (navegador) ou com `Host` que não seja
  `127.0.0.1`/`localhost` na porta dele (DNS rebinding) é recusado antes de
  ler o corpo.
- **Responde antes de processar.** O hook HTTP é síncrono, e o Claude espera a
  resposta. O corpo é validado e enfileirado, e a resposta `{}` ("sem
  decisão") sai na hora. Uma fila de um consumidor só aplica os avisos na
  ordem de chegada: "pergunta" e "voltou a trabalhar" não trocam de lugar.
- **Porta fixa** (`Application:AgentEventsPort`, 47831). Um Claude aberto
  guarda a URL até sair, e reabrir o app na mesma porta faz os que ficaram
  abertos voltarem a ser ouvidos. Se a porta estiver ocupada, vale qualquer
  livre para as sessões novas.
- Sem porta, o agente abre sem acompanhamento. Acompanhar é um extra e nunca
  impede de abrir.

**Avisar só na mudança, e não a quem já está olhando.** `RecordActivity`
devolve `true` só quando a atividade **muda**: o mesmo aviso repetido não
avisa duas vezes, e só a mudança é gravada (um `PostToolUse` por ferramenta
não vira uma escrita no banco por ferramenta). Se o terminal do agente é a
janela em primeiro plano (`ITerminalWindowManager.IsInForegroundAsync`, pela
mesma janela que o foco usa), o usuário está conversando com ele. Aí o estado
muda, mas nenhum aviso aparece por cima da conversa. Voltou a trabalhar ou o
processo saiu: o aviso sai da tela.

**O terminal pisca na barra de tarefas.** Junto com o aviso, a janela do
terminal do agente pisca (`FlashWindowEx` com `FLASHW_ALL | FLASHW_TIMERNOFG`,
via `ITerminalWindowManager.FlashAsync`, achada pela mesma janela do foco). O
aviso do canto pode ser dispensado ou nem caber na pilha; o botão laranja na
barra continua apontando onde está a pendência. Quem para de piscar é o
próprio Windows, quando o terminal vem para a frente, sem o app vigiar o foco.
Se o agente voltar a trabalhar antes, `StopFlashingAsync` (`FLASHW_STOP`)
apaga. Com várias abas na mesma janela do Windows Terminal, pisca a janela, não
a aba.

**O aviso é o do lembrete, com outra cara.** O `AlertPresenter` também
implementa `IAgentAttentionPresenter`. É a mesma pilha no mesmo canto, na tela
do painel, porque dois apresentadores empilhariam janelas uma sobre a outra.
A `AgentAlertWindow` nunca rouba o foco. Mostra a tarefa, repositório ·
branch, a pergunta ou o começo da resposta, e os botões "Abrir terminal"
(`FocusAgentSession`, o mesmo do selo) e "Dispensar". Há um aviso por sessão,
atualizado no lugar.

**Tela.**

- O selo da linha vira "⚠ Claude Code · aguardando você", "✓ Claude Code ·
  revisar" ou "⚠ Claude Code · erro", na cor de atenção. Com vários agentes,
  mostra o mais urgente (pergunta > erro > revisão > trabalhando), e o balão
  e o menu dizem o de cada repositório.
- Com pendência, a linha ganha uma moldura âmbar que respira (opacidade, 1,4 s)
  até o usuário clicar no selo; depois fica parada e fraca enquanto a
  pendência durar. "Já vista" é a chave (tarefa, ambiente, atividade,
  `ActivityChangedAt`), guardada em memória no `TodayViewModel`: o refresh não
  reacende a moldura, e uma pergunta nova do mesmo agente — outro horário —
  volta a pulsar. Reabrir o app volta a pulsar tudo, de propósito.
- O status do card diz a atividade, com "Às 18:55 · <pergunta>" embaixo e se a
  sessão avisa o app.
- A caixa "Avisar quando o agente precisar de mim ou terminar" fica junto dos
  parâmetros e segue o ADR-033: nasce ligada, e o valor usado no "Iniciar"
  vira o padrão (`AgentSettings.MonitorActivity`). A coluna `Arguments` ficou
  anulável: a linha pode existir só pelo acompanhamento, e os parâmetros
  continuam "nunca salvos".

**Persistência.** A migration `AgentActivity` acrescenta a `AgentSessions`
`HookTokenHash`, `ExternalSessionId` (o `session_id` do Claude, o último,
porque muda com `/clear`), `Activity`, `ActivityMessage` e
`ActivityChangedAt`, e a `AgentSettings`, `MonitorActivity`. Não há tabela de
eventos. O estado fica na sessão, e cada evento vai para o log
(`AgentEventReceived`, `AgentActivityChanged`, `AgentEventRejected`).

**Limites aceitos:**

- com o app fechado, os avisos se perdem (conexão recusada, o hook falha sem
  travar o Claude). Ao reabrir, a atividade mostrada é a última recebida, com
  o horário;
- sessões abertas antes desta versão, ou com o acompanhamento desligado, não
  avisam;
- com vários consoles como abas de uma janela do Windows Terminal, "está
  olhando" vale para a janela, e não para a aba (a limitação do ADR-030);
- as variáveis de ambiente, segredo incluído, são herdadas pelos comandos que
  o Claude roda. É a mesma fronteira de confiança: eles rodam como o usuário;
- conferido com o Claude Code real (`claude -p`): cabeçalhos interpolados,
  `Content-Length`, `Host` do loopback, `UserPromptSubmit`, `PostToolUse`,
  `Stop` com `last_assistant_message`, `SessionEnd` e os hooks do usuário
  rodando junto. `PreToolUse` de `AskUserQuestion`/`ExitPlanMode` e
  `Notification` só acontecem no modo interativo. Eles seguem o formato
  documentado e têm teste de tradução, mas ainda não foram vistos chegando de
  um Claude real.

---

## ADR-038 — Anotação: Markdown do VS Code e pré-visualização enquanto escreve

**Decisão:** o leitor da anotação (ADR-024) passa a desenhar o que o preview do
VS Code desenha, e a janela ganha três modos enquanto a tarefa está em aberto:
**Escrever**, **Visualizar** e **Lado a lado**. `Ctrl+Shift+V` alterna entre
escrever e visualizar, o mesmo atalho do VS Code. Concluída, a anotação continua
sendo só leitura, sem os botões de modo.

**Por que o leitor antigo "não formatava".** `MarkdownDocument` era um
subconjunto: `#`, `##`, listas de um nível, `**`, `*`, `__` e `~~`. O resto
aparecia cru. `###` virava parágrafo com os `#` à mostra. Crase, bloco de
código, citação, link, tabela, `- [ ]` e `---` também apareciam como texto. O
recuo de uma sublista era jogado fora, e ela se juntava à lista de cima. O
parser agora reconhece:

- títulos de 1 a 6, também sublinhados com `===` e `---`;
- listas aninhadas pelo recuo, com numeração por nível que começa no número
  do primeiro item, como no CommonMark;
- tarefas `- [ ]` / `- [x]`, citação (lida de novo como documento, então cabe
  lista e código dentro), bloco de código com ```` ``` ```` ou `~~~`, linha
  horizontal e tabela do GitHub com alinhamento;
- código entre crases, link `[texto](url)`, imagem (vira link para ela),
  `<url>` e endereço solto, `_itálico_` e escape com `\`.

A ênfase agora segue a regra de flanco: `2 * 3 * 4` é conta, não itálico, e
`nome_de_variavel` não vira itálico. Isso resolveu o limite do ADR-024: a ênfase
encostada no marcador de fora (`**muito *mesmo***`) agora sai certa.

**Três diferenças do VS Code, de propósito:**

- `__` continua sendo **sublinhado**, porque é o que a barra escreve desde o
  ADR-024. Trocar para negrito mudaria o desenho das anotações que já existem.
- a quebra de linha simples continua sendo quebra. No CommonMark ela vira
  espaço, mas as anotações foram escritas contando com ela;
- HTML embutido aparece como texto.

**Um leitor só.** A pré-visualização e a tarefa concluída usam o mesmo
`MarkdownView`. Dois leitores desenhariam a mesma anotação de dois jeitos. Editor
e leitor ficam numa `UniformGrid` de uma linha: o que está escondido não ocupa
coluna. Assim cada modo usa a largura inteira, e "lado a lado" divide ao meio.
Nesse modo a coluna da janela alarga de 920 para 1680px, só enquanto a aba da
anotação está à vista. Os botões de formatar somem em "Visualizar": não há
seleção para formatar.

**Link abre com clique simples**, como no preview. Ninguém mais sabe onde cada
`Run` caiu, então o clique acha o link pela posição no `TextLayout`
(`HitTestPoint`). As posições são contadas à mão, e o `LineBreak` conta como
`Environment.NewLine`. Um teste confere isso contra o layout de verdade. Só
abrem `http`, `https` e `mailto`: um `file:` numa anotação não deve virar
execução de programa.

**Cores de apoio vêm do texto.** O fundo do código, as bordas da tabela e a
linha horizontal são o `Foreground` com pouca opacidade. Assim servem em
qualquer fundo. Link e barra da citação chegam por propriedade
(`LinkForeground`, `AccentBrush`), com os recursos do tema.

**Limites aceitos:** o leitor não sincroniza a rolagem com o editor no modo
lado a lado. Imagem não é carregada. Link por referência (`[x][ref]`) e nota de
rodapé aparecem como texto. Bloco de código recuado com quatro espaços não é
código: esse recuo já significa sublista, e é o mais comum numa anotação.

---

## ADR-039 — `@` no texto do agente: os ambientes da tarefa e os arquivos deles

**Contexto:** com vários ambientes por tarefa (ADR-031), o texto para o agente
(ADR-033) costuma citar outro repositório da mesma tarefa: "olhe o projeto
`C:\Projetos\MyTaskApp-feature-…`". Escrever o caminho do worktree, e mais
ainda o de um arquivo dentro dele, é lento e dá erro.

**Decisão:** na caixa "Texto para o Claude Code", digitar `@` abre uma lista
com os **ambientes prontos da mesma tarefa**, cada um com uma chave curta, o
nome da pasta do repositório (`@MyTaskApp`, `@eco-api`). Enter insere o
caminho absoluto do worktree. Tab (ou digitar `/`) entra nele: `@MyTaskApp/`
lista a raiz, e o que vem depois da barra procura dentro. Numa pasta, Tab entra
de novo e Enter insere a pasta. Num arquivo, os dois inserem o caminho do
arquivo. Com duas letras ou mais, `@texto` sem ambiente procura nos arquivos de
todos os ambientes, e cada item mostra de qual é.

**Só a mesma tarefa.** A lista vem dos ambientes gravados na tarefa, e o caso de
uso `ListEnvironmentFiles(TaskId, DevelopmentId)` tira o ambiente **da
tarefa** (`GetDevelopment`). Um id de outra tarefa é recusado como ambiente
inexistente. Ambiente sem worktree (Erro, Removido, Criando) não entra: não há
pasta para citar.

**O texto guarda o caminho, e não a chave.** É a regra do ADR-026: quem lê o
texto é o agente, e ele entende caminho, não apelido do app. Remover o worktree
depois não reescreve textos antigos.

**A lista de arquivos vem do Git**: `git ls-files --cached --others
--exclude-standard -z`. Entram os arquivos commitados e os novos, e fica de
fora o que o `.gitignore` esconde (`bin`, `obj`, `node_modules`). Varrer a pasta
traria dezenas de milhares de arquivos gerados. As pastas saem dos caminhos dos
arquivos, porque o Git não lista pasta. O teto é de 50 000 arquivos
(`ListEnvironmentFilesHandler.MaxFiles`), e a lista avisa quando corta.

**Quando busca.** Só quando um `@` abre, e de novo a cada `@` novo, em segundo
plano: o agente cria arquivos enquanto o usuário escreve. A lista velha vale até
a nova chegar. Sem lista ainda, o popup espera aberto com "Carregando os
arquivos…". Falhar só vai para o log e fecha a lista.

**Busca por trecho, no estilo do Ctrl+P** (`Notes/PathReference`, puro e
testado sem janela). As letras precisam aparecer na ordem, não juntas:
`tdvm` acha `TaskDevelopmentViewModel.cs`. O que casa só no nome do arquivo
ganha do que precisa da pasta, e começar o nome com o digitado ganha mais. O
nome exato vem primeiro. Começo de palavra (depois de `/ . - _`, ou a maiúscula
do camelCase) e letras seguidas somam, e cada salto desconta. O casamento é o
**melhor**, e não o primeiro: com o guloso, `tdvm` pegava o `v` de
"De**v**elopment" e perdia para `TodayViewModel`. É programação dinâmica em
duas linhas, O(texto × busca), com as linhas na pilha. Antes dela vem a
conferência barata de que as letras existem na ordem. Medido num caso
sintético de 50 000 arquivos em pastas aleatórias (200 000 entradas): 20 a 55 ms
por tecla. Um repositório de verdade tem muito menos pastas.

**Pasta conhecida delimita.** Em `@MyTaskApp/src/Views/tod`, se `src/Views`
existe, a busca é só dentro dela. Se não existe, a busca vale para o caminho
inteiro, e `views/tod` também acha `src/…/Views/TodayView.axaml`.

**O mesmo binder, com dois ganchos.** `IAliasCompletionSource` ganhou dois
membros com implementação padrão. `ContinuationFor` diz o que o Tab põe no
lugar do `@texto` sem fechar a lista (entrar na pasta), e `null` mantém o Tab
como Enter. `IsTokenChar` diz até onde vai o token, que aqui inclui a barra.
As fontes de antes (diretórios das etiquetas, comandos) não mudaram.
`AliasCompletion.FindToken` e `Accept` ganharam uma sobrecarga com o predicado.

**Uma instância para a aba.** `ReferenceCompletionViewModel` mora em
`TaskDevelopmentsViewModel`, como `DirectoryCompletion`, e chega ao card do
agente por `AgentSessionViewModel.References`. A caixa é a do ambiente da
frente, e ele aparece marcado "este ambiente". A lista é a mesma para todas as
abas.

**Limites aceitos:**

- nome de arquivo com espaço não é achado depois de um espaço, porque o
  espaço encerra o token. A busca por trecho quase sempre acha sem ele;
- o caminho inserido não leva aspas. O `claude.cmd` do npm recusa `"` no
  texto (ADR-030), e o agente lê o caminho do jeito que está;
- ambientes de **outras** tarefas não aparecem. É a regra pedida, e não um
  esquecimento.

---

## ADR-040 — Modelo e esforço do agente, escolhidos no card

**Contexto:** o Claude Code aceita `--model` e `--effort`, mas escolhê-los
exigia lembrar a flag e digitá-la no campo "Parâmetros" (ADR-033) antes de
cada "Iniciar".

**Decisão:** o card do agente ganha duas listas, **Modelo** e **Esforço**,
entre o texto para o agente e os parâmetros. A primeira opção de cada uma é
"Padrão", que não acrescenta nada e deixa o agente decidir. A prévia "Roda: …"
já mostra o que foi escolhido.

**O provider diz as opções.** `IAgentCliProvider.Models` e `Efforts` devolvem
`AgentCliOption(Value, Label, Arguments)`. O valor é o que se grava, o nome é o
que a tela mostra, e os argumentos são o que entra no comando. Só o
`ClaudeCodeCliProvider` sabe que é `--model opus` ou `--effort high`. Os dois
membros têm implementação padrão vazia, e um agente sem essa escolha não mostra
as listas.

**Modelos por apelido** (`fable`, `opus`, `sonnet`, `haiku`), e não pelo nome
completo. O apelido aponta para a versão mais nova da família, então a lista
não envelhece a cada modelo novo. Os esforços são os níveis do `--effort`:
`low`, `medium`, `high`, `xhigh`, `max` (Baixo … Máximo).

**Lembrado como os parâmetros.** `AgentSettings` ganhou `Model` e `Effort`, e
`StartAgentSession.Model`/`Effort` seguem a regra do ADR-033. Informado, vira
o padrão no mesmo `SaveChanges` da sessão. `null` usa o salvo, e vazio é
"Padrão". Por isso o "Abrir Claude Code" do menu da linha (ADR-036) já abre com
a última escolha. Um valor salvo que o agente deixou de oferecer volta a ser
"Padrão", em vez de travar a abertura.

**Conferido antes de gravar.** Valor fora da lista do agente é recusado
(`DomainException`). Modelo e esforço são conferidos antes dos parâmetros e
gravados depois deles, então nada recusado deixa os outros salvos.

**Ordem no comando:** os parâmetros do campo, depois modelo e esforço, depois
o `--permission-mode plan` e o texto (ADR-030):
`claude --dangerously-skip-permissions --model opus --effort high "texto"`.
Com `--model` também no campo, vale o da lista, que vem por último. Em
"Padrão", vale o do campo.

**Limites aceitos:**

- um `claude` antigo que não conheça `--effort` recusa a flag e fecha. O card
  mostra "encerrou logo ao abrir", e "Padrão" volta a abrir;
- nome completo de modelo (`claude-opus-5-5`) continua indo pelo campo
  "Parâmetros". A lista cobre o caso comum.

---

## ADR-041 — Temas: sete paletas, "Automático" segue o Windows

**Contexto:** o ADR-017 fixou o app no escuro (`RequestedThemeVariant="Dark"`)
porque seguir o Windows, naquela época, dava uma chapa preta de 900x700. O
problema era o tamanho e o preto puro, não a ideia de seguir o sistema. Agora o
painel é um widget com paleta própria, e dá para oferecer o claro sem voltar
ao problema antigo.

**Decisão:** sete temas, e mais "Automático", que é o padrão:

| Id              | Nome            | Tipo   | Destaque              |
|-----------------|-----------------|--------|-----------------------|
| `paper`         | Papel           | claro  | índigo                |
| `sepia`         | Sépia           | claro  | petróleo              |
| `charcoal`      | Carvão          | escuro | índigo (o de sempre)  |
| `nordic`        | Nórdico         | escuro | gelo                  |
| `plum`          | Ameixa          | escuro | magenta               |
| `ponta`         | Ponta           | escuro | verde-limão           |
| `high-contrast` | Alto contraste  | escuro | amarelo               |

"Automático" (`system`) usa Papel com o Windows claro, Carvão com o Windows
escuro e Alto contraste quando um tema de contraste do Windows está ligado
(`PlatformColorValues.ContrastPreference`). A troca no sistema vale na hora,
por `IPlatformSettings.ColorValuesChanged`. Um tema escolhido à mão ganha de
tudo, inclusive do alto contraste do sistema: quem escolheu já viu como fica.

A escolha fica em `widget.json` (`WidgetState.Theme`), como texto e não como
enum, para um tema novo não exigir migração. Um id desconhecido, de tema
removido ou de arquivo editado à mão, volta a ser `system`. Quem atualiza tem
um arquivo sem a chave e passa a seguir o Windows. Com o Windows escuro, isso
dá o mesmo Carvão de antes.

**A paleta é C#, e não um `.axaml` por tema.** `Theming/ThemeCatalog.cs` tem
as cores. `ThemeResources.Build` monta um `ResourceDictionary` com os pares
`Widget{Nome}Color`/`Widget{Nome}Brush` e as chaves do Fluent que o app
reaponta. `ThemeController` troca esse dicionário dentro de
`Application.Resources.MergedDictionaries`, no mesmo lugar, e ajusta
`RequestedThemeVariant` para o Fluent pintar o que a paleta não cobre (barra de
título das janelas auxiliares, rolagem, seletor de cor). Em C#, o teste de
contraste lê a mesma paleta que a tela desenha, sem subir janela.
`Tokens.axaml` ficou só com o que não muda entre temas: raios, fonte de
ícones e as transparências de propósito.

**Toda cor de tema é `DynamicResource`.** Um `StaticResource` resolve uma vez,
na carga, e prende a tela ao tema que estava ativo quando ela abriu.
`ThemeResourcesTests` varre os `.axaml` e falha se uma tela congela uma chave
`Widget*`, ou se pede uma chave que algum tema não define. Os hex que ainda
estavam soltos (sombra do painel e dos alertas, fundo dos botões do modo
discreto, texto do botão de perigo, o visto branco da marca e do Markdown)
viraram tokens: `WidgetShellShadow`, `WidgetPopupShadow`,
`WidgetGhostChromeBrush`, `WidgetOnAccentBrush`.

**Contraste medido, e não a olho** (`ThemeContrastTests`, WCAG 2.2):

- texto principal: 7:1 em fundo, cartão e hover (AAA, 1.4.6);
- texto de apoio, inclusive o nível mais baixo, onde fica a data de 10px:
  4,5:1 (AA, 1.4.3);
- sinalização (perigo, atenção, informação, sucesso): 4,5:1, porque vira
  texto ("atrasada", o link da anotação);
- destaque como forma (ícone ligado, barra, checkbox): 3:1 (1.4.11);
- letra sobre o botão de destaque, nos três estados: 4,5:1;
- no Alto contraste, as bordas também passam de 3:1.

O Carvão não passava. A letra branca sobre `#6366F1` dava 4,47:1, e o
`TextLow` `#6B7180` dava 3,6:1. Mudaram para `#5F63EF`, que visualmente é a
mesma cor, e `#838996`. O hover do destaque no escuro agora fecha em vez de
abrir, como no Fluent, porque abrir tirava a letra branca do mínimo.

**`AccentText` separado de `Accent`.** Num tema escuro com letra branca no
botão, a mesma cor não chega a 4,5:1 contra o fundo e contra o branco ao mesmo
tempo. Pela fórmula, a luminância precisaria ser ≥ 0,233 e ≤ 0,183 ao mesmo
tempo. Então o destaque usado como **texto** (`WidgetAccentTextBrush`: rótulos de
código, "ok", o chip do agente) é outra cor, e o destaque como preenchimento
continua sendo `WidgetAccentBrush`.

**Semântica fixa por matiz.** Vermelho é perigo, âmbar é atenção, azul é
informação e verde é sucesso, em todos os temas. Por isso nenhum destaque usa
essas matizes: Sépia é petróleo, Ameixa é magenta (rosa ficaria perto do
perigo), Nórdico é gelo. No Alto contraste o destaque é amarelo, e a atenção
virou laranja.

**Ponta, a exceção de marca.** O Ponta veio depois, na identidade verde da
Ponta, e é o único tema com destaque na matiz do sucesso. A paleta da marca vai
para os papéis de sempre, sem chave nova: petróleo `#104544` no fundo, `#154E44`
no cartão, `#1E5B45` no hover, `#2D6F44` na borda, e o limão `#7CBB3B` só na
ação (`#A2D152` no hover e como texto, `#5EA341` pressionado). Para o verde não
virar duas coisas, o sucesso é menta (`#6EE7B7`), a mais de 40° de matiz do
limão (`ThemeCatalogTests`). Dois tons saíram da paleta por contraste: a letra
do botão é `#0A2F2E`, porque `#104544` sobre o pressionado dava 3,5:1, e o
`AccentSoft` é `#24573A`, porque o limão como texto sobre o teal `#1E5B45` dava
4,47:1.

**Armadilhas do Fluent encontradas renderizando, e não nos testes:** várias
chaves do Fluent apontam para outras por `StaticResource` dentro do próprio
dicionário do tema. Reapontar `TextOnAccentFillColorPrimaryBrush` não chega ao
visto da checkbox (`CheckBoxCheckGlyphForeground*`), ao texto do botão
`accent` (`AccentButtonForeground*`) nem ao `ToggleButton` ligado. No Nórdico o
"Abrir" saía branco sobre gelo, e no Alto contraste o visto saía branco sobre
amarelo. Essas chaves são reapontadas uma a uma. O `Button` comum usa
`ButtonBackground`, que é branco ou preto translúcido, e não
`ControlFillColorDefault`. Ele passou a usar o `TextHigh` do tema translúcido:
no Sépia era cinza frio sobre creme, e no Carvão 20% de quase-branco é o que
já era.

A seleção de texto (`TextBox`, `SelectableTextBlock` das anotações) é pintada
com o destaque, mas o Fluent deixa a letra na cor do texto. Com destaque claro
(Nórdico, Ameixa, Alto contraste, Ponta), o selecionado ficava branco sobre
claro, perto de 2:1. Agora a letra selecionada é `WidgetOnAccentBrush`
(`Styles/Widget.axaml`). Nos temas de letra branca no botão, nada muda.

**A escolha no menu.** "Tema ▸" no menu do painel, com "Automático" primeiro.
Cada item tem uma amostra nas cores do tema (fundo, cartão e um ponto de
destaque; o "Automático" é meio Papel, meio Carvão) e a descrição no balão. É
rádio de verdade (`ToggleType="Radio"`), então o leitor de tela anuncia qual
está marcado. O item tem `StaysOpenOnClick`: dá para passar pelos temas e ver
cada um aplicado no painel sem reabrir o menu.

**Limites aceitos:**

- a cor das etiquetas é do usuário e não muda com o tema. Uma etiqueta amarela
  clara num tema claro tem pouco contraste como bolinha. O texto sobre ela
  continua certo (`TagColor.PrefersDarkText`);
- o destaque não segue a cor de destaque do Windows. Cada tema foi validado com
  o próprio destaque, e uma cor arbitrária do sistema quebraria os mínimos
  acima.

## ADR-042 — Um som por estado do Claude Code, com sons do app e do usuário

**Contexto:** o ADR-037 avisa na tela e pisca o terminal quando o Claude para
esperando o usuário, termina a resposta ou falha. Mas o aviso é mudo, e quem
está em outra janela (ou longe da tela) não percebe. O pedido: tocar um som por
estado, ligar e desligar cada um, escolher o som, com alguns sons que já vêm no
app e a possibilidade de subir os próprios.

**Decisão:** cada estado que avisa ganha um som próprio. A escolha é feita em
"Sons do Claude Code…", no menu do painel.

| Estado (`AgentActivity`) | Na tela | Som de fábrica |
|---|---|---|
| `WaitingForUser` | Aguardando você | Chamada |
| `WaitingReview` | Aguardando revisão | Concluído |
| `Failed` | Erro na resposta | Alerta |

- **Os estados são os do aviso** (`AgentSession.NeedsAttention`). "Voltou a
  trabalhar" não toca: não interrompe ninguém.
- **Toca onde o aviso aparece, e só ali.** `RecordAgentEventHandler` toca junto
  com o aviso do canto, depois de piscar o terminal. As regras do ADR-037 valem
  para o som: só na mudança de estado, e nada com o terminal do agente em
  primeiro plano, porque o usuário já está olhando.
- **Nasce ligado, com um som diferente por estado.** Dá para saber, sem olhar,
  se o Claude perguntou algo ou só terminou.
- **Tudo vale na hora.** A janela não tem "Salvar": marcar, escolher, adicionar
  e excluir já gravam. "Restaurar padrão" volta os três estados ao de fábrica.

**Os sons do app são sintetizados, e não arquivos.** `SoundSynthesizer` monta
cada som a partir de uma receita de notas: parciais com ataque de 5 ms e
decaimento exponencial, normalizados e gravados como WAV PCM 16 bits mono a
44,1 kHz. São sete: Chamada, Concluído, Alerta, Sino, Dois toques, Suave e
Bolha. Assim não há licença de áudio de terceiros nem asset para esquecer no
instalador. O mesmo id sempre gera os mesmos bytes. O WAV vai para uma pasta
descartável (`%TEMP%\MyTaskApp\sounds`) na primeira vez que toca, e só é
reescrito se a receita mudar. O player toca arquivo, então os sons do app e os
do usuário seguem o mesmo caminho.

**Os sons do usuário são copiados.** "Adicionar som…" copia o arquivo para
`%APPDATA%\MyTaskApp\sounds` (ou `MYTASKAPP_DATA_DIR\sounds`). Não guarda uma
referência para onde ele estava: mover ou apagar o original não emudece o
aviso, e a pasta sobrevive à atualização como o banco (ADR-018). A pasta é o
catálogo, sem tabela: o id é `custom:<arquivo>`, e o nome mostrado é o nome
do arquivo sem extensão. Nome repetido ganha " (2)", mesmo com outra extensão.

- **Aceita WAV e MP3, até 10 MB.** A assinatura é conferida, e não só a
  extensão (`RIFF…WAVE`, `ID3` ou um quadro MPEG). Um arquivo renomeado seria
  aceito e ficaria mudo justo na hora do aviso.
- **O id vem do banco e é conferido.** Um `custom:..\..\algo` não sai da pasta.
- **Excluir** pede confirmação. Os estados que usavam o som voltam ao de
  fábrica no banco, e não só na tela.
- **Sumiu por fora do app?** O aviso toca o som de fábrica daquele estado, em
  vez de ficar mudo, e a tela mostra o de fábrica.

**Tocar: MCI do Windows, numa thread STA só dele.** O `MessageBeep` dos
lembretes não toca arquivo. O `PlaySound` só toca WAV. Um pacote de áudio seria
dependência nova para um recurso só do Windows. O MCI (`winmm.dll`,
`mciSendStringW`, dispositivo `mpegvideo`) toca WAV e MP3 com o que o Windows
já tem. Conferido na máquina:

- numa thread **MTA** o `open` falha com o erro 266 (o `mpegvideo` é DirectShow
  e quer STA). Por isso o `WindowsAudioPlayer` tem uma thread STA própria, de
  fundo, criada no primeiro som. Abrir, tocar e fechar moram nela, porque o
  dispositivo MCI é da thread que o abriu;
- **um som por vez:** um aviso novo interrompe o que ainda estiver tocando;
- quando o som acaba, a thread fecha o dispositivo e solta o arquivo. Ela
  confere o estado a cada 200 ms;
- como o `ISoundPlayer`, **nunca lança**. Uma falha do MCI vai para o log
  (`AlertSoundFailed`), e o aviso na tela continua.

**Persistência.** A migration `AgentAlertSounds` cria a tabela de mesmo nome,
com uma linha por estado: `Activity` (o número do enum, chave), `IsEnabled` e
`SoundId`. Sem linha, vale o de fábrica (ADR-014). Os sons são globais, e não
por agente. Hoje só há o Claude Code, e um segundo agente pode ganhar a coluna
`ProviderId` quando existir.

**Limites aceitos:**

- sem volume próprio: vale o volume do Windows para o app. Um MP3 alto entra
  alto;
- um som longo toca inteiro, a menos que outro aviso o interrompa;
- fora do Windows, o `IAudioPlayer` é o objeto nulo, e nada toca;
- os lembretes continuam com o `MessageBeep` da escada (ADR-004). Este ADR é só
  dos avisos do agente.

---

## ADR-043 — Tag opcional: o worktree parte de uma versão

**Contexto:** "Iniciar implementação" (ADR-027) sempre parte da ponta da branch
de origem. Para depurar um cliente que está numa versão antiga, o usuário
precisava do código daquela versão, e não do que está na `main` hoje.

**Decisão:** abaixo de "Branch de origem", um combo **"Tag (opcional)"**. Ele
começa em "Nenhuma — a ponta da branch", e o fluxo é o de sempre. Com uma tag
escolhida, a branch nova nasce nela:
`git worktree add --no-track -b {nova} {caminho} refs/tags/{tag}`.

- **Sempre uma branch nova, nunca HEAD destacado.** O resto do app conta com
  uma branch no worktree: a conferência depois de criar, a bolinha da lista
  (ADR-034), o agente. E quem depura costuma querer commitar o conserto. Um
  nome como `hotfix/1.4.2-cliente-x` fica a cargo do usuário, no campo de
  sempre.
- **A tag manda no ponto de partida; a branch continua sendo a origem.**
  `SourceBranch` grava a branch escolhida, como antes. A tag vai para uma
  coluna nova, `TaskDevelopments.SourceTag` (anulável, migration
  `TaskDevelopmentSourceTag`). O cartão "Ambiente pronto" mostra "Tag de
  origem" só quando há uma.
- **Com tag, a origem não é atualizada.** O fast-forward da branch de origem
  serve para partir da ponta mais nova, e aqui ela não entra no worktree.
  Atualizá-la só arriscaria recusar por nada (divergiu, alterações locais). A
  etapa aparece como "pulada", com o nome da tag.
- **A tag é conferida depois do fetch**, na etapa da origem, e só pelo nome
  exato. Uma tag que não existe para ali, sem gravar nada.
- **Sempre `refs/tags/{tag}`, nunca o nome curto.** Uma branch `v1.0.0` ao
  lado da tag `v1.0.0` deixaria o nome curto ambíguo. Pelo mesmo motivo, a
  listagem lê `%(refname)` e não `%(refname:short)`, que vira `tags/v1.0.0`
  nesse caso.
- **Branch que já existe vence a tag.** O fluxo reaproveita a branch existente
  (ADR-027), e ela tem o código dela. O aviso da etapa diz "…e não a tag X", e
  `SourceTag` não é gravada, para o registro não dizer uma origem que não é.
- **A bolinha conta a partir da tag.** A consulta da tela Hoje entrega
  `refs/tags/{tag}` como base da comparação. Contar contra a `main` mostraria
  como "commits" tudo o que a versão não tem.

**Listagem.** `IGitClient.ListTagsAsync` roda
`git for-each-ref --sort=-v:refname --format=%(refname) refs/tags`: a versão
mais nova primeiro, em ordem de versão (`v1.10.0` antes de `v1.9.0`). Quem
procura a versão de um cliente procura pelo número. As tags chegam junto com as
branches, em `ListBranches`.

**Qual tag já vem escolhida.** Numa recarga da mesma pasta, a que estava. Num
"tentar de novo", a da tentativa anterior. Fora isso, nenhuma. Nem a branch
padrão do diretório (ADR-035) nem os outros ambientes da tarefa (ADR-031)
sugerem tag: tag é exceção, e cada repositório tem as suas versões.

**Armadilha: o fetch não ganhou `--tags`.** Com `--tags`, o fetch passa a
buscar `refs/tags/*` explicitamente. Uma tag que alguém moveu no remoto é
recusada ("would clobber existing tag"), e o fetch sai com erro. Como o fetch é
obrigatório, isso travaria todo "Iniciar implementação" daquele repositório,
com ou sem tag. O fetch de sempre já traz as tags que apontam para commits das
branches buscadas (o tag following do Git).

**Limites aceitos:**

- uma tag que só existe no remoto e aponta para um commit fora de qualquer
  branch remota não é trazida pelo fetch, então não aparece no combo;
- uma tag movida no remoto não é atualizada localmente; o Git não reescreve
  tags sem `--force`, e a porta não tem `--force` (ADR-027);
- o combo lista todas as tags, sem filtro por branch.

## ADR-044 — Versionamento: o Release Please sobe a versão, a tag não se move

**Contexto:** a versão já tinha fonte única (ADR-018), mas alguém precisava
lembrar de editar o `VersionPrefix`, criar a tag na mão e escrever as notas.
Não havia changelog, checksum nem um jeito de dizer, de dentro do app, que
código estava instalado. Com clientes em versões diferentes, "qual versão você
tem?" precisa levar a um commit.

**Decisão:** **Release Please** (`googleapis/release-please-action@v4`,
`release-type: simple`, manifest). Ele lê os Conventional Commits desde a
última release e mantém aberto um PR `chore(master): release X.Y.Z` com o
`CHANGELOG.md` e o número novo. Mesclar o PR cria a tag `vX.Y.Z` e a release em
rascunho, e o mesmo workflow chama o `release.yml`, que gera os instaladores
**a partir da tag**. Processo de uso em `docs/release-process.md`.

- **Por que o Release Please, e não MinVer ou git-cliff.** O que importa é o
  commit da tag já conter o próprio changelog e a própria versão. Com git-cliff
  o changelog viraria um commit depois da tag. Com MinVer a versão sairia da
  tag no build, mas o Release Please já guarda o número no manifest, e seriam
  duas fontes. Nenhum pacote NuGet nem Node entra no repositório: é uma action.
- **A versão continua em `VersionPrefix`**, agora editada pelo bot (anotação
  `x-release-please-version` na linha). Os scripts de empacotamento não
  mudaram: continuam lendo `-getProperty:Version`. O manifest repete o número
  por exigência da ferramenta, e `ReleaseProcessTests` quebra a build se os
  dois divergirem.
- **Squash merge, título do PR no padrão.** O título vira o único commit no
  master, então só ele precisa estar no padrão, e o `pr-title.yml` reprova o
  que não estiver. Exigir isso de todo commit de todo PR seria atrito diário
  para nada.
- **Rascunho com a tag já criada** (`draft` + `force-tag-creation`). O
  rascunho preserva o teste de fumaça manual antes de alguém baixar. Sem
  `force-tag-creation`, o GitHub só cria a tag ao publicar, e a release
  seguinte não acharia a anterior. Isso dá um changelog com o histórico
  inteiro.
- **`release.yml` não cria nada, só anexa.** Ele confere a tag no formato
  `vX.Y.Z` e se `Version` é igual à tag; exige rascunho **sem artefatos**;
  sobe com `gh release upload` sem `--clobber`. Release publicada não se
  refaz: um cliente pode já ter aquele arquivo. Sai o gatilho de push de tag.
  Tag criada com `GITHUB_TOKEN` não dispararia nada, e com um PAT dispararia em
  dobro. Quem chama o build é o `release-please.yml`.
- **Nomes e hashes.** `MyTaskApp-{versão}-{rid}-setup.exe` e
  `MyTaskApp-{versão}-{rid}.tar.gz` (o Windows deixou de ser
  `MyTaskAppSetup-{versão}.exe`), mais um `SHA256SUMS`. As notas ganham o SHA
  do commit e os hashes.
- **Hotfix sem as features do master:** branch de manutenção `release/vX.Y.x`
  a partir da tag. O Release Please roda nele também (`target-branch` é o
  branch do push) e propõe o PATCH só com o que entrou ali. O ADR-043 cria o
  worktree direto na tag.

**Versões do assembly.** `AssemblyVersion` e `FileVersion` ficam como o SDK
deriva (`M.m.p.0`): o exe não tem consumidor externo nem strong name, então
não há binding para quebrar. `InformationalVersion` é o que se mostra: o SDK já
cola o SHA (`1.5.0+<sha>`), via Source Link. O csproj do Desktop acrescenta um
`AssemblyMetadata("BuildDate")`.

**Onde a versão aparece.** `Composition/AppVersion` separa versão, commit e
data. A última linha do menu ☰ mostra `MyTaskApp 1.5.0 · a82f91c ·
2026-10-02`, e o clique copia os três com o SHA inteiro. A linha
`ApplicationStarted` do log leva os mesmos três. Não há janela "Sobre": seria
uma tela inteira para uma linha. O balão da bandeja ficou como estava, porque
ele já é o contador de pendências (ADR-016).

**Primeira versão.** Nunca houve tag nem release. O manifest começa em
`1.0.0`, a versão das builds anteriores, com `bootstrap-sha` no master de
quando o processo entrou, para não varrer um histórico que não segue o padrão.
A primeira release da pipeline é a **1.1.0**.

**Limites aceitos:**

- sem `RELEASE_PLEASE_TOKEN`, o PR de release nasce sem CI (eventos do
  `GITHUB_TOKEN` não disparam workflows). O `release.yml` roda os testes na tag
  antes de anexar qualquer coisa, e o PR só mexe em versão e changelog;
- se o build do rascunho falhar por código, aquela tag fica sem release, e o
  conserto é a próxima versão;
- a data da build é a do dia em que se compilou, não a do commit. Duas builds
  locais do mesmo commit em dias diferentes mostram datas diferentes, e o SHA
  é o que identifica o código;
- assinatura digital continua só preparada (`docs/release-process.md`, seção
  10): sem certificado não dá para verificar, e um passo que nunca rodou daria
  a impressão de existir.

---

## ADR-045 — Jira: a issue vira contexto da tarefa, sem virar um clone do Jira

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

### O retrato, e por que ele basta

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

### Autocomplete: dentro da captura, e Enter continua sendo "capturar"

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

### Branch: convenção do tipo, e nunca duplicada

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

### Conexão: um clique, e o segredo longe de disco legível

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

### Tela

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

## ADR-046 — Fonte de ícones embutida: o cabeçalho também tem ícones no Linux

**Contexto:** instalado no Linux, o cabeçalho do painel aparecia sem o
alfinete, sem o "recolher" e sem o "⋯" do menu — e, na lista, sem os ícones de
etiqueta, anotação e lembrete. Os botões estavam lá e respondiam ao clique; o
que faltava era o desenho. Os glifos são caracteres da área de uso privado da
`Segoe Fluent Icons`/`Segoe MDL2 Assets` (`U+E712` é o "⋯", por exemplo), e
essas fontes só existem no Windows. Sem a fonte, o caractere não tem quem o
desenhe e o botão fica vazio. Os testes não pegavam porque rodam no Windows.

**Decisão:** o app embute `Assets/Fonts/MyTaskAppIcons.ttf`, uma fonte pequena
(10 glifos, menos de 3 KB) que responde pelos **mesmos códigos** da Segoe, com
os contornos do Fluent UI System Icons (MIT, Microsoft). Ela entra no fim do
`WidgetIconFont`:

```
Segoe Fluent Icons, Segoe MDL2 Assets, avares://MyTaskApp/Assets/Fonts#MyTaskApp Icons
```

No Avalonia 12 a lista vira uma família composta, e tanto a escolha da fonte
quanto o fallback por caractere percorrem as entradas na ordem. No Windows a
Segoe responde primeiro e nada muda na tela; no Linux (e no macOS) quem desenha
é a embutida. Nenhuma tela, view model ou teste de glifo precisou mudar.

**Por que não trocar tudo por `PathIcon`:** seria o caminho "sem fonte", mas
mexe em toda tela que usa ícone, troca as propriedades `…Glyph` de texto por
geometria e muda a aparência no Windows, que é onde o app vive. A fonte de
reserva resolve o Linux sem tocar no Windows. A Segoe não pode ir junto: a
licença dela não permite redistribuir.

**A fonte é gerada, não desenhada:** `scripts/generate-icon-font.py`
(`pip install fonttools`) baixa os SVGs de 20px num commit fixo do repositório
de origem e monta a TTF. As métricas copiam as da Segoe (em de 2048,
ascendente 2048, descendente 0, avanço de 1 em) para o glifo ocupar a mesma
caixa nos dois sistemas. Rodar duas vezes gera o mesmo binário.

**A guarda:** `IconFontTests` varre `src/MyTaskApp.Desktop` atrás de todo
código da área de uso privado — literal, `` ou `&#xE712;` — e exige um
glifo para cada um na fonte embutida, lida pelo `avares://` de verdade. Ícone
novo sem passar pelo script quebra o teste no Windows, em vez de sumir só no
Linux.

**Limites aceitos:**

- fora do Windows o traço é o do Fluent UI System Icons, parecido mas não
  idêntico ao da Segoe;
- o alfinete solto (`U+E718`) e o fixado (`U+E840`) usam o contorno vazado e o
  cheio; na Segoe Fluent os dois são o mesmo desenho e quem diferencia é a cor.

## ADR-047 — PR aberta da branch, pelo GitHub CLI

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

## ADR-048 — HUD: o pino deixa de ser "transparente" e vira "visível sem atrapalhar"

**Contexto:** o pino do ADR-017 ligava `Topmost` e o modo discreto num clique
só. O modo discreto apagava o fundo do painel (`Background="Transparent"`) e o
ADR registrava o custo: o retângulo continuava capturando cliques. Na prática o
painel ficava difícil de enxergar, a área "vazia" bloqueava o app de trás, e
sair do modo exigia achar um alfinete flutuante ou a bandeja. Um booleano
(`IsPinned`) controlava três coisas por baixo.

**Decisão:** dois conceitos de janela, uma máquina de estados explícita e
preferências independentes.

- `WindowMode { Normal, Hud, HudCollapsed }` é o único estado que decide a
  forma da janela. Só os comandos da moldura o movem: `EnterHud`, `ExitHud`,
  `ToggleHud`, `CollapseHud`, `ExpandHud`. Recolher e expandir não valem fora do
  HUD; da pílula também se sai direto para a janela normal.
- **Separado disso**, cada um com o seu teste: "sempre no topo" da janela
  normal (`Topmost`), "manter o HUD sempre visível" (`Hud.AlwaysOnTop`), a
  opacidade do fundo do HUD (`Hud.Opacity`) e a região clicável (que não é
  preferência: é a forma da janela). `IsWindowTopmost` escolhe entre os dois
  "no topo" conforme o modo. Sair do HUD nunca deixa a janela grande presa na
  frente de tudo.
- O modo discreto **saiu**. O alfinete do cabeçalho virou "Fixar como HUD"; no
  HUD ele aparece aceso, e o clique volta à janela normal. "Sempre no topo"
  continua no menu, e continua sem tocar em geometria (o que o ADR-017 garante
  segue garantido por `WidgetPinTests`).

**A área transparente não bloqueia o mouse porque não existe.** No Windows,
pixel transparente não deixa o clique passar: quem decide é o retângulo da
janela (`WM_NCHITTEST`). Por isso o HUD não é "o painel com fundo apagado", e
sim uma janela do tamanho exato do cartão: sem o anel de 10px da sombra
(`Border.widgetShell.hud` zera margem e sombra), largura fixa por preset e
altura do conteúdo (`SizeToContent.Height` com `MaxHeight` do preset). Um HUD
com uma tarefa não ocupa a altura de dez, e cada pixel que ele não ocupa é
clique que chega no app de trás. Isso vale em qualquer plataforma, sem código
nativo.

No Windows, `IWindowBehaviorService.SetInteractiveRegion` fecha o que sobra:
`SetWindowRgn` com o retângulo arredondado do cartão, para os cantos também
repassarem o clique. Conferido numa janela real (não no headless):
`WindowFromPoint` no centro do HUD devolve a janela do app; no canto arredondado
e fora do cartão, a janela de trás. Ao voltar para a janela normal a região é
removida. Duas alternativas descartadas:

- `WS_EX_LAYERED | WS_EX_TRANSPARENT` torna a janela **inteira** transparente
  ao mouse. Alterná-lo pela posição do cursor exigiria ler o ponteiro global
  num timer — exatamente o "capturar o mouse" que o HUD não pode fazer.
- `HTTRANSPARENT` no `WM_NCHITTEST` (o `Win32Properties.NonClientHitTestResult`
  do Avalonia 12) só repassa o clique para janelas da **mesma thread**, então
  não serve para o VS Code, o terminal ou o navegador.

**Legibilidade antes de discrição.** O fundo do HUD é uma camada própria
(`Border.hudBackdrop`) com o pincel opaco do tema e a opacidade escolhida. O
texto fica em outra camada e nunca fica translúcido. O piso é 70%
(`HudSettings.MinOpacity`), e `Sanitized()` o impõe também a um `widget.json`
editado à mão. A borda do cartão fica (1px), para ele se separar de qualquer
fundo. No HUD a lista mostra só o que falta (`HideCompleted`, herdado do pino
antigo), e as quatro ações discretas da linha, que reservavam ~100px mesmo
invisíveis, saem do layout: anotação e menu voltam no hover; etiqueta e
lembrete ficam para a janela normal.

**Posição: canto da tela onde a janela está, na escala dessa tela.**
`HudPlacement.Resolve` é função pura sobre retângulos em pixels físicos, como o
`WidgetPlacement`. Ela faz o papel do `IHudPositionService` pedido; virou
estática porque não tem estado nem dependência. Seis cantos/bordas e
"Personalizada". A margem (12 DIP) é escalada pela tela de destino. A tela é a
do centro da janela normal no momento de entrar no HUD, e não sempre a
principal. Reposicionar a cada mudança de altura é o que faz um HUD ancorado
embaixo crescer para cima.

**Arrastar é escolha; o sistema mover não é.** O cabeçalho do HUD não tem
`ElementRole="TitleBar"`: o arrasto é iniciado pelo próprio painel
(`BeginMoveDrag`), e só depois disso um `PositionChanged` vira "posição
personalizada" (com 400ms de silêncio para dar o arrasto por encerrado). Uma
troca de DPI ou um monitor desligado também movem a janela e, sem essa
distinção, transformariam "superior esquerdo" em "personalizada" sem ninguém
pedir.

**A janela normal volta exatamente para onde estava.** Ao entrar no HUD, a
geometria normal é carimbada em `_state`, e enquanto o HUD estiver na tela o
`Capture()` não grava posição nem tamanho: o `widget.json` guarda a janela
normal, e `Hud.X/Y` guarda o arrasto do HUD. Ao sair, `RestoreNormalPosition`
põe o canto superior esquerdo de volta, e `ApplyMode` repõe o tamanho do modo.

**O X é configurável, e nunca ignorado.** `CloseBehavior { Exit, Tray, Hud }`,
padrão `Tray` (o que o X sempre fez, ADR-016). `CloseRouting.Decide` é pura;
`MainWindow.OnClosing` só executa. O X do cabeçalho, o Alt+F4 e o "fechar
janela" da barra de tarefas passam por ali:

| Escolha | Janela normal | HUD |
|---|---|---|
| Fechar o aplicativo | sai (pelo encerramento do App) | sai |
| Minimizar para a bandeja | esconde | esconde |
| Entrar no modo HUD | vira HUD | **pergunta**: voltar à janela normal, ocultar na bandeja ou sair |

Sem ícone na bandeja, "esconder" vira "sair", e o item "Ocultar na bandeja"
some dos menus: sem ícone, não haveria caminho de volta. Desligar o Windows e
encerrar o app (`ApplicationShutdown`, `OSShutdown`) não perguntam nada.
"Sair do MyTaskApp" entrou no menu do painel e no do HUD, pelo mesmo
encerramento da bandeja (`ExitHandler`).

**HUD recolhido.** O HUD pode virar uma pílula (`✓ MyTaskApp` e o número de
pendências) pelo menu. Com "Usar HUD recolhido" ligado, ele entra assim e volta
à pílula 1,2s depois de o mouse sair. A pílula abre com o clique, ou com o
mouse parado 350ms sobre ela, para que atravessar a pílula a caminho de outra
coisa não abra nada. Não recolhe com a captura aberta, com o aviso da primeira
vez à vista ou com um menu aberto. Os menus são outra janela, então o ponteiro
"sai" do HUD ao entrar neles, e um conjunto estático de `Popup` abertos cobre
isso.

**Atalho global: existe, mas nasce desligado.** `IGlobalHotkeyService`, no
Windows com `RegisterHotKey` + `Win32Properties.AddWndProcHookCallback`, sem
hook de teclado. Ctrl+Shift+Espaço alterna janela e HUD, e revela o app se ele
estiver na bandeja. Desligado por padrão porque, no Visual Studio e no VS Code,
a mesma combinação é "informações de parâmetro", e um atalho global a roubaria
sem aviso. A opção diz isso na tela. Se outro programa já for dono da
combinação, a caixa desmarca e a tela explica o motivo. Fora do Windows a opção
some (no Wayland não há atalho global por desenho; no X11 fica para quando
alguém pedir).

**Iniciar no HUD.** "Iniciar o MyTaskApp no modo HUD" ganha de como a janela
estava ao fechar; sem ele, o app reabre no modo em que foi deixado
(`WidgetState.WindowMode`). É a exceção ao "o login do Windows sobe direto na
bandeja" (ADR-023): quem pediu o painel permanente quer vê-lo depois do login,
e o HUD é justamente a forma que não aparece na frente de ninguém.

**Descoberta.** Na primeira vez no HUD, um aviso dentro do próprio cartão diz o
que ele é, onde se ajusta e oferece "Entendi" (`Hud.IntroSeen`). Dentro do
cartão, e não numa janela por cima: quem acabou de fixar o app quer ver onde
ele foi parar.

**Configuração:** "Janela e comportamento…" (menu do painel, do HUD e da
bandeja) liga direto na moldura (`WidgetChromeViewModel`), então cada escolha
vale na hora e vai para o `widget.json` pelo mesmo caminho do resto, sem
segundo mecanismo e sem botão "Salvar". O arquivo ganhou `WindowMode`,
`CloseBehavior`, `StartInHud`, `GlobalHotkey` e um bloco `Hud` aninhado. Um
arquivo antigo abre como antes: X esconde na bandeja e a janela abre normal. A
exceção é quem atualiza **com o pino ligado** (`Topmost` e `Ghost`): reabre no
HUD, porque reabrir como janela grande fixada no topo seria o pior dos dois. O
campo `Ghost` só é lido, nunca mais gravado.

**Animação:** 150ms de opacidade ao entrar, sair, recolher e abrir, e nada mais.
Tamanho de janela não anima.

**Limites aceitos:**

- fora do Windows não há recorte de região. Sobram os cantos arredondados do
  cartão (alguns pixels) capturando clique. Não há margem transparente em
  plataforma nenhuma;
- a região do `SetWindowRgn` é serrilhada (GDI não faz antialiasing). Nos 10px
  de raio quase não se vê;
- o HUD não redimensiona com o mouse: o tamanho é escolha de preset
  (Compacto, Normal, Expandido);
- no X11 o `BeginMoveDrag` volta antes do fim do arrasto. Uma pausa de mais de
  400ms no meio do arrasto pode encerrar o registro da posição antes da hora. O
  próximo arrasto corrige.

**Testes:** `WindowModeTests` (transições e independência dos conceitos),
`CloseRoutingTests`, `HudPlacementTests` (cantos, monitor secundário com
coordenada negativa, DPI, ponto personalizado num monitor que sumiu),
`WidgetStateStoreTests` (ida e volta, migração do pino antigo, arquivo editado à
mão) e `WidgetHudTests` (headless: cartão sem anel transparente, altura que
acompanha o conteúdo, volta exata da janela normal, o X em cada combinação, a
região pedida ao serviço, aviso da primeira vez).

## ADR-049 — Instalador fecha o app aberto, em vez de pedir que alguém o feche

**Contexto:** o `.iss` usava `AppMutex` (ADR-019). Com o app aberto, o Inno
mostrava "feche todas as instâncias e clique em OK" e ficava esperando. Só que o
app vive na bandeja (ADR-016): o usuário não sabe onde está a "instância", e
fechar a janela só a manda de volta para lá. O `CloseApplications=yes` também
não resolvia, porque o Restart Manager pede para a janela fechar, e fechar é
esconder.

**Decisão:** o instalador avisa e fecha.

- `PrepareToInstall` lista, por WMI, todo processo cujo **executável mora
  dentro de `{app}`**, mostra a lista num aviso ("será fechado para continuar a
  instalação", OK/Cancelar) e encerra cada um com `taskkill /F /PID`. Espera
  eles morrerem (até 10s) antes de deixar o Inno copiar arquivos. Cancelar para
  na página *Preparando para Instalar* sem mexer em nada.
- O **resto** é do Restart Manager: `CloseApplications=force` com
  `CloseApplicationsFilter=*.*` fecha qualquer outro processo que segure um
  arquivo da pasta (um terminal, um editor). Ele roda depois do
  `PrepareToInstall`, então já não encontra o MyTaskApp.
- O desinstalador faz o mesmo em `usAppMutexCheck`: depois do "tem certeza?" e
  antes de apagar qualquer arquivo.
- Numa atualização **silenciosa** o aviso vale como OK, e o app que foi fechado
  volta no fim com `--startup`, na bandeja e sem elevação
  (`runasoriginaluser`). Com assistente, quem decide é a caixa "Iniciar o
  MyTaskApp", que já existia.

**Pelo caminho, não pelo nome.** Um MyTaskApp rodando de outro lugar (uma build
de desenvolvimento) não segura nenhum arquivo da instalação e não tem por que
morrer. O `unins000.exe` fica de fora da lista: ele mora em `{app}` e é quem
está desinstalando. O mutex segue como contrato com o `SingleInstance`, mas
virou detecção de reserva: só decide quando o WMI não responde, e aí o
encerramento é por nome (`/IM MyTaskApp.exe`).

**Forçado, e sem `/T`.** Não há como pedir para o app sair: fechar a janela o
manda para a bandeja, e a versão que está sendo substituída é a antiga, que não
conheceria um pedido novo de "encerre-se". Matar é seguro para os dados porque o
banco é SQLite (uma transação interrompida é desfeita no próximo start). Sem
`/T` porque os filhos do app (agentes, terminais, `gh`) são trabalho do usuário
e não rodam de dentro de `{app}`.

**Limites aceitos:**

- o ícone da bandeja do processo morto fica até o mouse passar por cima, como
  em qualquer processo encerrado à força;
- o tamanho e a posição da janela do último instante não são gravados — o
  `widget.json` fica com o que foi salvo no último movimento;
- listar processos por WMI leva uns dois segundos, que somam ao *Preparando
  para Instalar* de toda atualização. Numa instalação nova a pasta não existe e
  a listagem nem acontece.

**Testes:** `WindowsInstallerContractTests` trava as decisões (sem `AppMutex=`,
aviso com OK por padrão, `force` + `*.*`, `usAppMutexCheck`, nada de `/T`,
reabertura silenciosa na bandeja). O comportamento foi conferido num Windows
real com uma cópia isolada do instalador (outro `AppId`, outro nome, outra
pasta): atualização silenciosa com o "app" aberto e um segundo processo
travando `appsettings.json` — o primeiro saiu pelo `taskkill`, o segundo pelo
Restart Manager, e a instalação terminou com código 0; desinstalação silenciosa
com o app aberto removeu a pasta inteira.

## ADR-050 — Prazo: responsabilidade contínua, aviso por degrau

**Contexto:** o app nasceu para a tarefa do dia. Uma tarefa que leva vários
dias não tinha como dizer "isto precisa estar pronto sexta às 18:00", e a lista
a tratava mal: capturada na segunda e ainda aberta na terça, ela caía em
ATRASADAS (o `TodayClassifier` compara a data agendada com hoje), mesmo com a
entrega lá na frente. O pedido era um prazo opcional que o app acompanhe sozinho,
avisando mais conforme ele chega — sem virar Jira, Kanban nem calendário.

**Decisão:** três conceitos independentes, cada um no seu lugar.

| Conceito | Pergunta | Onde mora |
|---|---|---|
| Horário | quando pretendo fazer | `TaskOccurrence.ScheduledDate/Time` (ADR-002) |
| Lembrete | quando o app me chama | `ReminderPolicy` na série, `ReminderState` na ocorrência (ADR-004) |
| Prazo | quando precisa estar pronto | `TaskOccurrence.DeadlineDate/Time`, `DeadlineAlertState` na ocorrência, `TaskItem.DeadlineAlerts` na série |

Uma tarefa pode ter qualquer combinação deles, inclusive nenhum, que é a tarefa
de sempre e continua exatamente igual.

### O prazo é da ocorrência

É um "quando", como o agendamento, e a ocorrência é o que se conclui, o que a
lista desenha e o que o histórico guarda (ADR-001). A política dos avisos é da
série, espelhando o par `ReminderPolicy`/`ReminderState`. A recorrência ainda
não existe no código (o ADR-003 está só no documento); quando o materializador
for escrito, ele herda a obrigação de decidir o prazo de cada ocorrência nova —
uma regra relativa, como "o dia da ocorrência às 18:00", ou nenhum. Os testes
fixam hoje o que vale para qualquer desenho: o prazo é por ocorrência, concluir
para os avisos e o prazo original fica, para dizer depois se a entrega foi no
prazo (`DeadlineStatus.Met`/`Missed`).

**Hora de parede, hora obrigatória.** `TaskDeadline(DateOnly, TimeOnly)`, no
estilo do `TaskSchedule`, e o instante só na borda, por `IUserClock.ToInstant`.
A hora é obrigatória porque um prazo sem hora não sabe quando passa a estar
atrasado; quem escolhe só o dia recebe o horário padrão da configuração
(18:00 de fábrica).

### O próximo aviso não é coluna

`DeadlineAlertState` guarda o passado — o degrau mais grave já avisado, quando,
e até quando foi adiado —, e não o futuro. `DeadlineAlerting.Decide` é função
pura de (degraus ligados, repetição do atraso, prazo, estado, agora):

1. adiado e o adiamento não venceu: nada;
2. o degrau ligado mais grave já cruzado é mais grave que o último avisado: avisa;
3. o adiamento venceu: avisa o degrau atual de novo;
4. atrasada, com repetição, e o intervalo passou desde o último aviso: avisa de novo.

Três consequências, todas desejadas:

- **Coalescência por construção**, como a do ADR-004: quem ficou com o app
  fechado durante a véspera, as 8 h e as 2 h recebe **um** aviso, o de 2 h. Três
  dias fechado depois do prazo dão um aviso de atraso, e o próximo conta dali.
- **A configuração global vale na hora.** Ao contrário do `ReminderPolicy`, que
  é copiado para a tarefa na criação (ADR-014), a política de prazo é lida viva a
  cada tique. Mudar "Padrão" não exige rearmar banco nenhum — que era justamente
  a surpresa que o ADR-014 quis evitar.
- **Prazo recém-definido não avisa de si mesmo.** O estado nasce no degrau em que
  o prazo já está (`DeadlineAlerting.CurrentStage`): marcar "daqui a uma hora"
  não dispara o aviso de 2 h no mesmo clique. Prazo no passado é recusado.

`[Flags] DeadlineAlertStage` serve às duas perguntas — "quais degraus estão
ligados" e "qual foi o último avisado" — porque o valor maior é sempre o mais
grave. A sobrescrita da tarefa é `DeadlineAlertStage?`: nula segue a global,
`None` é silenciosa, qualquer conjunto é personalizado e vence a global, inclusive
a desligada (§20: a tarefa crítica continua avisando).

### O despacho anda no tique dos lembretes

`DispatchDeadlineAlertsHandler` roda dentro de `ReminderScheduler.TickAsync`,
depois dos lembretes, em escopo próprio e com `try/catch` próprio: um que lança
não cala o outro. Não é um terceiro agendador porque a razão do ADR-021 — 30 s
contra 6 h — não se aplica: o degrau de 2 h precisa da mesma precisão dos
lembretes. Não há timer por tarefa. O despacho segue o resto da casa: respeita
"pausar lembretes", marca e grava antes de apresentar (ADR-004), e acima de
três avisos vira um resumo. Som só para 2 h e atraso; a véspera avisa calada.

As candidatas vêm de `IDeadlineAlertQuery` (pendentes com prazo, fora do
arquivo e da lixeira), e o índice parcial `IX_TaskOccurrences_Deadline` cobre a
busca. Não filtrar por "venceu" no SQL é a troca por não ter coluna de próximo
aviso: as tarefas com prazo são poucas.

### A seção PRAZOS

O classificador ganhou uma regra só, e só para quem tem prazo:

- prazo passado (em hora de parede) → **ATRASADAS**;
- agendada para uma hora de hoje → o plano do dia, AGORA ou HOJE, com o prazo
  como rótulo;
- o resto — data passada, futura, sem data, hoje sem horário → **PRAZOS**.

Cada ocorrência continua numa seção só. PRAZOS fica entre HOJE e SEM HORÁRIO,
ordenada pelo prazo e sem arrasto (a pergunta da seção é "o que vence
primeiro"), e o cabeçalho leva o resumo do §11 ("1 atrasada · 2 hoje · 3 na
semana") — uma linha, e não um painel. O `TodayQuery` passou a trazer as
pendentes com prazo de qualquer data: uma tarefa marcada para quinta com
entrega na sexta já aparece na segunda. No HUD, PRAZOS mostra só severidade
de atenção para cima (até 48 h); o prazo de daqui a duas semanas fica na janela
normal, e os números continuam contando tudo.

### Uma regra de texto, e nada calculado na tela

`DeadlineAssessment` decide status e severidade (atrasada; urgente no dia ou a
≤ 8 h; atenção a ≤ 48 h; normal), e `DeadlineFormatter` escreve: "5 dias e 17
horas", nunca "137 horas"; "1h 42min" abaixo de 2 h; "ATRASADA · há 3 horas".
A severidade vai escrita no texto e a cor só reforça (§5). A Application monta
um `TaskDeadlineView` por linha; a linha, o card, o HUD e os avisos só copiam.
O "tempo real" é o refresh de 60 s que o quadro já tinha.

### Tela

Na linha, um relógio e o rótulo abaixo do título. No menu ⋯, "Definir/Alterar
prazo" com os atalhos (Hoje, Amanhã, Final da semana, Próxima semana, Em 3 dias,
Em 1 semana, Personalizado…) e "Remover prazo" — os atalhos são clique de
code-behind porque o comando precisa da linha e do atalho juntos. Na janela da
tarefa, um card que grava cada coisa na hora, como o do Jira: prazo e quanto
falta, atalhos, dia e hora, os avisos da tarefa, a próxima ação (§14) e a
estimativa (§15, só guardada e mostrada por enquanto). O aviso de prazo entra na
mesma pilha dos lembretes e do agente, com chave própria — a mesma ocorrência
pode ter lembrete e prazo na tela ao mesmo tempo —, e oferece Abrir, "+1 dia"
(muda o prazo, à vista), "Adiar 1 h" (só o aviso, §21) e OK. Quando o Claude
Code para esperando o usuário e a entrega está a 48 h ou menos, o aviso dele
diz o prazo (§26). A configuração ganhou "Alertas de prazo" na janela de
lembretes, no mesmo "Salvar": Padrão, Personalizado ou Desativado.

**"Final da semana" é sexta, fixo.** O app não tem configuração de semana, e a
cultura pt-BR começa a semana no domingo, o que faria o fim cair no sábado.
`DeadlineShortcuts.LastWorkday` é o lugar de mudar isso.

### Banco

Migration `TaskDeadlines`: `Deadline_Date`/`Deadline_Time` e
`DeadlineAlert_*` em `TaskOccurrences`; `DeadlineAlerts`, `NextAction` e
`EstimateTicks` em `Tasks`; a tabela de linha única `DeadlineSettings`, sem
semente (ADR-014). Tudo nasce nulo ou 0: quem atualiza continua sem prazo e
nada é armado — há teste partindo do banco de antes. Status, severidade e dias
restantes não são colunas.

### Fora de escopo, de propósito

- **Jira.** O retrato da issue não traz `duedate` e nada sincroniza. O caminho
  futuro é um `ExternalLink.DueDate` lido como sugestão ("a issue vence sexta —
  usar como prazo?"), nunca escrito por cima do prazo local sem o usuário pedir.
- **Planejamento.** Com prazo e estimativa, "reserve 2 h por dia" é uma conta
  simples; fica para quando houver onde mostrar sem virar gerenciador de
  projeto.
- **Horário silencioso** continua fora, como no ADR-004: as saídas são pausar
  pela bandeja e silenciar a tarefa.

**Testes:** domínio (`DeadlineAlertingTests`, `DeadlineAssessmentTests`,
`DeadlineFormatterTests`, `DeadlineShortcutsTests`, `TaskItemDeadlineTests`,
`TodayClassifierDeadlineTests`), Application (despacho com política global,
sobrescrita, pausa e ordem gravar → apresentar; casos de uso; quadro com
PRAZOS; o tique rodando os dois despachos e um não derrubando o outro),
Infrastructure (ida e volta, configuração, consultas, upgrade a partir de
`BranchSettings` e o app fechado de segunda a sábado contra SQLite de verdade)
e Desktop (linha, seção, HUD, comandos, card, aviso e configuração, mais os
bindings headless do submenu e do card). Tudo com `FakeTimeProvider`.

---

## ADR-051 — Comandos rápidos: o comando global vira botão no worktree

**Contexto:** até aqui o app só rodava comando no worktree de dois jeitos: os
pós-Worktree (ADR-028), em sequência logo depois de criar o ambiente, e o
"Testar" da janela de globais, numa pasta escolhida à mão. Para "rodar a
aplicação" de uma tarefa o usuário ainda abria um terminal, ia até a pasta do
worktree e digitava `dotnet run`. O pedido era o contrário: cadastrar uma vez,
ligar ao projeto, e ter na tarefa um botão "▶ Executar aplicação" que resolve
pasta, worktree, variáveis e terminal sozinho.

**Decisão:** o comando global do ADR-028 é o comando rápido. Ligado ao
**diretório de uma etiqueta** (o repositório), ele aparece como botão na seção
"⚡ Comandos" de todo ambiente pronto daquele repositório, e só roda quando o
usuário clica.

| | Pós-Worktree (ADR-028) | Comando rápido |
|---|---|---|
| Quando roda | sozinho, ao criar o worktree | só no clique |
| Onde mora | lista da tarefa | diretório da etiqueta |
| Como roda | escondido, em sequência | escondido **ou** num terminal visível |
| Histórico | só na tela | `CommandExecutions` |

Os pós-Worktree continuam exatamente como eram.

### Um comando, e não dois cadastros

`DevelopmentCommand` ganhou o que só o botão usa, tudo com o padrão de antes:
`Name` (o rótulo; sem ele, a tela mostra o alias), `Mode` (`Execute` ou
`Terminal`), `WorkingDirectory` (relativa ao worktree; `null` é a raiz),
`KeepTerminalOpen` e `RequiresConfirmation`, mais a definição tipada dos
parâmetros (`DevelopmentCommandParameter`: rótulo, tipo texto/número/lista,
padrão, obrigatório, opções). O alias continua obrigatório e único, porque a
lista pós-Worktree o chama; quem só quer o botão escreve o nome, e o alias é
sugerido a partir dele. Um `{nome}` sem definição continua texto obrigatório,
e definições de nomes que saíram do texto somem ao salvar.

### Diretório, e não etiqueta

A etiqueta ECO CORE pode ter `@ecossistema-core` e `@eco-web`;
`dotnet run --project src/Eco.Web` só faz sentido em um deles. Por isso a
associação (`TagDirectoryCommand`) é filha do `TagDirectory`, dentro do agregado
`Tag`: ordem, ligado/desligado (desligar não apaga) e o **override** — outro
texto e/ou outra pasta só ali. O override é `null` para "usar a configuração
global", e `.` na pasta força a raiz mesmo que o global aponte uma subpasta. A
associação guarda a referência ao global, e não uma cópia: editar o global vale
para todos os diretórios. Excluir o global leva os botões junto (cascata) e a
tela avisa quantos antes.

**O ambiente não aponta para etiqueta** — ele guarda uma cópia do caminho do
repositório (ADR-027). `QuickCommandCatalog` (puro) acha os diretórios, de
**qualquer** etiqueta, cujo caminho é o repositório ou uma pasta dentro dele,
comparados como o sistema compara (`WorktreePathPlanner.Canonical`). Um
diretório numa subpasta (`…\src\Eco.Web`) roda na mesma subpasta do worktree.
Os diretórios das etiquetas da própria tarefa vêm primeiro, e o mesmo comando
em dois diretórios aparece uma vez, com a configuração do primeiro. A
comparação é em C#, e não em SQL, pelo mesmo motivo do ADR-031: maiúsculas e
barras.

### Variáveis de contexto, sem quebrar os parâmetros de antes

`{worktree}` (= `{worktree.path}`), `{worktree.name}`, `{repository}`
(= `{repository.path}`), `{repository.name}`, `{branch}`, `{task.id}`,
`{task.title}` e `{tag}` (a etiqueta do diretório). Os nomes sem ponto têm a
forma de um parâmetro do usuário, e um global antigo pode ter um `{branch}`
preenchido por `@x branch=main`. Daí as regras:

- `CommandParameters.Names` não mudou: o `branch=main` continua sendo parâmetro.
- **O valor explícito vence**; o contexto só preenche o que ficou sem valor.
- O preenchimento é de **uma passada** (`CommandVariables.Fill`): um valor que
  contenha `{branch}` entra como texto.
- `CommandAliasResolver` ganhou `CommandContext? context = null`; sem contexto, é
  o comportamento de antes, e todos os testes do ADR-028 ficaram como estavam.
- Os pós-Worktree passam o contexto real ao rodar, então `{worktree}` passou a
  funcionar nos globais que eles chamam. Ao conferir a lista, antes de o worktree
  existir, as variáveis são **adiadas** (`CommandContext.Deferred`): não são
  cobradas, e ficam no texto até a execução. `{tag}` não entra no adiamento:
  ali não há diretório que o preencha. Entrada literal não é tocada.

**Injeção.** O título da tarefa pode ter vindo do Jira. Só os valores de
**contexto** que o comando usa são conferidos, depois de montada a linha: no
Windows, `" % & | < > ^` e quebra de linha; fora dele, também `` ` $ ; \ ( ) '``.
O comando é recusado dizendo qual variável e qual caractere. Espaço não conta —
a dica do formulário manda pôr aspas (`code "{worktree}"`). O valor que o usuário
digita num parâmetro continua indo como escrito, como no ADR-028.

### Execução: casos de uso, e não a tela

`PrepareQuickCommand` devolve o comando efetivo, a pasta conferida e o que
perguntar; `RunQuickCommand` **monta a linha de novo** com o que leu agora (a
prévia da tela é conveniência, não verdade), grava a execução como `Queued` e
então:

- **Execução** roda pelo `ICommandExecutor` de sempre, com o output ao vivo e o
  exit code; cancelar é `Stopped`, tempo esgotado e shell que não abre são
  `Failed`. O mapeamento de estado é o mesmo da lista
  (`CommandSequence.StateOf`).
- **Terminal** abre o shell do sistema numa janela visível pelo mesmo lançador
  do agente (ADR-030), que devolve PID e início:
  `%ComSpec% /d /s /k "chcp 65001>nul & linha"` (ou `/c` com "manter aberto"
  desligado). `TerminalLaunchOptions` ganhou `RawArguments`, porque o cmd não
  entende o `\"` com que o `ArgumentList` escaparia as aspas do usuário, e o
  shell precisa de caminho absoluto (sem `ComSpec`, o `cmd.exe` da pasta do
  sistema). A porta é `ITerminalCommandLauncher`, que recebe a linha: comando
  rápido é shell de propósito.

Tudo isso mora na Application, atrás do `IUseCaseRunner`: a tela só pede. É o
que deixa a mesma execução ao alcance de um controle remoto no futuro, sem
reescrever nada.

**Um `dotnet run` por vez em cada ambiente.** O segundo brigaria pela porta;
com o terminal aberto, o botão fica "🟢 Executando desde 14:02" e oferece
"Mostrar terminal" (`ITerminalWindowManager.FocusAsync`). Um escondido por vez,
porque o painel de output é um.

### O processo é a verdade, como no agente

`CommandExecution` é um agregado próprio, com cópias do nome, da linha e da
pasta, PID e início do processo, exit code, e o fim de cada output (64 000
caracteres). O output vai para o banco local, que é dado do usuário, e **não**
para o log (ADR-028). O histórico guarda as 20 últimas execuções de cada
ambiente.

`CommandExecutionMonitor` segue o `AgentSessionMonitor`, sem o timer: vigia o
PID de cada terminal aberto (`IAgentProcessTracker.WatchExitCode`, que lê o
exit code no evento de saída — só dá porque o vigia segura o handle antes de o
processo sair), reconcilia ao abrir o app e a cada leitura da tela, e avisa por
`ExecutionsChanged`, que a `App` repassa à janela aberta. O que um timer pegaria
a mais, a tela pega na próxima vez que olha.

**O registro em processo.** Uma execução escondida não tem PID gravado; sem
mais nada, a reconciliação não distinguiria "rodando aqui" de "o app caiu com
ela rodando". O monitor guarda as que rodam neste processo
(`TrackInProcess`); as outras, `Running` e sem processo, viram `Failed` ("o app
foi fechado enquanto o comando rodava"). O mesmo registro é o gancho de um
"Parar" futuro.

**Exit code do terminal.** Com `/k`, o processo é o shell, que sobrevive ao
comando: o exit code é o de quem fechou a janela e não diz nada. Fechou, é
`Completed` com exit code nulo ("Terminal fechado"). Com `/c`, é o errorlevel do
comando. Um processo que saiu com o app fechado também fica sem exit code.

### Tela

- **Comandos globais…:** Nome e apelido lado a lado, o tipo (Execução ou
  Terminal), a pasta, "Manter o terminal aberto" e "Pedir confirmação", uma
  linha por `{nome}` para tipo, rótulo, padrão e opções, e a dica das variáveis.
  A lista mostra o que cada botão faz ("Terminal · pasta src/Eco.Web · em 2
  diretórios").
- **Etiquetas…:** cada diretório ganhou "Comandos (n)", com ☑ ligado, ↑/↓,
  "Personalizar" ("Usar configuração global" × "Personalizar para este
  diretório") e "+ Adicionar comando".
- **Aba Desenvolvimento:** o card "⚡ Comandos", entre o ambiente pronto e o
  agente: um "▶ Nome" por comando, com a linha e a última execução; "Mostrar
  terminal", "Ver saída" e "Cancelar"; e "+ Executar comando…", que roda
  qualquer global como cadastrado, sem a personalização do diretório.
- **O diálogo antes de rodar** (`QuickCommandPromptWindow`) só abre quando há
  parâmetro ou confirmação: os campos (texto, número, lista, já com o padrão) e
  a linha final ao vivo, com a pasta. Com confirmação, ler a linha é a
  confirmação; sem campo a preencher, o foco nasce em Cancelar, para o Enter
  reflexo não rodar o que pediu para ser lido. Sem nada a perguntar, o clique
  roda.
- Fechar a janela com um escondido rodando cancela e mostra, como nos
  pós-Worktree; o segundo "X" fecha. Um terminal aberto não segura a janela.

### Banco

Migration `QuickCommands`: colunas novas em `DevelopmentCommands` e as tabelas
`DevelopmentCommandParameters`, `TagDirectoryCommands` e `CommandExecutions`.
Nada é apagado; os globais de antes sobem como comandos escondidos na raiz. O
`KeepTerminalOpen = true` das linhas antigas é escrito à mão na migration, e o
modelo **não** tem `HasDefaultValue(true)`: o EF tomaria o `false` do CLR por
"não informado" e gravaria `true` no lugar. Os índices de ordem não são únicos
(ver `TaskDevelopmentCommandConfiguration`). O histórico perde o vínculo — e
não a linha — quando o ambiente sai da lista, o global é excluído ou a
associação some; só a exclusão definitiva da tarefa o leva.

### Fora de escopo, de propósito

- **Parar e reiniciar.** PID, início e o registro em processo já estão
  gravados; falta o caso de uso e o botão.
- **Menu ⋯ da linha** com os comandos: fica para depois, como o "Abrir Claude
  Code" veio depois do card (ADR-036).
- **Terminal fora do Windows:** o lançador não suportado responde, e o botão
  mostra a falha. O Linux entra trocando o lançador, como no ADR-030.
- Tela de histórico completa (a v1 mostra a última execução de cada comando),
  variáveis de ambiente `MYTASKAPP_*` no terminal, variáveis de contexto em
  entradas literais dos pós-Worktree, e um timer de reconciliação.
- Um terminal aberto no worktree segura a pasta: "Remover Worktree" cai no
  fluxo do ADR-029, que mostra o `cmd.exe` e oferece encerrar.

**Testes:** domínio (configurações e atomicidade do global, parâmetros tipados,
variáveis e pasta relativa, associação no diretório, ciclo da execução),
Application (contexto no resolvedor sem mudar o de antes, pós-Worktree com
contexto, catálogo por caminho, preparar e executar nos dois modos, recusas,
histórico, foco, fim pelo vigia, monitor e o registro em processo, e o registro
no contêiner — que passou a incluir também os handlers do ADR-028),
Infrastructure (ida e volta, consultas, cascatas, upgrade a partir de
`TaskDeadlines`, a linha do shell, a linha crua até o lançador e o exit code de
um processo de verdade) e Desktop (o card, o diálogo, o formulário dos globais
e a lista do diretório, mais os bindings headless das três janelas).
