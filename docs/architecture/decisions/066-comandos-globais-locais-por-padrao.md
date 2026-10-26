# ADR-066 — Comandos globais só deste computador, a menos que compartilhados

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-16, commit `a9dc1f1` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | IsShared, IsDetached, Compartilhar entre computadores, DevelopmentCommand, CommandSharing, RetryWaitingReceivedChangesHandler, tombstone |
| **Relações** | revisa: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md); relacionado: [ADR-051](051-comandos-rapidos-no-worktree.md), [ADR-055](055-comando-so-do-diretorio.md), [ADR-062](062-lista-por-computador-onde-aparece.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md), [ADR-088](088-catalogo-fechado-de-operacoes-remotas.md) |
| **Código** | `src/MyTaskApp.Domain/Commands/DevelopmentCommand.cs`, `src/MyTaskApp.Infrastructure/Sync/Apply/RemoteChangeApplier.cs`, `src/MyTaskApp.Server.Application/PushProcessor.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20261016063217_CommandSharing.cs` |

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

## Desmarcar: cada um fica com a sua cópia

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

## Compartilhar de novo: vale a versão daqui

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

## O mesmo apelido aqui e lá

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

## Alternativas

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

## Consequências

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

## Divergências com o código (conferido em 2026-10-24)

- O comentário XML de `src/MyTaskApp.Server.Application/PushProcessor.cs` ainda diz que só o vínculo tarefa↔etiqueta revive; o código (perto da linha 185) já faz o que este ADR decidiu.
