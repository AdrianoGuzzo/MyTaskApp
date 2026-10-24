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
| Npgsql | 10.0.3 | o ADO.NET das operações de banco (ADR-056); subiu de 10.0.2 porque o provider do EF exige |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | só o servidor de sincronização (ADR-061); o desktop continua só SQLite |
| ASP.NET Core Identity + Google + DataProtection.EF | 10.0.12 | contas do servidor de sincronização, só por login externo (ADR-061) |
| OpenIddict | 7.7.2 | servidor OAuth do desktop: code + PKCE, refresh rotativo, revogação (ADR-061) |
| Testcontainers.PostgreSql | 4.16.0 | testes do servidor contra PostgreSQL real; pulam sem Docker |
| Caddy | 2.10 (`caddy:2.10-alpine`) | só o proxy TLS do compose de produção do servidor de sincronização (ADR-070) |
| .NET MAUI (Microsoft.Maui.Controls) | 10.0.110 | só o app do celular (ADR-067), Android e iOS; a versão acompanha o workload `maui-android` |

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

**Nas telas:** o caminho acima mora em `ScreenErrorPath`
(`Desktop/ViewModels`). O ViewModel informa só o que é dele:

- o evento de log;
- se limpa a linha de status ao começar;
- se o cancelamento é silencioso.

`ScreenErrorPathTests` fixa esse caminho tela a tela. As telas com um passo a mais
mantêm o próprio `TryAsync`: Sincronização (`SyncApiException`), alertas (avisam
quem os abriu) e post-its (recarregam a lista). Elas estão listadas em
`docs/refactoring/PROGRESS.md`.

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

**Etiquetar na janela da tarefa.** Logo abaixo do título editável, as
etiquetas aparecem como **pílulas com o nome**, porque ali há largura, ao
contrário da linha de 360px. O botão "Etiquetas" abre o mesmo seletor
(`TaskTagEditorViewModel`). Ele **grava a cada marca**, como o da linha e o card
do prazo (ADR-050), e fica **fora do "Salvar"**, que é do título e da anotação:
um "Cancelar" não desfaz etiqueta. Ao abrir, o seletor relê também **as
etiquetas da tarefa** (`GetBoardTask`), e não só a lista de todas. A janela
fica aberta por horas e `SetTaskTags` grava o conjunto inteiro, então marcar
sobre um retrato velho desfaria em silêncio o que a linha mudou nesse
meio-tempo. Com a tarefa concluída, a janela é somente leitura: as pílulas
ficam, sem "×" nem seletor.

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
| `ponta`         | Verde Pecuária  | escuro | verde-limão           |
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

**Verde Pecuária, a exceção de marca.** O Verde Pecuária veio depois, na
identidade verde da Ponta, e é o único tema com destaque na matiz do sucesso.
Chamava-se "Ponta"; o id `ponta` ficou, porque é o que está gravado no
`widget.json` de quem já o escolheu, e trocar o id mandaria essa pessoa de
volta para "Automático". A paleta da marca vai
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
(Nórdico, Ameixa, Alto contraste, Verde Pecuária), o selecionado ficava branco sobre
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
  ali não há diretório que o preencha. Entrada literal não era tocada; desde o
  ADR-060, ela tem as **variáveis de contexto** preenchidas (e só elas).

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

**"Pedir confirmação" também é do caso de uso.** No começo só a tela cobrava:
o `RunQuickCommandHandler` rodava quem o chamasse sem perguntar. Agora
`RunQuickCommand` leva `Confirmed`, e o handler recusa o comando que pede
confirmação e chega sem ela, conferindo o comando lido na hora: se ele passou
a pedir confirmação depois da prévia (por sync, por exemplo), não roda sem
perguntar de novo. A tela marca o pedido depois do diálogo. É a preparação da
orquestração remota: um pedido de longe vai confirmar por uma aprovação, e não
por esta tela.

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

---

## ADR-052 — Tempo trabalhado: o período é a verdade, o relógio é desenho

**Contexto:** a estimativa existia (ADR-050), mas o tempo gasto não. O pedido
era registrar **quando o trabalho começou e quando terminou**, com ▶/⏹ na
própria linha, lançamento manual para o que se esqueceu de cronometrar, edição,
exclusão, o total contra a estimativa e um histórico por dia. Um cronômetro que
sobrevive a fechar o app, ao crash, ao reboot e à suspensão, e só um
cronômetro correndo no app inteiro.

**Decisão:** `TimeEntry` é um período — `StartedAt` e `EndedAt`, instantes UTC
(ADR-011) — de uma **ocorrência**, com a origem (`Timer` ou `Manual`), uma
observação e `CreatedAt`/`UpdatedAt`. Ativo é `EndedAt` nulo, e nada mais. A
duração é sempre `EndedAt − StartedAt`; não existe coluna de horas, de total,
nem de "está correndo".

### Por que o período, e não as horas

As horas são derivadas, e guardar o derivado é guardar duas respostas para a
mesma pergunta (o argumento do ADR-020). E é o período que deixa as perguntas
de depois baratas: tempo por dia, por semana, por etiqueta, por issue do Jira,
por worktree — todas são `SUM` sobre períodos filtrados por data e juntados à
ocorrência. Um total agregado não responderia nenhuma delas. Os índices
`(TaskOccurrenceId, StartedAt)` e `StartedAt` já servem a essas consultas, que
ficam para quando houver relatório.

### Agregado próprio, e da ocorrência

A ocorrência é a unidade de trabalho (ADR-001): é ela que se conclui e que a
lista desenha, e o prazo já mora nela. Mas o período **não** é filho do
`TaskItem`: a lista cresce sem teto e viria junto em toda leitura da tarefa, e a
regra que mais importa — um cronômetro no app — atravessa tarefas de qualquer
jeito. É o desenho do `CommandExecution` (ADR-051): agregado próprio, ligado
por id, com FK em cascata para `TaskOccurrences` (a primeira do código). A
exclusão definitiva leva os períodos junto, como leva as execuções.

A tarefa continua dizendo o que vale nela: `TaskItem.EnsureTimerCanStart`
(pendente e na lista principal) e `EnsureTimeCanBeLogged` (na lista principal;
lançar à mão numa concluída vale, porque esquecer de iniciar é justamente o
caso). Arquivado e lixeira são somente leitura, como no ADR-020.

### As regras, e onde moram

- **Fim depois do início, e nada no futuro** — `TimeEntry`, que valida tudo
  antes de atribuir (Edição atômica). Corrigir preserva a origem; o período que
  corre não se edita, se para.
- **Sem sobreposição na mesma tarefa** — `TimeLog.FindOverlap`, função pura no
  estilo do ADR-010, com intervalos semiabertos: `14:00 → 15:00` e
  `15:00 → 16:00` só se encostam. O período ativo vale até agora. A regra é
  **por tarefa**, como foi pedido: dois lançamentos em tarefas diferentes no
  mesmo horário são aceitos (trabalho em paralelo existe, e quem quiser
  impedir sabe onde mexer).
- **Um cronômetro no app** — `StartTimerHandler` relê o ativo **do banco**,
  nunca do ViewModel (o quadro pode ter um minuto). Duplo ▶ na mesma tarefa é
  no-op. Outra tarefa correndo recusa, a menos que o comando traga
  `ReplaceRunning` — que é a resposta do "Parar e iniciar" —, e aí o anterior
  fecha e o novo abre no mesmo `SaveChanges`.
- **Parar ao concluir** — concluir, cancelar, arquivar e mandar para a lixeira
  fecham o período da tarefa no mesmo instante e na mesma transação
  (`RunningTimer.StopIfOnAsync`). Reabrir não retoma: retomar seria inventar
  um intervalo de trabalho que ninguém fez.
- **Relógio que voltou** — se o ⏹ chega antes do início gravado (o relógio do
  sistema andou para trás), o período é **descartado** em vez de recusado: um
  cronômetro impossível de parar prenderia o usuário, e um período de duração
  zero não é trabalho.

### O banco também segura o segundo cronômetro

Um índice único filtrado em `EndedAt` não segura nada no SQLite: num índice
único os NULLs são distintos entre si. O que funciona é indexar uma
**expressão** que vale o mesmo em toda linha ativa:

```sql
CREATE UNIQUE INDEX "IX_TimeEntries_SingleActive"
ON "TimeEntries" (("EndedAt" IS NULL)) WHERE "EndedAt" IS NULL;
```

O EF não modela índice sobre expressão, então ele está em SQL cru na migration
`TimeEntries`. É cinto e suspensório, como `IX_AgentSessions_TaskDevelopmentId_Active`:
o caso de uso já recusa antes, e a violação não é traduzida para mensagem —
chegar ali é defeito, e cai no erro genérico com log. O mesmo índice serve a
busca do ativo, que tem o mesmo predicado.

**O risco, e quem avisa:** o modelo não conhece o índice, e uma migration
futura que reconstrua a tabela (o SQLite faz isso em várias alterações de
coluna) o perderia sem aviso. `TimeEntryPersistenceTests` confere que ele
existe em `sqlite_master` depois de **todas** as migrations.

**A ordem na troca.** Com o índice, o UPDATE que fecha o anterior tem de chegar
ao banco antes do INSERT do novo. O EF 10 emite nessa ordem, mas não a
promete; um teste contra SQLite de verdade fixa a troca num `SaveChanges` só.
Se um dia o EF mudar a ordem, é esse teste que quebra, e a saída é fechar e
abrir em dois `SaveChanges`.

### O relógio é desenho

Nada conta segundos. O tempo corrido é `TimeProvider.GetUtcNow() − StartedAt`,
calculado por quem desenha. `ActiveTimerViewModel` é o único objeto do app que
tica — `TimeProvider.CreateTimer` de 1 s, ligado só enquanto há cronômetro — e
o tique só recalcula o texto. O banco muda no ▶ (INSERT) e no ⏹ (UPDATE), e em
nenhum outro momento. É por isso que fechar o app às 14:30 e abri-lo às 15:45
mostra 01:45: o primeiro quadro carregado traz o ativo (`TodayBoard.ActiveTimer`,
por `IActiveTimerQuery`) e o relógio volta a subtrair. Não há laço de fundo
preservando estado, nem tratamento especial para suspensão — não há o que
tratar.

O relógio é um só, e não um por linha: as linhas são recriadas a cada recarga
do quadro, e um relógio por linha recomeçaria do zero a cada minuto. A linha,
a faixa do HUD e a aba "Tempo" amarram no mesmo `ElapsedText`.

### Tela

- **Na linha**, um botão discreto antes do horário: parado, ▶ e o total
  ("4h 32min") — some até o hover sem nada lançado, meio aceso com tempo
  lançado; correndo, ⏹ e o relógio, sempre aceso, na cor de destaque, e a linha
  ganha um fundo de destaque leve. É por ele que se para sem abrir a tarefa.
  Concluída mostra o total e não oferece ▶.
- **A troca** pergunta pelos nomes ("Você está trabalhando em … Deseja parar
  essa tarefa e iniciar …?"), com "Parar e iniciar" e o foco em Cancelar. A
  pergunta é da tela; a recusa sem ela é do caso de uso.
- **A faixa do cronômetro** (`⏹ Título · 01:37:42`), acima da lista: na janela
  normal, só quando a linha não está à vista — uma tarefa marcada para amanhã
  também pode estar sendo trabalhada hoje —, e no HUD sempre, porque lá o
  filtro esconde seções. Uma linha, e não um painel.
- **No HUD** o ▶ parado sai do layout, como a etiqueta (ADR-048), e o ⏹ fica.
- **A aba "Tempo"** da janela da tarefa: ▶/⏹ e o relógio; "Registrado 4h 32min
  · em andamento 27min" — o que corre fica à parte, e o total não muda a cada
  segundo —; o gasto contra a estimativa ("4h 59min / 6h · 83%", ou "+1h 20min
  acima da estimativa" na cor de cautela, sem bloquear nada); e os períodos
  agrupados pelo dia do usuário ("Hoje", "Ontem", "05/10/2026"), com
  ✎ e 🗑 no hover. O card do plano ganhou uma linha "Gasto: …" ao lado da
  estimativa.
- **O diálogo** de lançar e corrigir (`TimeEntryWindow`, atrás da porta
  `ITimeEntryEditor`): dia, início, fim e observação. "Termina no dia seguinte"
  só aparece quando o fim fica antes do início — adivinhar transformaria um
  erro de digitação em 22 horas. A gravação é do caso de uso: uma recusa
  (sobreposição, fim no futuro) aparece no próprio diálogo, que fica aberto com
  o que foi digitado.
- **Os campos são texto**, e não `TimePicker`/`CalendarDatePicker`: girar um
  seletor para 08:31 é mais lento que digitar. `WallClockInput` lê `0831`,
  `831`, `8h31` e `8` como hora, e `06/10/2026`, `0610`, `6/10`, `061026`,
  `6`, "hoje" e "ontem" como dia. Sem ano (ou sem mês), vale a ocorrência mais
  recente até hoje — em 03/01, `28/12` é o do ano que acabou —, porque período
  no futuro não existe. Ao sair do campo o texto volta por extenso (`08:31`),
  ou uma mensagem diz o que não foi lido; no meio da digitação, nada reclama
  (`083` é o caminho para `0831`). Entrar no campo seleciona tudo, e o
  calendário continua ao lado da data, com o futuro riscado.
- **Excluir** pergunta com o período e a duração, faixa de aviso e foco em
  Cancelar (ADR-020). O app não tem desfazer, e não ganhou um.

### Auditoria

Lançar, corrigir e excluir à mão entram na trilha existente
(`TaskAuditOperation.TimeEntryAdded/Changed/Deleted`), com o período nos
detalhes — "06/10 14:00 → 15:30 (1h 30min) · Manual", e o antes → depois na
correção. O ▶/⏹ **não** entra: o próprio período já é o registro, e auditá-lo
dobraria a tabela sem dizer nada novo.

### Banco

Migration `TimeEntries`: a tabela, os dois índices do modelo e o índice do
cronômetro único em SQL cru. Nada é alterado nas tabelas existentes; quem
atualiza abre com zero períodos em toda tarefa, e há teste partindo de
`QuickCommands`.

### Fora de escopo, de propósito

- **Relatórios** (por dia, semana, etiqueta, issue, worktree). O modelo já os
  permite; falta a tela.
- **Jira.** Nada vai para o Jira. Um `TimeEntry` encerrado vira um worklog
  quase de graça — início, duração e comentário —, mas enviar é decisão do
  usuário e fica para a integração (ADR-045).
- **Sobreposição entre tarefas diferentes** e **limite de duração** de um
  período (um cronômetro esquecido no fim de semana dá 60 h, e a correção é
  editar).
- **Retomar ao reabrir** e **desfazer** a exclusão.

**Testes:** domínio (`TimeEntryTests`, `TimeLogTests` com cada forma de
sobreposição, `WorkTimeFormatterTests`, `TaskItemTimeTrackingTests`);
Application (▶ e ⏹, duplo ▶, troca recusada e confirmada, relógio que voltou,
**fechar e abrir o app** — 14:00 inicia, 16:00 outro handler encontra o mesmo
início e 2 h corridas —, **suspensão** — 14:00, 1 h 30 sem tique nenhum, ⏹
dá 1 h 30 —, lançar, corrigir e excluir com auditoria, o histórico por dia e o
quadro); Infrastructure (ida e volta com o tique exato, o índice recusando o
segundo ativo, o índice existindo depois de todas as migrations, a troca num
`SaveChanges`, a cascata, a soma da lista e o upgrade); e Desktop (a linha, a
pergunta de troca contra o caso de uso de verdade, o relógio ticando sem chamar
caso de uso nenhum, a aba e o diálogo, mais os bindings headless da linha, da
faixa no HUD, da aba e do diálogo). Tudo com `FakeTimeProvider`.

---

## ADR-053 — Histórico de 7 dias: uma projeção, e não uma tabela

**Contexto:** com o tempo trabalhado gravado (ADR-052), faltava responder de
relance "o que eu fiz ontem?", "o que eu fiz esta semana?", "quanto trabalhei
naquela tarefa na terça?". O checklist continua sendo das tarefas de agora: o
histórico não pode virar dashboard nem ocupar espaço fixo na tela.

**Decisão:** o histórico é uma **projeção** do que já está gravado, e não uma
cópia. São duas fontes, lidas só dentro da janela:

| Fonte | O que diz | Onde cai |
|---|---|---|
| `TaskOccurrence.CompletedAt` | concluída, e quando | no dia local da conclusão |
| `TimeEntry` (`StartedAt`/`EndedAt`) | trabalhada, e quanto | em cada dia local que o período tocou |

Nenhuma migration e nenhuma tabela nova. Os índices de `CompletedAt` e de
`TimeEntries.StartedAt` já existiam.

### A janela, no fuso do usuário

A janela vai de hoje até seis dias atrás, sempre sete dias, incluindo os vazios.
Ela vai da meia-noite local do primeiro dia até a meia-noite local de amanhã,
exclusiva, e as duas pontas saem de `IUserClock.ToInstant(dia, 00:00)`. Não se
usa `+24 h`: o dia da troca de horário de verão tem 23 ou 25 horas, e é o
`UserClock` que sabe disso (ADR-002).

`ActivityHistoryBuilder` é uma função pura, no estilo do ADR-010. Ela recebe as
fronteiras e a conversão para data local já prontas, e:

- **parte o período na meia-noite**: 23:00 → 01:30 conta 1h num dia e 1h 30min
  no outro;
- **corta o que caiu fora da janela**: o período que começou antes dela conta só
  a parte de dentro;
- **soma os períodos da mesma ocorrência no mesmo dia** e funde o resultado com a
  conclusão daquele dia: uma linha "Concluída 15:42 · 45min";
- **leva o cronômetro que corre até agora**: o histórico olha o mesmo
  `agora − StartedAt` que a linha desenha.

### A ocorrência, e não a série

A chave de cada linha é `(dia, ocorrência)`. Uma série (ADR-003, quando
existir) aparece com a ocorrência de cada dia, e o clique abre aquela
ocorrência. O período já é da ocorrência (ADR-052), e a conclusão também.

### Lixeira fora, arquivo dentro

Arquivar é guardar. O que foi feito naquela semana continua tendo sido feito,
mesmo que o arquivamento automático (ADR-020) já tenha recolhido a tarefa. A
lixeira é o que o usuário mandou embora, e fica de fora. Reabrir apaga o
`CompletedAt` (`TaskOccurrence.Reopen`), e a conclusão desfeita sai do histórico junto: o
histórico mostra o estado, e não um log de eventos.

### Duas idas ao banco

`ActivityHistoryQuery` faz as conclusões numa consulta e os períodos noutra,
cada uma com o título num join. Nunca há uma consulta por tarefa. Um teste conta
os comandos com um `DbCommandInterceptor` e outro lê um ano de dados. O recorte
exato por dia é do domínio. O SQL só filtra o intervalo, com o intervalo
semiaberto e os índices.

### Abrir a tarefa de dias atrás

A janela da tarefa nasce de uma linha do quadro (`TaskRowViewModel`), e uma
tarefa de três dias atrás já saiu do quadro. Por isso a montagem da linha do
`TodayQuery` (definição, etiquetas, agentes, worktrees e tempo) virou um
método compartilhado, e `FindOccurrenceAsync` busca uma ocorrência só, com a
mesma linha. `GetBoardTask` a projeta com a mesma regra do quadro. Fora de uma
seção, ela não se diz atrasada. A janela é a de sempre, deduplicada por tarefa,
e abre na aba **Tempo**. Concluída, ela abre só para leitura, como abriria pelo
quadro. Na lixeira, não abre.

### Tela

- **↺** (`E81C`, somado à fonte embutida, ADR-046) no cabeçalho normal e no do
  HUD, com a dica "Ver histórico dos últimos 7 dias". No **HUD compacto** ele
  sai: em 260 px, o contador "0/2" virava "0,".
- Um `Flyout`, e não uma janela: o checklist continua à vista atrás, e o clique
  fora fecha. O painel carrega a cada abertura, porque o que se quer ver é o
  agora.
- **Esc** fecha. O flyout não faz isso sozinho, e abrir o flyout não tira o foco
  da janela. Por isso a `MainWindow` trata o Esc na descida, antes da caixa de
  captura, mas só enquanto o histórico está aberto. Com o foco dentro do painel,
  quem trata é o próprio painel.
- O dia leva o cabeçalho das seções ("HOJE · TER 06/10", "ONTEM · SEG 05/10",
  "DOM · 04/10"), com a abreviação do `DeadlineFormatter`. Cada linha mostra ✓
  ou ○, o título e o detalhe. Uma concluída sem tempo lançado mostra
  "Concluída 14:31", e não "· 0min": a falta de cronômetro não prova que não
  houve trabalho.
- No rodapé fica "18 concluídas · 24h 35min", ou "Nenhuma atividade
  registrada". As cores vêm todas de tokens (ADR-041).

### Fora de escopo, de propósito

Gráficos, dashboard, calendário, produtividade, ranking, metas, exportação,
filtros, histórico ilimitado e comparação entre semanas. O número de dias é um
parâmetro do caso de uso (`GetActivityHistory(Days)`), e o painel pede sempre
`Week`. Oferecer 14 ou 30 dias é trocar o que a tela pede, e não a consulta.

**Testes:**
- Domínio (`ActivityHistoryBuilderTests`): os sete dias, o oitavo de fora, a
  conclusão no dia local, a tarefa longa dia a dia, a soma no mesmo dia, a
  meia-noite partida, o cronômetro até agora, a ocorrência de cada dia e o
  relógio que voltou.
- Application: a janela no fuso, inclusive no dia de 25 horas de Nova York;
  rótulos, detalhes e resumo; e `GetBoardTask`.
- Infrastructure, com SQLite de verdade: as bordas do intervalo, a lixeira e o
  arquivo, o período que começou antes e o que corre, dois comandos para 50
  tarefas, um ano de dados e a busca de uma ocorrência.
- Desktop, headless: o botão nos dois cabeçalhos e fora do HUD compacto, os
  sete dias, ✓ e ○, o estado vazio, ×, Esc com o foco na janela e no painel, o
  clique abrindo a ocorrência certa e as cores de cada tema.

## ADR-054 — Post-it: o que ainda não merece virar tarefa

**Contexto:** a captura rápida (ADR-013) transforma toda linha em tarefa para
hoje. Isso é certo para "comprar pão" e errado para "falar com o João sobre a
API", "ideia de cache" ou "perguntar sobre homologação": coisas que se quer
ver, e não marcar como feitas. Elas acabavam no checklist, contando como
pendência, ou não eram registradas.

**Decisão:** `StickyNote` é um agregado próprio (`Domain/StickyNotes`), uma
janela por post-it, e a única ponte com as tarefas é "Transformar em tarefa".

### Entidade própria, e não um `TaskItem` sem data

O ADR-007 escolheu "Inbox é estado, não entidade", porque o item do inbox **é**
uma tarefa ainda não triada. O post-it não é: não tem ocorrência, prioridade,
prazo, lembrete nem conclusão, e não pode aparecer em ATRASADAS, AGORA, HOJE,
PRAZOS ou CONCLUÍDAS. Como `TaskItem`, cada consulta do quadro ganharia um
"menos os post-its", e cada caso de uso de tarefa uma recusa para eles. A
conversão é explícita, e por isso a entidade é outra.

**Três grupos de estado, separados:**

- **Conteúdo:** texto, etiqueta (uma só), cor, destaque e opacidade.
- **Janela:** aberta, fixada, posição e tamanho.
- **Ciclo de vida:** `ArchivedAt` e `DeletedAt` independentes, com o estado
  derivado (`StickyNoteLifecycle`), igual ao ADR-020. Arquivado e na lixeira
  são somente leitura. Restaurar da lixeira devolve ao arquivo quem estava lá.

**A geometria mora no banco, e não no `widget.json`.** O ADR-017 pôs a posição
do painel num arquivo porque ela "não é dado do usuário". Aqui são N janelas
que nascem e morrem com a linha. Um arquivo à parte teria de ficar em sincronia
com o banco a cada criação, conversão e expurgo, e um post-it apagado deixaria
uma posição órfã. A convenção é a do widget: posição em pixels físicos e tamanho
em DIP.

**Sem trilha de auditoria.** `TaskAuditEntries` existe para investigar o que o
usuário não desfaz sozinho num checklist. Um post-it apagado não deixa pergunta
a responder depois, e uma linha por post-it descartado afogaria a trilha. A
conversão grava o `Created` da tarefa, que já está na trilha.

**`ConvertedTaskId` não tem chave estrangeira**, pela mesma razão da auditoria
do ADR-020: a tarefa pode ser expurgada, e a referência continua dizendo o que
aconteceu.

### Fechar é esconder, e em branco é descartar

O X, o Esc, o Ctrl+W e o Alt+F4 passam pelo mesmo caminho: gravar o texto,
gravar a posição e só então marcar a janela como fechada. Encerrar o app não
marca nada como fechado, porque um post-it fixado tem de voltar.

**Um post-it em branco é apagado ao fechar.** "Novo Post-it" seguido de X é um
arrependimento, e uma lista cheia de "Post-it vazio" seria o oposto de captura
sem burocracia. É a única exclusão sem lixeira, e só vale para o que não tem
nada escrito. Quem decide é o agregado (`EnsurePermanentDeletionIsAllowed`).

**Excluir não pergunta; excluir definitivamente pergunta.** Mandar para a
lixeira se desfaz na aba Lixeira, e o prazo é o mesmo dos checklists, na mesma
varredura (`RunLifecycleMaintenance`), com a mesma reconferência em memória.

### Só os fixados reabrem

Na subida do app voltam para a tela os post-its **ativos, abertos e fixados**,
e sem roubar o foco. Um post-it solto que ficou aberto continua na lista. Reabrir
todos encheria a área de trabalho de quem faz login, e fixar já é a forma de
dizer "quero ver isto sempre".

### Uma janela por post-it, e nenhum laço

`StickyNoteWindowManager` guarda `Guid → janela`. Nunca há duas janelas do mesmo
post-it, pela mesma razão do dicionário de anotações do `App`: duas telas do
mesmo texto teriam duas versões dele. Não há processo, timer ou consulta por
post-it:

- o texto grava numa pausa de 500 ms da digitação;
- a posição grava 600 ms depois do fim do arrasto;
- os dois gravam na hora quando a janela perde o foco.

Os `DispatcherTimer` de cada janela só correm enquanto há algo pendente. As
gravações de um post-it passam por um semáforo. Sem ele, "fechar" logo depois
de digitar poderia chegar ao banco antes do texto e descartar como "em branco"
um post-it que estava sendo escrito.

**Sem botão na barra de tarefas** (`ShowInTaskbar=False`): cinco post-its não
podem virar cinco botões lá embaixo. O caminho de volta é a lista, no menu do
painel e na bandeja.

**Arrastar só pelo cabeçalho**, com `ElementRole="TitleBar"`, e os botões dentro
dele com `User` (armadilha nº 4 do ADR-017). O texto fica fora da área de
arrasto: selecionar com o mouse não move a janela. As oito alças de
redimensionar viraram o controle `ResizeGrips`, usado pelo widget e pelos
post-its, porque duas cópias em XAML divergiriam na primeira correção.

**Multi-monitor:** a posição restaurada passa pelo `WidgetPlacement.Clamp`. Um
post-it do monitor desligado volta para a tela que mais o cobre. Um post-it novo
nasce no canto superior direito da tela onde o widget está, em cascata.

### Fixar é ordem Z, de cada post-it

`Topmost` só daquele post-it, independente do "sempre no topo" da janela normal
e do HUD (ADR-048). Como no ADR-017, fixar não toca em posição nem tamanho, e há
teste para isso. Fixado, o alfinete fica sempre visível. É a indicação de que
aquele post-it não sai da frente.

### A cor é tinta sobre o tema, com contraste medido

O problema: o usuário escolhe "amarelo" e o app tem sete temas. Um retângulo
amarelo com a letra do tema por cima quebra o Carvão, e letra preta fixa quebra
o Alto contraste. `StickyNoteTint` (em C#, como a paleta do ADR-041) calcula o
desenho:

- **A cor é tinta sobre o cartão do tema.** A matiz entra 16% no escuro e 24% no
  claro. O modo **Atenção** soma 12% e põe uma faixa lateral de 3px.
- **A claridade volta para a faixa em que o texto passa.** A fórmula da WCAG,
  resolvida para o fundo, dá um teto de luminância com letra clara e um piso
  com letra escura, para os três níveis de texto (7:1 no principal, 4,5:1 nos
  de apoio). Sem isso, no Carvão a tinta clareava o fundo, a régua a cortava até
  quase nada, e o "laranja" saía cinza. Há teste que exige a tinta visível, e
  não só legível.
- **O texto é sempre o do tema.** Quem cede é a tinta, nunca a letra.
- **No Alto contraste não há tinta.** A cor vira borda de 2px, ajustada para 3:1
  contra o fundo.
- **Atenção sem cor própria usa o âmbar de atenção do tema**, a semântica fixa
  do ADR-041.

`StickyNoteTintTests` passa todos os temas × sem cor, as 7 cores da paleta, as
12 de etiqueta, branco e preto × Normal e Atenção pelos mesmos mínimos do
ADR-041. A conta de contraste saiu dos testes para `ColorContrast`, para a tela
e o teste usarem a mesma. Como a cor é calculada, um `DynamicResource` não a
alcança: `ThemeController.Changed` avisa os post-its abertos para repintar.

**Cor da etiqueta:** é lida a cada carga, como no ADR-025. Recolorir a etiqueta
repinta quem usa a cor dela (a janela Etiquetas avisa o gerenciador). Com uma
cor própria escolhida, a etiqueta não muda a cor do post-it. Excluir a etiqueta
põe `TagId` em nulo (`SET NULL`), e o post-it volta à cor do tema em vez de
desenhar uma cor que não existe.

**Opacidade** (100/90/80/70) vai só no pincel do fundo, com o piso de 70% do
HUD. O texto nunca fica translúcido.

### Transformar em tarefa sem perguntar nada

`StickyNoteTaskDraft` é pura. A primeira linha não vazia é o título, cortado em
200 caracteres na palavra. O texto inteiro vai para a anotação quando sobrou
algo fora do título. A tarefa nasce **para hoje, sem hora**, pela razão do
ADR-013: ela tem de aparecer na tela que existe. A etiqueta vem junto. O post-it
inteiro convertido vai para os **arquivados**, com `ConvertedTaskId`. Tudo sai
num `SaveChanges` só.

**`TaskCreation` é o caminho único de criar tarefa uma a uma:** lembrete
padrão, armar, registrar e auditar. `CreateTask` e a conversão passam por ele.
Duas cópias divergiriam no dia em que criar tarefa ganhasse uma regra, e o
post-it convertido nasceria sem ela.

**Criar tarefa com a seleção** converte só o trecho, e o post-it fica como
está. A seleção só é lida com o foco no texto. Clicar no ⋯ tira o foco, e a
seleção que importa é a de antes do clique.

**A lista age pela janela quando o post-it está aberto.** Arquivar, converter ou
fixar pela lista um post-it aberto passa pelo `StickyNoteViewModel` dele, que
grava o texto pendente antes. Chamar o caso de uso direto perderia a última
frase.

### Atalho: Ctrl+Alt+N global, ligado por padrão

Ctrl+Shift+N era o pedido, e é o atalho **dentro** do app (painel e post-it).
Como atalho **global**, ele roubaria "nova janela" do VS Code, "janela anônima"
do navegador e "nova pasta" do Explorer. Ctrl+Alt+N não é de nenhum desses. Ao
contrário do Ctrl+Shift+Espaço do ADR-048, ele nasce **ligado**, inclusive para
quem atualiza (o `widget.json` antigo não tem a chave). Em
*Janela e comportamento…* dá para escolher Ctrl+Alt+N, Ctrl+Shift+N ou Desligado.
Se outro programa já for dono da combinação, a escolha volta para Desligado e a
tela diz o motivo.

`WindowsGlobalHotkey` passou a ser uma instância por combinação
(`HotkeyGesture`), cada uma com seu id de `RegisterHotKey`. O `WM_HOTKEY` diz
qual disparou, e o gancho de cada uma só responde ao seu. A dona dos dois
registros continua sendo a `MainWindow`, cuja janela existe mesmo escondida na
bandeja. O `WM_HOTKEY` dá ao processo o direito de trazer uma janela para a
frente, e por isso o post-it abre focado mesmo com outro programa em uso.

### Fora de escopo, de propósito

- **"Lembrar depois".** O post-it não é lembrete. A arquitetura não impede: seria
  um `RemindAt` no agregado e um passo no tique do `ReminderScheduler`, no molde
  do ADR-050.
- **Markdown e links clicáveis.** O editor é um `TextBox` simples. Captura
  rápida não precisa da barra de formatação do ADR-038, e o texto cru vai inteiro
  para a anotação da tarefa, que já é Markdown.
- **Busca na lista**, até a quantidade justificar.
- **Ficar mais discreto parado.** O pedido de "esmaecer quando fixado e sem
  foco" virou só esconder os controles: os botões aparecem com o mouse ou com o
  foco no post-it, e o alfinete fica quando fixado. Esmaecer o texto contraria
  a régua de contraste acima.

**Limites aceitos:**

- sem botão na barra de tarefas, um post-it solto atrás de outras janelas só
  volta pela lista ou pelo Alt+Tab;
- encerrar o app pela bandeja logo depois de digitar, sem a janela perder o
  foco, pode perder os últimos 500 ms de texto. Na prática o clique na bandeja já
  tirou o foco, e isso gravou o texto.

**Testes:**

- Domínio: o agregado inteiro, o rascunho da tarefa e a paleta.
- Application: um teste por caso de uso, a conversão (título, anotação,
  etiqueta, seleção, recusa sem gravar nada) e o expurgo pela varredura.
- Infrastructure, com SQLite de verdade: ida e volta de todos os campos, posição
  e tamanho, a etiqueta recolorida e a excluída, as três listas, o corte da
  prévia no SQL, quem reabre na subida, a conversão de ponta a ponta até o quadro
  de hoje e o upgrade a partir de `TimeEntries`.
- Desktop, headless:
  - papéis de arrasto e alças, e o pino só como ordem Z;
  - o X que não exclui, o Alt+F4 pelo mesmo caminho, a pausa da digitação e o
    foco no texto;
  - uma janela por post-it, a subida só com os fixados, a troca de tema
    repintando e a etiqueta recolorida chegando;
  - a lista agindo pela janela aberta;
  - o atalho registrado, trocado, recusado e solto;
  - o menu da bandeja.

---

## ADR-055 — Comando só do diretório: o botão sem passar por Comandos globais

**Contexto:** no ADR-051, todo botão "▶" do worktree nasce de um comando
global, que depois é ligado ao diretório da etiqueta. Para um comando que só faz
sentido num repositório (`npm run dev` do front, o `docker compose up` de um
serviço só), isso obrigava a cadastrar um global, inventar um `@apelido` que
ninguém vai chamar, e conviver com ele na janela de globais, no autocomplete
dos pós-Worktree e no "+ Adicionar comando" de todos os outros diretórios. O
pedido: manter os globais, e permitir criar o comando direto no diretório.

**Decisão:** o comando só do diretório é o mesmo `DevelopmentCommand`, com um
dono: `TagDirectoryId`. `null` é global, como sempre foi.

| | Global (ADR-028/051) | Só do diretório |
|---|---|---|
| Onde se cria | *Comandos globais…* | *Etiquetas… → Comandos → + Novo comando* |
| Apelido | obrigatório e único | nenhum |
| Nome | opcional (cai no apelido) | obrigatório: é o rótulo do botão |
| `@alias` nos pós-Worktree e "Testar" | sim | não |
| "+ Executar comando…" do card | sim | não: fora do botão, ele não existe |
| Em outros diretórios | sim, com personalização | não |
| Personalizar | override no diretório | edita-se o próprio comando |
| Tirar do diretório (×) | o global continua | exclui o comando, depois de perguntar |

### Um comando, e não um segundo cadastro

Uma tabela própria repetiria o que o comando já tem: modo, pasta, terminal,
confirmação e a definição tipada dos parâmetros (com a tabela de parâmetros
junto), e o catálogo, a preparação, a execução, o "um por vez" e o histórico
teriam de aprender um segundo tipo. Com o dono no próprio comando, tudo isso
fica igual: a associação (`TagDirectoryCommand`) continua dando a ordem e o
ligar/desligar, e a execução continua apontando para um `DevelopmentCommandId`.

- `Alias` passou a ser anulável. O índice único continua: o SQLite não toma
  NULLs repetidos por duplicata. O global ainda o exige (`Create`/`Update`); o do
  diretório nasce sem (`CreateForDirectory`/`UpdateForDirectory`). Cada um
  recusa a edição do outro tipo, com a janela onde ele se edita.
- `IDevelopmentCommandRepository.ListAsync` passou a trazer **só os globais**:
  é o que a janela de globais, o `@alias` e o avulso do card leem.
  `ListForDirectoriesAsync` traz os do diretório.
- O catálogo (`QuickCommandCatalog.Entries`) recebe os globais **e** os comandos
  dos diretórios que casaram com o repositório, e não distingue os dois: a
  associação diz qual é qual. A busca de um botão (`QuickCommandTargets.Find`)
  só cai no avulso para um global.
- `AddTagDirectoryCommand` recusa oferecer o comando de um diretório em outro.

### O dono é o diretório, e não a etiqueta

Pelo mesmo motivo do ADR-051: a etiqueta pode ter vários repositórios. A chave
estrangeira `DevelopmentCommands.TagDirectoryId → TagDirectories` é em cascata:
excluir o diretório, ou a etiqueta, leva os comandos dele (e os parâmetros, pela
cascata que já existia). O histórico perde o vínculo e fica, como na exclusão
de um global. Tirar o botão do diretório (`RemoveTagDirectoryCommand`) exclui
o comando junto. Ele não tem outro lugar onde aparecer, e um comando sem botão
seria lixo invisível.

### Banco

Migration `DirectoryOnlyCommands`: `Alias` anulável e a coluna `TagDirectoryId`,
com índice e chave estrangeira. O SQLite não altera coluna, e o EF reconstrói a
tabela. Um teste sobe de `StickyNotes` com global, parâmetro, botão e histórico,
e confere que nada se solta. O `Down` exclui antes os comandos de diretório,
escrito à mão: sem apelido, eles voltariam com `""` e esbarrariam no índice
único.

### Tela

- **Etiquetas… → Comandos:** o seletor de globais virou "+ Adicionar global", e
  embaixo vem "+ Novo comando", com a dica do que ele faz (ou, sem global a
  oferecer, de onde cadastrar um). O comando do diretório mostra "Só deste
  diretório" e **Editar** no lugar de **Personalizar**. O × dele pergunta antes,
  porque exclui.
- **O diálogo** (`DirectoryCommandWindow`, porta `IDirectoryCommandEditor`) é o
  formulário de Comandos globais sem o apelido: nome, comando, tipo, pasta,
  terminal, confirmação, parâmetros e descrição. Segue o molde do ADR-052: o
  caso de uso grava, e a recusa aparece com o diálogo aberto. A sincronização
  das linhas de parâmetro e a dica das variáveis passaram para
  `DevelopmentCommandParameterEditorViewModel`, usadas pelos dois formulários.
- Excluir um diretório com comandos avisa que os comandos criados só nele saem
  junto.

### Fora de escopo, de propósito

- **Converter** um comando do diretório em global, ou o contrário. Hoje é
  recriar; o dono não muda depois de criado.
- **Comando só da etiqueta**, valendo para todos os diretórios dela: o caso que
  motivou o ADR-051 (`@eco-web` × `@ecossistema-core`) mostra por que a unidade
  é o diretório.
- **Testar** pela janela de etiquetas: o botão no worktree já é o teste.

**Testes:** domínio (dono, sem apelido, nome obrigatório, atomicidade e as
recusas cruzadas de edição), Application (criar no fim da lista, recusa sem
nome e em diretório que não existe sem deixar comando para trás, fora dos
globais, editar e ler, excluir ao tirar do diretório, recusa em outro diretório,
botão no worktree rodando na pasta certa, sem avulso), Infrastructure (ida e
volta sem apelido, dois sem apelido no índice único, cascata do diretório e da
etiqueta, histórico solto e o upgrade a partir de `StickyNotes`) e Desktop (o
diálogo, criar, recusa com o diálogo aberto, cancelar, editar, excluir
perguntando e o global sem perguntar, e os bindings headless da lista e do
diálogo).

## ADR-056 — Operações de banco: cópia de produção só por dump anônimo, e uma política que a tela não contorna

**Contexto:** o pedido foi gerenciar conexões PostgreSQL, copiar bancos entre
ambientes e anonimizar dados sensíveis no caminho. A trava era firme:
**produção nunca sofre escrita**, nem `INSERT`/`UPDATE`/`DELETE`, DDL, restore,
`createdb`/`dropdb`, SQL livre ou mascaramento estático. Desenvolvimento,
Teste e Homologação nunca são copiados de volta. E esconder um botão não conta
como proteção.

**Decisão:** uma feature inteira nas camadas de sempre, com a segurança no
Domain e repetida até a porta do processo.

| Camada | O que entrou |
|---|---|
| Domain | `DatabaseConnection` (sem senha), `EnvironmentPolicy`, `IDatabaseSecurityPolicy`, perfis, `DatabaseOperationAudit`, `SensitiveText`, `SecretText`, `MaskingScriptBuilder` |
| Application | portas estreitas (ferramentas, servidor, Anonymizer, dump, restore, cofre, diretório), os casos de uso, o diagnóstico e o fluxo Copiar + Anonimizar |
| Infrastructure | `PostgresToolLocator`, `PgToolRunner` + `PostgresProcessGuard`, Npgsql somente leitura, `PostgresCredentialStore`, `DatabaseOperationWorkspace`, `SecretToolSecretStore` |
| Desktop | *☰ → Bancos de Dados…*: Conexões, Diagnóstico, Copiar Banco, Perfis, Histórico |

O guia de uso, instalação e configuração está em
[`database-operations.md`](database-operations.md).

### Só dump anônimo, por role mascarada

> **Substituído pelo ADR-058:** a anonimização passou a ser feita na própria
> consulta, sem o PostgreSQL Anonymizer nem nada instalado na origem. O texto
> abaixo fica como registro da decisão original.

O PostgreSQL Anonymizer 2.x tem *transparent dynamic masking*: um usuário
marcado `MASKED` lê as colunas já mascaradas. O `pg_dump` feito por ele grava
dados anonimizados, com `--no-security-labels --exclude-extension=anon` para
o destino não herdar as regras. **O dado bruto de produção nunca chega ao
disco desta máquina**, e o app não altera produção.

- **As regras ficam no servidor, e quem as aplica é o DBA.** O app sugere
  colunas, guarda o perfil confirmado, gera o script `SECURITY LABEL` e confere
  o servidor contra o perfil (`pg_seclabel`, que tem o mesmo formato no 1.x e
  no 2.x).
- **Duas conexões para o mesmo banco.** A normal é a origem e serve para
  inspecionar e verificar. A mascarada é a do perfil e faz o dump. A política
  exige o mesmo host, porta e banco nas duas.
- **O canário, antes de qualquer dump.** As mesmas linhas são lidas pelas duas
  conexões, como hashes salgados. Se vierem iguais, a máscara não está ativa
  para aquela role ("rótulo posto, TDM desligado") e a cópia para sem ter
  criado arquivo nenhum.
- **O arquivo é conferido.** O `pg_restore --list` não pode ter `SECURITY LABEL`
  nem a extensão anon, e precisa ter dados.

**Alternativa rejeitada:** um dump bruto num diretório protegido, restaurado
num banco temporário do servidor de destino, anonimizado ali com
`anon.anonymize_database()` e copiado de novo. Funciona sem o Anonymizer em
produção, mas o dado bruto existiria em disco e num segundo servidor. Era
muito mais código para o caminho que o pedido manda evitar quando há
alternativa. O Anonymizer 1.x é detectado e recusado com instruções.

### Política central, e quatro barreiras

`IDatabaseSecurityPolicy` fica no Domain, como lógica pura. `Evaluate` devolve
**todas** as violações, para o [Validar] listar de uma vez. `Demand` lança
`DatabaseSecurityException`, que é um `DomainException` e chega à tela como
mensagem. Ela é chamada em quatro lugares:

1. **No caso de uso**, com as conexões lidas do banco do app. A recusa é
   auditada como *Blocked*.
2. **No fluxo de cópia**, de novo antes de `dropdb`, `createdb` e `pg_restore`.
3. **No `PostgresProcessGuard`**, dentro do `PgToolRunner`, logo antes do
   `IProcessRunner`. Ele não confia em quem chamou: recusa
   `pg_restore`/`createdb`/`dropdb` contra produção ou contra o endpoint de
   uma conexão de produção (um "Desenvolvimento" que aponta para o banco de
   produção é produção). Recusa também `psql` com qualquer coisa além de
   `--version` e `pg_dump` simples de origem que exige anonimização.
4. **No servidor:**
   - o Npgsql abre com `default_transaction_read_only=on`, e cada consulta
     roda numa transação `READ ONLY` com rollback;
   - o `pg_dump` recebe o mesmo por `PGOPTIONS`;
   - um teste confere que toda SQL do catálogo começa com `SELECT`/`WITH` e não
     tem verbo de escrita.

`StaticMasking` e `ExecuteSql` entraram no enum de operações sem
implementação: existem para a política recusá-los com nome. O app não tem
console SQL.

### O ambiente encaixa as permissões, e não recusa

Production tem exatamente o conjunto obrigatório: ler, fazer dump, ser origem
e exigir anonimização. Critical Production tem o mesmo conjunto e ainda:

- só alimenta Test e Staging;
- a verificação é obrigatória;
- não deixa manter o dump anônimo;
- pede o nome do banco digitado para confirmar;
- bloqueia colunas de alta probabilidade sem regra.

As permissões pedidas são cortadas pelo teto do ambiente e completadas pelo
piso, ao gravar e **a cada leitura** (`DatabaseConnection.Permissions`).

**Alternativa rejeitada:** lançar ao gravar "Produção com restore". A
segurança seria a mesma, mas mudar o ambiente para Produção falharia até
desmarcar cinco caixas. O encaixe na leitura ainda protege de uma linha
editada à mão no SQLite. A tela mostra o encaixe ao vivo e trava os bits que o
ambiente decide.

A confirmação de produção também é conferida no caso de uso
(`RunDatabaseCopy.ProductionConfirmed`): pular a janela não pula a confirmação.

### A senha não volta

- **A senha não sobe:** `IDatabaseCredentialStore` só guarda, confere e apaga.
  Quem lê é o `IPostgresPasswordReader`, interno da Infrastructure, na hora de
  abrir a conexão ou montar o ambiente do processo.
- **Cofre do sistema:** o `ISecretStore` do Jira (ADR-045), com o nome
  `postgres-<id>`. O registro saiu do grupo do Jira para valer aos dois.
  - No Linux entrou o `SecretToolSecretStore`: o valor vai só pela entrada
    padrão (o `ProcessRunner` ganhou `StandardInput`, em UTF-8 sem BOM).
  - Efeito colateral: o token do Jira também passa a ser guardado no Linux.
- **Comandos:** os que carregam senha usam `SecretText`, cujo `ToString()` é
  `***`. Records imprimem todos os membros.
- **Processo:**
  - a senha vai por `PGPASSWORD`, nunca em argumento;
  - as variáveis `PG*` herdadas saem;
  - `PGPASSFILE` aponta para o nada;
  - `--no-password`.
- **Máscara:** `SensitiveText.Mask` cobre a senha conhecida, credencial em URI,
  `password=`/`token`/`secret`/JSON e `Authorization`. Ela roda em cada linha
  das ferramentas e dentro da entidade de auditoria.
- **Erros do servidor:** do Npgsql, só `MessageText` e `SqlState`. O `Detail`
  traz valor de linha.
- **Nomes:** banco, servidor ou usuário começando com `-` é recusado (viraria
  opção). Nome de banco com `=` ou em formato de URL é recusado (o libpq lê
  como connection string). Servidor com vírgula também (lista de hosts).

### Processos: a mesma porta, com saída ao vivo

O `ProcessRunner` continua sendo a única porta para processos (ADR-027).
`IProcessRunner` ganhou uma sobrecarga com `IProgress<CommandOutputLine>`,
com implementação padrão para os dublês existentes. O pump saiu do
`ShellCommandExecutor` para ser compartilhado. O caminho sem streaming
continua com `ReadToEndAsync`, porque o parser do Git lê o texto cru. Um valor
`null` no ambiente agora **remove** a variável herdada.

O `--verbose` das ferramentas sai pelo stderr. Só é pintado como erro o que diz
que é. O progresso lê "dumping contents of table", "processing data for table"
e "creating INDEX", com `LC_MESSAGES=C`. A fração do dump e do restore é pesada
pelo tamanho de cada tabela, nunca volta e não passa de 99% antes do fim.

### Diretório isolado, na pasta local

`%LOCALAPPDATA%\MyTaskApp\database-operations\operation-yyyyMMdd-HHmmss\{dump,anonymized,logs,metadata.json}`.

- **Pasta local, e não o `Root` do ADR-018:** o AppData *Roaming* de um perfil
  de domínio sincroniza com um servidor no logoff. `UserDataLocation` ganhou
  `LocalRoot`, que respeita `MYTASKAPP_DATA_DIR`.
- **Permissões:** no Linux as pastas nascem com modo 700, sem janela entre
  criar e restringir.
- **No fim:**
  - o dump bruto e os logs saem sempre;
  - o anônimo sai, a não ser que o perfil peça para mantê-lo;
  - fica o `metadata.json`.
- **Varredura:** o `.operation` diz de quem é a pasta. A varredura roda na
  primeira abertura da janela Bancos de Dados (e não ao abrir o app), marca
  como interrompida a operação que uma queda deixou "em andamento" e apaga as
  pastas que sobraram, sem tocar numa cópia viva (o `DatabaseOperationGate`
  sabe quais estão vivas).

### Verificação sem trazer dado

A estrutura (schemas, tabelas, constraints por tipo, índices e sequences) e as
contagens são comparadas direto. Acima de um milhão de linhas estimadas, vale
a estimativa do catálogo.

As colunas com regra são comparadas por `md5(sal || chave)` →
`md5(sal || valor)`, com sal aleatório por execução, pareadas pela chave
primária:

- 100% iguais à origem: FAIL;
- mais da metade iguais: aviso.

Nenhum valor real, nem hash sem sal, chega ao app.

### Banco

Migration `DatabaseOperations`: só tabelas novas, sem coluna de senha (um teste
confere no `PRAGMA table_info`):

- `DatabaseConnections`, com as permissões numa coluna de bits;
- `AnonymizationProfiles` e `AnonymizationRules`, com as regras em cascata;
- `DatabaseCopyProfiles`, com `Restrict` para as conexões e o perfil;
- `DatabaseOperationAudits`, sem chave estrangeira, como o `TaskAuditEntry`,
  para a trilha sobreviver à conexão excluída.

Um teste sobe de `DirectoryOnlyCommands` com dados e confere que nada se solta.

### Tela

- **Uma janela, cinco abas, um ViewModel por aba.** O ViewModel da janela só
  carrega e redistribui o cadastro.
- **Copiar Banco:**
  - [Executar] só acende depois de [Validar] a mesma seleção;
  - a confirmação "ATENÇÃO" usa o texto do pedido, e o `ConfirmationRequest`
    ganhou `RequiredText` para a produção crítica;
  - os logs reusam o `CommandOutputView`.
- **Perfis:** as sugestões de colunas chegam desmarcadas. Só o que o usuário
  confirma vira regra.
- **Fechar o app** cancela a cópia em andamento, para a limpeza rodar.

### Fora de escopo, de propósito

- **Console SQL**, e mascaramento estático em qualquer ambiente.
- **O fallback com dump bruto**, rejeitado acima.
- **Restore a partir de arquivo pela tela**, e agendamento de cópias.
- **Anonymizer 1.x**, detectado e recusado.
- **PostgreSQL real no CI.** Os testes de integração rodam só com
  `MYTASKAPP_TEST_POSTGRES` apontando para um servidor descartável.

**Testes:**

- **Domínio:**
  - a política, com a matriz completa de ambientes e cada regra do pedido:
    produção nunca é destino nem alterada, Prod → Dev/Test/Staging só com
    anonimização, Dev/Test/Staging → Prod proibido, Critical mais restrita,
    endpoint disfarçado, bancos do sistema;
  - o encaixe de permissões, as entidades, a máscara, o `SecretText` e o script
    com nomes hostis.
- **Application:**
  - o fluxo inteiro com fakes: ordem e percentual das etapas, recusa antes de
    qualquer processo com auditoria *Blocked*, confirmação, canário, arquivo
    com regras, falha, timeout, código de saída, cancelamento, gate de
    concorrência, verificação com FAIL, artefato mantido, senha fora da
    auditoria;
  - os cadastros: a senha no cofre e só a referência no banco, nenhum DTO com
    senha;
  - o diagnóstico, as sugestões e a compatibilidade de versões.
- **Infrastructure:**
  - processo: `ProcessRunner` com entrada sem BOM, saída ao vivo e variável
    removida;
  - segredos: `secret-tool` e o cofre das senhas;
  - ferramentas: os argumentos exatos sem senha, o ambiente, a guarda, a saída
    mascarada e o localizador com disco falso;
  - leitura: os parsers, o catálogo só leitura e os inspetores com sessão
    falsa;
  - os diretórios, a persistência e o upgrade;
  - um teste opcional contra PostgreSQL real.
- **Desktop:** as permissões travadas, a senha limpa, [Executar] só depois de
  validar, o texto exato da confirmação, o nome digitado, o progresso, o erro,
  o histórico e a janela headless.

## ADR-057 — Conexão por servidor, banco escolhido na cópia, e apelidos

**Contexto:** no ADR-056, cada conexão apontava para um banco só. Copiar
`eco_core_1010` e depois `eco_core_2020` do mesmo servidor pedia duas
conexões, com a mesma senha guardada duas vezes. O pedido foi:

- cadastrar a conexão sem o banco;
- escolher o banco na hora de copiar, cada um com a sua anonimização;
- guardar a escolha com um **apelido** (`lock_eco_core_1010`);
- carregar o dump num banco novo, `lock_eco_core_1010_yyyyMMdd_HHmmss`, para
  saber de quando é cada cópia.

**Decisão:** o banco passa a ser opcional na conexão e é **resolvido no
plano**. Daqui para a frente, nada vê uma conexão sem banco.

### Resolver no plano, e não em cada etapa

O `DatabaseCopyPlanner` já lia as conexões do banco do app e montava os
snapshots que a política, a guarda e as ferramentas recebem. Agora ele também
completa o banco de cada lado, com `snapshot.WithDatabase(...)`:

| Lado | Com banco fixo | Só servidor |
|---|---|---|
| Origem | o da conexão; outro banco no pedido é recusado | o `SourceDatabase` do pedido (do apelido, da lista do servidor ou digitado) |
| Conexão mascarada | a do perfil, e a política confere que é o mesmo banco | herda o banco da origem |
| Destino | o da conexão, como antes | um banco novo: `{apelido ou banco de origem}_{yyyyMMdd_HHmmss}`, na hora local do `TimeProvider` |

Por isso política, guarda, `PgArguments`, auditoria e verificação quase não
mudaram. `EndpointKey`, `SameEndpoint` e "a mascarada é o mesmo banco da
origem" continuam comparando bancos concretos. Os `SECURITY LABEL` são por
banco, então a validação contra o `pg_seclabel` acontece no banco escolhido.

O horário do nome é fixado a cada `PlanAsync`. O [Validar] mostra um nome, e
o [Executar] gera o seu, alguns segundos depois. A tela mostra só o padrão
(`lock_eco_core_1010_aaaaMMdd_HHmmss`), e o nome real vai no resultado e no
Histórico.

### Banco novo: só cria, nunca apaga

Um destino com nome gerado não existe, então não há o que apagar.

- `PrepareDestination` roda só `createdb --template=template0`.
- A política pede `CreateDatabase` e **não** pede `DropDatabase`. Para isso, a
  `DatabaseOperationRequest` ganhou `NewDestinationDatabase`.
- "Recriar o destino" some da tela nesse caso.
- A confirmação de produção diz "um banco novo será criado" e deixa de ser
  irreversível.

### Produção sem banco protege o servidor inteiro

A chave de uma conexão sem banco é `host:porta/*`. Uma conexão de produção
cadastrada só com o servidor protege **todos os bancos dele**:
`DatabaseConnectionSnapshot.IsAmong` aceita a chave exata ou o curinga.

- A política (`DestinationMatchesProtectedEndpoint`) e o
  `PostgresProcessGuard` usam o mesmo método.
- Um banco chamado `*` é recusado, para não colidir com o curinga.

### Sem banco, nada roda

O libpq, sem `--dbname`, conecta no banco com o nome do usuário, que seria um
alvo que a política não julgou. Uma conexão sem banco é recusada em três
pontos, com `DatabaseNotChosen`:

- na política: cópia, restore, `createdb` e `dropdb`;
- na guarda do processo;
- em `PgArguments.DatabaseOf`, que a `NpgsqlPostgresSessionFactory` também usa.

As leituras que fazem sentido sem banco vão ao banco de manutenção
(`postgres`):

- **testar a conexão:** versão e privilégios, sem os schemas, que seriam os do
  `postgres`;
- **listar os bancos:** `pg_database` sem templates e sem o próprio
  `postgres`, numa consulta que também passa pelo teste "só leitura".

### O apelido é a origem, e não a cópia inteira

`SavedDatabase` guarda conexão, banco e perfil de anonimização. O destino fica
de fora: o mesmo `lock_eco_core_1010` vai para a máquina local hoje e para
Homologação amanhã.

- O alias é o prefixo do nome do banco copiado, então segue um identificador
  simples: minúsculas, `[a-z0-9_]`, sem começar por dígito.
- O limite é de 47 caracteres, porque 63 (NAMEDATALEN − 1) menos os 16 de
  `_yyyyMMdd_HHmmss` dão 47.
- Ao salvar, o app confere que a conexão pode ser origem e que a anonimização
  lê o mesmo servidor (e o mesmo banco, se a mascarada tiver um fixo).
- O resto (produção, destino, Anonymizer no servidor) é da política, a cada
  cópia.
- O plano recebe o `SavedDatabaseId` só para nomear o destino. Se a origem ou
  o banco do pedido não baterem com os do apelido, recusa: a tela mudou um
  campo depois de escolher.

**Alternativa rejeitada:** pôr o banco no perfil de anonimização. O vínculo
"este banco usa esta anonimização" é justamente o apelido. No perfil, a mesma
regra LGPD teria de ser duplicada por banco.

### Banco

Migration `SavedDatabases`:

- `DatabaseConnections.Database` vira opcional. No SQLite, a tabela é
  recriada, e um teste sobe de `DatabaseOperations` com conexões e perfis e
  confere os bancos e o `PRAGMA foreign_key_check`.
- Tabela nova `SavedDatabases`, com índice único NOCASE no alias e `Restrict`
  para a conexão e o perfil. Excluir qualquer um dos dois avisa antes.
- `DatabaseCopyProfiles.SourceDatabase`, para os perfis de cópia também
  apontarem um banco num servidor.
- `DatabaseOperationAudits.SourceDatabase` e `DestinationDatabase`. O
  Histórico mostra `ECO Servidor/eco_core_1010 → Local/lock_eco_core_1010_…`.

### Tela

- **Conexões:** "Banco (opcional)". Sem ele, o teste diz "conectado ao
  servidor; o banco é escolhido na cópia".
- **Copiar Banco:**
  - **Apelido** no topo;
  - **Banco** logo abaixo da origem, quando ela é só o servidor: um
    `AutoCompleteBox` com a lista do servidor, que aceita digitar se a lista
    não vier;
  - a prévia do nome abaixo do destino;
  - **Salvar como** + [Salvar apelido]. Com um apelido escolhido, atualiza
    aquele, e é assim que se renomeia.
- **Perfis:** a lista de apelidos com Usar/Excluir. Também aparece um campo
  "Banco" quando a conexão mascarada (ou a origem do perfil de cópia) é só o
  servidor, para sugerir colunas, gerar o script e validar.

### Fora de escopo, de propósito

- **Limpar as cópias antigas.** Cada cópia cria um banco, e os anteriores
  ficam no destino. Apagar é com o usuário, por enquanto.
- **Apagar o banco novo quando a cópia falha.** Ele fica com o nome da
  tentativa, para inspecionar. Nenhum `dropdb` roda sem pedido explícito.

**Testes:**

- **Domínio:**
  - conexão sem banco, `WithDatabase` e o curinga;
  - a política: banco não escolhido, servidor de produção protegido,
    `createdb` sem `dropdb`;
  - o apelido: formato, limite de 47 e o nome com data, prefixo limpo e corte;
  - perfil de cópia e auditoria com os bancos.
- **Application:**
  - a resolução no plano, a hora local e o apelido que não bate;
  - a validação com o nome novo e o fluxo que só cria;
  - `createdb` exigido sem `dropdb`, e a confirmação crítica pelo banco
    escolhido;
  - a lista do servidor e os handlers do apelido;
  - exclusões barradas pelo apelido, e os perfis com banco informado.
- **Infrastructure:**
  - a guarda e os argumentos sem banco;
  - o servidor de produção na guarda, e a sessão que não abre sem banco;
  - o teste e a lista pelo banco de manutenção;
  - persistência e upgrade.
- **Desktop:**
  - a lista e o banco digitado;
  - o apelido que preenche e se solta, e salvar o apelido;
  - a prévia, a confirmação e o resultado com o banco;
  - os apelidos em Perfis e a janela headless.

## ADR-058 — Anonimizar na consulta, sem instalar nada no banco de origem

**Contexto:** o ADR-056 anonimizava pelo PostgreSQL Anonymizer 2.x. O DBA
instalava a extensão no banco de produção, marcava um usuário `MASKED` e
rodava o script `SECURITY LABEL`; o `pg_dump` feito por esse usuário saía
mascarado. Na prática, o banco de produção do usuário não tinha a extensão, e
instalar algo em produção não é uma opção. Sem ela, a cópia anonimizada não
rodava.

**Decisão:** a máscara vai **na própria consulta**, montada pelo app, com
funções nativas do PostgreSQL. O modo Anonymizer sai do app.

### O fluxo

1. **Validar as máscaras.** O app lê só o catálogo da origem: tabelas com
   linhas próprias (`relkind 'r'`, partições incluídas, com a tabela
   particionada de cima), colunas com tipo, `NOT NULL` e se são geradas, as
   chaves (PK, índice único, FK nos dois sentidos) e os objetos grandes. O
   `MaskingPlanner` julga cada regra e monta o plano de cada tabela.
2. **A estrutura, da mesma foto.** Uma transação `REPEATABLE READ READ ONLY`
   na origem exporta o snapshot (`pg_export_snapshot()`), e o `pg_dump
   --schema-only --snapshot=…` lê exatamente aquele instante. O arquivo tem
   zero `TABLE DATA` — o app confere antes de restaurar.
3. **Pre-data, dados, post-data.** `pg_restore --section=pre-data` cria as
   tabelas sem índices nem chaves. Cada tabela vai por
   `COPY (SELECT … FROM ONLY t) TO STDOUT` na transação da origem e
   `COPY t (…) FROM STDIN` no destino, repassado em blocos, sem interpretar as
   linhas. Depois, `--section=post-data` cria índices, constraints, FKs e
   triggers, e cada sequence recebe o `last_value` da origem (`setval`).
4. **Verificar.** Estrutura e contagem como antes; nas colunas mascaradas, as
   variáveis não podem bater com a origem (hash salgado, como no ADR-056) e as
   fixas precisam estar em **todas** as linhas do destino.

**O que continua garantido:** o dado real não sai do servidor de origem. O
valor é trocado dentro do `SELECT`; não chega ao disco nem à memória do app. E
o app continua não escrevendo nada em produção: a sessão é só leitura duas
vezes (`default_transaction_read_only` e `SET TRANSACTION READ ONLY`).

### Um catálogo, e não SQL livre

As máscaras são um enum (`MaskingMethod`) que o `MaskedSelectBuilder` traduz:
hash, e-mail falso, parcial, nome falso, texto fixo, número fixo, vazio, data
deslocada e ruído numérico. Só `md5`, `left`, `right`, `repeat`, `CASE`,
`round`, aritmética e `CAST`.

- **SQL livre rodaria em produção.** Mesmo numa sessão só leitura, uma
  expressão do usuário poderia chamar função com efeito fora do banco, travar
  tabela ou ler o que não devia. O catálogo cobre os casos de LGPD sem esse
  risco. A exceção, pedida depois, é a condição das linhas sem máscara
  (ADR-080), com as proteções de lá. O script depois da cópia (ADR-082) é SQL
  livre também, mas só no destino, nunca na origem.
- **Nenhum argumento vira SQL cru.** Números são relidos em C#; texto passa por
  `SqlQuoting.QuoteLiteral`; nomes por `QuoteIdentifier`; o tipo do `CAST`
  vem do `format_type` do próprio servidor.
- **Determinístico.** As máscaras variáveis derivam do `md5` do valor: o mesmo
  e-mail vira sempre o mesmo falso, então junções por e-mail continuam batendo,
  e um índice único continua único no hash e no e-mail falso.
- Um teste confere que nenhum SELECT gerado tem palavra de escrita nem `anon.`.

### O que a validação recusa

- **Chave (PK ou FK).** Mascarar o id quebraria as ligações entre as tabelas
  — ou as FKs, criadas depois dos dados. Quem identifica a pessoa é o CPF ou o
  e-mail.
- **Índice único com máscara que repete** (parcial, texto fixo, nome falso): o
  índice falharia no post-data.
- **Tipo errado**, **vazio em `NOT NULL`**, **coluna gerada** (o destino a
  recalcula das colunas já mascaradas) e **coluna que sumiu**.

Os problemas entram na política como `MaskingRulesInvalid`, junto com as
colunas de alta probabilidade sem regra (que bloqueiam em Produção crítica,
como antes).

### A guarda

- **Estrutura pode sair de quem exige anonimização.** A operação nova
  `SchemaDump` é permitida; a guarda do processo não confia na flag: um
  `pg_dump` de origem protegida só passa com `--schema-only` nos argumentos e
  sem `--data-only` nem `--section`. O dump com dados continua recusado.
- **A escrita do COPY não é um processo**, mas passa pelo mesmo julgamento de
  um `pg_restore` (`PostgresProcessGuard.CheckDestination`): produção, ou um
  banco de servidor de produção, nunca recebe nada.
- O snapshot vai como argumento do `pg_dump`: só no formato que o PostgreSQL
  gera.

### Perfis e banco

- O perfil perde `PolicyName`; `ConnectionId` passa a ser só a conexão de onde
  ler as colunas para sugerir e pré-visualizar. O "Gerar script" e o "Validar
  no servidor" saem; entra **Pré-visualizar**: o mesmo SELECT com `LIMIT 5`,
  só das colunas mascaradas.
- Migration `QueryMasking`: `AnonymizationRules.Kind`/`Expression` viram
  `Method`/`Argument`, convertidos por SQL — `partial_email` → E-mail falso,
  `partial(c,a,…,b)` → Parcial(a,b), `dummy_*name` → Nome falso,
  `random_date` → Data ±365, `noise(c,p)` → Ruído p×100, `NULL` → Vazio,
  literal → texto ou número fixo, o resto → Hash. A validação da próxima cópia
  aponta o que não couber no tipo. `PolicyName` sai.
- O apelido (ADR-057) não confere mais se a anonimização "lê o mesmo
  servidor": as regras são julgadas contra a origem de verdade, a cada cópia.

### Tabelas sem dados

Pedido depois da primeira versão: escolher, no perfil, tabelas que vão sem
linhas — logs, auditoria, filas. Ficou no **perfil de anonimização**, e não no
de cópia, porque é ele que pertence a um banco (pelo apelido) e já lê a lista
de tabelas da origem; o de cópia liga duas conexões, sem saber que tabelas há.

- `AnonymizationProfile.SkippedTables` (tabela `AnonymizationSkippedTables`,
  migration `SkippedTables`), substituída inteira ao salvar, como as regras.
- O `MaskingPlanner` marca `SkipData` na tabela (e nas partições de uma
  particionada); a cópia não roda o COPY dela. A estrutura vem do pre-data e
  as chaves do post-data, como as outras.
- **FK de uma tabela com dados para uma sem dados é recusada** antes de tudo
  (o catálogo agora lê `ForeignKeyTables`): o post-data falharia.
- Uma coluna sensível numa tabela sem dados conta como coberta — o dado não
  sai —, inclusive para a regra de Produção crítica.
- A verificação tira essas tabelas (e as particionadas de cima delas) da
  comparação de linhas e confere que chegaram **vazias**.

**Alternativa rejeitada:** a quarentena no destino — `pg_dump` com dados,
restaurado num banco temporário do destino, anonimizado ali e renomeado. É a
mesma alternativa que o ADR-056 recusou: o dado real existiria em disco e num
segundo servidor, mesmo que por pouco tempo.

### Qual conjunto de ferramentas

Achado no teste real: o `PostgresToolLocator` preferia a pasta do `pg_dump`
mais novo, e o pgAdmin 18 ao lado de um PostgreSQL 14 virava o escolhido. O
`pg_restore` 17+ manda `SET transaction_timeout = 0`, que um servidor 16 ou
mais antigo recusa — a cópia comum e a mascarada paravam no restore.

- O localizador guarda **todos** os conjuntos (`PostgresClientTools.Sets`, um
  por pasta com `pg_dump`). O diagnóstico geral continua mostrando o mais novo.
- `ForSource(versão da origem)` escolhe o **menor conjunto que lê a origem**.
  A cópia valida com ele, e `PgDumpRequest`/`PgRestoreRequest` levam a versão
  da origem até o `PgToolRunner`, que roda o dump e o restore do mesmo conjunto.
- `PostgresCompatibility` ganhou a regra "`pg_restore` 17+ só para destino
  17+": se só houver ferramentas novas, o [Validar] e a cópia param antes do
  dump, dizendo qual versão instalar.

**Fora de escopo, de propósito:** cópia em paralelo (as tabelas vão uma por
vez, numa transação só; o `--jobs` vale só para a cópia comum), objetos
grandes (avisados e deixados de fora) e máscaras novas sem passar pelo
catálogo.

**Testes:**

- **Domínio:** o catálogo (tipos, argumentos, únicos), as regras do perfil e a
  política (`SchemaDump`, `MaskedDataCopy`, `MaskingRulesInvalid`).
- **Application:** o `MaskingPlanner` (partição, chave, único, NOT NULL,
  gerada, coluna que sumiu, sem regra), o fluxo inteiro na ordem — catálogo →
  snapshot → estrutura → pre-data → COPY por tabela → post-data → sequences →
  fechar —, falha e cancelamento no meio do COPY, dump de estrutura com dados,
  só dados, a pré-visualização e a verificação das fixas.
- **Infrastructure:** o SQL exato de cada máscara, nomes hostis, argumentos
  que não são número, nenhuma escrita no SELECT; os argumentos com snapshot e
  section; a guarda (`--schema-only` obrigatório, destino julgado); o catálogo
  e a pré-visualização com sessão falsa; a migration com regras antigas; e um
  teste opcional contra PostgreSQL real com partição, coluna gerada, FK, índice
  único e sequence.
- **Desktop:** a máscara e o parâmetro por linha, a pré-visualização e o perfil
  sem política nem script.

## ADR-059 — Servidor MCP local: Kestrel só no loopback, e os casos de uso da tela

**Contexto:** o usuário quer que um cliente de IA (o Claude Code) opere o app
pelo [MCP](https://modelcontextprotocol.io): tarefas, cronômetro e horas,
conexões de banco e, com prioridade, os perfis de anonimização. O guia de uso,
com todas as ferramentas, está em [`mcp-server.md`](mcp-server.md).

**Decisão:** um servidor MCP **dentro do processo do app**, com o SDK oficial
(`ModelContextProtocol.AspNetCore` 2.2, spec 2026-07-28), transporte
Streamable HTTP **sem sessão**, em `http://127.0.0.1:{porta}/mcp`, sempre com
token. As ferramentas chamam os mesmos casos de uso que a tela chama.

### Por que Kestrel agora, se o ADR-037 o recusou

O ADR-037 escreveu um HTTP mínimo sobre `TcpListener` porque o Kestrel
"traria o ASP.NET inteiro para um app de bandeja". Lá era um endpoint de uma
rota, que só recebe avisos. O MCP é um protocolo inteiro:

- JSON-RPC e negociação de versão;
- Streamable HTTP com SSE e cabeçalhos padronizados;
- uma spec que mudou três vezes em um ano.

Escrever isso à mão seria manter um segundo SDK. O oficial roda sobre ASP.NET
Core. O custo é o `FrameworkReference Microsoft.AspNetCore.App` no projeto
`MyTaskApp.Mcp` e um instalador maior, porque a publicação é self-contained e
sem trimming.

O ADR-037 **não muda**: a porta dos hooks continua no `TcpListener`.

### Projeto próprio, e o contêiner do app

- `src/MyTaskApp.Mcp` conhece a Application e o Domain, e nada do Desktop. O
  Desktop o referencia e só liga, desliga e mostra.
- O Kestrel tem o próprio contêiner, montado com
  `WebApplication.CreateEmptyBuilder`. Não lê `appsettings`, variáveis
  `ASPNETCORE_*` nem URLs de fora: o único endereço é o
  `Listen(IPAddress.Loopback, porta)` do código.
- Não existe configuração de "escutar na rede", e é de propósito: seria a
  configuração acidental que o pedido proíbe.
- Esse contêiner **não cria casos de uso**. Recebe do app, como instâncias, o
  `McpGateway` (que tem o `IUseCaseRunner`), o relógio e o log. Cada
  ferramenta roda no escopo do app, com o `DbContext` do app, como um clique
  na tela (ADR-012). Não há segundo `DbContext` concorrente.

### O gateway, e nenhuma regra no MCP

Toda ferramenta passa pelo `McpGateway`, que não tem regra de negócio. Ele:

- recusa escrita no modo **somente leitura**;
- serializa as chamadas MCP (`SemaphoreSlim(1)`), porque o SQLite do app não
  tem WAL e a tela também grava. Correção posterior: o banco sempre esteve em
  WAL, porque o EF Core cria o arquivo assim (o `-wal` existe ao lado do
  `mytaskapp.db` de quem usa o app). A abertura agora também o garante
  (`DatabaseInitializer`), para um arquivo que tenha chegado em outro modo. A
  serialização fica: o WAL deixa ler durante uma gravação, mas continua
  aceitando um escritor por vez;
- liga `OperationOrigin.Enter("MCP")`, um `AsyncLocal` que o `ICurrentUser`
  lê. Assim a auditoria grava "adria (MCP)" sem mudar a assinatura de nenhum
  caso de uso;
- traduz `DomainException` na frase da regra (erro da ferramenta), e qualquer
  outra exceção num código curto, com a pilha só no log;
- avisa o `IDataChangeNotifier` depois de gravar. O `App` recarrega o quadro, a
  aba "Tempo" das janelas abertas e a janela de Bancos, e o que a IA cria
  aparece na hora, sem esperar o tique de 60 s.

### O que faltava, e entrou na camada certa

O que não existia virou caso de uso na Application, sem lógica paralela:

- **Busca de tarefas** (`SearchTasksHandler` + `TaskSearchQuery`). O quadro
  Hoje não lista pendentes sem data nem futuras. A linha devolvida é a mesma
  do quadro, montada pelo `TodayQuery.DescribeAsync` e pelo
  `GetTodayBoardHandler.Describe`, e o atraso usa o mesmo `TodayClassifier`.
- **Edição em lote** (`EditTaskHandler`, `CreateDetailedTaskHandler`). A tela
  edita cada coisa no seu card; a IA manda tudo junto. O handler chama os
  casos de uso da tela (`UpdateTask`, `UpdateTaskPlan`, `SetDeadline`…)
  dentro de `IUnitOfWork.ExecuteInTransactionAsync`, então um prazo recusado
  desfaz o título já trocado. A transação tem uma implementação padrão que só
  executa, para os dublês de teste continuarem valendo.
- **Relatórios de horas** (`TimeReportHandlers` + `TimeEntryReportQuery`): por
  intervalo de dias locais, tarefa, etiqueta, issue e origem, com recorte na
  meia-noite do usuário e os ids de cada total.
- **Perfis de anonimização por partes** (`ChangeAnonymizationProfileHandler`):
  - incluir, trocar ou remover uma regra preserva tudo o que não foi citado;
  - a proposta passa pelas validações do domínio num rascunho, e o handler
    devolve o diff;
  - `Apply=false` é a pré-visualização. Para gravar, `ExpectedUpdatedAt` é
    obrigatório e recusa uma leitura velha;
  - a validação contra o banco é o mesmo `MaskingPlanner` da cópia, em sessão
    só leitura;
  - a análise separa o comprovado, o risco estático e o que falta saber, e
    nunca afirma conformidade com a LGPD.
- **Correção encontrada no caminho:** nome repetido de perfil de anonimização
  estourava como `DbUpdateException`, pelo índice NOCASE. Agora é uma
  `DomainException` com a frase, na tela e no MCP.

### Segurança

HTTP no loopback não é autenticação: qualquer processo e qualquer página do
navegador alcançam `127.0.0.1`. São quatro barreiras antes do SDK, na
`McpSecurityMiddleware`:

1. a conexão vem do loopback;
2. o `Host` é `127.0.0.1` ou `localhost` na porta do servidor, contra DNS
   rebinding;
3. qualquer `Origin` é recusado, e não há CORS;
4. o `Authorization: Bearer` traz o token. São 256 bits, guardados no cofre do
   sistema e comparados pelo hash em tempo constante, como no ADR-037.

O corpo da requisição vai até 1 MB. Senhas não entram nem saem: criar e editar
uma conexão pelo MCP não aceita senha, e a resposta diz só `hasPassword`. Um
teste varre por reflexão tudo o que as ferramentas devolvem, atrás de
propriedade com cara de segredo.

A revisão achou mais quatro pontos, e todos entraram:

- **A senha segue a conexão.** O `SaveDatabaseConnection` mantém a senha ao
  editar, e o teste de conexão a usa. Trocar o host pelo MCP e testar mandaria
  a senha para outro servidor. Por isso, numa conexão com senha guardada,
  servidor, porta, usuário e SSL só mudam pela tela, e o ambiente só fica igual
  ou mais restrito.
- **Porta exclusiva.** O socket do Kestrel é criado com `SO_EXCLUSIVEADDRUSE`
  no Windows, para nenhum processo escutar junto com `SO_REUSEADDR`.
- **Enxurrada.** São até 64 conexões, e as recusas entram no log e na tela no
  máximo uma vez por segundo.
- **Prazo e tela.** O `AlertPresenter.DismissAsync` não espera a thread da tela
  quando é chamado de fora dela. Antes, o MCP segurava a transação do SQLite
  esperando a tela, enquanto a tela esperava o banco.

### Ciclo de vida

- O `McpServerManager` é singleton do app, com a máquina de estados
  Parado → Iniciando → Ativo | Erro → Encerrando.
- Um `SemaphoreSlim` impede start e stop concorrentes. A tela mostra o estado
  real, e não a configuração.
- Sobe fora da thread da tela. Junto com o app, só se estiver habilitado e com
  "iniciar com o app".
- Parar é gracioso por até 3 s. O `Dispose` síncrono espera esse limite,
  porque o contêiner do app descarta de forma síncrona (o mesmo cuidado dos
  agendadores, ADR-015).
- A configuração (habilitado, porta 5180, iniciar com o app, somente leitura) é
  dado do usuário e mora numa linha única no banco (migration
  `McpServerSettings`, só aditiva). Instalação nova e atualização chegam
  **desligadas**.

### Fora de escopo, de propósito

- **Executar comandos, SQL, cópia ou restore pelo MCP.** Os comandos
  cadastrados só se listam. Executar processos reais pede uma política de
  confirmação própria, que fica para outra decisão. A única exceção é a
  leitura do banco numa sessão da Consulta Inteligente, com segredo próprio e
  um usuário que só lê (ADR-081).
- **Senha pela conversa.** Ela ficaria no histórico do cliente de IA.
- **Exclusão definitiva.** `task_delete` manda para a lixeira.
- **stdio, SSE legado, sessões e notificações do servidor.** O modo stateless
  basta para ferramentas, e parar não deixa nada pendurado.
- **Escrita em etiquetas, diretórios, comandos, post-its e lembretes
  globais.** Fica na tela até haver pedido.

**Testes:**

- **`MyTaskApp.Mcp.Tests`:** o Kestrel de verdade numa porta livre, o cliente
  oficial, e a Application e a Infrastructure reais sobre SQLite temporário.
  Cobre o ciclo de vida, a porta, as quatro barreiras, o protocolo e cada área
  de ferramentas, inclusive a ausência de segredo nas respostas e nos logs.
- **Application:** configuração, token, origem, relatórios e edição de perfis,
  com fakes.
- **Infrastructure:** store, upgrade a partir de `SkippedTables`, transação,
  busca e relatório.
- **Desktop:** a tela com gerente falso, a janela headless, a bandeja e o
  composition root real.

## ADR-060 — Banco do ambiente: a cópia na aba Desenvolvimento e o `{database}` dos comandos

**Contexto:** a cópia de banco (ADR-056/057/058) só existia na janela Bancos
de Dados. O usuário quer:

- copiar o banco da própria aba Desenvolvimento;
- já ao criar o worktree, para o ambiente nascer pronto, com o seu banco;
- passar o nome criado aos comandos seguintes — `configura-ambiente {nomeDoBanco}`.

O nome é dinâmico: `{prefixo}_aaaaMMdd_HHmmss` (ADR-057), conhecido só no fim
da cópia.

**Decisão:** o ambiente (`TaskDevelopment`) guarda o banco da última cópia, e
os comandos o recebem em variáveis de contexto: `{database}` (=
`{database.name}`), `{database.host}`, `{database.port}` e `{database.user}`.

### O retrato, e não a conexão

`TaskDevelopmentDatabase` é owned e opcional em `TaskDevelopments` (migration
`DevelopmentDatabase`, só aditiva). Guarda nome, host, porta, usuário, o nome
e o id da conexão de destino, a auditoria e quando. É uma **cópia**, pela mesma
razão dos caminhos no ADR-027: a conexão pode ser editada ou apagada, e o banco
continua onde foi criado. O id só sugere o destino de "Copiar de novo".

- **A última cópia vence.** Copiar de novo troca o retrato; o banco anterior
  fica no servidor.
- **Nunca apaga.** Remover o worktree não apaga o banco, e a confirmação diz
  qual banco fica. O retrato continua no ambiente removido. Tentar de novo no
  mesmo ambiente (outro `git worktree add`) começa sem banco.
- **Nunca a senha.** Ela fica no cofre, com a conexão. Não há
  `{database.password}`: quem monta connection string usa o pgpass ou a
  variável de ambiente que já usa.

### A ordem: validar, Git, copiar, comandos

1. "Iniciar implementação" confere os comandos e, com "Copiar banco para este
   ambiente" marcado, **valida a cópia e pede a confirmação de produção antes
   do Git** (`ValidateDevelopmentDatabaseCopyHandler`, que também recusa com
   outra operação de banco em andamento). Recusar não cria nada.
2. O worktree é criado como sempre, e o painel já fica Pronto: o worktree
   serve sem o banco.
3. A cópia roda (`CopyDevelopmentDatabaseHandler`), com barra, etapa, Cancelar
   e logs no card do banco, dentro do card Pronto.
4. Só com a cópia bem-sucedida os comandos pós-Worktree rodam, já com
   `{database}`. Se ela falha ou é cancelada, nada é desfeito, e a mensagem
   manda copiar de novo e depois "Executar comandos".

O caso de uso só envolve o `RunDatabaseCopyHandler`. Política, confirmação
digitada de produção crítica, a porta de uma operação por vez e auditoria
ficam onde estão. Ele confere o ambiente antes da cópia e grava o nome gerado
depois. Gravar é com `CancellationToken.None`, e sem a guarda da lista
principal, porque o banco já existe no servidor.

### O que copiar

A escolha usa os cadastros da janela Bancos de Dados, lidos só quando ela abre:

- **Apelido + servidor.** O destino só pode ser uma conexão **sem banco
  fixo**, fora de produção e ativa. Cada ambiente ganha um banco novo, sem
  `dropdb`. A anonimização vem do apelido e pode ser trocada. Não recria o
  destino nem guarda o dump.
- **Perfil de cópia.** As opções são as do perfil. Se o destino tem banco
  fixo e "Recriar o destino", a prévia avisa que ele será apagado.
  - Com a origem só de servidor, o banco vem do perfil e pode ser trocado,
    como na aba Copiar Banco.
  - "Salvar como" grava origem, banco e anonimização como apelido
    (`SaveSavedDatabaseHandler`), para repetir pelo modo apelido.
  - Havendo apelido para a origem e o banco escolhidos, o pedido leva o
    `SavedDatabaseId`, e o banco novo ganha o nome do apelido.

Não há cópia padrão por diretório: o usuário escolhe a cada ambiente.

### As variáveis do banco

- **Sem banco copiado, a variável é recusada com o motivo.** O
  `CommandContext` ganhou um "indisponível com motivo" ("este ambiente não tem
  banco copiado…"). O comando para antes do shell, inclusive na forma com
  ponto. Isso também fechou uma brecha antiga: `{repository.name}` sem contexto
  ia cru ao shell, porque `CommandParameters.Names` não enxerga ponto. Agora
  falta como qualquer parâmetro.
- **No valor de um parâmetro.** `@configura-ambiente nomebanco={database}`
  passa o banco a um comando pronto que já tem parâmetro. O valor explícito
  tem as variáveis de contexto preenchidas antes de entrar no comando, ainda
  numa passada só. Na janela de parâmetros do comando rápido vale o mesmo,
  inclusive como valor padrão: `{database}`.
- **Na entrada literal.** `configura-ambiente {database}` direto na lista
  pós-Worktree funciona, porque a entrada literal passou a ter só as variáveis
  de contexto preenchidas (era intocada no ADR-051). Parâmetro do usuário,
  `{ $_ }`, `HEAD@{1}` e `${env:X}` seguem como escritos: não são nome de
  variável.
- **Antes do worktree.** `ValidateCommandEntries` ganhou `DatabaseScheduled`.
  Com a cópia marcada, `{database…}` fica adiado como `{worktree}`. Sem ela, é
  recusado já no "Iniciar implementação".
- **"Testar"**, na janela de globais, não tem ambiente. A mensagem sugere
  passar `database=meu_banco`.
- **Compatibilidade.** Um global antigo com um parâmetro `{database}` deixa de
  pedir o valor na janela do comando rápido, porque o nome passou a ser de
  contexto. `@x database=valor` continua vencendo, como todo valor explícito.
- **A dica da lista pós-Worktree** não pede mais `worktree=…` nem
  `database=…`: as variáveis de contexto ficam no texto, como na execução.

### `{apelido}` e o autocompletar com `{`

O nome do apelido de banco também é variável: `{lock_eco_core_1010}` vale o
banco copiado quando a cópia do ambiente veio daquele apelido. O retrato do
banco guarda o apelido (`Database_Alias`, migration `DevelopmentDatabaseAlias`).
Continua um banco por ambiente.

- **Variável de runtime.** Os apelidos só existem no cadastro, então o
  `CommandContext` ganhou `DatabaseAliasVariable`: nomes fora da lista fixa,
  com valor, adiados ou indisponíveis com motivo.
  - O apelido da cópia vale o nome do banco, mesmo que tenha saído do cadastro.
  - Os outros dizem de onde o banco veio ("veio do apelido X", "não veio de
    um apelido", "não tem banco copiado").
  - Uma variável fixa com o mesmo nome (`database`, `branch`…) vence.
  - O resolvedor passou a olhar `context.Knows(nome)`, e não só a lista fixa.
    Por isso o apelido tem a mesma conferência de motivo e de caractere
    perigoso, na entrada literal, no `@global` e no valor explícito.
- **Nome de apelido nunca é parâmetro.** O editor de comandos globais grava
  uma definição para todo `{nome}`, então "o parâmetro declarado vence" não
  funcionaria.
  - `ParametersOf` exclui os apelidos, e o comando rápido não pede o valor.
  - O editor não cria a linha de parâmetro.
  - Só o `nome=valor` explícito vence.
  - Um parâmetro antigo com o nome de um apelido criado depois passa a ser
    preenchido pelo ambiente.
- **Antes do worktree,** `ValidateCommandEntries` recebe o apelido da cópia
  marcada (o apelido escolhido, ou o do perfil, se existir). Ele fica adiado
  como `{database}`, e os outros são recusados com o motivo.
- **O `{`.** `AliasCompletion.FindToken` ganhou o gatilho.
  - O `{` vale em qualquer lugar da linha, menos depois de `$` e `@`
    (`${env:X}`, `HEAD@{1}`).
  - Aceitar engole o `}` que já estava depois do cursor.
  - A fonte é `PlaceholderCompletionViewModel`, com a lista de
    `CommandPlaceholders`: os apelidos primeiro, depois as variáveis.
  - Na lista pós-Worktree é um segundo popup na mesma caixa do `@`. Na
    janela de parâmetros é o `ParameterValueBox`, com o valor de cada
    variável neste ambiente.

### Fechar a janela e o app

- A janela da tarefa **não fecha** com cópia em andamento. Ela mostra a aba e
  pede para aguardar ou cancelar. Não cancela sozinha, porque cancelar deixa o
  destino pela metade: é a regra da criação do worktree, e não a dos comandos.
- Fechando o app, `DatabaseCopyRuns` cancela as cópias da aba, como a janela
  Bancos de Dados já fazia com a dela. A limpeza do diretório temporário roda
  no caminho de volta.

O MCP (`worktree_list`) mostra o nome e o servidor do banco de cada ambiente,
sem o usuário. Continua sem copiar (ADR-059).

**Testes:**

- **Domain:** Ready obrigatório, a última vence, remover mantém, tentar de
  novo limpa, arquivada grava o que terminou, validações.
- **Application:**
  - o handler sobre o cenário de cópia real com fakes: nome gerado, falha que
    mantém o anterior, produção sem confirmação, porta ocupada;
  - contexto e resolvedor: indisponível com motivo, valor explícito, variável
    no valor e na entrada literal;
  - a validação com e sem cópia e o comando rápido.
- **Infrastructure:** ida e volta do owned e a troca pela próxima cópia.
- **Desktop:**
  - a ordem validar → Git → copiar → comandos;
  - recusar a confirmação não cria nada;
  - a falha da cópia segura os comandos;
  - a escolha (destinos, apelido, perfil);
  - a janela headless.
- **MCP:** `worktree_list` com o banco e sem o usuário.

## ADR-061 — Sincronização opcional: outbox local, mescla por campo no servidor

**Contexto:** o usuário quer as mesmas tarefas em vários computadores, sem
perder o que o app é hoje: local, rápido e inteiro sem rede. As exigências:

- a sincronização é **opcional**: sem conta, nada muda e nada é gravado;
- nada de senha, token ou connection string sai do computador;
- caminho de pasta de um computador nunca sobrescreve o de outro;
- login pelo Google, sem segredo dentro do executável do desktop;
- dispositivos com apelido e revogação, e isolamento entre contas.

O guia de uso e o fluxo estão em [`sync.md`](sync.md). O servidor, a
configuração e a implantação estão em [`sync-server.md`](sync-server.md).

**Decisão:** um servidor próprio (ASP.NET Core + PostgreSQL), opcional. No
desktop, um **outbox** gravado na mesma transação da mudança, e um ciclo
push → pull por polling. O servidor é a autoridade da ordem: ele **mescla por
campo** e decide o conflito pela versão dele, e não pelo relógio de ninguém.

### Alternativas

- **CRDT por campo** (LWW-register, OR-set). Converge sem servidor central, mas
  o "vencedor" seria o relógio de um computador, e o perdedor sumiria sem
  aviso. Pediria ainda metadados por campo em todas as tabelas do SQLite. O
  pedido é ver o que perdeu, e o servidor já existe para a conta.
- **Last-write-wins pela data de alteração.** Simples, mas o relógio do
  notebook atrasado ganharia ou perderia por acaso, e a edição de outro campo
  seria apagada junto.
- **Mescla arbitrada pelo servidor (escolhida).** O servidor guarda, por
  campo, a versão e o dispositivo da última escrita. Uma operação diz em que
  versão se baseou. Campo que ninguém mexeu depois dessa versão é aplicado.
  Campo que outro dispositivo mexeu volta recusado, com o valor do servidor, e
  vira conflito visível no dispositivo que perdeu. A sincronização nunca
  trava.
- **Banco compartilhado** (Litestream, réplica do SQLite, pasta na nuvem). Não
  separa o que é de cada máquina (caminhos, janelas, segredos) e corrompe com
  dois escritores.

### Classificação dos dados

Cada tipo sincronizado tem um mapa explícito de campos no
`MyTaskApp.Sync.Contracts`: nome, tipo, nulidade, tamanho, imutável, política
de conflito, referências e chave única. Do lado do desktop, cada coluna do EF
é **compartilhada** ou **só local**. Um teste percorre todas as colunas
mapeadas e falha com coluna sem classificação, inclusive em owned types.

Dado que nunca sai da máquina não está no mapa e, portanto, não tem como sair.
A tabela completa está em [`sync.md`](sync.md#o-que-sincroniza). As decisões
que mudam o domínio são mínimas:

- **`TagDirectory.Path` passa a ser anulável:** "não mapeado neste computador".
  A migration `TagDirectoryPathOptional` recria a tabela sem perder linhas.
- **`TaskDevelopmentStatus.NotCreatedHere`:** o ambiente veio de outro
  computador. "Iniciar implementação" o cria aqui.
- **`TaskItem.ReconcileDerivedState()`:** depois de uma mudança remota,
  recalcula a conclusão e o estado dos lembretes, sem regra paralela.

### Captura: interceptor, retrato e outbox

Nenhum caso de uso muda. Um `SaveChangesInterceptor` (`SyncCaptureInterceptor`)
funciona assim:

- só age com a instalação ligada a uma conta (`SyncCaptureSwitch`);
- usa o change tracker para achar as raízes tocadas. Cada raiz é projetada
  pelo mapa e comparada com o **retrato** guardado em `SyncEntityStates`. Isso
  resolve owned substituído e listas reaproveitadas por posição, que o change
  tracker sozinho descreve mal;
- grava a operação no `SyncOutbox` **no mesmo `SaveChanges`**, portanto na
  mesma transação, inclusive dentro de `ExecuteInTransactionAsync`;
- junta duas edições seguidas numa operação ainda não tentada;
- de uma exclusão, manda só a raiz. As cascatas são do servidor;
- não captura o cronômetro enquanto ele roda (`EndedAt == null`).

A origem da tarefa (quem criou, quando, quem mexeu por último) mora em
`SyncEntityStates`, e não nas tabelas de domínio.

### Aplicação remota: SQL pelos metadados do EF

O que chega do servidor é escrito pelo `SqlRowWriter`: SQL parametrizado,
montado a partir dos metadados do EF (nome de coluna e conversores de ticks,
enum, `DateOnly`). Esse caminho:

- não passa pelos guards do domínio, que recusariam estado legítimo vindo de
  outro computador (uma tarefa concluída lá, um alias já mudado);
- não precisa dos construtores privados;
- não passa pelo interceptor, então não gera operação de volta. Um
  `SyncSuppression` (`AsyncLocal`) é a segunda barreira;
- não tem as colunas locais no mapa, então é impossível escrever um caminho
  vindo de fora. Linha nova recebe os padrões locais: pasta nula, ambiente
  "não criado aqui", post-it fechado.

Cada página é uma transação SQLite que também avança o cursor. Se uma página
falha três vezes, ela é aplicada linha a linha, com savepoint, e a linha ruim
fica marcada para "Tentar de novo" via `/fetch`. Campo com operação local
pendente mantém o valor local.

### Servidor

- **Cursor denso.** O push trava a linha do workspace (`SELECT … FOR UPDATE`)
  e numera cada mudança com `LastSeq + 1`. O número confirma em ordem, e
  nenhum leitor pula uma transação lenta. O pull lê as entidades compactadas
  por `LastSeq`. O pai vai junto se ainda não foi entregue, e a tarefa nova
  vai com uma ocorrência. A página tem teto de entidades e de bytes
  (ADR-071). O cursor é opaco, e um cursor à frente do servidor
  (backup restaurado) recebe 410, e o cliente recomeça do zero.
- **Idempotência.** `ProcessedOperations` guarda o resultado de cada `OpId`,
  com o hash do pedido. O reenvio devolve o mesmo resultado como duplicado, e o
  mesmo `OpId` com outro conteúdo é recusado.
- **Exclusão não ressuscita.** Excluir vira tombstone, propagado pelas
  referências. Update em tombstone volta `entity_deleted`. Só associações
  (tarefa ↔ etiqueta, com id UUIDv5 determinístico) podem reviver.
- **Versão base do servidor (SEC-017).** A `baseVersion` decide o conflito de
  campo e o renascimento depois da exclusão (ADR-066), então só vale uma que
  o servidor deu: de 0 até a versão atual, tombstone incluído, e 0 para o que
  ele nunca viu. Fora disso, a operação, de qualquer tipo, volta
  `invalid_base_version` com a versão do servidor, e a recusa fica guardada
  como as outras permanentes. Sem a trava, uma versão inventada (10^12)
  venceria todo conflito e reviveria o que outro excluiu: inofensivo no
  workspace pessoal da v1, mas não com Editores num workspace compartilhado.
  O desktop sempre manda a versão que conhecia, que veio do servidor. A única
  exceção é um backup do servidor restaurado. Nesse caso, a recusa baixa a
  versão conhecida e a base das operações na fila para a que voltou, e a
  operação fica em "falhas": o "Tentar de novo" e as próximas edições saem
  com uma base válida. Uma futura poda de tombstones (ADR-072) precisa rever o
  "0 para o que ele nunca viu".
- **Isolamento.** Toda referência precisa existir **no mesmo workspace**. Quem
  não é membro recebe 404. O `device_id` da operação tem de ser o do token.
- **Um valor, um texto (SEC-014).** O push grava o que sai de
  `SyncFieldValidator.Normalize`: todo `Guid` em `D` minúsculo e todo
  `Instant` em UTC (`…fffffff'Z'`), também dentro de objeto e de item de lista,
  como o `SyncValue` escreve. Antes, o mesmo `tagId` em `N` ou `B` passava pela
  chave de unicidade como outro valor e criava duplicata que o banco local
  recusaria no pull. O `Instant` exige fuso explícito (`Z` ou `±hh:mm`); sem
  ele, cada lado leria um horário. O hash da idempotência continua sobre o
  pedido como veio. O que um servidor de antes gravou fora do formato fica
  como está: os clientes sempre mandaram o canônico.

### Autenticação

- O Identity só aceita login externo (Google, com `prompt=select_account`). O
  cookie dele vive 10 minutos e só serve para atravessar o
  `/connect/authorize`. **A API nunca aceita cookie**, só Bearer, então não há
  CSRF possível nela. O logout por formulário tem antiforgery, cujo cookie só
  anda por HTTPS fora dos testes. Por HTTP, o logout responde 400 dizendo que
  exige HTTPS. A volta do login (`returnUrl`) segue a regra do
  `LocalRedirect` e não aceita espaço. O que não é caminho deste servidor
  vira `/` (SEC-013).
- O OpenIddict é o servidor OAuth do desktop:
  - cliente público `mytaskapp-desktop`, sem secret;
  - authorization code com **PKCE S256 obrigatório** (o `plain` foi removido);
  - redirect em `127.0.0.1`, com a porta efêmera de cada login e o resto
    exato, e código sem pergunta só logo depois do login que o próprio pedido
    começou (ADR-074; antes, porta fixa 47833 e consentimento implícito);
  - access token de 15 min e refresh rotativo de 30 dias;
  - uma autorização por dispositivo, com a claim `device_id`, e validação da
    autorização **e do token** a cada chamada. Revogar o dispositivo recusa
    na hora toda chamada dele (403 `device_revoked`) e revoga o refresh.
- **Revogação de token (SEC-010).** Só com `EnableAuthorizationEntryValidation`,
  o `/connect/revoke` do access token respondia 200 e o token continuava
  aceito até vencer (15 min). Revogar o refresh também não o derrubava. E,
  comprovado por teste, nem a autorização revogada derrubava o access: sem a
  validação do token, a da autorização não recusava nada. Agora:
  - **`EnableTokenEntryValidation`** na validação: o access revogado leva 401
    na hora, e a autorização revogada também (entrar de novo no mesmo
    dispositivo derruba o access anterior). Custa uma busca por chave em
    `OpenIddictTokens` a cada chamada, além da que a autorização já fazia.
    Com o tráfego da sincronização (um ciclo por minuto por dispositivo,
    token bucket por usuário), não há cache próprio. Se pesar, o caminho é um
    cache curto, e a janela da revogação passa a ser o TTL dele.
  - **Revogar o dispositivo revoga só o refresh (e o código que sobrou).** A
    autorização e o access ficam: revogados, levariam 401, o app tentaria
    renovar, levaria `invalid_grant` e pediria "conecte de novo" sem saber
    que precisa de um id novo. Assim, o access segue chegando ao
    `DeviceFilter`, que responde 403 `device_revoked` na hora, e o app se
    registra como dispositivo novo. Era o que já valia de fato antes, já que
    a validação da autorização não recusava. O tipo gravado do access é
    `TokenTypeIdentifiers.AccessToken` (a URN), não o `TokenTypeHints`.
  - **O "sair" revoga o access e o refresh.** Mesmo com a validação nova, o
    OpenIddict revoga só o token pedido: revogar o refresh deixa o access de
    pé. O `SignOutAsync` (desktop e celular) revoga primeiro o access que
    está na memória, se ainda vale, e depois o refresh. Sem rede, os dois só
    deixam de existir aqui, como antes.

  Alternativa descartada: o "sair" revogar o dispositivo
  (`POST /api/v1/devices/{id}/revoke`). Derrubaria tudo de uma vez, mas o
  computador voltaria como dispositivo **novo** a cada saída (`device_revoked`
  e `Rotate`), com o antigo sobrando revogado na lista. E não resolveria o
  `/connect/revoke` para outros clientes (RFC 7009).
- O celular é um segundo cliente público, `mytaskapp-mobile`, com as mesmas
  regras e a volta pelo esquema do app em vez do loopback (ADR-067).
- **Issuer fixo (SEC-007).** Sem `SetIssuer`, o `Host` da requisição decidia
  três coisas:
  - o issuer e os endpoints do discovery (`Host: evil.example` publicava
    `http://evil.example/`);
  - o issuer que a validação aceita: o token emitido por `localhost:5280`
    levava 401 ID2088 no IP da LAN, na mesma porta;
  - o `redirect_uri` mandado ao Google.

  Agora:
  - **`Sync:Issuer`** (`https://<nome público>/`) vai no `SetIssuer`. A
    validação usa o servidor local e herda o mesmo issuer, então o token vale
    por qualquer endereço que chegue ao servidor.
  - **Fora de Development e dos testes, o servidor não sobe** sem o issuer,
    sem `https`, com `AllowedHosts` vazio ou `*`, ou sem o nome do issuer na
    lista. O compose de produção monta os dois a partir do `SYNC_HOST`.
  - **`AllowedHosts` barra o `Host` forjado com 400**, antes do OpenIddict e
    do Google. Ele cobre também o que o issuer fixo não cobre: o OpenIddict
    monta os endpoints do discovery sobre o endereço da requisição, e o
    handler do Google monta o `redirect_uri` com `Request.Scheme` e
    `Request.Host`. Com o nome público no `Host` e o `X-Forwarded-Proto` do
    Caddy (ADR-070), o `redirect_uri` é `https://<nome público>/signin-google`.
  - **Development aceita só `localhost`, `127.0.0.1` e `[::1]`.** Ele continua
    sem issuer fixo, porque `dotnet run` (5280) e o compose de desenvolvimento
    (8080) usam portas diferentes.

  Trocar o issuer invalida os tokens já emitidos, e todos entram de novo.
  Alternativa descartada: fixar também os endpoints do discovery no issuer,
  por um handler próprio do OpenIddict. Os clientes não leem o discovery, e
  com `AllowedHosts` restrito o `Host` forjado já não chega a ele.
- O token do Google nunca é token da API.
- O desktop guarda o refresh token no cofre do sistema e o access token só na
  memória. A renovação é serializada, e um 401 renova uma vez e tenta de novo.
  Um dispositivo revogado que tenta entrar recebe `device_revoked` e volta
  como dispositivo novo.
- **Teto de dispositivos e textos do dispositivo (SEC-015).** Cada
  `/connect/authorize` com um `device_id` novo criava um dispositivo, sem
  teto: 15 logins, 15 dispositivos. O apelido aceitava `U+202E`, e a
  plataforma e a versão, o ESC das sequências ANSI. Agora:
  - **`Sync:MaxActiveDevicesPerUser`** (padrão 20; zero desliga) conta os
    dispositivos não revogados da conta. O authorize de um dispositivo novo
    acima dele volta com `device_limit_reached`, e nada é gravado. Entrar de
    novo num que já existe não conta, e revogar um libera a vaga.
  - O desktop e o celular mostram o código com orientação: revogar, num
    computador já conectado (janela Sincronização), um dispositivo que não
    se usa mais. O aparelho recusado não tem token, então não lista nem
    revoga nada.
  - O teto é brando: dois authorizes simultâneos de dispositivos novos podem
    passar um do teto. Para uso pessoal, não vale uma trava no banco.
  - Apelido, plataforma e versão perdem os caracteres das categorias `Cc` e
    `Cf` (controles e formatação bidi), e só depois vale o tamanho: 60 no
    apelido (vazio é recusado na renomeação e vira "Dispositivo" no registro)
    e 40 cortados na plataforma e na versão. A plataforma com NUL deixou de
    dar 400 (SEC-008): o NUL sai do texto como os outros controles. O ZWJ
    também é `Cf`, então emojis compostos viram os emojis separados.
  - `Capabilities` do "olá", ignoradas na v1, aceitam até 16 itens de 1 a 64
    caracteres, sem `Cc`/`Cf`. Acima disso, 400 `bad_request`.
- Rate limit: login por IP, sincronização em token bucket por usuário, resto
  da API por usuário. O 429 traz `Retry-After`, que o agendador respeita como
  piso. O "por usuário" só passou a valer com o ADR-069: antes, tudo caía no
  IP.
- Logs sem token, código, query nem corpo, no servidor e no desktop. Um teste
  procura tokens no log do servidor.

### Comandos de outro computador

O texto de um comando é dado compartilhado, mas rodá-lo é deste computador. Um
comando global, do diretório ou do ambiente alterado por **outro**
dispositivo passa pelo `ICommandReviewGate` antes da primeira execução. Ele é
chamado pelos três caminhos de execução, e a janela de Sincronização mostra o
texto inteiro e a origem.

Executar comandos remotamente, terminais remotos e envio de arquivos ficam
fora de escopo.

### Desktop: agendador e tela

- O `SyncScheduler` roda 3 s depois de uma mudança local, a cada 60 s e na
  hora com "Sincronizar agora". É execução única. Falha passageira espera com
  *full jitter* entre 2 s e 5 min. Bloqueado (login vencido, dispositivo
  revogado, app velho), ele só acorda com um aviso.
- A janela "Sincronização…" mostra conta, estado, conflitos, falhas, comandos a
  revisar e dispositivos. A janela da tarefa mostra a origem.
- O `DeviceId` fica em `%LOCALAPPDATA%`, fora do banco: um banco copiado para
  outra máquina não se passa pela original.

### Consequências

- O desktop ganhou quatro tabelas (`SyncFoundation`, aditiva) e uma coluna
  anulável. Instalação sem conta não grava nada nelas.
- Toda coluna nova num tipo sincronizado exige decidir "vai ou fica", e o
  teste de classificação lembra.
- `ExecuteUpdate`/`ExecuteDelete` continuam proibidos fora de `Sync` e do
  servidor (`NoBulkWritesTests`): uma escrita em lote passaria por fora da
  captura.
- O servidor tem solution própria, `MyTaskApp.Server.slnx`, no mesmo
  repositório. `MyTaskApp.slnx` ficou só com o desktop. `Sync.Contracts` está
  nas duas. A do servidor lista Domain, Application e Infrastructure só porque
  o ponta a ponta sobe dois desktops reais. O CI e o `coverage.ps1` compilam e
  testam as duas.
- O CI ganhou o job `server-integration` no ubuntu, com PostgreSQL por
  Testcontainers. No Windows, esses testes pulam, e a cobertura do desktop vem
  dos testes de captura, aplicação, cliente e motor sobre SQLite.

**Testes:**

- **Contratos:** JSON, UUIDv5, coerência do registro e validação.
- **Server.Application:** mescla, push, pull e dispositivos, em memória.
- **Server.IntegrationTests:** PostgreSQL real, a API pelo HTTP (PKCE, tokens,
  revogação, isolamento, rate limit, logs) e o ponta a ponta com dois desktops
  inteiros: origem, mescla, conflito e reaplicação, exclusão sem ressurreição,
  resposta perdida e reinício, diretório por computador, instalação existente,
  revogação, token vencido no meio do ciclo e isolamento entre contas.
  `IssuerTests` (SEC-007) cobre o issuer fixo e o `AllowedHosts`:
  - `Host` forjado → 400;
  - issuer fixo mesmo com `AllowedHosts` aberto;
  - o token e o refresh valem por outro host, e sem issuer fixo levam 401;
  - em Development, só os nomes do loopback;
  - em Production, a recusa ao subir para cada configuração errada;
  - o `redirect_uri` do Google no nome público.

  Na revogação (SEC-010):
  - o access revogado leva 401 na hora;
  - revogar só o refresh deixa o access de pé (o motivo de o "sair" revogar
    os dois);
  - entrar de novo no mesmo dispositivo derruba o access anterior;
  - o dispositivo revogado leva 403 `device_revoked`, não 401, e o refresh
    dele `invalid_grant`.

  Na versão base (SEC-017), `PushProcessorTests` e `ApiTests` cobrem:
  - negativa, à frente da versão atual, para entidade que o servidor não tem
    e além do tombstone → `invalid_base_version`, sem mudar nada;
  - a versão inventada não vence o campo que outro dispositivo já mudou;
  - os casos legítimos de conflito e de renascimento seguem iguais.
- **Infrastructure:** captura, aplicação remota, cliente HTTP e motor contra um
  servidor roteirizado. O "sair" revoga access e refresh, e sem access na
  memória só o refresh. A recusa `invalid_base_version` de um servidor
  restaurado baixa a versão conhecida, e o reenvio sai com ela.
- **Desktop:** a janela, a linha de origem e o composition root real.

## ADR-062 — Cada computador com a sua lista: o "onde aparece" da tarefa

**Contexto:** com o ADR-061, tudo o que um computador cria aparece em todos.
Funciona, mas confunde o dia a dia de quem tem um computador do trabalho e um
pessoal: as tarefas dos dois ficam misturadas numa lista só. O usuário quer:

- cada computador com a sua lista;
- saber o que foi feito em cada um, separado;
- compartilhar uma tarefa entre os computadores só quando pedir, nunca
  automático;
- **mandar** uma tarefa para outro computador ("estou no trabalho e lembrei de
  algo para fazer em casa").

**Decisão:** a tarefa e a etiqueta ganham um **lugar**: só num computador da
conta, ou em todos. Esse lugar é um campo sincronizado como qualquer outro.
Tudo continua indo para o servidor e para todos os computadores, e **cada um
filtra a sua lista**. Escondido não é privado.

### Alternativas

- **Não sincronizar o que é de um computador só.** É mais privado, mas fica
  sem backup, e mandar para outro computador exigiria subir na hora a tarefa
  inteira, com horas, agendamentos e etiquetas. O servidor teria que filtrar o
  pull por destinatário, e uma tarefa que mudasse de lugar teria que chegar
  inteira, fora do cursor. Fica para depois, se a privacidade passar a ser o
  pedido.
- **Espaços de trabalho separados** (um workspace por computador, já previsto
  no esquema do servidor). Separa demais: compartilhar seria copiar, e a mesma
  tarefa viraria duas.
- **Lugar como campo da tarefa, filtro local (escolhida).** Mandar, compartilhar
  e espiar o outro computador são instantâneos, porque os dados já estão aqui.

### O lugar

- `DevicePlacement`: só num computador (`Device` + id) ou em todos
  (`Everywhere`). Na tarefa e na etiqueta, são duas colunas anuláveis
  (`PlacementKind`, `PlacementDeviceId`), para o filtro ser um predicado
  simples.
- **Sem lugar é o legado** e aparece em todos: é o que existia antes, ou numa
  instalação que nunca conectou. "Só num computador" sem computador também
  conta como legado. O contrato não tem como proibir essa combinação, e é
  melhor aparecer em todos do que sumir de todos.
- No fio é o campo Object `placement` (`kind`, `deviceId`). Ele mescla como
  **um valor só**, então tipo e computador nunca se separam. O mesmo valor nos
  dois lados não é conflito. Valores diferentes ao mesmo tempo viram conflito
  visível, rotulado "onde aparece".
- O campo é **opcional**, porque tudo o que o servidor gravou antes não o tem.
  `SyncSchema.Current` não sobe: o cliente antigo ignora o campo, o servidor o
  guarda, e o servidor novo o valida. (Desde o ADR-072, o servidor recusa o
  campo que não conhece: campo novo pede o servidor atualizado antes.)
- **Correção na captura:** um campo ausente no retrato e nulo na projeção não é
  mudança. Sem isso, a primeira edição de qualquer tarefa depois do upgrade
  mandaria `placement: null` e apagaria o lugar gravado por outro computador.

### Quem é "este computador"

`DevicePlacementState` (singleton) guarda:

- o id atual (`device.json`);
- o id com que o banco foi ligado;
- os **ids antigos**: revogado e reconectado, o computador volta com outro id.

O `SyncState` guarda os ids antigos e os apelidos da conta. Assim o seletor e o
"Enviar para" funcionam ao abrir o app e sem rede. Antes da primeira conexão,
nada filtra, nada nasce com lugar, e o `device.json` nem é lido.

### Assentamento do legado

Depois do join e de cada pull, o que está sem lugar e foi **criado por este
computador** (`SyncEntityStates.CreatedByDeviceId`) ganha lugar:

- **Tarefa:** fica só aqui.
- **Etiqueta:** fica só aqui, ou vai para **todos** se marca alguma tarefa que
  não é só daqui.

Só o criador assenta. Assim, dois computadores nunca gravam valores
diferentes no mesmo item. Enquanto o criador não atualiza, o item fica sem
lugar e aparece em todos: nada some por esperar. O que está num id antigo
deste computador vai para o atual pelo mesmo caminho, e o outro computador
passa a ver o apelido de agora.

Uma etiqueta juntada pelo nome na primeira conexão (a "Casa" criada nos dois)
vira de todos pelo reconcile, pelo domínio. A diferença de lugar nessa fusão
não vira conflito.

O assentamento passa pela captura como qualquer edição. Uma tarefa ainda não
enviada recebe o lugar na mesma operação de criação.

### Visibilidade

O filtro é **explícito em cada consulta**, como o de arquivados e lixeira, e
não um query filter global: as buscas por id, a restauração e a varredura
precisam ver tudo. `PlacementPredicates.VisibleIn` é o mesmo predicado para
tarefa e etiqueta.

- **Seguem o seletor** (no MCP, o `device`): Hoje, busca, estatísticas,
  arquivados e lixeira, histórico da semana, horas e lista de etiquetas.
- **Sempre daqui:** lembretes e avisos de prazo. A tarefa de outro computador
  toca lá. O filtro vem antes do lote, para as escondidas não ocuparem a vez
  das visíveis.
- **Sem filtro:**
  - buscas por id;
  - o cronômetro ativo (é desta máquina);
  - a varredura de arquivamento e lixeira;
  - os post-its (continuam compartilhados);
  - os chips de etiqueta de uma tarefa visível (o vínculo segue a tarefa);
  - os worktrees no MCP (só existem onde foram criados).
- **Etiquetas:** o seletor de etiquetas mostra as daqui e as de todos, mais as
  que já marcam a tarefa aberta, para dar para tirá-las. Criar uma etiqueta
  com o nome de uma escondida aqui reaproveita essa etiqueta e a compartilha,
  em vez de dar "o nome já existe".

### Tela

- **Seletor "Mostrando":** este computador, cada outro, ou todos. Só existe com
  outro computador na conta, ativo ou revogado; com um revogado, as tarefas
  dele se acham em "Todos". Não é lembrado: o app abre sempre neste
  computador. O histórico da semana usa o mesmo seletor, e é ali que se vê o
  que foi feito em cada um.
- **Selo na linha:**
  - a compartilhada mostra sempre "⇄ todos os computadores", porque mexer nela
    muda nos dois;
  - a de outro computador mostra o apelido;
  - a daqui só se identifica em "Todos".
- **"Onde aparece…"** no menu da linha, na janela da tarefa e na janela
  Etiquetas. As opções são só neste computador, em todos, ou enviar para cada
  outro computador. O envio avisa e oferece "Desfazer". Ele é recusado:
  - com o cronômetro correndo na tarefa;
  - para um computador revogado ou desconhecido, porque seria esconder de
    todos.
- **Caixa de captura com destino ("Para").** Volta para este computador depois
  de cada captura: mandar para o outro é exceção.
- **Chegou:** a tarefa que outro computador mandou para cá, ou compartilhou
  estando escondida aqui, ganha `ReceivedAt` (coluna local). Ela entra na
  seção **CHEGARAM** no topo, com ou sem data, até ser aberta, e o quadro
  avisa "Chegou 1 tarefa de Notebook". Uma tarefa nova "em todos" não é
  chegada. O lugar que muda rearma o lembrete, para a tarefa que passa a
  aparecer aqui não tocar um lembrete velho de uma vez.

### Consequências

- Migration `DevicePlacement`, aditiva:
  - quatro colunas em `Tasks`;
  - duas em `Tags`;
  - duas em `SyncState`.
- Os testes de upgrade semeiam pelo modelo atual em schema antigo com
  `SeedOnOlderSchemaAsync`, que acrescenta e tira as colunas novas em volta da
  gravação. Uma coluna nova numa tabela antiga não obriga mais a reescrever
  essas sementes em SQL.
- **Escondido não é privado:** todos os computadores e o servidor têm tudo, e
  o MCP com `device: all` devolve tudo.
- Um banco restaurado em outra máquina ainda ativa reivindica as tarefas da
  original, pelos ids antigos. É o mesmo cuidado de sempre com cópias de
  banco (ADR-061).
- Um computador ainda na versão anterior vê tudo e não muda lugar. As tarefas
  legadas dele ficam em todos até ele atualizar.
- Um envio e um compartilhamento ao mesmo tempo, em computadores diferentes,
  viram um conflito visível. Vence o que chegou primeiro ao servidor.
- `SyncSchedulerTests` passou a rodar numa coleção sem paralelismo. Com mais
  classes na suíte, a continuação do timer passava dos 3 ms de cada passo do
  relógio falso.

**Testes:**

- **Domain:** `DevicePlacement`, `Place`, chegada e etiqueta.
- **Contratos:**
  - a mudança sem `placement` passa;
  - o `kind` é conferido.
- **Server.Application:** o mesmo lugar dos dois lados não é conflito, e
  lugares diferentes são.
- **Application:**
  - quem é "daqui" (inativo, ids antigos, banco ligado com outro id);
  - os destinos recusados;
  - a criação nasce aqui ou onde foi mandada;
  - a etiqueta escondida reaproveitada;
  - o assentamento;
  - o que conta como chegada;
  - a seção CHEGARAM;
  - o cronômetro que impede o envio.
- **Infrastructure:**
  - classificação das colunas locais;
  - captura só do lugar;
  - campo novo sobre retrato antigo;
  - applier: cliente antigo, nulo explícito, mudança reportada e lembrete
    rearmado;
  - cada consulta filtrada;
  - o lembrete escondido fora do lote;
  - o ciclo inteiro: assentamento, etiqueta compartilhada, id novo e apelidos
    lembrados.
- **Server.IntegrationTests:** dois desktops inteiros:
  - mandar para o outro some daqui e chega lá;
  - o que existia fica com quem criou, sem conflitos;
  - a etiqueta criada nos dois vira de todos;
  - o computador que volta com outro id mantém as suas.
- **Desktop:** o seletor, os selos, a captura para outro computador, o
  "Desfazer", o aviso de chegada, o menu da linha e a janela desenhada.
- **MCP:** `device`, `placement`, o computador desconhecido e o caso sem
  sincronização.

## ADR-063 — A bolinha da sincronização no cabeçalho

**Contexto:** a sincronização (ADR-061) roda em segundo plano, e só a janela
*☰ → Sincronização…* mostrava o estado dela. Quem acabou de editar não sabia
se a mudança já tinha subido, se havia algo esperando ou se o app estava sem
conexão. O pedido: um indicador discreto, sempre à vista, que deixe isso claro.

**Decisão:** uma bolinha de 8px no cabeçalho, entre o título e o histórico,
sem texto e sem ocupar altura. Por isso ela aparece também no modo recolhido
e no HUD, menos no HUD compacto, que já tira o histórico pela largura. O
tooltip diz o estado por extenso. O clique abre um painel curto com
"Sincronizar agora" e "Abrir sincronização…" (ou "Conectar…", "Entrar de
novo…").

### Alternativas

- **Rodapé com bolinha e texto**, como a barra de status do VS Code. É o mais
  explícito, mas custa uma linha de altura num widget de 360×560, e o rodapé
  não existe nos modos recolhido e HUD.
- **Só na janela de sincronização** (o que havia). O estado fica a dois
  cliques, justo quando ele importa.
- **Bolinha no cabeçalho (escolhida).** Aparece em todos os modos e custa
  24px de largura.

### Estados

Em ordem de precedência: o primeiro que valer é o que aparece.

| Estado | Bolinha | Título |
|---|---|---|
| Sem servidor na build | oculta | — |
| Sem conta | anel cinza | "Só neste computador" |
| Precisa de você: login vencido, revogado, ciclo `Blocked` ou alteração recusada | vermelha cheia | "O login da sincronização expirou", "A sincronização parou", … |
| Pausada | anel cinza | "Sincronização pausada" |
| Sincronizando | azul, pulsando | "Sincronizando…" |
| Sem conexão: último ciclo `Transient`, ou erro gravado antes do primeiro ciclo | anel âmbar | "Offline" |
| Alterações esperando | âmbar cheia | "3 alterações esperando para subir" |
| Em dia, com o que revisar: conflito, comando, recebido com falha | verde com halo âmbar | "Sincronizado — 2 itens para revisar" |
| Em dia | verde cheia | "Sincronizado" |

- **A cor nunca vem sozinha**, como na bolinha do worktree (ADR-034). Cheia
  quer dizer que a sincronização anda; anel, que não anda; halo, que há algo a
  ver; o pulso, trabalho em andamento. O tooltip e o nome de acessibilidade
  repetem o estado em texto. As cores são os tokens de tema, que o teste de
  contraste já cobre.
- **Vermelho só quando precisa da pessoa.** Sem conexão não é erro: as
  alterações esperam no outbox, e o detalhe diz isso ("3 alterações guardadas
  aqui; sobem quando a conexão voltar. Tentando de novo sozinho").
- **Pausada vence "sem conexão"**, porque pausada não tenta. **Sincronizando
  vence "sem conexão"**, porque a tentativa em andamento é a notícia.
- **A hora é relativa** ("Última sincronização há 2 min"). Um relógio de 30 s
  só refaz o texto, sem ler o banco.

### Nada pisca à toa

O ciclo de rotina roda a cada minuto e costuma durar milissegundos. Pulsar a
cada minuto seria ruído. O "sincronizando" só aparece se o ciclo passar de
700 ms, ou de cara quando leva alterações daqui. O pulso sai junto com o
estado novo, sem um instante do estado velho no meio.

### De onde vem o estado

- **Reusa `GetSyncStatusHandler`**, o mesmo da janela. Não há consulta nova,
  porque a leitura só acontece em evento, nunca por polling.
- O `SyncScheduler` ganhou dois eventos:
  - `CycleStarted`, para o pulso;
  - `ChangeQueued`, quando uma gravação acorda o laço, antes da rajada. Sem
    rede, o próximo ciclo pode estar a 5 min de backoff, e a contagem de
    pendentes precisa subir na hora.
- **Desconectar e pausar também acordam o laço.** O ciclo é ocioso, sem rede,
  e o `CycleCompleted` faz a bolinha mudar na hora, e não no próximo minuto.
- O `SyncIndicatorViewModel` é singleton e entra no `TodayViewModel` por
  propriedade. Nula, a bolinha não aparece, e os testes que montam o painel
  não mudam.

### Consequências

- Um ciclo ocioso a mais ao desconectar e ao pausar. É barato: sem conta ou
  pausado, o motor volta `Idle` sem rede.
- Logo depois de abrir o app, um ciclo `Blocked` da sessão anterior aparece
  como "Offline" até o primeiro ciclo, que roda na abertura.

**Testes:**

- **Application:** o agendador avisa o começo do ciclo com `IsRunning`
  ligado, e avisa a gravação antes da rajada.
- **Desktop:**
  - a precedência, uma linha por estado;
  - os textos de cada estado;
  - o "há N min" segue o relógio sem reler o banco;
  - o ciclo de rotina não pisca, o longo pulsa depois da espera, e o que leva
    alterações pulsa de cara;
  - a bolinha e o painel desenhados no cabeçalho.

## ADR-064 — O repositório Git do diretório: a URL acompanha a pasta, e clonar dali

**Contexto:** o diretório da etiqueta (ADR-026) só tinha o caminho. Para
cadastrar um projeto novo, era preciso clonar no terminal, voltar e procurar a
pasta. E o caminho é de cada computador (ADR-061): o diretório chegava no
outro computador "não mapeado", sem nada que dissesse de onde baixá-lo. O
pedido: com o Git disponível, informar o repositório, escolher onde clonar,
com a pasta já sugerida, e, numa pasta que já é repositório, manter a URL
sempre de acordo com ela.

**Decisão:** o diretório ganha `RepositoryUrl`, opcional. No formulário de
*Etiquetas…*, com o Git encontrado, aparece o campo "Repositório Git" acima
do caminho:

- **Colar a URL com o caminho vazio** já escolhe onde clonar:
  `{pasta de clone}\{nome do repositório}`, e o alias junto
  (`@ecossistema-web`). Enquanto o usuário não mexe, corrigir a URL troca a
  sugestão. Um caminho escrito por ele nunca é trocado.
- **A pasta ainda não existe:** aparece "A pasta ainda não existe. Clonar
  baixa o repositório nela", com **Outra pasta…** (escolhe a pasta-mãe, e o
  repositório entra numa subpasta com o nome dele) e **Clonar**. O clone
  termina salvando o diretório: colar a URL e clicar em Clonar já é o
  cadastro inteiro.
- **A pasta já é um repositório:** "Procurar…" preenche a URL pelo `origin`
  dela, e salvar também lê o `origin`. Ao abrir a lista da etiqueta, cada
  pasta encontrada tem a URL conferida, e a que mudou é gravada.
- **O diretório que chegou de outro computador** traz a URL. Editá-lo já
  sugere a pasta e oferece o clone, e a linha diz "edite para clonar ou
  escolher a pasta".

Sem o Git, o campo e o clone não aparecem, e a URL gravada fica como está. No
lugar do campo, um aviso diz que o Git não está instalado e o que se perde
sem ele, com as instruções do ADR-027 (`GitInstallGuide`: o comando do
sistema para copiar, a página oficial) e um "Verificar novamente". Escondida
calada, a URL parecia não existir: quem não tem o Git não tinha como saber
que a função estava ali. O "Verificar novamente" não recarrega a lista: só
atualiza o Git e a pasta de clone dos cartões, e o formulário que o usuário
está preenchendo fica. Como a procura relê o PATH do registro (ADR-027),
basta instalar e clicar, sem reiniciar o app.

### A pasta manda

Com dois campos, um dia eles discordam. Quem vence é a pasta: o `origin` é o
que o Git usa de verdade, e quem trocou o remoto no terminal (um fork, um
servidor novo) espera ver o cadastro acompanhar. O contrário, um
`git remote set-url` a partir do formulário, mexeria na configuração do
repositório do usuário, e o app não altera repositório sem pedido (ADR-027).
Por isso a URL digitada vale para clonar e para pastas que não são
repositório, e é substituída pelo `origin` assim que houver um.

- **Mesma URL escrita de outro jeito não é mudança.** Com ou sem `.git`, com
  barra final ou maiúscula no servidor, é o mesmo repositório
  (`GitRemoteUrl.AreSame`). Sem isso, a lista regravaria e sincronizaria a
  URL a cada abertura.
- **Um `origin` que sumiu não apaga a URL.** A última conhecida ainda serve
  para clonar em outro computador.
- **Só o `origin`.** É o remoto que o clone cria, e é o mesmo que a PR aberta
  já usa (ADR-047).

### A URL viaja, o caminho não

O caminho continua de cada computador. A URL vale em qualquer um, e por isso
entra na sincronização como campo do diretório (`repositoryUrl`). É ela que
faz o diretório "não mapeado" virar um clique em vez de uma pesquisa. Um
cliente mais velho ignora o campo (ADR-061), sem mudar a versão do contrato.

**Credenciais nunca entram.** Um `https://token@github.com/…` copiado do
remoto iria para o banco e, pela sincronização, para o servidor. O domínio
tira usuário e senha de `http(s)://` ao normalizar. O usuário do SSH
(`git@…`, `ssh://git@…`) não é credencial e fica.

### Que URL é aceita

As formas que o Git aceita para clonar: `https://`, `http://`, `ssh://`,
`git://`, `file://` e a curta do SSH, `[usuário@]servidor:caminho`. Como no
Git, com `://` é URL e nunca a forma curta (`ftp://x` não vira servidor
"ftp"), e uma letra antes do `:` é unidade do Windows. Recusadas: caminho
local, espaço, mais de 2048 caracteres e qualquer coisa começando com `-`,
que o Git leria como opção. Além disso, o clone passa os dois valores depois
de `--`.

### O clone

- **`IGitClient.CloneAsync`** roda `git clone -- {url} {caminho}` sem pasta de
  trabalho, com 30 minutos de prazo e cancelamento ("Cancelar clone").
- **É o único comando em que o Git Credential Manager pode abrir a janela de
  login.** Os outros rodam com `GCM_INTERACTIVE=Never` porque são de fundo: um
  fetch esperando login travaria até o timeout. O clone foi pedido por um
  clique, e um repositório privado sem login salvo só tem esse caminho.
  `GIT_TERMINAL_PROMPT=0` continua: não há terminal para alguém digitar.
- **Já clonado não é erro.** Se a pasta existe e o `origin` é o mesmo
  repositório, nada é baixado, e o diretório é salvo.
- **As recusas viram frase:** pasta existente e não vazia, login, repositório
  não encontrado, servidor inalcançável, tempo esgotado. O resto mostra a
  última linha do Git. O comando inteiro vai para o log.
- **O caso de uso não grava o diretório.** Ele devolve caminho e URL, e a tela
  salva como sempre. Se salvar falhar (alias repetido), o clone fica e o
  formulário espera a correção. E o app não apaga o que baixou: a pasta é do
  usuário.
- **Cancelar** mata o processo. O Git não tem como limpar a pasta pela metade,
  e a mensagem diz isso e onde ela ficou.

### Onde clonar por padrão

A pasta-mãe mais comum entre os diretórios já mapeados neste computador
(`SuggestCloneFolder`): quem guarda os projetos em `C:\Projetos` clona lá sem
procurar. Sem nenhum diretório, a pasta do usuário. Não há configuração nova:
a sugestão sai do que o usuário já fez, e "Outra pasta…" resolve a exceção.

### Alternativas

- **Um botão "Clonar repositório…" separado, com janela própria.** Mais um
  lugar para cadastrar a mesma coisa. No formulário, a URL e o caminho ficam
  lado a lado, e o diretório que chegou sem pasta já tem a URL ali.
- **Clonar sem salvar, deixando o "Adicionar diretório" para o usuário.** Um
  clique a mais sem decisão nenhuma no meio.
- **Conferir o `origin` a cada tecla do caminho.** Um processo do Git por
  tecla. A conferência acontece ao escolher a pasta, ao salvar e ao abrir a
  lista. Ao digitar, só a existência da pasta é conferida, o que decide se o
  clone é oferecido.

### Consequências

- Abrir a lista de uma etiqueta roda um `git remote get-url` por pasta
  encontrada, depois de a lista aparecer. Sem o Git, nenhum.
- A detecção do Git roda a cada abertura de *Etiquetas…*, como na aba
  Desenvolvimento: instalar o Git com a janela escondida passa a valer na
  próxima abertura, e com ela aberta, no "Verificar novamente".

**Testes:**

- **Domain:** as formas aceitas e recusadas, credenciais tiradas, o nome da
  pasta que o clone cria, a mesma URL escrita de outro jeito e a troca que só
  conta quando muda.
- **Application:** o `origin` lido e normalizado; o clone com os valores
  normalizados, o já clonado, sem Git, as recusas traduzidas; a lista que
  acompanha o `origin` e grava uma vez só; a pasta de clone sugerida.
- **Infrastructure:** os argumentos e o ambiente do `git clone`; a URL indo e
  voltando no SQLite; um clone de verdade de um `origin` local, com a URL lida
  de volta e a recusa de clonar de novo na mesma pasta.
- **Desktop:** a sugestão de pasta e de alias, o caminho do usuário
  respeitado, "Outra pasta…", o clone oferecido só para pasta inexistente,
  nada sem Git além do aviso com o comando de instalação, o "Verificar
  novamente" que libera a URL sem perder o formulário, clonar e salvar, falha e cancelamento, a URL pelo `origin` ao
  procurar e ao salvar, o diretório de outro computador, a lista recarregada
  quando o `origin` mudou e o formulário desenhado com o "Clonar" ligado ou,
  sem Git, com o aviso e os botões ligados.

## ADR-065 — Limpeza de worktrees: os sem tarefa e os de tarefa encerrada, com força

**Contexto:** o worktree sai do disco por dois caminhos: pela aba
Desenvolvimento ou pela pergunta feita ao concluir a tarefa (ADR-034). O
resto se acumula:
- o worktree de quem respondeu "Manter";
- o de tarefa arquivada, cancelada ou na lixeira;
- o de tarefa apagada de vez. O purge (§7) apaga as linhas de
  `TaskDevelopments` em cascata, e a pasta e o registro no Git ficam sem
  ninguém que aponte para eles;
- o criado fora do app, à mão ou por um agente (`.claude/worktrees`).

O pedido foi uma faxina: apagar todo worktree que não está ligado a uma
tarefa, ou cuja tarefa terminou. Ela apaga inclusive os worktrees com
alterações pendentes.

**Decisão:** uma aba **Worktrees** em *Gerenciamento de dados*, ao lado de
Lixeira. Ela lista os candidatos, todos marcados, e **Limpar N worktrees**
apaga os marcados com `git worktree remove --force`, depois de uma confirmação
irreversível que conta quantos têm alterações a perder. A branch fica no
repositório, como em toda remoção do app.

### Onde procurar

A varredura junta duas fontes:
- os **diretórios mapeados das etiquetas** neste computador
  (`ListMappedDirectoryPathsAsync`), cada um levado à raiz do repositório
  com `rev-parse --show-toplevel`. O que não é repositório fica de fora;
- os **repositórios das tarefas** (`TaskDevelopment.RepositoryPath`).

As duas fontes são deduplicadas pelo caminho canônico. Em cada repositório
roda um `git worktree list --porcelain`. Um repositório em que o Git falha
fica fora da lista, e a aba diz qual.

### Quem entra

- **Sem tarefa:** nenhum ambiente pronto ou em criação aponta para a pasta.
- **De tarefa encerrada:** concluída, arquivada ou na lixeira, pelo
  `TaskItem.Lifecycle`. A tarefa cancelada não tem estado próprio no ciclo de
  vida: tudo cancelado não tem `ConcludedAt` e continua `Active`. O domínio
  ganhou `TaskItem.IsSettled` (nenhuma ocorrência pendente) só para esse caso,
  em vez de a consulta remontar a regra.
- **O ambiente pronto de tarefa encerrada que o Git já esqueceu** (sobra de
  pasta, ou pasta sumida) também entra. A remoção de sempre resolve os dois
  casos.

**Nunca entram:**
- o worktree principal e o repositório bare;
- o worktree de tarefa em andamento;
- o ambiente em criação;
- a pasta que é ela mesma um diretório mapeado de etiqueta. O usuário a
  cadastrou, então ela está em uso, mesmo sem tarefa.

Cada linha traz a mesma sonda da bolinha da lista (`ProbeWorktreesHandler`):
alterações não commitadas e commits sem push. No worktree sem tarefa, os
commits contam a partir da branch do worktree principal.

### Marcados, menos dois

- **Travado com `git worktree lock`:** vem desmarcado. Alguém pediu
  explicitamente que o Git não mexesse nele. Marcado, sai com
  `--force --force`, que é como o Git aceita passar por cima da trava.
- **Com agente de IA do app aberto (ADR-030):** não pode ser marcado. A
  remoção recusaria de qualquer jeito.

### A exceção ao "sem `--force`"

O ADR-027 proíbe operação destrutiva no `IGitClient`, e a remoção comum
continua recusando worktree com alterações. A limpeza é a exceção, e só ela
passa `WorktreeRemoveForce` além de `None`. Ela se apoia em três coisas, e só
nelas:
1. o usuário vê cada worktree, com as alterações de cada um;
2. ele escolhe quais saem;
3. a confirmação é irreversível e diz quantos têm trabalho a perder.

O commit sem push não se perde, porque a branch fica.

### Antes de cada um, de novo

Entre a varredura e o clique, o mundo muda. Por isso o comando confere de
novo, worktree por worktree, e o que não passa fica com o motivo, sem parar
os outros:
- **quem é o dono agora:** uma tarefa reaberta, ou que adotou o worktree sem
  dono, faz o worktree ficar. Uma tarefa apagada de vez depois da varredura
  faz o worktree ser tratado como sem tarefa;
- **se é o principal:** nunca sai, mesmo que um candidato montado à mão
  aponte para ele;
- **quem está com a pasta aberta** (`IDirectoryRemover.FindLockersAsync`, a
  mesma detecção do ADR-029). Sem força, o Git recusaria. Com força, ele
  apaga os arquivos debaixo de um terminal ou agente rodando ali e só deixa a
  pasta vazia presa. Com alguém segurando, nada é apagado, e a mensagem diz
  quem. Na limpeza em lote, nenhum processo é encerrado.

O ambiente de tarefa sai pelo mesmo `RemoveWorktreeHandler` da aba
Desenvolvimento, com `Force`: fica `Removed`, e a recusa por agente aberto
continua valendo. O worktree sem tarefa sai pelo mesmo miolo
(`WorktreeEraser`), que remove o registrado, apaga a sobra ou faz o prune. O
banco copiado do ambiente (ADR-060) não é apagado, como na remoção comum.

### A varredura não roda ao abrir a janela

Ela chama o Git em cada repositório. Por isso roda quando a aba aparece e em
**Procurar**, não a cada abertura do Gerenciamento de dados. Depois de
limpar, a lista é varrida de novo, e a lista Hoje é recarregada para a
bolinha e o "Worktree criado" sumirem na hora.

### Alternativas

- **Limpar sozinho, ao concluir ou no purge.** Apagar alterações não
  commitadas sem o usuário ver a lista é exatamente o que o ADR-027 existe
  para impedir.
- **Um item de menu que apaga tudo de uma vez.** Um worktree criado à mão
  para um experimento não teria como ser poupado.
- **Só os worktrees criados pelo app.** O pedido incluía os criados fora
  dele. E a tarefa apagada de vez já não deixa rastro de que o worktree era
  do app.
- **Apagar a branch junto.** Commit sem push se perderia de verdade. Com a
  branch, a pasta some e o trabalho fica recuperável.

### Consequências

- A janela de Gerenciamento de dados ficou mais larga (780) para as cinco abas
  caberem numa linha.
- `IGitClient.RemoveWorktreeAsync` ganhou o parâmetro `force`. O padrão é sem
  força, e a revisão de segurança do Git continua olhando um lugar só.

**Testes:**

- **Domain:** `IsSettled` com cancelar, reabrir e concluir.
- **Application:**
  - o principal e o de tarefa ativa ficam fora;
  - o motivo certo para cada estado da tarefa;
  - repositório alcançado duas vezes;
  - a pasta mapeada como diretório;
  - alterações reportadas, travado e agente aberto;
  - o ambiente que o Git esqueceu;
  - o repositório que falha;
  - a remoção forçada de worktree com alterações, e a dupla força no travado;
  - a pasta aberta pulada sem encerrar nada;
  - a tarefa reaberta depois da varredura;
  - o principal recusado.
- **Infrastructure:**
  - os argumentos `--force` e `--force --force`;
  - com o Git de verdade, um worktree criado à mão com arquivo não commitado e
    o de uma tarefa concluída saem, e as duas branches ficam.
- **Desktop:**
  - o que vem marcado;
  - os textos de cada linha;
  - marcar e desmarcar todos;
  - recusar não apaga;
  - a pergunta conta o que se perde;
  - confirmar varre de novo e avisa a lista;
  - a limpeza parcial;
  - a aba que só varre quando aparece.

## ADR-066 — Comandos globais só deste computador, a menos que compartilhados

**Contexto:** a sincronização (ADR-061) mandava todo comando global para todos
os computadores. Só que um comando costuma ser de uma máquina só: caminho
absoluto, executável instalado ali, script que só existe naquela pasta
(`eco-sync`, `C:\Projetos\core\run.ps1`). No outro computador, ele aparecia na
lista e falhava ao rodar. O pedido: o comando global não é compartilhado, a
menos que o usuário marque um check para isso.

**Decisão:** o comando ganha `IsShared`. No formulário de *Comandos globais*,
"Compartilhar entre computadores" fica embaixo de "Pedir confirmação", e a
lista marca o compartilhado com "⇄ todos os computadores", como a tarefa
(ADR-062).

- **O comando novo nasce desmarcado.** É o domínio que decide
  (`DevelopmentCommand.Create`), não a tela, então o MCP e qualquer caminho
  futuro também criam comandos locais.
- **Os que já existiam continuam compartilhados.** Eles já estavam nos outros
  computadores. A migration `CommandSharing` grava `true` nas linhas antigas;
  o modelo não tem `HasDefaultValue`, pelo mesmo motivo do `KeepTerminalOpen`
  (ADR-051).
- **O comando do diretório (ADR-055) sempre viaja**, com o diretório. Ele não
  tem o check, e `SetSharing` recusa.
- **Os botões de diretório seguem o comando.** O vínculo de um comando local
  também fica aqui. Senão, o servidor esperaria para sempre um pai que nunca
  chega (`parent_missing`).
- **A escolha é de cada computador e não viaja.** `IsShared` é coluna local no
  mapper. O comando que chega de outro computador entra marcado, porque lá ele
  foi compartilhado.

### Desmarcar: cada um fica com a sua cópia

Desmarcar um comando compartilhado não apaga nada em lugar nenhum. Este
computador para de mandar **e** de receber mudanças dele e dos botões dele:

- o que ainda não tinha saído do outbox não sai mais;
- a edição, e também a exclusão, que chega de outro computador é ignorada;
- excluir aqui não manda exclusão: a cópia de lá fica.

Para isso, o estado da sincronização ganha `IsDetached` ("este computador
parou de acompanhar a cópia do servidor"). Ela não é `IsDeleted`: a linha
local continua, e o portão de revisão de comando alheio (ADR-061) continua
valendo para ela. A mudança ignorada ainda anda a versão (`ServerVersion`). A
revisão que estava em dia anda junto, porque o texto daqui não mudou; a
pendente continua pendente.

A exclusão local de um comando desmarcado leva os botões pela cascata do
banco, sem o change tracker ver. A captura lê esses botões antes do
`SaveChanges` e os desliga também. Sem isso, a varredura os acharia "sumidos"
e mandaria a exclusão para os outros computadores.

### Compartilhar de novo: vale a versão daqui

Marcar de novo manda o comando inteiro, e os botões dele, sobre a última versão
que o servidor mostrou. Como a versão base alcança todos os campos, a mescla
por campo do servidor aceita os campos daqui: quem marcou é quem vale.

Se outro computador excluiu o comando nesse meio-tempo, ele volta. O servidor
passa a reviver uma entidade excluída quando a versão base da operação alcança
a do tombstone, e alcançar é igualar: versão base à frente da do servidor é
recusada antes, com `invalid_base_version` (SEC-017, ADR-061). Só quem já viu a exclusão chega a essa versão, e quem manda a
entidade inteira está recriando de propósito. Quem não viu continua recebendo
"excluído em outro dispositivo". Antes, só os vínculos (tarefa ↔ etiqueta)
reviviam.

### O mesmo apelido aqui e lá

Chega de outro computador um `@build` compartilhado, e aqui já existe um
`@build` só deste computador. Antes, a primeira conexão juntava os dois pelo
nome (ADR-061). Agora:

- o daqui não é tocado: nem renomeado, nem juntado;
- o que chegou fica de fora, com o aviso na janela de Sincronização: "O
  comando @build de outro computador não entrou: @build já existe aqui como
  comando só deste computador. Renomeie um dos dois ou marque 'Compartilhar
  entre computadores' no daqui.";
- os botões dele esperam com ele, e não falham;
- cada ciclo busca de novo, por id, o que espera
  (`RetryWaitingReceivedChangesHandler`). Renomeado o daqui, o de lá entra
  sozinho no ciclo seguinte. Excluído lá, o aviso some.
- Marcado o daqui, o servidor recusa o apelido repetido, e o fluxo da primeira
  conexão junta os dois: um `@build` só, com as diferenças como conflito
  visível.

### Alternativas

- **`IsShared` no contrato, sincronizado.** A escolha de um computador
  mandaria nos outros, e desmarcar teria de mandar algo para o servidor,
  justamente o que o usuário pediu para não acontecer.
- **Reaproveitar `IsDeleted` para "desligado".** Ele desliga o portão de
  revisão e faz a varredura tratar a linha como excluída.
- **Desmarcar apaga dos outros computadores.** Foi oferecido e recusado: quem
  já usa o comando lá não deve perdê-lo porque mudou de ideia aqui.
- **Id novo ao compartilhar de novo.** Exigiria reescrever as chaves dos
  botões e das execuções, e o comando de lá viraria um segundo `@build`.
- **Juntar pelo apelido, como antes.** O comando local seria trocado pelo de
  lá sem ninguém pedir.

### Consequências

- Enquanto algo espera um apelido livre, cada ciclo faz uma busca por id. Sem
  nada esperando, nenhuma.
- "Exclusão não ressuscita" ganha uma exceção, a da versão base que alcança o
  tombstone. Para clientes anteriores nada muda: eles só chegavam a essa
  versão aplicando o tombstone, que apaga a linha, e daí em diante não mandam
  nada daquele id.
- **Ordem de publicação: o servidor primeiro.** Um servidor antigo responderia
  `entity_deleted` a quem compartilha de novo, e o cliente apagaria a cópia
  local.
- Um botão criado em outro computador enquanto o comando estava desmarcado
  aqui não chega quando ele volta a ser compartilhado. O botão continua lá.
- Na outra ponta, a tarefa que chama `@build` num computador sem ele avisa: "O
  comando @build não existe neste computador. Cadastre-o em Comandos globais
  (ou marque 'Compartilhar entre computadores' no computador onde ele existe)
  ou corrija o apelido."

**Testes:**

- **Domain:** o global nasce local, o do diretório vai sempre e recusa o
  check, compartilhar não é edição (`UpdatedAt` fica), editar mantém a
  escolha.
- **Application:** criar compartilha só quando pedido, e editar sem dizer
  mantém.
- **Infrastructure:**
  - a escolha gravada;
  - a migration mantém os antigos compartilhados;
  - a coluna classificada como local, com o padrão "compartilhado" para o que
    chega;
  - a captura: o novo fica aqui com os botões; marcar manda o comando e os
    botões, pai primeiro; desmarcar descarta o que não saiu e não manda mais
    nada; excluir o local não manda exclusão, nem dos botões, nem na
    varredura; o compartilhado excluído ainda sai de todos; compartilhar de
    novo manda tudo sobre a última versão; o comando do diretório sempre vai;
  - o que chega: o comando de lá entra marcado; edição e exclusão de um
    desligado não entram e andam a versão; o botão novo de um desligado fica
    de fora; o mesmo apelido de um local espera, com o local intacto, e entra
    depois de renomeado; a exclusão de lá tira o aviso;
  - o ciclo busca de novo o que espera e para quando nada espera.
- **Server.Application:** quem viu a exclusão recria com a entidade inteira;
  pela metade, não.
- **Server.IntegrationTests:** o roteiro inteiro com dois desktops e o
  PostgreSQL: local não vai, marcado vai, desmarcado cada um com a sua cópia,
  excluir lá não exclui aqui, compartilhar de novo traz de volta a versão de
  quem marcou.
- **Desktop:** o formulário começa desmarcado e carrega a escolha, desmarcar
  pede confirmação, a marca na lista e o texto de excluir.

## ADR-067 — O celular como mais um dispositivo da sincronização (.NET MAUI)

**Contexto:** o pedido era um app para Android e iOS que mostre todas as
tarefas numa lista, crie, edite, conclua e filtre, funcione sem rede por um
tempo e sincronize com o resto. O repositório já tinha quase tudo:

- Domain, Application e Sync.Contracts são `net10.0` puro, sem UI, com as
  regras nos handlers (os mesmos que o MCP chama, ADR-059);
- o servidor de sincronização (ADR-061) com OAuth, outbox, merge por campo,
  idempotência por `OpId`, conflitos visíveis e isolamento por workspace;
- as listas por dispositivo (ADR-062).

Faltavam um projeto mobile, um login que não dependesse do loopback, um
cofre de segredos para Android e iOS, e um jeito de usar os dados e a
sincronização sem levar junto Git, terminais, Jira e PostgreSQL.

**Decisão:** o celular é **mais um dispositivo da sincronização**. Ele roda a
mesma Application sobre o mesmo SQLite (mesmo `DbContext`, mesmas migrations,
mesmo outbox) e fala só com a API de sincronização que já existe. Não há
endpoint REST de "tarefas".

### Alternativas

- **Uma API REST de tarefas no servidor**, com o celular como cliente fino e
  cache local. Exigiria rodar as regras do domínio no servidor, sobre o jsonb
  do PostgreSQL, e conviver com o merge por campo da sincronização: duas
  maneiras de gravar a mesma tarefa, uma delas sem outbox. Recusada.
- **Celular lendo o PostgreSQL direto, ou um SQLite compartilhado.** Contra o
  ADR-061 e sem isolamento por usuário. Recusada.
- **Mais um dispositivo (escolhida).** Funciona sem rede desde o primeiro
  dia, conflitos e reenvios já estão resolvidos e testados, e nenhuma regra
  é escrita duas vezes.

### Projetos

| Projeto | Alvo | Papel |
|---|---|---|
| `MyTaskApp.Mobile.Core` | `net10.0` | ViewModels (CommunityToolkit.Mvvm), apresentação, composição e as portas da plataforma |
| `MyTaskApp.Mobile` | `net10.0-android` (+ `net10.0-ios` no macOS ou com `-p:IncludeIos=true`) | Telas XAML, Shell e adaptadores MAUI |
| `MyTaskApp.Mobile.Core.Tests` | `net10.0` | ViewModels sobre a pilha de verdade com SQLite temporário |

O núcleo fica sem MAUI para os testes rodarem no CI do desktop sem workload;
um teste de arquitetura garante isso. `Mobile.Core` e seus testes estão no
`MyTaskApp.slnx` (e no portão de cobertura). A casca MAUI tem a solução
própria, `MyTaskApp.Mobile.slnx`.

### O que mudou nos projetos existentes

Tudo aditivo; o desktop se comporta como antes.

- **`AddLocalData`** (Infrastructure): os dados e a sincronização (SQLite,
  repositórios, consultas, captura, aplicação, cliente HTTP) saíram de dentro
  do `AddInfrastructure`, que agora o chama e completa com o que só o
  computador tem.
- **Login sem loopback.** `ISyncAuthorizationRedirect` diz por onde o login
  volta e com qual cliente. Sem registro, vale o loopback do desktop
  (`mytaskapp-desktop`). O celular registra o `AppLinkAuthorizationRedirect`
  (`mytaskapp-mobile`, volta por `io.github.adrianoguzzo.mytaskapp://oauth/callback`),
  que recebe a volta entregue pelo `WebAuthenticator` (Custom Tabs no
  Android, `ASWebAuthenticationSession` no iOS). PKCE, troca do código, cofre
  do refresh e renovação continuam sendo do `SyncAuthenticationService`.
- **Servidor:** `PublicClientSeeder` semeia os dois clientes públicos, cada um
  só com os seus redirects (`Sync:MobileRedirectUris`). Um código emitido
  para um cliente não vira token no outro.
- **`ISecretStore` público**, para o app entregar o cofre da plataforma
  (`SecureStorage`). As implementações do desktop continuam internas, e não
  há `InternalsVisibleTo` para o mobile.
- **`SyncScheduler.StopAsync`:** o laço para em segundo plano e volta com
  `Start`, sem nunca rodar dois ciclos juntos.
- **`PlacementMatch`:** o filtro de origem exata na busca (abaixo).
- **`SyncIndicatorClassifier`:** a precedência do indicador (ADR-063) saiu do
  ViewModel do desktop para a Application. O celular decide do mesmo jeito.
- `SyncEngine.Hello` identifica Android e iOS; `DeviceIdentityFile` aceita o
  nome do aparelho como apelido.

### Origem, projeto e etiquetas

- **Origem = o "onde aparece" do ADR-062:** compartilhada, só neste celular,
  ou só num computador da conta. Não há entidade de origem, e não foi criada
  uma: contextos como Trabalho e Estudos continuam sendo etiquetas.
- **A lista do celular é de todos os dispositivos** (`DeviceView.All`). O
  filtro de origem é **exato**: "Notebook" é só o que está no notebook, sem
  as compartilhadas. Isso é o `PlacementMatch`, diferente do `PlacementFilter`,
  que responde "o que aparece num dispositivo". O legado sem lugar conta como
  compartilhada. (O ADR-076 levou o filtro para chips no topo, lembrados entre
  aberturas, e separou pendentes e Histórico.)
- **Tarefa nova no celular nasce compartilhada.** No desktop o padrão é "este
  computador"; uma tarefa só no celular sumiria da visão padrão do desktop.
- **Projeto não existe** no domínio. Não foi inventado: o detalhe mostra o
  desenvolvimento (repositórios e worktrees) em modo somente leitura, e
  "projeto" fica como decisão em aberto.
- As etiquetas são as de sempre; o celular escolhe entre as existentes e não
  tem cadastro próprio.

### Telas

- **Tarefas:** busca com espera de 300 ms; atalhos Todas, Pendentes,
  Vencidas, Concluídas e Canceladas; folha de filtros (prioridade, etiquetas,
  origem, ordem); páginas de 50; puxar para atualizar pede um ciclo; concluir
  e reabrir pela linha; estados de carregando, vazio, sem resultados e erro.
- **Detalhe:** tudo o que a busca sabe, a procedência (criada e alterada em
  qual dispositivo), o histórico local e o desenvolvimento. As ações aparecem
  conforme a regra permite; quem decide é o caso de uso.
- **Formulário:** criar e editar. Na edição só vai o que mudou. O status
  passa pelas transições do domínio: reabrir vem antes de editar, porque o
  prazo só muda em pendente, e concluir ou cancelar vem depois. Sair com
  alterações pergunta antes.
- **Conta:** estado da sincronização, conflitos ("Reaplicar o meu" ou
  "Manter o do servidor"), recusadas (tentar de novo ou descartar),
  dispositivos, pausar e sair.
- Tema claro e escuro pelo sistema, com as cores do desktop (Carvão e o claro
  índigo). O ADR-075 trouxe os sete temas do desktop, com escolha na aba Conta.

### Consequências

- O celular recebe **todos** os dados da conta, como os computadores:
  escondido não é privado (ADR-062).
- A auditoria é local e não sincroniza (ADR-061): o histórico do detalhe é o
  que aconteceu neste celular.
- A sincronização roda **só em primeiro plano**: o laço para em
  `Window.Stopped` e volta em `Resumed`, e também quando a rede volta. Sem
  sincronização em segundo plano.
- **Sem lembretes nem notificações** no celular. Quem avisa são os
  computadores que mostram a tarefa.
- O servidor valida campo, tamanho, referência e posse, mas não roda as
  regras de estado (ADR-061). Um cliente adulterado poderia gravar uma
  transição que a Application recusaria. Num workspace pessoal, o risco é da
  própria pessoa.
- Desktop chama o celular de "computador" em "Enviar para…" e na
  procedência. Ficou assim.
- **Backup do sistema desligado** para os dados do app (`allowBackup=false`
  no Android, `NSUrlIsExcludedFromBackupKey` no iOS). Um backup restaurado
  noutro aparelho levaria o id deste dispositivo, e os dois falariam em nome
  do mesmo.
- O MAUI faz trimming parcial; o JSON por reflexão (identidade do dispositivo,
  captura) precisa de `JsonSerializerIsReflectionEnabledByDefault=true`. No
  iOS, o EF Core exige o interpretador (`UseInterpreter`).
- **iOS configurado, não validado:** não houve Mac neste ambiente
  (docs/mobile.md).

**Testes:**

- **Mobile.Core:** composição real (cada caso de uso que as telas pedem
  monta); lista, filtros, paginação, busca com espera, estados de tela,
  concluir e reabrir; formulário (validação, edição mínima, as transições de
  status, origem, prazo recusado pelo domínio, perguntar antes de sair);
  detalhe por estado; entrar e a aba Conta; apresentação; camadas.
- **Infrastructure:** o login do celular inteiro com o cliente e o redirect
  do app, `device_revoked`, recusa, `state` errado, abandono, troca de login;
  `AddLocalData` sem nada do computador; origem exata no SQLite.
- **Application:** parar e voltar o agendador (inclusive com um ciclo preso
  na rede); a origem chegando à busca.
- **Servidor:** o cliente do celular e os redirects de cada cliente; e, de
  ponta a ponta com PostgreSQL, o celular entrando pela tela Entrar e
  sincronizando com um desktop nos dois sentidos, sem rede e de volta, e o
  filtro de origem.

## ADR-068 — Servidor de sincronização fechado: lista de quem pode entrar

**Contexto:** a auditoria de segurança da API (SEC-004, ação G-06) viu que o
`/account/external-callback` criava conta e workspace para qualquer login do
Google, e que o `/connect/authorize` emitia tokens com o escopo `sync` para
essa conta. Sem lista, convite ou cota, qualquer pessoa com conta Google virava
usuário autenticado em segundos. Isso tornava exploráveis o pull sem teto
(SEC-005), os campos sem limite no push (SEC-006) e os dispositivos ilimitados
(SEC-015). O servidor é de uso pessoal: quem sincroniza é o dono e, no máximo,
algumas contas conhecidas.

**Decisão:** o servidor é **fechado**. Só entra a conta Google que está em
`Sync:Registration:AllowedEmails` ou em `Sync:Registration:AllowedSubjects`
(`RegistrationOptions`).

- **As duas listas vazias = ninguém entra.** Esquecer a variável no deploy
  recusa todo mundo, em vez de abrir o servidor. Não há modo "aberto".
- **O e-mail só vale verificado.** O Google diz isso em `email_verified` (no
  userinfo v2, `verified_email`), e o handler do Google ganha uma claim action
  que vira a claim `email_verified` com "true" ou "false". Sem a claim, o
  e-mail não conta. A comparação ignora maiúsculas e espaços, sem outra
  normalização (os pontos do Gmail contam).
- **O `sub` é a alternativa estável.** Ele não muda quando o e-mail da conta
  muda, e é o mesmo `ProviderKey` que já identifica o login.
- **A recusa é no callback, antes de achar ou criar a conta.** Fora da lista:
  403 em ProblemDetails com `code = registration_closed` (código novo em
  `SyncErrorCodes`). O callback não cria conta, workspace nem sessão, e apaga o
  cookie externo. O log registra `SyncRegistrationRefused` com o `sub`, para o
  administrador saber o que pôr na lista.
- **Quem já tem conta também é barrado.** O `/connect/authorize` confere de
  novo, porque a sessão do Identity vive dez minutos e a lista pode ter mudado
  nesse tempo. Ali a recusa é `access_denied`, que o desktop já mostra como "O
  login foi recusado.". O `/connect/token` confere a cada troca e renovação e
  responde `invalid_grant`, que o desktop já trata como "conecte de novo".
- **A conferência da conta existente usa o que está gravado:** o `sub` de cada
  login do Google e o e-mail com `EmailConfirmed`. Para isso ficar fiel, o
  callback grava a cada login o e-mail e o "verificado" que o Google mandou. A
  conta nova já nasce com `EmailConfirmed` = `email_verified`. Antes, era
  "tem e-mail".
- **Nada é apagado.** Tirar da lista corta o acesso. Os dados, o workspace e os
  dispositivos ficam, e voltam se a conta entrar na lista de novo.

### Alternativas

- **Aberto, com cotas por conta** (entidades e bytes por workspace, teto de
  dispositivos, métrica de cadastros). Seria o modelo de um serviço público. Não
  é o caso deste servidor, e as cotas ainda não existiam: os tetos por entidade
  e por workspace vieram com a ação G-05 (SEC-006, ADR-072). Elas protegem o servidor de
  qualquer usuário, inclusive de um da lista, e continuam necessárias.
- **Os dois modos, por configuração** (`Mode = Closed|Open`). Seria mais um
  caminho para testar e documentar sem uso hoje, e o modo aberto só serve com
  as cotas. Fica para quando houver o pedido.
- **Convite ou aprovação manual** (a conta nasce pendente e alguém aprova).
  Pede tela de administração e um estado novo na conta. A lista na configuração
  resolve o caso pessoal sem nada disso.
- **Bloquear só os cadastros novos.** Tirar alguém da lista não cortaria o
  acesso de quem já entrou, e a lista não serviria para revogar uma conta.
- **Conferir a lista em toda requisição da API.** O access token vive 15
  minutos, e o `/connect/token` já confere na renovação. Uma consulta a mais em
  cada push ou pull não compensa esse intervalo.

### Consequências

- **Atualizar o servidor pede a lista antes.** Sem ela, quem já estava
  conectado perde a renovação em até 15 minutos e não consegue conectar de
  novo (docs/sync-server.md, Implantação).
- O access token já emitido continua valendo até vencer
  (`Sync:AccessTokenLifetime`). A lista é lida ao subir, como o resto da
  configuração (`SyncServerOptions`): mudar a lista pede reiniciar o servidor.
- Uma conta criada antes desta versão tem `EmailConfirmed` = "tinha e-mail" até
  o próximo login. Para quem quer não depender disso, existe o `sub`.
- A recusa no callback é uma página do servidor: o desktop continua esperando
  a volta no loopback até o usuário cancelar. É o mesmo que acontece com
  qualquer erro no navegador antes do `/connect/authorize`.
- O `deploy/sync-server/docker-compose.yml` passa `SYNC_ALLOWED_EMAIL` para
  `Sync__Registration__AllowedEmails__0`.

**Testes:**

- **Servidor (PostgreSQL real, `RegistrationTests`):** um sujeito fora da lista
  não ganha conta, workspace, dispositivo nem token, e recebe 403 com
  `registration_closed`. Um sujeito na lista entra normalmente, e a lista do
  teste substitui a padrão. Um e-mail na lista entra só verificado, sem
  diferença de maiúsculas nem espaços. Sem lista, ninguém entra. Uma conta
  tirada da lista perde a renovação (`invalid_grant`) e o login, mas mantém os
  dados. Uma sessão ainda aberta é recusada no `/connect/authorize`
  (`access_denied`).
- O `ApiFactory` põe na lista os sujeitos que a suíte usa (`ana`, `bia`,
  `google-1`), e o `FakeGoogleHandler` manda `email_verified`, que o cabeçalho
  `X-Test-Google-Email-Verified` pode tornar falso.

## ADR-069 — Rate limit da API por conta, de verdade

**Contexto:** a auditoria de segurança da API (SEC-003) mostrou que o "por
usuário" do ADR-061 era por IP. O `UseRateLimiter` rodava antes do
`UseAuthentication`, e o esquema padrão é o cookie do Identity, não o Bearer.
Por isso o `context.User` chegava vazio ao limitador, e as políticas `sync` e
`api` caíam todas em `ip:…`. Numa instância isolada com `ApiPerMinute=3`, a
conta B levou 429 depois de a conta A gastar as 3. Um anônimo também levou
429. Atrás do proxy, ou num NAT, um cliente sem conta esgotava a API de todos.

**Decisão:** o rate limit tem **dois estágios**, cada um no ponto do pipeline
em que tem o que precisa:

```text
UseRouting
UseRateLimiter(borda)  ← por IP: teto global + login
UseAuthentication      ← o OpenIddict responde /connect/* aqui dentro
IdentifyApiCaller      ← Bearer validado pelo esquema do OpenIddict → sub
UseRateLimiter()       ← API: sub do token; sem token válido, balde anônimo do IP
UseAuthorization       ← 401/403
```

- **Borda, por IP, antes de qualquer autenticação.**
  - `GlobalPerMinute`, padrão 600, em toda rota (`0` desliga).
  - `AuthPerMinute` em `/account/*`, `/connect/authorize` e `/connect/token`.
  - Ela tem de vir antes porque o OpenIddict valida o pedido de token no
    `HandleRequestAsync` do `UseAuthentication`, e responde ali mesmo o
    `invalid_grant` e o `invalid_request`. Com o limitador depois, o teste de
    login (4 pedidos inválidos com `AuthPerMinute=2`) dava 400 em vez de 429.
- **API, depois do `UseAuthentication` e antes do `UseAuthorization`.**
  - Um passo próprio autentica com
    `OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme`, só nas rotas
    com a política `sync` ou `api`. Valida assinatura, validade e autorização
    não revogada.
  - O `sub` vai numa feature da requisição. Claim de token não validado nunca
    entra na chave.
  - O handler guarda o resultado, e a política `sync` da autorização o
    reaproveita: o token não é validado duas vezes.
  - Com `sub`, a partição é `u:{sub}`, como o ADR-061 queria.
  - Sem token, ou com token inválido, vencido ou revogado, a partição é
    `anon:{IP}`, com `AnonymousPerMinute` (padrão 30). O 401 que vem depois
    não gasta a cota de nenhuma conta.
- As duas instâncias do middleware leem a mesma metadata
  (`RequireRateLimiting`), e um nome de política desconhecido derruba a
  requisição. Por isso cada estágio declara as três políticas, e a que é do
  outro estágio não limita (`GetNoLimiter`).

### Alternativas

- **Mover o `UseRateLimiter` para depois de `UseAuthentication` e
  `UseAuthorization`** (a recomendação da auditoria).
  - O login deixaria de contar os erros de `/connect/*` (o teste acima).
  - Depois da autorização, quem falha na autenticação já levou 401 e nunca
    chega ao limitador, então não haveria como limitar anônimos.
  - Recusada.
- **Validar o Bearer antes do `UseAuthentication`, com um estágio só.** O
  handler de validação do OpenIddict só funciona depois dele, porque é ali que
  nasce a transação ("An error occurred while retrieving the OpenIddict
  validation context"). Medido: 32 dos 57 testes de integração deram 500.
- **Trocar o esquema padrão por um que escolha Bearer ou cookie pelo
  cabeçalho.** Mexeria no login do navegador e no `/connect/authorize` só
  para servir ao limitador. E o login continuaria fora do alcance de um
  limitador posto depois da autenticação.
- **Limitar o anônimo no `IAuthorizationMiddlewareResultHandler`, na hora do
  401.** Daria uma terceira forma de limitar, fora do middleware e das opções,
  com o 429 e o `Retry-After` escritos à mão.

### Consequências

- Os limites por IP (global, login e anônimo) dependem do
  `ForwardedHeaders__KnownProxies` (SEC-001/G-01). Sem ele, atrás do proxy,
  viram um balde só para o servidor inteiro. Os limites por conta não
  dependem.
- Um token válido é validado, inclusive a consulta da autorização no banco,
  antes da cota da conta. Não há como partir pela conta sem isso. O teto
  global, que vem antes, limita o volume por IP.
- Num NAT compartilhado com um abusador, quem está com o token vencido pode
  receber 429 do balde anônimo em vez de 401. O cliente espera o
  `Retry-After` e renova depois. Não perde dado.
- O balde anônimo é por política: `sync` e `api` contam separado.
- Os contadores continuam na memória de cada instância.

**Testes (Server.IntegrationTests, PostgreSQL real):**

- A gasta a cota da API, recebe 429 com `Retry-After` e `rate_limited`, e B,
  do mesmo IP, continua com 200.
- O mesmo no token bucket da sincronização (pull).
- Sem token e com token forjado, 401 até esgotar o balde anônimo e 429
  depois. A ainda faz as suas 3 chamadas.
- O teto global responde 429 com `Retry-After` em `/health`.
- O login continua com 429 nos pedidos inválidos de `/connect/token`.
- Os quatro primeiros falham com o pipeline antigo.

## ADR-070 — O servidor de sincronização só vai para a rede em Production, atrás de TLS

**Contexto:** a auditoria de segurança da API (SEC-001, ação G-01, P0)
encontrou o servidor de sincronização rodando numa máquina da LAN em
`ASPNETCORE_ENVIRONMENT=Development`. Nesse ambiente:

- o OAuth anda em HTTP: o `DisableTransportSecurityRequirement()` só é ligado
  em Development e nos testes;
- os tokens são assinados e cifrados com os certificados de desenvolvimento do
  OpenIddict, recriados no host. Cada recriação derrubava todos os refresh
  tokens: era o "pede para entrar de novo todo dia";
- não há HSTS nem redirect para HTTPS, e as migrations sobem com o app.

Quem enxerga a LAN captura o access token e o refresh token, e com o refresh
token tem a conta por até 30 dias. O código de produção já existia (PFX
próprios, HSTS, `KnownProxies`), mas não havia um jeito pronto de publicar com
ele, nem nada que impedisse Development de servir a rede.

**Decisão:**

1. **Uma implantação de produção pronta:** `deploy/sync-server/docker-compose.prod.yml`.
   - A API roda em Production, sem porta publicada, com disco só de leitura,
     sem capabilities e com `no-new-privileges`.
   - Na frente, o Caddy é o único com porta publicada (80 e 443). Ele termina
     o TLS e responde HTTP com 308.
   - O Caddy tem IP fixo numa sub-rede própria, e esse IP é o
     `ForwardedHeaders__KnownProxies__0`. Só dele a API aceita o
     `X-Forwarded-Proto`, e só com ele ela vê HTTPS: o HSTS, o issuer
     `https://` e o OpenIddict aceitando o OAuth dependem disso.
   - `AllowedHosts` é o nome público.
   - O PostgreSQL fica numa rede `internal`, sem porta e sem saída. As
     migrations são o serviço `migrate`, com o bundle da imagem.
   - O certificado TLS vem de uma CA própria do Caddy (`tls internal`, para a
     LAN), do Let's Encrypt ou de um arquivo, conforme `SYNC_TLS`.
2. **Segredos em arquivo.** Os PFX de assinatura e de cifragem, as senhas
   deles, a connection string e a chave secreta do Google são *secrets* do
   compose, montados em `/run/secrets`. O `Program` lê essa pasta com o
   `AddKeyPerFile`: o nome do arquivo é a chave (`ConnectionStrings__Sync`,
   `Sync__SigningCertificate__Password`). Nada disso aparece em `docker
   inspect`, no `prod.env` ou na imagem. O `.dockerignore` tira os segredos
   até do contexto do build, e também o `bin/obj` do host, que quebrava o
   restore dentro da imagem.
3. **Development não serve a rede.** O `DevelopmentLoopbackGuard` é um
   `IHostedLifecycleService`. No `StartedAsync`, depois que o Kestrel abriu as
   portas, ele lê os endereços (`IServerAddressesFeature`). Se o ambiente é
   Development e algum endereço não é `localhost`, 127.0.0.0/8, `::1` ou
   socket local, ele registra `SyncServerDevelopmentOnNetwork` em Critical e
   lança a exceção, e o host não sobe. O `StartedAsync` roda depois de todos
   os `StartAsync`, inclusive o do servidor, e antes de o host se dar por
   iniciado: a janela entre abrir as portas e recusar é a de um método.
   - A única exceção é explícita: `Development__AllowNonLoopback=true`. O
     compose de desenvolvimento a liga, porque dentro do contêiner a API
     escuta em todas as interfaces, mas a porta só é publicada no `127.0.0.1`
     da máquina.

### Alternativas

- **nginx no lugar do Caddy.** Exigiria emitir e renovar o certificado por
  fora (certbot) e escrever o redirect e os cabeçalhos à mão. O Caddy faz o
  redirect, o ACME e a CA interna da LAN com quatro linhas de configuração.
- **TLS no próprio Kestrel.** A imagem passaria a carregar e renovar
  certificado. O desenho do ADR-061 já previa o TLS no proxy, e a imagem fica
  igual para qualquer provedor.
- **Verificar os endereços antes de abrir as portas**, lendo `urls`,
  `ASPNETCORE_URLS`, `HTTP_PORTS` e `Kestrel:Endpoints`. São quatro fontes, e
  ainda há o `Listen` em código. O endereço que o Kestrel de fato abriu é a
  única fonte certa.
- **Recusar por requisição** (`Connection.LocalIpAddress` fora do loopback).
  O servidor subiria "bem" e falharia em silêncio para quem está na rede. Um
  erro na subida é visto por quem publicou.
- **Senhas em variável do `prod.env`.** Mais simples, mas aparecem no `docker
  inspect` e no ambiente de qualquer processo do contêiner.
- **Rotação sem derrubar o login**, com o PFX antigo e o novo registrados
  juntos. O OpenIddict aceita vários, mas a configuração teria de ganhar uma
  lista. Com certificados de 730 dias, a troca derruba o login uma vez a cada
  dois anos. Fica para quando isso pesar.

### Consequências

- **O servidor da LAN para de subir como está.** Em Development num IP da
  LAN, ele agora recusa. Ele precisa ir para o compose de produção, e o
  `portproxy` do `localhost:5280` deixa de fazer sentido.
- **Nome público obrigatório.** O Google só aceita redirect em HTTPS com
  domínio de TLD público. O `SYNC_HOST` é um nome assim, mesmo que aponte
  para um IP da LAN.
- **Confiar na CA interna.** Com `tls internal`, cada computador importa a
  raiz do Caddy (docs/sync-server.md §5). O celular só confia nas CAs do
  sistema (ADR-067): para ele, Let's Encrypt ou um certificado de CA pública.
- **Trocar os PFX derruba o login de todos uma vez.** O roteiro de rotação
  está em docs/sync-server.md §2.
- **Efeito colateral no SEC-007.** Com o Caddy só atendendo o `SYNC_HOST` e o
  `AllowedHosts` restrito, um `Host` forjado não chega ao discovery por esse
  caminho. O issuer fixo (G-07) veio depois: `Sync__Issuer`, obrigatório
  fora de Development (ADR-061, "Autenticação").
- O teste da guarda roda em Development e, por isso, faz o OpenIddict criar
  (ou reaproveitar) os certificados de desenvolvimento no repositório de
  certificados do usuário que roda a suíte.

**Testes:**

- **Server.IntegrationTests:** em Development, com `0.0.0.0`, `+`, um IP da
  LAN ou `[::]` ao lado de `localhost`, o servidor não sobe, e o log tem o
  Critical. Só com loopback, ele sobe. Com a exceção ligada, sobe em todas as
  interfaces. Fora de Development, a guarda não se aplica. E a tabela do que
  conta como loopback.
- **À mão, no compose de produção (17/10/2026, Docker Desktop):**
  - `http://` → 308 para `https://`;
  - HSTS `max-age=31536000`;
  - issuer `https://sync.test/`;
  - JWKS com `CN = MyTaskApp Sync Signing`;
  - `POST /connect/token` em HTTP direto na API, mesmo com `X-Forwarded-Proto`
    forjado → 400 ID2083;
  - PostgreSQL inalcançável pela rede do proxy;
  - `Host` forjado → 400;
  - a imagem em Development, escutando em `http://+:8080`, recusando subir sem
    a exceção.

## ADR-071 — O pull com teto por página, sem reenviar a família da tarefa

**Contexto:** a auditoria de segurança da API (SEC-005, ação G-04, P1) mediu
o pull com 1 tarefa e 1.000 ocorrências. Cada página de `limit=1` devolveu
cerca de 1.000 mudanças (480 KB, 700 ms), e o cursor andou uma entidade por
vez. Um dispositivo novo baixaria cerca de 480 MB para 1.001 entidades.

O `limit` só contava o log. Para cada item da página, o servidor:

- buscava os pais, uma consulta por referência, com entidades rastreadas pelo
  EF;
- se o item fosse tarefa, buscava **todas** as ocorrências dela.

O fecho era transitivo. A ocorrência da página puxava a tarefa de carona, e a
tarefa puxava todas as irmãs. O cursor só andava pelo log, então, com a tarefa
alterada depois das ocorrências, a família inteira voltava a cada página.
Qualquer usuário autenticado derrubava o servidor, e uma tarefa recorrente
longa deixava a sincronização lenta.

Duas garantias do ADR-061 precisam continuar valendo no cliente (desktop e
celular usam o mesmo motor):

- **O pai antes do filho.** O SQLite tem FK imediata de ocorrência para
  tarefa e de lançamento de horas para ocorrência. Um filho sem pai derruba a
  página três vezes, e na quarta vira `apply_failed`, que só sai pelo "Tentar
  de novo".
- **A tarefa com ocorrência.** Não há constraint. Mas a tarefa sem ocorrência
  some das listas, que partem da ocorrência, e os casos de uso esperam uma.
  Basta **uma**: cada ocorrência que chega depois põe a tarefa no reconcile,
  que recalcula a conclusão e os lembretes.

**Decisão:** a página do pull continua fechada, mas só com o que o cliente
pode não ter. Os relacionados vêm em lote, e a resposta inteira tem teto.

- **"Já entregue" por evidência.** O cliente tem `X` se alguma entidade viva
  que aponta para `X` tem `LastSeq` até o cursor. Quando esse filho foi
  entregue, `X` foi junto. Por indução, a tarefa entregue já tem ocorrência
  local. Do zero, nada foi entregue. O erro possível só vai para o lado de
  mandar a mais.
- **Pai de carona** só se estiver vivo, depois do cursor, fora da página e sem
  evidência de entrega. Isso vale em cadeia: o lançamento de horas puxa a
  ocorrência, que puxa a tarefa. O pai que o cliente já tem não volta a cada
  página.
- **Companheiro** (`TravelsWithParent`): a tarefa sem evidência de entrega e
  sem ocorrência viva na página leva **uma** ocorrência, a viva de menor
  `LastSeq` depois do cursor. Se houver alguma na janela da página, é ela,
  então nada se repete. As outras vêm pelo próprio log.
- **Em lote, sem rastreamento.** Os relacionados são carregados por nível a
  partir da página:
  - os pais por `WHERE EntityId IN (…)`;
  - a primeira ocorrência de cada tarefa num `DISTINCT ON` só;
  - a evidência de entrega numa consulta só, por `SyncReferences` ⋈
    `SyncEntities`.

  Tudo com `AsNoTracking`. São algumas consultas por página, qualquer que seja
  o tamanho da família. A montagem em si é memória pura (`PullPageBuilder`).
- **Teto da resposta** (`Sync:Pull`, `PullLimits`): `MaxEntities` (padrão
  1.000) e `MaxBytes` (padrão 1 MiB, somando o JSON de cada mudança), contando
  o que vem de carona.
  - A página percorre o log em ordem e corta antes do item cujo fecho
    passaria do teto. O cursor seguinte é o do último item incluído, e
    `hasMore` fica verdadeiro.
  - O primeiro item sempre entra, com o que ele exige, para o cursor andar.
    Só ele, sozinho, passa do teto: uma tarefa com descrição maior que
    `MaxBytes` vai numa página só dela. O teto por entidade é do G-05.

O contrato não muda de forma, e o `SyncSchema.Current` não sobe. O cliente já
segue `hasMore` e `NextCursor`, não espera a página cheia e ignora pela versão
o que já tem.

### Alternativas

- **Guardar no cursor o que foi de carona.** O cursor deixaria de ser um
  número e cresceria com a família. A evidência pelo próprio banco dá o mesmo
  sem estado no cliente.
- **Nenhum companheiro: as ocorrências só pelo log.** Uma tarefa nova cortada
  no fim da página chegaria sem ocorrência e ficaria invisível até a página
  seguinte. Uma ocorrência basta, e na prática é a que já viria.
- **Só o teto, mantendo a família inteira.** Com 1.000 ocorrências e teto
  menor, a tarefa não caberia em página nenhuma. E o reenvio a cada página
  continuaria.

### Consequências

- Uma tarefa recorrente pode chegar com parte das ocorrências e completar nas
  páginas seguintes do mesmo ciclo. Entre uma página e outra, a conclusão
  derivada reflete o que já chegou.
- 1 tarefa e 1.000 ocorrências custam 1.002 entidades no total. Antes, era
  cerca de 1.000 por página.
- Uma linha que falhou na aplicação isolada (`apply_failed`) não ganha mais
  o pai de carona a cada página: ele só volta quando mudar ou pelo "Tentar de
  novo". Antes, o reenvio constante às vezes a curava sozinho.
- O `FindAsync` rastreado ficou só para o push, onde a escrita precisa dele.

**Testes:**

- **Server.Application:**
  - 1 tarefa e 1.000 ocorrências, com a tarefa antes e depois delas, em
    `limit=1` e no padrão: 1.001 distintas, no máximo 1.002 no total;
  - o teto de entidades com o cursor sempre andando;
  - o teto de bytes, com o item maior que ele indo sozinho;
  - o pai já entregue fora da carona;
  - o lançamento de horas com a ocorrência e a tarefa;
  - o número de leituras do store, constante.
- **Server.IntegrationTests (PostgreSQL real):**
  - a reprodução da auditoria pelo HTTP, com no máximo 2 mudanças por página;
  - `Sync:Pull:MaxEntities=50` respeitado;
  - dois desktops com teto de 3 entidades: tarefas, etiqueta, horas,
    ocorrências concluídas e tarefas renomeadas chegam inteiras, sem
    `apply_failed`.

## ADR-072 — O push só guarda o contrato, com teto por entidade e cota por workspace

**Contexto:** a auditoria de segurança da API (SEC-006, ação G-05) mostrou que o
push aceitava qualquer nome de campo e o mesclava no `jsonb` da entidade. Na
instância isolada, três pushes com um campo desconhecido de 300 KB deixaram uma
tarefa com cerca de 900 KB. `isAdmin`, `role`, `userId` e `createdByUserId`
foram aceitos, gravados e devolvidos no pull. Não tiveram efeito de
autorização, mas ficaram gravados. Nada limitava o tamanho de uma entidade nem
do workspace. O `SyncFieldValidator` deixava passar o campo desconhecido de
propósito: o ADR-062 contava com isso para um cliente mais novo mandar um campo
que o servidor ainda não conhecia.

Antes de mudar, conferimos quem manda campo extra:

- **No código:** ninguém. O desktop e o celular projetam só as colunas do
  mapper (`SyncEntityMapper.Project` lança se a coluna não está no contrato), e
  o `SyncRecorder` só difere as chaves da projeção. O histórico de
  `Definitions/` só tem adições (`placement` na 1.13, `repositoryUrl` na 1.15),
  nenhuma remoção ou renomeação. Uma versão antiga manda um subconjunto do
  contrato de hoje.
- **No servidor real (17/10/2026, consulta só de leitura):** toda chave
  gravada, de topo, de objeto e de item de `parameters`, está no registro. São
  677 entidades e 746 KB no único workspace. A maior entidade tem 38 KB, e a
  maior descrição, 33.442 caracteres.

**Decisão:**

- **Campo fora do registro recusa a operação inteira**, com o código novo
  `unknown_field` e os caminhos na mensagem (`isAdmin`, `reminder.role`,
  `parameters[1].userId`). Vale para o topo, para o membro de objeto e para o
  membro do item de lista (`SyncFieldValidator.FindUnknown`). O
  `SyncFieldValidator.Validate` continua ignorando o desconhecido, porque o
  desktop o roda no pull. Essa recusa vem antes da do U+0000 (SEC-008): o
  campo desconhecido com NUL leva `unknown_field`, e o NUL do nome vai escrito
  como `\0` na frase, que também é gravada no servidor.
- **Por operação, e não por campo.** O cliente de hoje transforma um
  `RejectedField` de motivo desconhecido em conflito visível e adota o valor do
  servidor. Um `Error` com código novo cai no caminho genérico: a operação fica
  como falha, com a mensagem do servidor na janela da sincronização. Isso já
  funciona em toda versão instalada, sem mudar o cliente.
- **Campo novo no contrato: o servidor é atualizado antes dos clientes.** Isso
  revê o ADR-062. `SyncSchema.Current` continua sem subir para campo novo; o
  que muda é a ordem da implantação, que o docs/sync-server.md já pedia.
- **O que um servidor de antes guardou fora do contrato sai na próxima
  escrita** da entidade (só o topo). No servidor real não havia nada.
- **Máximo de texto:** `command.parameters[].options` ganha 25.049, as 50
  opções de 500 caracteres, uma por linha. O valor vem de
  `CommandParameterSpec.MaxOptionsLength`, que as regras do domínio já
  implicavam. **A descrição da tarefa continua sem máximo de campo**, a pedido
  do usuário: ela guarda textos longos, e o domínio continua sem teto (o
  `TaskItem.NormalizeDescription` não mudou). Quem a segura no servidor é o
  teto da entidade.
- **Teto por entidade, depois da mescla** (`entity_too_large`): o JSON que
  seria gravado, com as opções do contrato e em bytes UTF-8, contra
  `Sync:Push:MaxEntityBytes`, padrão 768 KiB. É o maior que ainda cabe no corpo
  de 1 MiB do Kestrel, já que a criação manda tudo junto. Com acentos, cabem
  cerca de 250 mil caracteres de descrição; em texto sem acento, cerca de 750
  mil.
- **Cota por workspace** (`quota_exceeded`): `Sync:Push:MaxWorkspaceEntities`
  (padrão 100.000 entidades vivas) e `Sync:Push:MaxWorkspaceBytes` (padrão
  256 MiB, somando o JSON de todas as linhas, tombstones incluídos).
  - O uso é lido uma vez por push, com o workspace já travado, e acompanhado em
    memória a cada gravação, inclusive nas cascatas. A soma vem de uma coluna
    nova, `SyncEntities.DataBytes`. A migração `EntityDataBytes` a preenche com
    `octet_length("Data"::text)`, que conta um pouco a mais por causa dos
    espaços do `jsonb`; a próxima escrita de cada entidade acerta.
  - **Quem não cresce sempre passa**: atualizar sem aumentar, encolher e
    excluir funcionam acima da cota, para sempre dar para liberar espaço.
  - O tombstone não conta como entidade. Nada o apaga, e contá-lo prenderia
    quem excluiu muito. Ele pesa só nos bytes.
  - A recusa por cota não fica guardada para a idempotência, como o
    `parent_missing`: depende do que o workspace ocupa na hora, e o mesmo
    `opId` reenviado depois de liberar espaço é aplicado.
- Zero desliga qualquer um dos três tetos.

### Alternativas

- **Ignorar o campo desconhecido em silêncio** (não guardar e aplicar o resto).
  Seria compatível com um cliente mais novo que o servidor, mas esse cliente
  acharia que o valor foi sincronizado quando não foi. Como nenhuma versão em
  uso manda campo extra, a recusa explícita não quebra ninguém.
- **Guardar o desconhecido com teto de quantidade e de bytes** (a outra
  recomendação da auditoria). Manteria o ADR-062 como era, mas o servidor
  continuaria guardando e distribuindo o que não valida, e isso pesaria quando
  houver workspace compartilhado.
- **Recusa por campo (`RejectedField`).** O cliente atual a trata como conflito
  e adota o valor do servidor: um campo conhecido recusado com valor nulo
  apagaria o dado local.
- **Máximo de campo para a descrição** (50.000 caracteres, alinhado com um teto
  novo no domínio). O usuário preferiu manter os textos longos; o teto da
  entidade já impede o crescimento sem limite.
- **Contadores na linha do workspace** em vez de somar `DataBytes`. Seriam
  mais baratos, mas exigem manter dois números certos em toda escrita,
  inclusive nas cascatas, e corrigir o desvio se algum dia divergirem. A soma
  pela chave primária, uma vez por push, custa pouco no tamanho de hoje.
- **Contar os tombstones na cota de entidades.** Nada os apaga, então excluir
  não liberaria espaço.

### Consequências

- Um cliente mais novo que mande um campo que este servidor não conhece recebe
  `unknown_field`, e a entidade para de sincronizar naquele computador até o
  servidor ser atualizado. Por isso o servidor vem antes.
- Uma tarefa com descrição acima do teto fica só no computador onde foi
  escrita. A operação aparece como falha na janela da sincronização, com o
  tamanho e o teto, e não some em silêncio. Os dois tetos são configuráveis.
- Um comando com 50 parâmetros de escolha, todos com 50 opções de 500
  caracteres, é válido no domínio e passa do teto da entidade. Na prática não
  acontece. Se acontecer, ele fica local, como a descrição grande.
- Excluir o que nunca chegou ao servidor ainda grava um tombstone fora da cota
  de entidades. É uma linha pequena, que conta nos bytes, e o rate limit
  (ADR-069) segura o volume. Uma limpeza de tombstones antigos fica para
  depois.
- O pull pagina por bytes (ADR-071, `Sync:Pull:MaxBytes`, 1 MiB). Uma entidade
  de até 768 KiB cabe numa página sozinha.

**Testes:**

- **Sync.Contracts.Tests:** o `FindUnknown` aponta o campo, o membro de objeto
  e o membro do item de lista, e fica vazio no que o contrato conhece. O
  `Validate` continua ignorando o desconhecido. `options` com 25.050
  caracteres é recusado, e uma descrição de 100 mil caracteres passa no campo.
- **Infrastructure.Tests:** os limites que só o domínio tem (membros de
  `parameters`, `options`, conteúdo do post-it) são iguais no contrato, e a
  descrição continua sem máximo.
- **Server.Application.Tests:**
  - criação e atualização com campo fora do contrato não gravam nada;
  - `isAdmin`, `role`, `userId` e `createdByUserId` são recusados, e um membro
    estranho dentro de `placement` também;
  - a chave legada some na próxima escrita;
  - a entidade acima do teto é recusada na criação e na atualização, e a
    versão anterior fica;
  - a cota de entidades recusa a próxima criação, e a atualização e a exclusão
    passam;
  - a cota de bytes recusa o que cresce e deixa encolher;
  - a recusa por cota não é reaproveitada pelo reenvio.
- **Server.IntegrationTests (PostgreSQL real):**
  - o campo fora do contrato é recusado no push e não volta no pull;
  - `DataBytes` é gravado e o uso soma vivos e bytes;
  - a migração preenche o tamanho do que já estava gravado;
  - pela API, `Sync:Push:MaxEntityBytes` vem da configuração e recusa com
    `entity_too_large`.

## ADR-073 — As chaves do Data Protection cifradas no banco, com um PFX fora dele

**Contexto:** a auditoria de segurança da API (SEC-011, ação G-11, P2)
apontou que o servidor de sincronização grava as chaves do Data Protection
(`PersistKeysToDbContext`) sem `ProtectKeysWith*`. O XML de cada chave fica em
claro na tabela `DataProtectionKeys`. Essas chaves protegem o cookie do
Identity, o cookie do retorno do Google e o token antiforgery. Quem lê o
banco, ou um backup dele (SEC-002), forja o cookie do login de qualquer conta,
conclui o `/connect/authorize` e recebe tokens da API: passa da leitura do
banco à escrita pela API.

As chaves ficam no banco de propósito (ADR-061): várias instâncias atrás de um
balanceador precisam da mesma chave. O que faltava era cifrá-las com algo que
não esteja no banco.

**Decisão:**

1. **Um terceiro PFX, só para isso:** `Sync:DataProtectionCertificate`
   (`Path` ou `Base64`, e `Password`), carregado como os PFX do OpenIddict,
   vai no `ProtectKeysWithCertificate`. Cada chave nova vai
   para o banco com o segredo num `encryptedSecret`, e só quem tem o PFX a abre.
   - Em Production, sem ele o servidor não sobe. A conferência vem antes da do
     OpenIddict.
   - Em Development e nos testes, ele é opcional, e sem ele tudo continua como
     antes.
   - No compose de produção, ele é um *secret*, como os outros (ADR-070).
2. **Rotação sem derrubar o login:** `Sync:DataProtectionPreviousCertificates`
   (uma lista no mesmo formato) vai no `UnprotectKeysWithAnyCertificate`. Os
   anteriores só decifram as chaves que eles cifraram. A chave nova sai sempre
   com o atual. Como o Data Protection troca de chave a cada 90 dias, depois
   desse prazo o anterior pode sair.
3. **A chave gravada em claro é revogada ao subir.** Ligar o certificado não
   cifra o que já está na tabela, e a chave em claro seria a chave em uso por
   até 90 dias: quem leu o banco continuaria forjando o cookie. Com o
   certificado configurado, o `PlaintextKeyRevoker` (um `IHostedService`) lê as
   linhas de `DataProtectionKeys` e revoga, pelo `IKeyManager`, toda chave sem
   `encryptedSecret` que ainda não foi revogada. Ele registra
   `SyncServerPlaintextKeysRevoked` em Warning, sem o XML. O Data Protection
   gera a próxima chave já cifrada. Isso vale também para um backup antigo
   restaurado. Como a chave já revogada fica de fora, as linhas não crescem a
   cada subida.
   - **Ele parte antes do serviço do próprio Data Protection.** O
     `DataProtectionHostedService` carrega o key ring ao subir. No .NET 10, um
     ring invalidado (a revogação o invalida) continua sendo entregue enquanto
     se atualiza em segundo plano. Carregado antes da revogação, o primeiro
     login ainda sairia com a chave em claro. Por isso o revogador é
     registrado antes do `AddDataProtection`. O teste pegou isso só sob carga,
     na suíte inteira.

### Alternativas

- **Cofre de chaves (Azure Key Vault, AWS KMS).** É o caminho da documentação
  da Microsoft para nuvem, mas o servidor roda num host próprio com Docker, sem
  cofre. O PFX como *secret* do compose é o mesmo processo dos do OpenIddict.
- **Reaproveitar o PFX de cifragem do OpenIddict.** Seria um arquivo a menos,
  mas trocar um obrigaria a trocar o outro, e o PFX dos tokens tem o próprio
  roteiro de rotação (ADR-070). Separados, cada um gira quando precisa.
- **Só documentar um `DELETE` das chaves antigas.** Seria menos código, mas
  dependeria de quem opera lembrar, e de lembrar de novo a cada backup
  restaurado. A revogação ao subir não esquece.
- **Revogar todas as chaves ao subir com o certificado pela primeira vez.**
  Isso exigiria guardar em algum lugar que "já foi feito", e revogaria também
  as chaves cifradas de outra instância. Revogar só as que estão em claro não
  precisa de estado.
- **Recusar subir com chave em claro na tabela.** O operador teria de apagar à
  mão, e o servidor ficaria fora do ar até isso. A revogação resolve sem
  parar.

### Consequências

- **Production pede mais um PFX.** Quem atualiza sem ele vê o servidor
  recusar a subida, com a mensagem dizendo qual variável falta.
- **Na primeira subida com o certificado**, quem estava no meio de um login (o
  cookie dura 10 minutos) volta ao Google uma vez. Os tokens dos aparelhos são
  do OpenIddict, assinados e cifrados com os PFX dele, e não dependem dessas
  chaves: ninguém precisa conectar de novo.
- **A chave revogada continua na tabela, em claro.** Ela não vale mais, mas
  continua no banco e nos backups. docs/sync-server.md §2 tem o `DELETE`
  opcional para tirá-la.
- **Trocar o PFX sem configurar o anterior** deixa as chaves antigas
  ilegíveis. O Data Protection as deixa de lado e gera uma nova. É o roteiro
  de emergência, com o mesmo custo da revogação.
- **O PFX não vai junto com o backup.** O backup sozinho não abre as chaves.
  Restaurado sem o PFX, o servidor gera chaves novas.

**Testes (Server.IntegrationTests, PostgreSQL real):**

- **Com o certificado**, login e logout (antiforgery) funcionam, e toda linha
  `key` de `DataProtectionKeys` tem `encryptedSecret`.
- **Sem o certificado, fora de Production**, o login funciona e a chave fica
  em claro.
- **Chave em claro de antes:** subir com o certificado a revoga, o primeiro
  `Protect` logo depois de subir já usa outra chave, a chave nova sai cifrada,
  login e logout funcionam, e subir de novo não revoga outra vez.
- **Rotação:** um payload protegido com o PFX antigo abre no servidor com o
  novo e o antigo na lista dos anteriores.
- **Emergência:** sem o antigo, o payload não abre, e o login funciona com uma
  chave nova cifrada.
- **Production sem o certificado** não sobe. Com ele, a subida passa à
  conferência dos PFX do OpenIddict.
- Desligar o `ProtectKeysWithCertificate` faz falhar três desses testes, e
  desligar a revogação faz falhar o da chave em claro.

## ADR-074 — Consentimento no `/connect/authorize` fora do login recém-feito, e loopback com porta efêmera

**Contexto:** a auditoria de segurança da API (SEC-018, ação G-18, P3) levantou
a hipótese de que bastava uma navegação vinda de outro site para o servidor
emitir um código em silêncio. O motivo:

- os clientes eram `ConsentTypes.Implicit`;
- o `/connect/authorize` emitia o código sempre que havia sessão do Identity;
- essa sessão vive 10 minutos, e o cookie é `SameSite=Lax`, que o navegador
  manda numa navegação de primeiro nível com GET, mesmo vinda de outro site.

A hipótese foi reproduzida num Chrome de verdade (headless, guiado por CDP),
contra uma instância isolada com o Google falso dos testes:

1. A pessoa conectou o desktop e a sessão ficou aberta.
2. Uma página em `localhost` (outro site) fez `location.href` para o
   `/connect/authorize`.
3. O pedido saiu com `Sec-Fetch-Site: cross-site`, levou o cookie, recebeu um
   302 para `127.0.0.1:47833/callback?code=…` e gravou um dispositivo com o
   nome escolhido pela página: "Dispositivo do terceiro".
4. O POST de outro site, enviado sozinho, não levou o cookie
   (`SchemefulSameSiteLax`) e caiu no login.

O código vai para a máquina da própria pessoa, e o PKCE impede a troca sem o
verificador. Sobram dois problemas:

- o dispositivo indesejado, com o nome que o terceiro escolheu (SEC-015);
- a porta fixa: qualquer processo que escute a 47833 recebe os códigos do
  navegador.

**Decisão:**

1. **Sem pergunta, só o login que o próprio pedido começou.**
   - **Marca da sessão.** No `/account/external-callback`, quando o
     `returnUrl` é um `/connect/authorize`, a sessão do Identity abre com a
     claim `mytaskapp_authorize_flow`. Ela guarda o SHA-256 de `client_id`,
     `redirect_uri`, `state`, `code_challenge` e `device_id` daquele pedido.
   - **Uso único.** O authorize só emite sem interação quando a claim bate
     com o pedido atual. Aí a marca é gasta: o cookie é regravado sem ela,
     com o mesmo vencimento. Abrir de novo o mesmo endereço já pergunta.
   - **Por que basta.** O `state` e o `code_challenge` são sorteados pelo app
     a cada login. Uma navegação de outro site não sabe quais foram, e uma
     sessão aberta por outro pedido não serve a este.
2. **Fora disso, uma tela de consentimento mínima.**
   - **Conteúdo.** Diz o dispositivo, o app e a conta, e tem **Conectar** e
     **Cancelar**. O formulário devolve, num POST para o próprio
     `/connect/authorize`, o pedido inteiro em campos ocultos e o token do
     antiforgery.
   - **Cancelar.** Leva `access_denied` à volta do app, e o app trata como
     recusa.
   - **POST sem antiforgery válido.** Leva 400, e nada é gravado.
   - **Resposta na query.** `consent` na query de um GET não conta.
   - **`prompt=none`.** Sem a marca, leva `consent_required`.
   - **Headers.** A página vai com `X-Frame-Options: DENY` e CSP
     `default-src 'none'; form-action 'self' <volta>; frame-ancestors 'none'`.
     A `<volta>` é a origem do `redirect_uri` (`http://127.0.0.1:<porta>`) ou o
     esquema do app do celular. O Chrome aplica o `form-action` também ao 302
     que vem depois do POST, e sem a volta ele bloquearia a chegada do código.
     O `redirect_uri` já foi validado pelo OpenIddict antes do passthrough.
   - **Sem HTTPS.** O cookie do antiforgery só anda por HTTPS fora dos testes.
     Por HTTP, a tela responde 400 dizendo isso, como o logout (SEC-013).
3. **Loopback com porta efêmera (RFC 8252 §7.3).**
   - **Servidor.** Os dois clientes passam a ser `ApplicationTypes.Native`, e
     o desktop é registrado como `http://127.0.0.1/callback`, sem porta.
     - O OpenIddict 7 já faz a comparação relaxada nesse caso: aceita
       qualquer porta não padrão quando o cliente é nativo, o registrado não
       tem porta e os dois são loopback com o mesmo esquema, host e caminho.
     - Não há handler próprio.
     - O `localhost` saiu: a RFC 8252 §8.3 não o recomenda, e o desktop nunca
       o usou.
   - **Desktop.** O `Sync:CallbackPort` passa a ter `0` como padrão: uma
     porta efêmera por login. O `redirect_uri`, no pedido e na troca do
     código, vem da sessão aberta (`ISyncAuthorizationSession.RedirectUri`),
     com a porta que o `LoopbackCallbackListener` de fato abriu. Fixar uma
     porta continua possível pela configuração.
   - **Celular (ADR-067).** Usa o esquema próprio e não o loopback; nada muda
     para ele. O login normal dele também sai sem pergunta, porque acabou de
     passar pelo Google.

O caminho de sempre, de "Conectar com o Google" no app até o código, não ganha
clique novo: ele passa pelo Google, onde o `select_account` já pedia um, e
volta ao mesmo pedido.

### Alternativas

- **Consentimento sempre.** É o mais simples e o mais forte, mas pede um
  clique a mais em todo login, logo depois de escolher a conta no Google, para
  proteger um caso que a marca da sessão já fecha.
- **`ConsentTypes.Explicit` do OpenIddict, com autorização permanente.** O
  OpenIddict guarda a autorização e não pergunta de novo, mas o que ele
  lembra é "este app nesta conta", e o que precisa de pergunta é cada
  dispositivo novo. Aqui cada login já cria uma autorização por dispositivo.
- **Sair da sessão logo depois do código.** Acabaria com a sessão que sobra,
  mas o celular e o desktop conectados em seguida voltariam ao Google duas
  vezes. Também não cobre a sessão que sobra quando o authorize recusa algo
  (dispositivo revogado, `device_id` ruim) antes de emitir.
- **Recusar pelo `Sec-Fetch-Site`/`Origin`/`Referer`.** O navegador pode não
  mandar esses headers, e uma navegação pela barra de endereço se parece com
  a de um link. Seriam uma heurística, não uma prova de que o pedido saiu do
  app.
- **Handler próprio para a porta do loopback.** Desnecessário: a comparação
  relaxada já existe no `OpenIddictApplicationManager` 7.x.

### Consequências

- **Ordem de implantação: servidor primeiro.**
  - O desktop antigo (porta 47833) continua entrando no servidor novo, porque
    a 47833 é uma porta como outra no loopback.
  - O desktop novo num servidor antigo recebe "redirect_uri inválido" no
    navegador. O servidor antigo só conhece a 47833, e não há como o app
    perceber isso e tentar a porta antiga.
- **Uma sessão que sobrou mostra a tela.** Acontece quando se conecta outro
  dispositivo em menos de 10 minutos, ou quando um login parou no meio. É um
  clique a mais, que só aparece fora do caminho normal.
- **Development por HTTP não mostra a tela** (400, como o logout). O caminho
  normal continua funcionando nele.
- **Risco residual: o fluxo começado por um terceiro sem sessão.** Ele cai no
  Google, que pede a escolha da conta (`select_account`). A pessoa que
  escolher a conta numa tela que ela não pediu cria um dispositivo com o nome
  que o terceiro escolheu. O código, de novo, só vai para o loopback dela,
  sem o verificador.
- **Revogar o dispositivo indesejado** continua sendo pela lista de
  dispositivos (ADR-061).
- **Configurações com `Sync__DesktopRedirectUris__*`** continuam somando à
  padrão. Uma volta com porta registrada ali passa a ser redundante.

**Testes:**

- **`ConsentTests`** (Server.IntegrationTests, PostgreSQL real):
  - navegação de outro site com a sessão aberta: tela, CSP e
    `X-Frame-Options`, sem `Location` e sem dispositivo;
  - o mesmo endereço aberto de novo depois do código;
  - a sessão aberta pelo login de outro pedido;
  - Conectar com antiforgery: código, troca e apelido;
  - Conectar sem antiforgery: 400 e nada gravado;
  - Cancelar: `access_denied`;
  - `consent=accept` na query não pula a pergunta;
  - `prompt=none`: `consent_required`;
  - a CSP do celular com o esquema do app.

  Os nove falham com a decisão desligada.
- **`ApiTests`:**
  - qualquer porta no `127.0.0.1` dá código, inclusive a 47833 antiga, e a
    troca exige a mesma porta;
  - `localhost`, `https`, outro caminho e outro host do loopback continuam
    recusados.
- **`TwoDeviceSyncTests` e `PhoneSyncTests`:** o desktop de ponta a ponta, na
  porta efêmera padrão e sem nenhuma volta extra configurada no servidor.
- **`SyncClientTests`:** o `redirect_uri` é a porta que o desktop abriu, e a
  troca do código manda o mesmo.

## ADR-075 — Celular: os temas do desktop, concluir sem acidente e o teclado que fecha

**Contexto:** usando o app no iPhone, quatro queixas:

1. **O teclado não fechava.** Nada no app o tirava da frente:
   - tocar fora do campo e rolar a tela não faziam nada;
   - o teclado do iOS não tinha botão "OK";
   - o Return do título não fazia nada, e o da descrição só pulava linha.

   No iOS não existe o "voltar" do Android, então quem abria a descrição
   ficava com metade da tela coberta.
2. **O estado da sincronização não aparecia.** Ele era só o texto de um
   `ToolbarItem` ligado por binding, sem cor, e no iOS esse texto não
   aparece de forma confiável.
3. **Feio, e fora dos temas do app.** O celular tinha uma cópia à mão de
   Carvão e Papel (`Colors.xaml`), em pares `AppThemeBinding`, e seguia o
   sistema. As abas não tinham ícone, e cada cartão levava um botão de texto
   "Concluir".
4. **Arrastar concluía sem querer.** O `SwipeView` estava em
   `Mode="Execute"`: o arrasto até o fim concluía, sem confirmação nem
   desfazer. No iOS nem o aviso aparecia, porque o toast só existia no
   Android.

**Decisão:**

1. **Os sete temas do desktop, mais "Automático"** (ADR-041).
   - **Catálogo.** `MobileThemeCatalog` (Mobile.Core) tem os mesmos ids,
     nomes, descrições e cores do `ThemeCatalog`, em texto: o núcleo do
     celular não pode depender do Avalonia.
   - **Cópia vigiada.** `ThemeParityTests`, no projeto de testes do desktop,
     compara cor a cor. Com as paletas iguais, o `ThemeContrastTests` vale
     para o celular. O projeto do desktop continua sem conhecer o celular;
     só o de testes referencia os dois.
   - **Aplicação.** `MauiThemeService` grava cada papel da paleta como um
     recurso de mesmo nome (`Canvas`, `Accent`…) e as telas o pedem por
     `DynamicResource`. Trocar o tema troca os valores, e a tela acompanha
     na hora.
   - **Variante do sistema.** Um tema escolhido fixa o `UserAppTheme`
     (claro/escuro), para teclado, barra de status e seletores nativos
     ficarem na mesma variante da paleta. "Automático" deixa o sistema
     decidir: Papel no claro, Carvão no escuro, e acompanha a troca ao vivo.
     Não há alto contraste automático, como o desktop faz com o Windows.
   - **Escolha.** Fica na aba Conta, seção "Aparência": uma linha por tema,
     com a amostra (fundo e destaque) e o visto no ativo. Vale ao tocar e
     fica nas `Preferences` do aparelho, chave `theme`, como o `widget.json`
     do desktop. Não sincroniza.
2. **Arrastar só revela o botão.** O `SwipeView` passou a `Mode="Reveal"`:
   o arrasto mostra "Concluir" (ou "Reabrir"), e nada muda até tocar nele.
   - **No cartão.** O botão de texto deu lugar a um círculo de marcar à
     esquerda, como nos Lembretes do iOS: 24 pontos de desenho e 48 de toque.
     Ele fica cheio, com o visto, na concluída.
   - **"Desfazer" em vez de pergunta.** Depois de concluir ou reabrir pela
     lista, a barra "Tarefa concluída · Desfazer" fica 8 s na tela (com 5 s,
     no teste no emulador, ela sumia antes do toque).
     "Desfazer" roda o caso de uso oposto na mesma ocorrência. Uma barra nova
     substitui a anterior e recomeça o prazo.
   - **Igual nas duas plataformas.** A barra é da própria tela, não um aviso
     do sistema. Sair da lista a fecha.
3. **O teclado sai da frente.**
   - **Tocar fora.** `HideSoftInputOnTapped` na lista e no formulário.
   - **"OK" em cima do teclado (iOS).** Em todo `Entry`, `Editor` e
     `SearchBar`, por `AppendToMapping` nos handlers, há um `UIToolbar` com o
     "OK" do sistema. O `Info.plist` declara `pt-BR`, e os botões do sistema
     ("OK", "Cancelar" da busca) saem em português.
   - **Arrastar para baixo (iOS).** Toda rolagem tem
     `KeyboardDismissMode = Interactive`, menos o `UITextView` da descrição,
     que rola o próprio texto.
   - **Return.** O título usa `ReturnType="Done"` e tira o foco; o "Buscar"
     da busca também.
4. **A sincronização à vista.** Uma pílula no topo da lista, tocável, que
   leva à aba Conta.
   - **Bolinha.** Tem as cores do ADR-063: sucesso (sincronizado), atenção
     (pendente), informação pulsando (sincronizando), perigo (atenção), anel
     de atenção (sem conexão) e anel apagado (pausada, só neste celular).
   - **Frase.** É o título do `SyncStatusText` e, quando está em dia, a idade
     ("Sincronizado · há 2 min"), na escala do desktop. Um relógio de 30 s
     só refaz esse texto.
   - **Aba Conta.** Passou a ouvir `CycleStarted` e `ChangeQueued`, e mostra
     "Sincronizando…" enquanto o ciclo corre.
5. **Polimento.**
   - Ícones nas abas (SVG pintado pela barra).
   - A barra do topo na cor do fundo.
   - Etiquetas com fundo suave na cor delas.
   - Sombra no botão +.
   - No iOS, o aviso curto ("Tarefa salva") passa a existir: uma etiqueta
     acima das abas, que o leitor de tela anuncia.

### Alternativas

- **Perguntar "Concluir esta tarefa?" a cada arrasto.** Protege, mas pede um
  toque a mais em todo uso certo. Revelar e desfazer protege o arrasto
  acidental e o toque errado, sem cobrar do uso normal.
- **Tirar o arrasto.** Perderia o gesto que quem usa iOS espera.
- **Paletas num projeto comum ao desktop e ao celular.** Exigiria tirar o
  `Avalonia.Media.Color` do `ThemePalette` e mexer no desktop inteiro por
  uma cópia que o teste já vigia.
- **Sincronizar o tema.** O desktop guarda o tema por computador, e não na
  conta. Um tema por aparelho continua coerente com isso.
- **CommunityToolkit.Maui** (Snackbar, `HideKeyboardAsync`). Seria mais uma
  dependência para três coisas pequenas que o MAUI e o UIKit já fazem.

### Consequências

- O cronômetro parado ao concluir **não volta** com "Desfazer": a tarefa
  reabre, o tempo não. É o mesmo de "Reabrir".
- As cores do celular vêm do código. O XAML usa só `DynamicResource` para
  cor, e um `StaticResource` de cor não acharia nada.
- **Nenhuma cor do tema passa por gatilho**, e foi o emulador que mostrou
  por quê.
  - **O defeito.** No Android, numa linha reciclada da lista (e num
    `Button`), o `DataTrigger` que sai devolvia a cor do tema de **antes** de
    uma troca. Títulos ficavam escuros sobre fundo escuro.
  - **Estados com cor.** O círculo, os atalhos e as etiquetas marcadas viram
    duas views alternadas por `IsVisible`, cada uma com o seu
    `DynamicResource`.
  - **Cor que depende de um valor.** Prioridade, prazo e a bolinha usam
    conversores que leem a cor do tema na hora. Na troca de tema, a lista é
    relida e a bolinha repintada.
  - **Gatilho que fica.** Só o que não é cor do tema, como a opacidade do
    título concluído.
- **Android, barra do topo.** A `AppBarLayout` do Shell vai por baixo da
  barra de status e era pintada com o `colorPrimary` do pacote do MAUI
  (#2C3E50). Um `ShellRenderer` próprio (`ThemedShellRenderer`) a pinta com a
  cor da barra e tira a sombra. Os ícones da barra de status seguem o claro ou
  escuro do tema.
- O teclado do iOS, o "OK" e o aviso curto foram escritos sem Mac local: o
  CI do iOS (`mobile-ios.yml`) compila, e o teste no aparelho está no
  checklist do docs/mobile.md.

**Testes:**

- **Mobile.Core:**
  - concluir pela lista abre o "Desfazer" e não um toast;
  - "Desfazer" reabre, e reconclui o que foi reaberto;
  - a barra some depois de 8 s, e uma nova recomeça o prazo;
  - a pílula diz "há N min" e o relógio a refaz;
  - os temas da aba Conta, o marcado e a troca;
  - catálogo (ordem, `Normalize`, `Resolve`) e paletas completas em hex;
  - `LastSuccessAt` no `SyncBadge`;
  - `Labels.Ago`.
- **Desktop:** `ThemeParityTests`, com os mesmos temas, na mesma ordem e com
  as mesmas cores.

## ADR-076 — Celular: pendentes na frente, Histórico numa aba e o dispositivo lembrado

**Contexto:** a lista do celular (ADR-067) misturava tudo.

1. **Muitos atalhos.** O topo tinha cinco: Todas, Pendentes, Vencidas,
   Concluídas e Canceladas. O padrão era "Todas", e as concluídas dividiam a
   tela com o que falta fazer.
2. **O dispositivo escondido.** O filtro de origem ficava na folha de
   Filtros, a dois toques e um seletor de distância.
3. **O foco não durava.** Nenhuma escolha da lista sobrevivia a fechar o app:
   quem só olha as tarefas do computador do trabalho escolhia de novo a cada
   abertura.

**Decisão:**

1. **A aba Tarefas é só o que falta fazer.** Atalhos Pendentes (o padrão) e
   Vencidas. Concluir pela linha tira a tarefa da lista, e a barra "Desfazer"
   do ADR-075 continua valendo. A linha não escreve mais "Pendente": o status
   só aparece quando foge disso (`TaskRow.ShowsStatus`).
2. **Concluídas e canceladas numa aba própria, "Histórico"**, entre Tarefas e
   Conta.
   - Atalhos Concluídas (o padrão) e Canceladas.
   - Mesma busca, mesma folha de filtros (`filters?for=history`) e o mesmo
     dispositivo.
   - Sem o botão +: o que nasce é pendente.
   - **Uma lista, dois ViewModels.** `TaskListViewModelBase` tem a busca, a
     paginação, a folha, o "Desfazer" e a sincronização.
     `TaskListViewModel` e `HistoryViewModel` só trocam os atalhos e as
     frases. A tela é uma `TaskListView` (ContentView) nas duas páginas.
   - **"Nada com estes filtros" pela aba.** `TaskFilter.BasePreset` diz qual é
     o atalho de partida de cada aba. Só escolher outro atalho estreita a
     lista, e a aba vazia diz "Nada pendente" ou "Nada no histórico".
3. **O dispositivo vira chips no topo, e é lembrado.**
   - **Chips.** Todas, Compartilhadas, Este celular e cada computador da
     conta, acima dos atalhos de status. Saiu da folha de Filtros, e
     `SheetCount` não o conta mais. Sem conta ligada, os chips não aparecem.
   - **Semântica exata (ADR-067).** "Notebook" é só o que está no notebook,
     sem as compartilhadas. É o mesmo `PlacementMatch` de antes.
   - **Um foco para o app.** O `DeviceFocus` (singleton) guarda a escolha das
     duas abas: trocar numa recarrega a outra.
   - **Lembrado.** A escolha fica no `IPreferenceStore`, chave
     `task-list-device` (as `Preferences` do MAUI). Grava a **chave** (`here`,
     `shared` ou o id do computador), e não o filtro. Os ids antigos deste
     celular entram na hora de buscar (`OriginCatalog.MatchFor`). "Todas" apaga
     a chave.
   - **Antes do diretório.** Na abertura, a escolha já filtra mesmo que o
     `SyncDeviceDirectory` ainda não tenha chegado. O chip ganha o nome quando
     ele chega.
   - **Quando não vale.** Um computador que o diretório já lido não conhece
     mais volta a "Todas". Sem conta ligada, a lista mostra tudo, mas a
     escolha não é apagada: religada a conta, volta.
   - **À vista.** Ao abrir, a fileira de chips rola até o escolhido.
   - **"Limpar filtros"** volta ao atalho de partida e a "Todas".

   (O ADR-079 trocou os chips pelo título da lista e levou "Vencidas" para a
   folha de filtros.)

### Alternativas

- **"Notebook" com as compartilhadas**, como o seletor "Mostrando" do desktop
  (ADR-062). Mostra o que aparece naquele computador, mas o usuário pediu o
  filtro exato, que já era o do celular.
- **Agrupar as pendentes por seção** (Vencidas, Hoje, Próximas, Sem data).
  Ficou a lista simples com o atalho Vencidas: menos rolagem e a mesma
  ordenação da folha.
- **Histórico por um botão no topo**, empilhado sobre Tarefas. Uma aba fica a
  um toque de qualquer tela, e a escolha foi do usuário.
- **Não lembrar, como o desktop.** O seletor do desktop não é lembrado
  (ADR-062): lá o padrão "este computador" já é o foco. No celular o padrão é
  "Todas", e o foco tem de ser escolhido; escolher a cada abertura era o
  incômodo.

### Consequências

- O desktop continua sem lembrar o "Mostrando". As duas telas resolvem
  problemas diferentes, e cada uma ficou com o padrão que serve a ela.
- As outras escolhas da lista (atalho, busca, prioridade, etiquetas, ordem)
  continuam valendo só enquanto o app está aberto.
- O celular passa a ter uma porta de preferência no núcleo. O tema do ADR-075
  continua gravando direto nas `Preferences` pela casca.

**Testes:**

- **Mobile.Core:**
  - a aba Tarefas é só de pendentes, sem "Pendente" na linha;
  - concluir manda para o Histórico, e reabrir lá devolve;
  - "Desfazer" nas duas abas;
  - o Histórico separa concluídas e canceladas, e tem a sua folha;
  - os chips só com conta ligada, com filtro exato;
  - a escolha volta num ViewModel novo sobre as mesmas preferências;
  - a escolha filtra antes do diretório chegar;
  - um computador desconhecido, ou a conta desligada, mostram tudo;
  - as duas abas olham o mesmo dispositivo, e "Limpar filtros" volta a
    "Todas";
  - `MatchFor`, `BasePreset` e `SheetCount` sem o dispositivo.
- **`PhoneSyncTests`:** a origem filtrada pelos chips, ponta a ponta.

## ADR-077 — Importar etiqueta de outro computador: a mesma etiqueta, sem o caminho

**Contexto:** com uma lista por computador (ADR-062), a etiqueta criada no
notebook fica escondida no desktop. Quem quer a mesma etiqueta aqui precisava
saber que "criar com o mesmo nome" a compartilha, ou achá-la em "Onde
aparece…", que só lista as etiquetas daqui. O pedido: ao criar uma etiqueta
nova, importar uma de outro computador com o nome, a cor e o repositório, sem
a pasta.

**Decisão:**

1. **Importar é compartilhar.** É a mesma etiqueta, colocada em "todos os
   computadores" (`PlaceTag` com `DevicePlacement.Everywhere`), como já faz o
   `CreateTag` com o nome de uma escondida.
2. **Nada novo no domínio, no sync ou no servidor.** O nome e a cor vêm com a
   etiqueta. Os diretórios vêm com o que já sincroniza: alias, URL do
   repositório, nome, descrição, branch padrão e comandos. O caminho continua
   coluna local (ADR-061/064), e o diretório chega "Não mapeado neste
   computador". O clone e o "escolher a pasta" do ADR-064 resolvem daí.
3. **Na janela Etiquetas, no formulário de etiqueta nova.**
   - **O botão.** "Importar de outro computador…" ao lado do título. Só
     aparece com outro computador na conta, com alguma etiqueta escondida aqui
     e fora da edição.
   - **O menu.** Uma linha por etiqueta, com a bolinha da cor e de onde vem:
     "Backend — Notebook", ou "um computador removido".
   - **A lista.** `GetTags(DeviceView.All)` filtrado por
     `!IsVisibleHere(placement)`. Ler essa lista não pode derrubar a janela:
     se falhar, o botão só some.
   - **Depois de importar.** A etiqueta já tem diretórios? O cartão dela abre
     com eles, para clonar ou escolher a pasta.

### Alternativas

- **Cópia independente**, só com alias e URL nos diretórios. O nome é único na
  conta (índice `NOCASE` local, `UniqueKeyFields` no servidor), então a cópia
  teria de nascer com outro nome. Também pediria um diretório sem caminho
  criado pelo domínio, que hoje só o sync escreve. O usuário preferiu a mesma
  etiqueta.
- **Nome repetido por computador.** Mexe na regra de unicidade, no contrato do
  sync, no servidor e na fusão por nome (ADR-061). É grande demais para um
  atalho.
- **Preencher o formulário e criar pelo "Criar etiqueta".** Daria a impressão
  de que a cor escolhida ali vale, mas o `CreateTag` com o nome de uma
  escondida só a compartilha. Trocar o nome no formulário criaria outra
  etiqueta sem diretórios.

### Consequências

- Cor e diretórios ficam compartilhados: editar num computador muda no outro.
  O caminho de cada um continua só dele.
- Comandos vinculados aos diretórios vêm junto, porque são do diretório.
- Para voltar, "Onde aparece…" manda a etiqueta de novo para o outro
  computador.

**Testes (Desktop):**

- `TagImportViewModelTests`:
  - só as etiquetas de outro computador são oferecidas, com a origem;
  - sem outro computador, nada é oferecido, e a lista é lida uma vez só;
  - a falha ao ler a lista só esconde o botão;
  - a edição esconde o botão;
  - importar coloca a etiqueta em todos e abre os diretórios não mapeados;
  - a falha ao importar aparece na tela;
  - na janela, o botão e o menu ligados ao comando.

## ADR-078 — O servidor de sincronização é do usuário: nenhum embutido, e a troca é explícita

**Contexto:** o endereço do servidor de sincronização vinha da build.

1. **Um servidor de alguém em todo APK.** O `-p:SyncApiBaseUrl` virava
   `AssemblyMetadata`, lido por `SyncOptions.FromBuild()` (desktop) e por
   `MauiProgram` (celular). O `release.yml` e o `mobile-ios.yml` o preenchiam
   com a variável `SYNC_API_BASE_URL` do repositório. Todo APK publicado
   apontava para o servidor pessoal do mantenedor, e o celular não tinha como
   trocar (`docs/mobile.md`, "Servidor fixo por build").
2. **O desktop de release já vinha sem servidor**, e só sincronizava com
   `Sync:ApiBaseUrl` escrito à mão no `appsettings.user.json`.
3. **Trocar de servidor não era detectado.** O `SyncState.ServerBaseUrl` era
   gravado no `LinkAsync` e nunca lido. O reset do `LinkAsync` só olhava conta
   e workspace, e o refresh token ficava no cofre com o nome fixo `sync`, sem
   saber de qual servidor era. Um endereço trocado levaria o cursor, as versões,
   o outbox e o token do servidor antigo para o novo.
4. **O cliente HTTP seguia redirecionamento** (o padrão do `SocketsHttpHandler`).

**Decisão:**

1. **Nenhum endereço vem da build.** Saíram o `FromBuild`, a `AssemblyMetadata`
   do Infrastructure e o `SYNC_API_BASE_URL` dos workflows. No celular, a
   build Debug só **sugere** `http://localhost:5280/` no campo
   (`SyncServerSuggestion`); nada é usado sem o desenvolvedor salvar.
2. **Uma regra para o endereço**, `SyncServerUrl.Check` (Application), usada
   pela tela, pelo `SyncOptions.BaseUri` e pela troca:
   - absoluto, `https`, sem usuário e senha, sem query nem fragmento;
     normalizado com uma `/` no fim;
   - `http` só no loopback **e** só quando o desenvolvimento autoriza
     (`Sync:AllowHttpLoopback`): a build Debug liga sozinha, no
     `AppServices` do desktop e no `MauiProgram`; a Release só com o valor
     explícito no `appsettings.user.json`. HTTP de rede nunca.
3. **O endereço muda em tempo de execução.** `ISyncServerAddress` (porta da
   Application) grava no mesmo `SyncOptions` singleton que o `SyncHttp` lê a
   cada pedido. Não há `HttpClient` a recriar, e o access token em memória é
   esquecido na troca.
4. **Onde fica.** No celular, nas preferências do aparelho
   (`IPreferenceStore`, chave `sync-server-url`), pelo `SyncServerSettings`,
   aplicado na abertura (`MobileLifecycle`) antes do primeiro ciclo. A URL não
   é segredo; o token continua no `SecureStorage`. No desktop, continua o
   `Sync:ApiBaseUrl` do `appsettings.user.json` (ADR-018); a tela de servidor é
   só do celular.
5. **A tela do celular** (*Conta → Configurar servidor*, `SyncServerViewModel`):
   informar, validar ao digitar, **Testar conexão**, salvar, tirar, estado e
   última sincronização. Sem servidor, a aba Conta diz "Sincronização
   desativada" e oferece configurar; nada é pedido na abertura.
6. **Testar conexão** (`ISyncServerProbe`): `GET health` e
   `GET .well-known/openid-configuration`, sem token, com o mesmo cliente e
   10 s de limite. O resultado tem nome: DNS, recusado, TLS, tempo,
   redirecionamento, "não parece um servidor do MyTaskApp", erro do servidor.
   Um `authorization_endpoint` em outro host é só aviso: o endereço anunciado
   nunca substitui o configurado. Nenhuma rota nova no servidor.
7. **Trocar ou tirar é explícito** (`ISyncAccount.SwitchServerAsync`):
   - com uma conta (ou o rastro dela) de outro servidor, a tela pergunta antes
     e diz quantas alterações não chegaram ao antigo;
   - o laço é parado e o ciclo em andamento termina de vez
     (`SyncScheduler.RunPausedAsync`), para nenhum pull gravar o cursor antigo
     depois;
   - sai da conta **no servidor antigo**, enquanto o endereço ainda é dele;
   - `ISyncLocalStore.ForgetServerAsync` apaga outbox, `SyncEntityStates` e
     conflitos, limpa cursor, endereço, conta e workspace, zera o
     `ClientSchemaVersion` e desliga a captura. O id deste aparelho e os
     apelidos ficam (ADR-062);
   - só então o endereço novo vale. Nada vai para ele antes de alguém entrar;
     ao entrar, a primeira conexão traz o que ele tem e a varredura manda o que
     só existe aqui.
8. **Defesa em profundidade, para o endereço que muda por fora da tela**
   (o `appsettings.user.json`, um backup restaurado):
   - o `SyncEngine` compara o servidor da conta ligada com o configurado e, se
     diferem, para com `server_changed` ("O servidor mudou: entre de novo")
     antes de qualquer pedido;
   - o `LinkAsync` trata outro servidor como outra conta;
   - o refresh token é gravado como `<servidor> <token>` e só é usado ou
     revogado nesse servidor. O valor antigo, só o token, vale até a próxima
     renovação, que já o grava amarrado;
   - o cliente da sincronização não segue redirecionamento
     (`AllowAutoRedirect = false`), e um `3xx` vira erro.
9. **Migração.** Sem nada salvo, um celular que já tinha entrado no servidor
   embutido (o `SyncState.ServerBaseUrl` existe) adota esse endereço como o
   salvo: a pessoa escolheu aquele servidor ao entrar, e atualizar o app não a
   desliga. Quem nunca entrou fica sem servidor. Tirar o servidor apaga o
   endereço da conta, e ele não volta na abertura seguinte. Nenhuma migração de
   schema: a coluna já existia.

### Alternativas

- **Vários perfis de servidor.** O `SyncState` é linha única, o outbox e as
  versões não têm servidor na chave, e o reset por conta já existia. Perfis
  pediriam mudar o schema e a captura para separar tudo por servidor. Um
  servidor ativo, com troca explícita, resolve o pedido.
- **Guardar o endereço no SQLite** (ADR-014). Resolveria desktop e celular
  juntos, mas o desktop já tem o `appsettings.user.json` e ficou sem tela; no
  celular, as preferências são onde as outras escolhas do aparelho já moram
  (ADR-076).
- **Perguntar ao atualizar** quem já estava ligado ao servidor embutido. O
  usuário preferiu adotar sem perguntar: a pessoa entrou naquele servidor por
  escolha, com o próprio login.
- **Mandar as operações pendentes ao servidor antigo antes de trocar.** Pediria
  rede no momento da troca e um caminho a mais para falhar; o estado atual das
  tarefas continua aqui e sobe no novo.
- **Uma rota `/info` anônima no servidor para o teste.** O `/health` e o
  discovery já existem; uma rota nova exigiria servidor atualizado para o
  teste funcionar.

### Consequências

- A build pública é neutra: o mesmo APK e os mesmos instaladores servem para
  qualquer pessoa, sem a infraestrutura do mantenedor. A variável
  `SYNC_API_BASE_URL` do repositório pode ser apagada.
- As alterações que não chegaram ao servidor antigo não vão mais para ele
  depois da troca: o aviso diz quantas são, e o estado local sobe inteiro no
  novo.
- O desktop continua sem tela de servidor; mudar o `appsettings.user.json` e
  reabrir cai no `server_changed` até conectar de novo.
- O teste de conexão confia no `/health` e no discovery: um servidor de outro
  produto que responda os dois passa no teste, e só o login diz que não é.

**Testes:**

- **Application:** `SyncServerUrlTests`: normalização, malformadas, esquemas,
  credenciais, query e fragmento, HTTP de rede e de loopback, `SameServer`.
- **Infrastructure:**
  - `SyncServerProbeTests`: sucesso, sob caminho, issuer em outro host, DNS,
    recusa, TLS, tempo, `3xx` sem seguir, 404, 5xx, corpo de outro produto,
    sem discovery, log sem endereço;
  - `SyncServerSwitchTests`: `server_changed` sem nenhum pedido, `LinkAsync` em
    outro servidor, troca com pendências (revogação no antigo, estado esquecido,
    tarefas mantidas), nada capturado nem enviado depois, tirar, salvar o
    mesmo, endereço inseguro recusado;
  - `SyncClientTests`: token de outro servidor nunca enviado, token antigo
    regravado amarrado, nada vai ao endereço novo, sair sem revogar no novo,
    redirecionamento não seguido;
  - `InfrastructureRegistrationTests`: endereço em tempo de execução com o
    mesmo `SyncHttp`, cliente sem redirecionamento, HTTP recusado fora do
    desenvolvimento.
- **Mobile.Core:** `SyncServerViewModelTests`: primeira abertura sem servidor,
  uso local completo sem nada a enviar, sugestão do Debug, salvar e reabrir,
  endereços inválidos, HTTP do Debug numa Release, trocar e tirar sem conta,
  trocar com conta e pendências, cancelar, tirar com conta, adoção de quem
  atualiza e não readoção depois de tirar, o texto do aviso, o teste de
  conexão, a aba Conta acompanhando.
- **`PhoneSyncTests`:** trocar de servidor com uma tarefa pendente: só a
  revogação sai, nada vai ao novo, a tarefa fica, e sobe ao entrar.

## ADR-079 — Celular: a aba Tarefas enxuta, o dispositivo no título e a Nova tarefa discreta

**Contexto:** depois do ADR-076, a aba Tarefas ainda empilhava cinco faixas
antes da primeira tarefa:

- a barra do topo com "Filtros";
- a pílula da sincronização;
- a barra de busca;
- a fileira de chips de dispositivo;
- a fileira Pendentes/Vencidas.

Por cima da lista ficava um "+" redondo de 60 pontos com sombra. O usuário
pediu:

- a tela focada nas pendentes;
- um botão de criar mais discreto;
- um lugar **de destaque** para o dispositivo. Os chips rolavam de lado e
  pareciam mais um filtro entre outros.

**Decisão:**

1. **O dispositivo é o título da lista.**
   - A barra da Shell sai das abas Tarefas e Histórico
     (`Shell.NavBarIsVisible="False"`). O topo é da `TaskListView`: legenda da
     aba ("PENDENTES", "HISTÓRICO") e, grande, o dispositivo em foco com um
     "⌄".
   - Um toque abre a folha de opções do sistema ("Mostrar as tarefas de"),
     com o atual marcado por "✓". É uma nova porta,
     `IDialogService.ChooseAsync`: `DisplayActionSheetAsync` na casca.
   - A folha devolve o texto tocado. Por isso, dois computadores com o mesmo
     nome ganham "(2)" nela.
   - Sem conta ligada, o título é o nome da aba e não abre nada.
   - "Todas" virou "Todos os dispositivos", que se lê bem como título.
   - O resto do ADR-076 continua: o `DeviceFocus`, a chave gravada e o filtro
     exato.
2. **Busca e filtros viram ícones** à direita do título.
   - São vetores (`Path`) com a cor do tema por `DynamicResource`, sem pacote
     de ícones e sem gatilho de cor.
   - A lupa abre a busca já com o teclado. Fechá-la apaga o texto e relê.
     Com texto digitado, a busca continua à vista. "Limpar filtros" também a
     fecha.
   - O funil ganha um ponto quando a folha tem algo ligado
     (`HasSheetFilters`). O leitor de tela ouve "Filtros (2)".
3. **"Vencidas" sai do topo e vai para a folha**, como "Só as vencidas".
   - O critério é o mesmo do quadro Hoje (`TaskFilter.OverdueOnly`), e conta
     no `SheetCount`.
   - A aba Tarefas fica com um atalho só, e a fileira some (`ShowsPresets`).
     O Histórico mantém Concluídas/Canceladas e não mostra a opção.
   - `TaskListPreset.Overdue` deixou de existir.
4. **"＋ Nova tarefa" no rodapé**, como nos Lembretes do iOS.
   - Só texto na cor de destaque (`TextAction`), numa faixa própria abaixo da
     lista. Não cobre nenhuma tarefa e fica ao alcance do polegar.
   - A barra "Desfazer" ocupa a largura toda, sem desviar do antigo "+".

### Alternativas

- **Barra "Mostrando: Notebook ⌄" abaixo do título "Tarefas".** É mais
  explícita, mas custa uma faixa a mais, justo o que se queria tirar.
- **Manter os chips, só maiores.** A escolha é de um toque, mas com muitos
  computadores a fileira rola de lado e esconde o escolhido.
- **Uma tela própria de dispositivos.** Teria espaço para ícones, mas a folha
  do sistema já resolve, e com o visual nativo de cada plataforma.
- **Ícone "+" no topo ou um "+" redondo menor.** O do topo fica longe do
  polegar. O redondo continua cobrindo a última tarefa.
- **Esconder a pílula de sincronização quando está em dia** e **enxugar a
  linha** (sem "Normal", sem a origem repetida). Foram oferecidas, e o
  usuário preferiu manter as duas como estão.

### Consequências

- As telas empilhadas (Filtros, detalhe, formulário) continuam com a barra da
  Shell e o botão de voltar.
- O topo próprio se apoia no `SafeAreaEdges` do .NET 10: a `Grid` raiz
  respeita a barra de status por padrão (`Container`).
- A escolha de "Só as vencidas" vale só enquanto o app está aberto, como as
  outras da folha.

**Testes:**

- **Mobile.Core:**
  - a aba Tarefas tem um atalho só e esconde a fileira; o Histórico mostra a
    dele;
  - o título abre a folha com o atual marcado, escolher filtra e grava, e
    cancelar não muda nada; sem conta, nem abre;
  - dois computadores com o mesmo nome se distinguem na folha;
  - `DeviceTitle` com e sem conta;
  - a busca atrás da lupa: abrir, continuar à vista com texto, fechar
    limpando e relendo, e "Limpar filtros" fechando;
  - "Só as vencidas" pela folha: o critério do quadro, "Filtros (1)", o ponto
    no funil, a folha reaberta com a marca, "Nada com estes filtros", e o
    Histórico sem a opção;
  - `TaskFilter.OverdueOnly` na busca, no `SheetCount` e no `IsNarrowed`.

## ADR-080 — Linhas sem máscara: uma condição SQL do usuário, por tabela, só na leitura da origem

**Contexto:** a cópia mascarada (ADR-058) aplica as regras em **todas** as
linhas de cada tabela. Pedido do usuário: deixar de fora da anonimização os
registros que não precisam dela — o usuário de teste, os registros de sistema —
escrevendo um `WHERE campo = valor`. Essas linhas chegam ao destino com os
valores reais; as outras continuam mascaradas.

O ADR-058 recusou SQL livre ("rodaria em produção"). O usuário escolheu, sabendo
disso, a condição livre em vez de um filtro montado pelo app (coluna, operador,
valor): ela cobre `like`, `in`, subconsulta e `or` sem uma tela de construtor.
Este ADR é a exceção, e as proteções que a acompanham.

**Decisão:**

1. **Onde fica.** No perfil de anonimização, ao lado das tabelas sem dados:
   `AnonymizationProfile.RowFilters` (tabela `AnonymizationRowFilters`,
   migration `AnonymizationRowFilters`), uma condição por tabela, substituída
   inteira ao salvar. A condição de uma tabela particionada vale para as
   partições, como as regras.
2. **Como entra no SQL.** Nenhuma linha some: cada coluna mascarada da tabela
   vira `CASE WHEN (condição) THEN coluna ELSE máscara END` no `COPY (SELECT …)`.
   Condição nula cai no `ELSE`: na dúvida, a linha sai mascarada. As FKs, a
   contagem de linhas e o post-data não mudam.
3. **A forma é conferida antes de virar SQL** (`RowFilterCondition.Normalize`,
   no domínio, ao salvar e de novo no `MaskedSelectBuilder`). É uma leitura
   léxica que garante que a condição não sai dos parênteses em volta dela:
   - sem `;`, sem comentário (`--`, `/* */`) e sem `$` (parâmetro e
     dollar-quoting);
   - sem barra invertida dentro de texto, que num `E'…'` mudaria onde o texto
     termina;
   - aspas simples e duplas fechadas, parênteses balanceados, até 2000
     caracteres, sem caractere de controle.

   Um `WHERE` colado no começo é tirado.
4. **Só roda na origem, só para ler.** Toda leitura com a condição usa as
   sessões só-leitura da origem: o `COPY` (`REPEATABLE READ` +
   `SET TRANSACTION READ ONLY`) e o inspetor (`default_transaction_read_only`
   + transação `READ ONLY`). No destino ela não roda nunca.
5. **O servidor confere antes da cópia.** O [Validar], a validação da cópia e
   a pré-visualização rodam `SELECT count(*) FROM ONLY t WHERE (condição)`
   (`UnmaskedRows.CountAsync`, `IPostgresServerInspector.CountMatchingAsync`):
   - a contagem vira aviso ("public.usuarios: 3 linha(s) vão sem máscara,
     com os valores reais"), registrado na etapa da operação;
   - uma condição que o servidor recusa vira **problema**, com a mensagem e o
     SQLSTATE dele. A cópia nem começa, em vez de quebrar no meio do `COPY`.
6. **A pré-visualização continua sem valor real:** o SELECT dela ganha
   `WHERE NOT coalesce((condição), false)`.
7. **A verificação deixa essas linhas de fora:**
   - **máscara variável:** o fingerprint da origem exclui as linhas da condição.
     No destino ela não é aplicada, porque pode citar uma coluna que já chegou
     mascarada;
   - **máscara fixa:** passa se as linhas sem o valor fixo no destino não
     passam das linhas da condição, contadas na origem.
8. **O MCP só mostra.** `get_details` traz as condições, e as alterações e o
   `duplicate` as mantêm. Nenhuma ferramenta grava ou executa a condição, e o
   `preview_masking` do MCP não a recebe. A garantia "nenhuma ferramenta
   executa SQL livre" (ADR-059) continua valendo: a condição só se escreve na
   tela.
9. **A condição não vai para o log.** Ela pode citar um valor real
   (`email = 'fulano@…'`). O log de salvar só conta quantas há.

O planner recusa condição numa tabela sem dados (o usuário escolhe um dos
dois) e avisa da condição numa tabela que não existe mais ou que não tem coluna
mascarada.

### Alternativas

- **Filtro estruturado** (coluna escolhida do catálogo, `=`/`in`, valor citado
  pelo app). Mantinha o ADR-058 sem exceção, mas não cobre `like`, faixa,
  subconsulta nem combinações. O usuário preferiu a condição livre.
- **Não copiar as linhas que batem** (`WHERE NOT condição` no `COPY`). Pediria
  outra checagem de FK (uma linha filha apontando para um pai que ficou de
  fora) e a comparação de linhas com a mesma condição. Não é o pedido: as
  linhas são para ir, só que sem máscara.
- **Validar a condição com um parser de SQL.** Um parser no app divergiria do
  PostgreSQL em algum canto. A leitura léxica só garante os parênteses; quem
  diz se a expressão é válida é o próprio servidor, na contagem.

### Consequências

- Ainda é SQL do usuário na produção. A sessão só-leitura recusa escrita
  (`nextval`, por exemplo, falha com `25006`), mas não impede uma condição cara
  (uma subconsulta sem índice, `pg_sleep`), nem uma função com efeito fora do
  banco que a origem tenha instalada (`dblink`, por exemplo). Isso fica com
  quem escreve a condição, e a contagem antes da cópia mostra o custo cedo.
- O usuário pode, sem querer, deixar sem máscara mais do que pensava:
  `true` copia a tabela inteira com os valores reais. A contagem aparece na
  pré-visualização e na validação, antes de qualquer cópia.
- A comparação da máscara variável pareia um pouco menos linhas numa tabela
  com condição (as 10.000 primeiras da origem, sem as linhas da condição).

**Testes:**

- **Domain:** `ProfileTests`: substituição inteira, uma por tabela, `WHERE`
  tirado, aspas dobradas e `;`/`--` dentro de texto aceitos, e recusas de `;`,
  comentário, `$`, barra invertida, aspas e parênteses abertos ou sobrando,
  controle, tamanho.
- **Application:**
  - `MaskingPlannerTests`: o plano leva a condição e a partição herda; condição
    em tabela sem dados é problema; tabela sumida ou sem máscara é aviso;
  - `RunDatabaseCopyHandlerTests`: contagem antes da cópia, condição no
    `COPY`, fingerprint da origem sem as linhas, verificação verde; condição
    recusada para antes de qualquer processo;
  - `DatabaseCatalogHandlersTests`: salvar, duplicar e comparar levam as
    condições; a pré-visualização conta e pula a tabela recusada;
  - `DatabaseOperationsServicesTests`: máscara fixa com a tolerância das
    linhas sem máscara.
- **Infrastructure:**
  - `MaskedSelectBuilderTests`: `CASE WHEN (condição)` no `COPY`,
    `WHERE NOT coalesce(…)` na pré-visualização e no fingerprint, condição que
    sairia dos parênteses nunca chega ao SQL;
  - `DatabaseOperationsPersistenceTests`: ida e volta, maiúsculas,
    substituição e cascata;
  - `PostgresIntegrationTests` (PostgreSQL real): a linha da condição chega com
    os valores reais e as outras mascaradas; contagem, coluna inexistente
    (`42703`) e `nextval` recusado (`25006`).
- **Desktop:** `RowFiltersScreenTests`: editar traz, incluir confere na hora,
  uma condição por tabela, remover, salvar e pré-visualizar levam as condições,
  a janela mostra a lista.
- **Mcp:** `DatabaseToolTests`: `get_details` mostra as condições; uma
  alteração por regra as mantém e o relido confere.

## ADR-081 — Consulta Inteligente: o Claude Code só lendo, e o banco recusando escrita no servidor

**Contexto:** uma pessoa sem conhecimento técnico quer perguntar "por que este
pedido está pendente?" e receber uma explicação baseada no código real e nos
dados de uma cópia local do banco. O app já tinha as peças separadas:
etiquetas com projetos e clone (ADR-026, ADR-064), cópias de banco por
ambiente (ADR-060), o Claude Code num terminal real com avisos (ADR-030,
ADR-037) e o servidor MCP com token (ADR-059). Faltava juntá-las sem abrir
escrita em lugar nenhum. O guia de uso está em
[`consulta-inteligente.md`](consulta-inteligente.md).

Decisões do usuário: a consulta é uma **aba da janela da tarefa**, com **uma
configuração por tarefa**, sem perfis globais; o app **cria o usuário somente
leitura**, com confirmação; as ferramentas de banco são **pré-aprovadas** no
Claude; no **Linux**, a abertura do terminal fica como limitação documentada.

**Decisão:**

1. **A configuração.** `TaskConsultation` (tabelas `TaskConsultations` e
   `TaskConsultationSources`, migration `TaskConsultation`), 1:1 com a tarefa:
   etiqueta, pastas em ordem (projetos da etiqueta ou ambientes da tarefa),
   banco (conexão + nome), instruções, modelo, esforço e a última pergunta.
   Só a FK da tarefa: etiqueta, diretório, ambiente e conexão são referências
   soltas, que o `ConsultationResolver` resolve a cada leitura e transforma em
   problemas legíveis ("o banco escolhido não existe mais"). Excluir uma
   conexão não é bloqueado por uma consulta, nem a apaga. Fica fora do
   `SyncRegistry`: guarda pastas e conexões deste computador.
2. **A sessão.** `AgentSession` ganha `Kind` (desenvolvimento ou consulta) e o
   hash do segredo do MCP. A consulta não tem ambiente (`CK` no banco) e uma
   só fica aberta por tarefa (índice único parcial). O id da conversa nasce no
   app (`--session-id`), e **Reabrir** é uma sessão nova com `--resume` dele.
   O monitor, a reconciliação e os avisos de "aguardando você" são os do
   ADR-030/037, com o rótulo "Consulta Inteligente".
3. **O Claude travado pelos argumentos, não pelo texto.** O
   `ClaudeCodeCliProvider` passa a implementar `IConsultationLaunchBuilder`:
   `--restricted --tools Read,Grep,Glob --strict-mcp-config`, as outras pastas
   em `--add-dir`, as instruções em `--append-system-prompt-file` e, com
   banco, só o MCP do app (`--mcp-config`) com as quatro ferramentas em
   `--allowedTools`. Os "Parâmetros" do card de desenvolvimento
   (`--dangerously-skip-permissions`) nunca entram. Conferido com o Claude
   Code 2.1.305 de verdade: o arquivo de instruções é lido, `${VAR}` no
   cabeçalho do `--mcp-config` é expandido do ambiente, e com `--tools` as
   ferramentas do MCP continuam lá e Bash não.
4. **O banco em três camadas** (`IConsultationDatabase`, `NpgsqlConsultationDatabase`):
   - **um usuário que só lê**, `mytaskapp_leitura`, criado por "Preparar acesso
     de leitura" (`IConsultationReadAccessProvisioner`) só em servidores de
     Desenvolvimento ou Teste que aceitam destino, com a senha gerada pelo app
     e guardada no cofre numa conexão "(leitura)", e a operação na auditoria
     (`DatabaseOperationType.PrepareReadAccess`);
   - **a conferência no catálogo**, antes de abrir e a cada chamada (cache de
     60 s só para o "sim"): superusuário, papéis de escrita, privilégio de
     escrita em tabela, `CREATE` no banco e `dblink` recusam;
   - **a execução**: a consulta vai como subconsulta de
     `SELECT * FROM (…) AS consulta LIMIT $1`, pelo protocolo estendido, numa
     sessão `default_transaction_read_only` e numa transação `READ ONLY` que
     termina em rollback, com `statement_timeout` e cortes de linhas, células e
     tamanho. Várias instruções, DDL, DML, CTE que altera, `COPY`, `DO`, `CALL`
     e `FOR UPDATE` são recusados pelo próprio PostgreSQL.

   A política ganha `QueryData`, recusada em produção e em servidor de
   produção com outro rótulo, como as outras.
5. **O MCP com segredo por sessão.** `consultation_database_describe`,
   `consultation_table_list`, `consultation_table_describe` e
   `consultation_query` (área "Consulta Inteligente"). Nenhuma recebe conexão
   ou banco. O `McpSecurityMiddleware` aceita, além do token do servidor, o
   segredo de uma consulta **aberta** (pelo hash, `ResolveConsultationAccess`)
   e põe quem chamou no `ClaimsPrincipal` (`McpCaller`). Filtros do SDK fazem
   cada lado ver só o seu: a consulta, as quatro ferramentas, sem resources nem
   prompts; o token do servidor, todas menos essas. O `McpGateway` confere de
   novo, e roda as de consulta fora da fila de uma operação por vez — do
   SQLite elas só leem, e a parte demorada é no PostgreSQL.
6. **A aba.** `TaskConsultationViewModel` e `TaskConsultationPanel`, quarta
   aba da `TaskNotesWindow`: Assunto, Projeto (com **Clonar…**, sempre
   confirmado, e **Escolher pasta…**), Dados (Verificar, Preparar acesso de
   leitura, Criar nova cópia local — a cópia do ADR-060, pelo mesmo
   `DevelopmentDatabaseViewModel`, que só vira o banco da consulta se terminar
   bem), Preparar (instruções, modelo, esforço, prévia e "Editar só para esta
   sessão") e Perguntar. Com uma consulta aberta, o banco não troca: a IA
   leria outro banco achando que é o mesmo.

### Por que não confiar só no prompt, nem numa lista de palavras

O prompt orienta; quem cumpre é o modelo, e um modelo pode errar ou ser
induzido pelo próprio dado que lê. Uma lista de palavras proibidas no SQL é
contornável (comentários, `$$`, funções `SECURITY DEFINER`, `set_config`) e
recusa consultas legítimas. As travas daqui são de quem executa: o Claude sem
ferramenta de escrita, o PostgreSQL com um usuário e uma transação que não
escrevem.

### Por que um usuário criado pelo app

Pedir que a pessoa crie um papel e dê `GRANT` é pedir o que ela não sabe
fazer. Usar a conexão administrativa com "só leitura" na sessão deixaria um
superusuário do outro lado — e superusuário desliga o `read only` com um
`SET`. O papel do app não tem poder nenhum além de entrar e ler, e o catálogo
confere isso antes de cada consulta.

### Exceção ao ADR-059

O ADR-059 deixou "SQL pelo MCP" fora de escopo. Continua fora para o cliente
do token do servidor. A exceção é só a sessão de consulta: um segredo que
nasce com o terminal, morre com ele e só alcança as quatro ferramentas, num
banco que não é de produção, com um usuário que só lê.

### Consequências

- A aba só abre o terminal no Windows, como a de Desenvolvimento.
- A cópia pode ter dados reais; anonimizar é escolha da cópia.
- PostgreSQL 14 e anteriores dão `CREATE` em `public` a todos: o catálogo
  acusa `CREATE` no banco, e a transação só leitura impede o uso.
- Preparar o acesso troca a senha do usuário de leitura daquele servidor: as
  consultas abertas seguem, porque cada chamada lê a senha do cofre.
- `server_info` conta 86 ferramentas; o cliente do token do servidor vê 82.

**Testes:**

- **Domain:** `TaskConsultationTests`, `ConsultationSessionTests` e
  `ConsultationPolicyTests`: pastas em ordem e sem repetição, trocar de
  etiqueta tira as pastas dela, nome de banco que é conexão recusado, o
  segredo só antes de abrir e só em consulta, `QueryData` nunca em produção e
  `PrepareReadAccess` só em Desenvolvimento ou Teste.
- **Application:** `ConsultationHandlersTests`: as sugestões sem nada salvo,
  o estado de cada pasta, URL sem credencial, produção fora da lista e
  recusada mesmo pedida, conexão excluída vira problema, limpar não toca no
  resto, o contexto sem servidor, usuário, senha ou segredo, abrir só com
  código, uma consulta por tarefa, Claude ausente, terminal que não abre,
  banco sem MCP ou com usuário que escreve, o segredo só no ambiente e morto
  com a sessão, reabrir a mesma conversa, banco fixo com consulta aberta,
  preparar cria o usuário, guarda a senha, audita e reaproveita, e as
  ferramentas leem pelo usuário de leitura, no teto, e param quando ele volta
  a poder escrever.
- **Infrastructure:** `TaskConsultationPersistenceTests` (ida e volta,
  limpar, excluir a conexão, uma consulta aberta por tarefa, o segredo só da
  aberta); `ClaudeCodeConsultationLaunchTests` (os argumentos, os arquivos, o
  segredo só no ambiente, `claude.cmd`); `ConsultationDatabaseIntegrationTests`
  contra PostgreSQL 14 real (Testcontainers).
- **Mcp:** `ConsultationToolTests`: o token do servidor não vê nem chama, a
  consulta vê só as quatro e nada mais, a consulta vai ao banco certo no teto,
  sessão encerrada e segredo desconhecido ficam fora;
  `ConsultationClaudeTests` com o Claude real, só com `MYTASKAPP_TEST_CLAUDE=1`.
- **Desktop:** `TaskConsultationViewModelTests`, `TaskConsultationRenderingTests`
  e `ConsultationCompositionTests`.

## ADR-082 — Script depois da cópia: SQL do usuário no destino, no fim, numa transação

**Contexto:** depois de copiar um banco anonimizado, o usuário ainda ajusta o
destino à mão: troca as URLs e os e-mails de configuração, desliga integrações,
limpa filas, cria o usuário de teste. O pedido é deixar esse ajuste no próprio
perfil de anonimização, como um script que roda no fim da cópia.

O ADR-058 recusou SQL livre por ele rodar na produção; o ADR-080 abriu a
exceção da condição, só na origem e só para ler. O script é o caso oposto: ele
escreve, e por isso só roda no destino, que nunca é produção.

**Decisão:**

1. **Onde fica.** `AnonymizationProfile.PostCopyScript` (coluna
   `PostCopyScript` em `AnonymizationProfiles`, migration
   `AnonymizationPostCopyScript`), texto livre de até 100.000 caracteres. Vazio
   é "sem script". Duplicar leva junto; comparar mostra o campo
   `postCopyScript`.
2. **Quando roda.** Numa etapa própria da cópia (`DatabaseCopyStep.PostCopyScript`,
   "Executando o script do perfil"), **depois da verificação**: a verificação
   compara a cópia com a origem, e um script que muda dados a faria mentir. Com
   a verificação reprovada, o script não roda. Sem script, a etapa fica "não
   executada". A cópia sem anonimização não usa o perfil, então não roda o script.
3. **Onde roda: só no destino**, por `IPostgresScriptRunner`
   (`NpgsqlScriptRunner`):
   - a política julga a cópia pedindo as permissões **Alterar dados** e
     **Executar SQL** do destino quando o perfil tem script
     (`AnonymizationFacts.HasPostCopyScript`). O [Validar] acusa antes, e a
     cópia nem começa;
   - antes da etapa, `policy.Demand(ExecuteSql)` no destino. Produção, ou um
     destino que aponte para um banco de produção, é recusado;
   - o executor repete a guarda do `pg_restore` (`PostgresProcessGuard`) e
     confere "Executar SQL" antes de conectar.
4. **Tudo ou nada.** O script inteiro vai num comando do Npgsql, dentro de uma
   transação. Um erro desfaz tudo o que o script fez, e o banco fica como a
   cópia o deixou. A cópia termina com falha, com a mensagem e o SQLSTATE do
   servidor. Para a transação valer, o perfil recusa ao salvar um comando que a
   encerre (`BEGIN`, `START`, `COMMIT`, `END`, `ROLLBACK`, `ABORT`):
   `PostCopyScriptText` faz uma leitura léxica que pula textos, identificadores
   citados, comentários e dollar-quoting, e olha só a primeira palavra de cada
   comando. `ROLLBACK TO SAVEPOINT` passa. Um `COMMIT` que escapasse vira
   mensagem clara no executor, e não "erro inesperado".
5. **Nada de `psql`.** Os meta-comandos dele (`\!`, `\copy`, `\i`) rodariam
   coisas nesta máquina. Pelo Npgsql, o script é só SQL.
6. **Fora do log.** O script pode citar valores (uma senha de teste, um
   e-mail). O log registra só se há script, as linhas alteradas e o SQLSTATE de
   uma falha. Os `RAISE NOTICE` vão para os logs da tela, não para o arquivo.
7. **O MCP só mostra.** `get_details` traz o script, e as alterações por lá o
   mantêm. Nenhuma ferramenta grava ou executa o script.

### Alternativas

- **Rodar antes da verificação.** A verificação acusaria o que o script mudou
  de propósito (uma linha apagada, uma coluna trocada) e reprovaria a cópia.
- **Rodar pelo `psql`.** Aceitaria scripts com `\set` e afins, mas abriria a
  porta dos meta-comandos que executam coisas na máquina do usuário.
- **Sem transação.** Aceitaria `VACUUM` e `CREATE INDEX CONCURRENTLY`, mas uma
  falha no meio deixaria o destino meio ajustado, sem dizer até onde.
- **Script na conexão de destino, e não no perfil.** O ajuste depende do
  sistema copiado, que é o que o perfil descreve; o destino é só o lugar.

### Consequências

- Desenvolvimento, Teste e Homologação nascem sem "Executar SQL": quem usa o
  script liga essa permissão no destino, uma vez.
- O script roda depois da verificação, então ela não confere o que ele mudou.
  Um script pode até desfazer uma máscara (`update clientes set email = …`); é
  SQL do próprio usuário, no banco dele de desenvolvimento.
- `VACUUM`, `CREATE INDEX CONCURRENTLY` e afins não rodam numa transação e
  falham com a mensagem do servidor.

**Testes:**

- **Domain:** `ProfileTests`: aparar, vazio remove, tamanho, controle, e os
  comandos de transação recusados, com as mesmas palavras aceitas dentro de
  texto, comentário, corpo `$$`, identificador e `ROLLBACK TO SAVEPOINT`;
  `DatabaseSecurityPolicyTests`: o script pede "Executar SQL" no destino, e
  `ExecuteSql` nunca alcança produção.
- **Application:** `PostCopyScriptTests`: roda no destino depois da
  verificação, com o NOTICE no log; destino sem a permissão recusa antes de
  tudo; sem script, a permissão não é pedida; verificação reprovada deixa o
  script de fora; falha do script falha a cópia; a cópia comum não o roda; o
  [Validar] avisa; salvar, duplicar e comparar levam o script.
- **Infrastructure:** `DatabaseOperationsPersistenceTests`: ida e volta e
  remoção; `PostgresIntegrationTests` (PostgreSQL real): `@`, `$1` e `DO $$`
  no script, o NOTICE, o rollback inteiro numa falha (`42P01`), o `COMMIT`
  escapado e a recusa sem "Executar SQL".
- **Desktop:** `PostCopyScriptScreenTests`: editar traz, novo começa vazio,
  salvar grava e vazio remove, a janela mostra o campo.
- **Mcp:** `DatabaseToolTests`: `get_details` mostra o script; uma alteração
  por regra o mantém, sem mudar o campo.

## ADR-084 — Orquestração remota: o servidor do usuário coordena, e cada máquina tem um agente que só disca para fora

**Estado:** proposto (fase 2). Nada implementado. O desenho completo está em
[`architecture/remote-orchestration.md`](architecture/remote-orchestration.md),
as ameaças em
[`architecture/remote-security-threat-model.md`](architecture/remote-security-threat-model.md)
e as entregas em
[`roadmap/phase-2-remote-orchestration.md`](roadmap/phase-2-remote-orchestration.md).

**Contexto:** o usuário quer, de um computador ou do celular, escolher uma
máquina, criar ou escolher uma tarefa, preparar o worktree, rodar os comandos
configurados, mandar um prompt ao Claude Code e acompanhar tudo, **sem o
desktop aberto na máquina que executa**. O app já tem as peças, todas locais:
worktrees (ADR-027), comandos (ADR-028/051/055), sessões do Claude (ADR-030/037),
cópias de banco (ADR-056/060) e um servidor de sincronização do próprio usuário
(ADR-061/078). Não há canal de comando: o ADR-061 deixou "executar comandos
remotamente" fora de escopo, e o `SyncCommandReviewGate` recusa até rodar um
comando cujo texto mudou em outro computador.

Exigências: Windows e Linux; pouco consumo; **nenhuma porta de entrada** nas
máquinas executoras; nada de Redis, RabbitMQ, Kubernetes ou outra
infraestrutura sem necessidade demonstrada; o modo offline intacto.

**Decisão:** três papéis.

1. **Controlador** (desktop ou celular): monta e assina o pedido.
2. **Coordenador**: o servidor de sincronização que já existe (ASP.NET Core,
   PostgreSQL, OpenIddict), com tabelas e endpoints novos. Guarda a operação,
   entrega ao agente e repassa eventos. Não executa nada e não decide sozinho
   o que roda (ADR-087).
3. **Agente** (`MyTaskApp.Agent`): processo sem tela em cada máquina
   autorizada, que **só abre conexões de saída** (HTTPS e WSS) e executa pelos
   mesmos casos de uso do desktop (ADR-085).

Os contratos ficam num projeto novo, `MyTaskApp.Remote.Contracts`, separado do
`Sync.Contracts`: a sincronização continua um protocolo de dados, e a
orquestração é um protocolo de pedidos.

### Alternativas

- **Um servidor de orquestração à parte.** Isolaria o código, mas pediria outro
  deploy, outra base de contas e outro OAuth para o usuário manter. O servidor
  do ADR-078 já tem contas, dispositivos, revogação, rate limit e TLS.
- **Conexão direta entre máquinas** (porta aberta, UPnP, VPN tipo Tailscale).
  Fere a exigência de nenhuma porta de entrada, ou põe uma dependência de rede
  fora do app. E não resolve o celular fora de casa.
- **Um broker de mensagens** (RabbitMQ, MQTT, Redis Streams). A carga é de
  poucas operações por hora, de um usuário. O PostgreSQL já é durável e
  transacional; um broker seria mais um serviço para proteger e atualizar, sem
  ganho mensurável.
- **Um serviço de terceiros** (GitHub Actions self-hosted, um runner de CI).
  Executaria de longe, mas fora do modelo de tarefa, worktree e sessão do app,
  e com credenciais em outro lugar.

### Consequências

- Sem servidor configurado, nada muda: o remoto é opcional como o sync.
- O servidor passa a ter tabelas de operação e um hub SignalR (ADR-086).
- Toda regra que hoje só existe numa tela precisa descer para a Application
  antes de ser usada de longe (o caso maior é o "Iniciar implementação" do
  `TaskDevelopmentViewModel`).

### Fora de escopo, de propósito

- Terminal remoto interativo, envio de arquivos, área de trabalho remota.
- Controlar máquinas de outra conta (o workspace continua pessoal).
- Rodar sem a sessão do usuário aberta (ADR-085).

## ADR-085 — O agente: um processo sem tela, do usuário, sobre o mesmo SQLite

**Estado:** proposto (fase 2). Nada implementado.

**Contexto:** a máquina executora precisa atender pedidos com o desktop
fechado. O que o agente usa só existe nela: o caminho do repositório
(`TagDirectory.Path`, só local desde o ADR-061), os comandos que não
sincronizam (ADR-066), as conexões de banco e as senhas no `ISecretStore`.
Hoje um único processo usa o banco: `SingleInstance` garante isso, e o
`DatabaseOperationGate` e o `McpGateway` serializam dentro do processo. O
arquivo já está em WAL, porque o EF Core o cria assim; o ADR-059 dizia o
contrário. Não existe host sem tela; o `McpTestHost` dos testes é o mais perto
disso.

**Decisão:**

1. **Outro executável, o mesmo código.** `MyTaskApp.Agent` compõe
   `AddApplication` + `AddInfrastructure` como o `McpTestHost`, com
   `ICurrentUser` e `IAlertPresenter` próprios, e nenhum ViewModel. O
   executor de casos de uso é o `ScopedUseCaseRunner` da Application, o mesmo
   do desktop e do celular.
2. **Como o usuário, no logon.** Tarefa agendada por usuário no Windows e
   `systemd --user` no Linux. DPAPI e libsecret continuam funcionando, porque
   são do usuário. Mutex próprio (`MyTaskApp.Agent`); o instalador fecha e
   reabre o agente como faz com o app (ADR-049).
3. **SQLite para dois processos.** O `DatabaseInitializer` garante o WAL a cada
   abertura. O escritor ocupado espera, porque o Microsoft.Data.Sqlite repete o
   comando até o timeout. A migration concorrente o EF já trava
   (`__EFMigrationsLock`). Feito na T-07. Falta: um processo mais velho que o
   esquema recusar subir.
4. **Um dono para cada laço**, por lease numa tabela (`ProcessLeases`: nome,
   PID, início, validade): o `SyncScheduler` e a manutenção rodam em quem tem o
   lease. Lembretes, bandeja e post-its são só do desktop. A renovação do token
   do sync roda sob o mesmo lease, relendo o cofre antes, para dois processos
   não girarem o mesmo refresh token.
5. **Gate de banco entre processos.** A `DatabaseOperationAudit` ganha o dono
   (PID + início); a recuperação só marca como interrompida a operação cujo dono
   não existe mais.
6. **IPC local.** Named pipe com `PipeOptions.CurrentUserOnly` (Unix socket no
   Linux), só para "dados mudaram", "sincronizar agora" e o pareamento local.
   Nada de execução por ali.

### Alternativas

- **Executor dentro do desktop.** É o mais simples, mas exige o desktop aberto:
  fere o requisito.
- **Agente como outro dispositivo de sync, com banco próprio.** Não teria os
  dados só locais (caminhos, conexões, senhas) que justamente não sincronizam.
- **Desktop como cliente fino do agente**, com todo o acesso a dados passando
  por ele. É o destino mais limpo, mas reescreve o acesso a dados de todas as
  telas. Fica como evolução possível.
- **Serviço de sistema** (Windows Service, systemd de sistema). Rodaria sem
  logon, mas DPAPI e libsecret são do usuário; pediria um cofre de máquina e
  daria ao agente mais poder do que o usuário tem.
- **Mutex e evento nomeados para coordenar.** O evento nomeado não existe no
  .NET em Unix; a tabela de lease funciona igual nos dois sistemas e dá para
  ver quem é o dono.

### Consequências

- Backup que copia só o `.db` perde o que ainda está no `-wal`. Não é novo, mas
  dois processos abertos o tornam mais provável (documentar em "Onde ficam os
  dados").
- A bolinha do sync (ADR-063) passa a ler o estado do banco quando o dono do
  lease é o agente.
- "Desktop fechado" quer dizer usuário logado, com a tela bloqueada ou não.

### Fora de escopo, de propósito

- Rodar sem a sessão do usuário (cofre de máquina).
- macOS (não há instalador hoje).

## ADR-086 — Operação remota: registro no PostgreSQL, diário no agente, SignalR só como campainha

**Estado:** proposto (fase 2). Nada implementado.

**Contexto:** uma operação pode durar horas (uma sessão do Claude, uma cópia de
banco). A rede cai, o celular dorme, o agente reinicia, o servidor é
atualizado. O estado não pode depender de uma conexão aberta. O sync (ADR-061)
só faz polling a cada 60 s, e uma aprovação de ferramenta do Claude esperando
um minuto é inviável.

**Decisão:**

1. **A verdade é durável dos dois lados.** No servidor, `RemoteOperations`
   (pedido assinado, estado, `NotAfter`, `CancelRequestedAt`) e
   `RemoteOperationEvents` (`(OperationId, Seq)` único). No agente,
   `RemoteOperationJournal` e os eventos ainda sem confirmação, no SQLite e
   fora do `SyncRegistry`.
2. **Idempotência em toda escrita.** Criar: `OperationId` (UUIDv7 do
   controlador) com o hash do pedido, como o `ProcessedOperations`. Aceitar: a
   linha do diário existe antes da resposta. Eventos: `Seq` denso gerado pelo
   agente; o servidor ignora repetidos e devolve o maior contíguo; o agente só
   apaga o que teve confirmação.
3. **Estados:** `Queued`, `Dispatched`, `Accepted`, `Running`,
   `WaitingApproval`, `Succeeded`, `Failed`, `Canceled`, `Rejected`, `Expired`,
   `Interrupted`. O servidor decide até o aceite; depois, o agente, com a
   transição conferida por uma tabela e regressão recusada. Pedido de
   cancelamento é carimbo, não estado.
4. **Tempo de vida.** `NotAfter` (padrão 10 min, teto 24 h): máquina desligada
   não roda amanhã o que foi pedido hoje. Despacho sem aceite volta à fila em
   2 min. Nada se repete sozinho: "Repetir" cria outra operação, com `RetryOf`.
5. **Checkpoints** nas operações compostas: cada etapa grava o fim no diário.
   Depois de cair, o agente retoma o que é retomável (preparar; worktree, por
   `AdoptExisting`) e marca `Interrupted` o que não é (cópia de banco, comando
   do usuário).
6. **SignalR (`/hubs/remote`) só avisa**: há operação, pediram cancelamento,
   chegaram eventos, há aprovação. Sem o hub, o agente busca por REST a cada
   30 s. Uma instância de servidor dispensa backplane.

### Alternativas

- **Só polling**, como o sync. Simples, mas aprovação e saída ao vivo ficariam
  com até um minuto de atraso.
- **Long polling ou SSE próprios.** Sem pacote novo, mas é protocolo para
  manter à mão (reconexão, keep-alive), e o SSE não tem cliente pronto no MAUI.
- **WebSocket cru.** Mesmo custo do anterior, sem fallback.
- **Estado na conexão** (o hub como fonte da verdade). Uma queda perderia
  eventos e aprovações; o servidor reiniciado esqueceria o que rodava.

### Consequências

- Pacote novo só nos clientes: `Microsoft.AspNetCore.SignalR.Client`. O
  servidor já tem o SignalR no framework.
- O proxy (Caddy/Traefik) precisa repassar WebSocket, e a query string do hub
  (que leva o token) sai do log.
- Os eventos ocupam o PostgreSQL: teto de 1 MiB por operação e retenção de
  30 dias.

### Fora de escopo, de propósito

- Ordem global entre operações de máquinas diferentes.
- Retomar o processo do SO depois de o agente cair: o processo morre junto
  (Job Object no Windows, grupo de processos no Linux).

## ADR-087 — Confiança: pareamento na máquina executora, chave por dispositivo e operação assinada

**Estado:** proposto (fase 2). Nada implementado.

**Contexto:** executar de longe é o maior poder que o app já teve. Hoje o
token do sync prova "esta conta, este dispositivo", e o servidor é a
autoridade. Para executar comandos isso não basta: um servidor invadido, um
backup vazado com as chaves de assinatura ou uma conta Google comprometida
poderiam mandar rodar qualquer coisa em todas as máquinas. É o mesmo cuidado
que levou ao `SyncCommandReviewGate`.

**Decisão:**

1. **Uma chave por instalação.** ECDSA P-256 (ES256), gerada no dispositivo; a
   privada vai para o `ISecretStore` (`"device-key"`) e nunca sai; a pública é
   registrada no servidor.
2. **Operação assinada.** Pedido, cancelamento e resposta de aprovação são JWS
   compactos assinados pelo controlador. O agente lê os bytes assinados, e não
   uma cópia do servidor. `TargetDeviceId`, `WorkspaceId`, `NotAfter` e
   `OperationId` estão dentro da assinatura; o agente guarda os ids vistos por
   30 dias.
3. **Confiança ancorada no executor.** O agente aceita só chaves de
   `TrustedControllers`, uma tabela local gravada por um pareamento confirmado
   **na própria máquina** (desktop ou CLI do agente). O código de 6 dígitos é
   derivado das duas impressões digitais e de um nonce: se o servidor trocar a
   chave no meio, os códigos não batem. A pessoa **digita** no executor o
   código que o controlador mostra (o executor não o exibe): comparar dois
   números na tela é fácil de aprovar sem olhar. Feito na T-05:
   `PairingCode`, `ConfirmPairingHandler` e as rotas `/remote/pairings`.
4. **Ligar o executor é local.** "Aceitar controle remoto" vem desligado, não
   sincroniza e só se liga na própria máquina.
5. **OAuth.** Escopo novo `remote` nos dois clientes, pedido só quando o
   usuário liga o remoto. Rate limiter `remote` separado do `sync` (ADR-069).
   O servidor já o conhece (T-02): `RemoteSchema.Scope`, permitido aos dois
   clientes e exigido, junto com o `sync`, pela política `ServerSetup.RemotePolicy`.
   O cliente só pode pedi-lo depois de o servidor estar atualizado: o OpenIddict
   recusa escopo desconhecido com `invalid_scope`, e o login do sync cairia junto.
6. **Revogar.** Revogar o dispositivo (ADR-061) revoga a chave, e os agentes a
   tiram de `TrustedControllers` quando recebem o aviso. Se o aviso não chegar,
   o servidor recusa o token do revogado de qualquer jeito.

### Alternativas

- **Confiar no servidor e no token** (o modelo do sync). Mais simples, mas um
  servidor comprometido vira execução remota em todas as máquinas.
- **mTLS por dispositivo.** Prende o canal, mas não o pedido: o servidor ainda
  repassaria qualquer coisa. E o proxy TLS (Caddy) teria de repassar o
  certificado do cliente.
- **Aprovar cada operação na máquina executora.** Seguro, mas inútil: não há
  ninguém na frente dela.
- **Pareamento por QR code na tela do executor.** Bom, e pode vir depois; o
  código numérico funciona também pelo CLI, numa máquina Linux sem tela.

### Consequências

- Parear exige estar uma vez na máquina executora.
- O celular precisa destravar (biometria/PIN do sistema) para assinar.
- O servidor confere a assinatura também, para recusar lixo cedo, mas a
  decisão é do agente.

### Fora de escopo, de propósito

- Cifra ponta a ponta dos eventos (o servidor ainda vê saídas e nomes): risco
  residual R-04 do modelo de ameaças.

## ADR-088 — Catálogo fechado de operações: nenhum texto de shell vem de fora

**Estado:** proposto (fase 2). Nada implementado.

**Contexto:** o app não tem allowlist de executáveis: o comando cadastrado
roda no `cmd`/`sh` como foi escrito (ADR-028). A confiança vem de quem
cadastrou, nesta máquina, ou de quem revisou (`ICommandReviewGate`).
`RequiresConfirmation` só é cobrado pela tela. `CommandVariables.UnsafeCharacter`
filtra só os valores de contexto.

**Decisão:**

1. **Tipos fechados:** `environment.inspect`, `worktree.provision`,
   `worktree.remove`, `command.run`, `agent.start`, `agent.prompt`,
   `agent.stop`, `database.copy`. Cada um tem um contrato tipado e um
   tradutor para os casos de uso existentes. Nenhum campo é texto de shell,
   SQL ou argumento de linha de comando.
2. **Comando só por id**, e só com `RemoteAllowed`. É uma coluna **só local**
   (fora do mapa do sync, então não muda de longe) que **cai quando o texto do
   comando muda**, aqui ou por sync. O `ICommandReviewGate` continua valendo.
3. **`RequiresConfirmation` é do caso de uso** (`RunQuickCommand.Confirmed`,
   ADR-051): a tela marca o pedido depois do diálogo, e o agente só o marca
   depois de uma aprovação remota.
4. **Parâmetros vindos de fora** passam por `UnsafeCharacter` e por um teto de
   tamanho, como os valores de contexto.
5. **Diretórios** só como ids (diretório da etiqueta, ambiente); o caminho é
   resolvido no agente, sem `..`, sem absoluto e sem link para fora do worktree.
6. **Catálogo publicado pelo agente**: diretórios, comandos, conexões e perfis
   do Claude liberados, com ids e nomes, **sem** caminho, host, usuário, texto
   de comando ou senha. O controlador monta a tela a partir dele.

### Alternativas

- **Comando livre com allowlist de executáveis** (`git`, `dotnet`, `npm`). Uma
  allowlist de executável não segura argumentos (`git -c core.sshCommand=…`,
  `npm run` de qualquer script) e o shell não interpreta o que a lista entende.
- **Comando livre com aprovação na máquina executora.** Não há ninguém nela.
- **Liberar todos os comandos já cadastrados.** Um comando escrito para rodar
  diante do usuário (um `deploy`) não deveria virar remoto sem uma escolha.

### Consequências

- Quem quer um comando novo de longe precisa cadastrá-lo e liberá-lo na máquina
  executora. É o preço, e é de propósito.
- A tela de comandos ganha "Disponível para controle remoto".

### Fora de escopo, de propósito

- Criar ou editar comandos, conexões e diretórios de longe.

## ADR-089 — Claude Code remoto: headless, perfil escolhido no executor e aprovação humana

**Estado:** proposto (fase 2). Nada implementado.

**Contexto:** hoje o Claude abre num terminal real (`WindowsTerminalLauncher`;
no Linux não abre), e o app o acompanha pelos hooks (ADR-037). O argumento
padrão do card é `--dangerously-skip-permissions` (ADR-033): faz sentido com o
usuário olhando, e não com ninguém na frente da máquina. A Consulta
Inteligente (ADR-081) mostrou o caminho: travar pelos argumentos, não pelo
texto.

Decisão do usuário: na v1, **headless com aprovação remota**.

**Decisão:**

1. **Modo headless** (`IHeadlessAgentProvider`, implementado pelo
   `ClaudeCodeCliProvider`): `claude -p` com `--output-format stream-json`,
   prompt pelo stdin (sem a limitação de `"` e `%` do `claude.cmd`),
   `--session-id` no primeiro prompt e `--resume` nos seguintes, `--model` e
   `--effort` (ADR-040), e tetos locais de `--max-turns` e `--max-budget-usd`.
   Sempre com `--restricted`, como a consulta. Cada prompt é um processo; a conversa
   é a `AgentSession`, que ganha `Mode` (Interativo/Headless), o status
   `Stopped` e o id da operação.
2. **Perfil escolhido no executor.** Perfis locais: "ler", "editar no
   worktree", "editar e rodar testes". Viram `--allowedTools`,
   `--disallowedTools` e um `--settings` com `permissions.deny` para os
   segredos do app, `~/.ssh` e afins. O controlador só escolhe um perfil do
   catálogo.
3. **Nunca pular permissões.** Os `DefaultArguments` do card não entram no modo
   headless; o agente recusa `--dangerously-skip-permissions` e
   `bypassPermissions` venham de onde vierem. O `--restricted` é a segunda
   trava: no Claude Code 2.1.309 ele recusa os dois já na leitura dos
   argumentos ("bypassPermissions not supported in restricted mode").
4. **Sem configuração do projeto.** O worktree é dado não confiável. O
   `--restricted` ignora os arquivos de configuração do usuário, do projeto e
   locais, e com eles os hooks e as permissões do repositório. As regras do
   agente entram por `--settings`, que continua valendo. O MCP é só o de
   aprovação (`--strict-mcp-config`).
5. **Aprovação humana.** Ferramenta fora do perfil vai para
   `--permission-prompt-tool`, servido por um MCP do agente em loopback, com
   segredo por sessão (o padrão do `McpSecurityMiddleware`). Vira um
   `ApprovalRequest`; sem resposta em 10 min, nega. Um perfil sem canal de
   aprovação usa `--permission-prompts none`: o que perguntaria é negado.
6. **Ambiente limpo.** Fora `PG*`, `GH_TOKEN`, `GITHUB_TOKEN` e os
   `MYTASKAPP_*` que não são da sessão.

### O worktree não é sandbox

O processo roda como o usuário e alcança tudo o que ele alcança. As travas
são as do Claude (ferramentas e permissões) e a do humano que aprova. Isolar
no sistema operacional (contêiner, AppContainer, bubblewrap) fica como perfil
futuro.

### Alternativas

- **Terminal interativo remoto.** Reaproveitaria o fluxo atual, mas exige
  sessão gráfica, só funciona no Windows e não dá para responder de longe.
- **`--permission-mode acceptEdits` sem aprovação.** Deixa o Bash de fora ou
  liberado demais; não há meio-termo sem o humano.
- **SDK do Claude em outra linguagem** (TypeScript, Python). Daria
  `canUseTool` em processo, mas poria um runtime novo nas máquinas. O CLI com
  `stream-json` cobre o mesmo, e o app já o detecta.
- **O Remote Control do próprio Claude Code** (`--remote-control`). Deixa
  continuar uma sessão pelo app do Claude, sem código nosso. Mas é uma sessão
  interativa (terminal), presa à conta do Claude e fora da tarefa, do worktree,
  da auditoria e das aprovações do MyTaskApp. Continua disponível para quem
  abre o Claude pelo card, e não substitui a operação remota.
- **Sessões em segundo plano do Claude** (`--bg`, `claude attach`, `logs`,
  `stop`). Resolveriam o processo, mas `logs` devolve a tela do terminal, e não
  eventos estruturados para repassar e aprovar.

### Consequências

- Flags conferidos no Claude Code 2.1.309 sem chamar o modelo (detalhes na
  seção 7 de [`architecture/remote-orchestration.md`](architecture/remote-orchestration.md)).
  `--permission-prompt-tool` e `--max-turns` são aceitos mas não aparecem na
  ajuda; o formato do `stream-json` e o contrato da ferramenta de permissão
  ficam para um teste opt-in (`MYTASKAPP_TEST_CLAUDE=1`) com o modelo de
  verdade, e o `DetectAsync` passa a exigir a versão mínima conferida.
- O `-p` pula o diálogo de confiança do diretório e ignora em silêncio um
  arquivo de configuração inválido: o agente só abre sessão em worktree de
  repositório cadastrado, e um teste valida o `--settings` que ele gera.
- O acompanhamento vem do `stream-json`; os hooks continuam só no modo
  interativo.

### Fora de escopo, de propósito

- Outros provedores de agente de IA (a porta permite, nenhum na v1).
- Sandbox do sistema operacional.

## ADR-090 — Cópia de banco de longe: as mesmas barreiras, e produção com aprovação digitada

**Estado:** proposto (fase 2). Nada implementado.

**Contexto:** o ADR-056 pôs a cópia de produção atrás de quatro barreiras
(política, fluxo, processo, PostgreSQL só leitura), e a confirmação de produção
é um diálogo na tela (`ProductionConfirmed`, `TypedConfirmation`). De longe,
não há tela na máquina executora. O `DatabaseOperationGate` e a recuperação de
interrompidas são do processo (ADR-085 trata).

**Decisão:**

1. `database.copy` só com ids do catálogo: conexões com `RemoteAllowed` (coluna
   local, desligada por padrão). Nenhuma operação remota cria, edita ou lê
   conexão, senha ou perfil.
2. O agente chama o **mesmo** `RunDatabaseCopyHandler`/
   `CopyDevelopmentDatabaseHandler`: política, `Demand` em cada etapa,
   `PostgresProcessGuard` e auditoria inalterados.
3. **A confirmação vira aprovação.** Quando a política pede confirmação, o
   agente abre um `ApprovalRequest` (`ProductionDatabase`) com origem, destino,
   perfil de anonimização e tabelas sem regra. `CriticalProduction` exige o
   nome do banco digitado na resposta assinada.
4. A auditoria (`DatabaseOperationAudit`) registra o dispositivo que assinou.

### Alternativas

- **Recusar produção de longe.** Mais simples, mas o caso de uso principal da
  cópia (ADR-060) é justamente produção anonimizada para o ambiente.
- **Confirmação no pedido**, e não depois da política. O controlador não sabe
  o que a política vai exigir; quem sabe é o executor, com as conexões dele.

### Consequências

- Uma cópia remota de produção sempre para em `WaitingApproval`, mesmo pedida
  pelo próprio usuário. É de propósito.

### Fora de escopo, de propósito

- Dump simples, restauração arbitrária, SQL livre e o script pós-cópia
  (ADR-082) disparados de longe sem passar pela cópia.
