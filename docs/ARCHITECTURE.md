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
O tema passou a ser **escuro por decisão**, com paleta própria em
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
monocromáticos e herdam a cor do botão. É uma dependência do Windows — o app já
era `WinExe` com P/Invoke em `user32`.

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
em outro lugar.

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
encosta no marcador de fora (`**muito *mesmo***`): o par de dentro sai literal.
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
- sem terminal embutido e sem ler o que se passa na sessão;
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
