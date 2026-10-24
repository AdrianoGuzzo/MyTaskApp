# ADR-062 — Cada computador com a sua lista: o "onde aparece" da tarefa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-13, commit `e011ad5` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | DevicePlacement, PlacementKind, DevicePlacementState, PlacementPredicates, Onde aparece, Enviar para, CHEGARAM, ReceivedAt |
| **Relações** | revisa: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md); revisto por: [ADR-072](072-push-so-guarda-o-contrato.md); relacionado: [ADR-053](053-historico-sete-dias-projecao.md), [ADR-066](066-comandos-globais-locais-por-padrao.md), [ADR-067](067-celular-dispositivo-da-sincronizacao.md), [ADR-076](076-celular-pendentes-historico-dispositivo-lembrado.md), [ADR-077](077-importar-etiqueta-de-outro-computador.md), [ADR-078](078-servidor-de-sync-do-usuario.md) |
| **Código** | `src/MyTaskApp.Domain/Devices/DevicePlacement.cs`, `src/MyTaskApp.Application/Devices/DevicePlacementState.cs`, `src/MyTaskApp.Application/Devices/PlaceTask.cs`, `src/MyTaskApp.Infrastructure/Persistence/Queries/PlacementPredicates.cs` |

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

## Alternativas

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

## O lugar

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

## Quem é "este computador"

`DevicePlacementState` (singleton) guarda:

- o id atual (`device.json`);
- o id com que o banco foi ligado;
- os **ids antigos**: revogado e reconectado, o computador volta com outro id.

O `SyncState` guarda os ids antigos e os apelidos da conta. Assim o seletor e o
"Enviar para" funcionam ao abrir o app e sem rede. Antes da primeira conexão,
nada filtra, nada nasce com lugar, e o `device.json` nem é lido.

## Assentamento do legado

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

## Visibilidade

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

## Tela

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

## Consequências

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
