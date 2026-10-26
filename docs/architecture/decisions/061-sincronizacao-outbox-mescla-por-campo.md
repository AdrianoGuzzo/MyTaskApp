# ADR-061 — Sincronização opcional: outbox local, mescla por campo no servidor

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-13, commit `6583ea0` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SyncOutbox, SyncCaptureInterceptor, SqlRowWriter, OpenIddict, PKCE, tombstone, baseVersion, SyncScheduler |
| **Relações** | revisa: [ADR-026](026-diretorios-da-etiqueta-alias.md); revisto por: [ADR-062](062-lista-por-computador-onde-aparece.md), [ADR-066](066-comandos-globais-locais-por-padrao.md), [ADR-069](069-rate-limit-por-conta.md), [ADR-071](071-pull-com-teto-por-pagina.md), [ADR-074](074-consentimento-authorize-e-porta-efemera.md); relacionado: [ADR-063](063-indicador-sincronizacao-no-cabecalho.md), [ADR-064](064-repositorio-git-do-diretorio-clone.md), [ADR-067](067-celular-dispositivo-da-sincronizacao.md), [ADR-070](070-servidor-sync-production-tls.md), [ADR-072](072-push-so-guarda-o-contrato.md), [ADR-073](073-chaves-data-protection-cifradas-com-pfx.md), [ADR-077](077-importar-etiqueta-de-outro-computador.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md), [ADR-086](086-operacao-remota-registro-diario-e-signalr.md), [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Sync/Capture/SyncCaptureInterceptor.cs`, `src/MyTaskApp.Infrastructure/Sync/Apply/SqlRowWriter.cs`, `src/MyTaskApp.Server.Application/PushProcessor.cs`, `src/MyTaskApp.Server.Api/ServerSetup.cs` |

**Contexto:** o usuário quer as mesmas tarefas em vários computadores, sem
perder o que o app é hoje: local, rápido e inteiro sem rede. As exigências:

- a sincronização é **opcional**: sem conta, nada muda e nada é gravado;
- nada de senha, token ou connection string sai do computador;
- caminho de pasta de um computador nunca sobrescreve o de outro;
- login pelo Google, sem segredo dentro do executável do desktop;
- dispositivos com apelido e revogação, e isolamento entre contas.

O guia de uso e o fluxo estão em [`sync.md`](../../sync.md). O servidor, a
configuração e a implantação estão em [`sync-server.md`](../../sync-server.md).

**Decisão:** um servidor próprio (ASP.NET Core + PostgreSQL), opcional. No
desktop, um **outbox** gravado na mesma transação da mudança, e um ciclo
push → pull por polling. O servidor é a autoridade da ordem: ele **mescla por
campo** e decide o conflito pela versão dele, e não pelo relógio de ninguém.

## Alternativas

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

## Classificação dos dados

Cada tipo sincronizado tem um mapa explícito de campos no
`MyTaskApp.Sync.Contracts`: nome, tipo, nulidade, tamanho, imutável, política
de conflito, referências e chave única. Do lado do desktop, cada coluna do EF
é **compartilhada** ou **só local**. Um teste percorre todas as colunas
mapeadas e falha com coluna sem classificação, inclusive em owned types.

Dado que nunca sai da máquina não está no mapa e, portanto, não tem como sair.
A tabela completa está em [`sync.md`](../../sync.md#o-que-sincroniza). As decisões
que mudam o domínio são mínimas:

- **`TagDirectory.Path` passa a ser anulável:** "não mapeado neste computador".
  A migration `TagDirectoryPathOptional` recria a tabela sem perder linhas.
- **`TaskDevelopmentStatus.NotCreatedHere`:** o ambiente veio de outro
  computador. "Iniciar implementação" o cria aqui.
- **`TaskItem.ReconcileDerivedState()`:** depois de uma mudança remota,
  recalcula a conclusão e o estado dos lembretes, sem regra paralela.

## Captura: interceptor, retrato e outbox

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

## Aplicação remota: SQL pelos metadados do EF

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

## Servidor

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

## Autenticação

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

## Comandos de outro computador

O texto de um comando é dado compartilhado, mas rodá-lo é deste computador. Um
comando global, do diretório ou do ambiente alterado por **outro**
dispositivo passa pelo `ICommandReviewGate` antes da primeira execução. Ele é
chamado pelos três caminhos de execução, e a janela de Sincronização mostra o
texto inteiro e a origem.

Executar comandos remotamente, terminais remotos e envio de arquivos ficam
fora de escopo.

## Desktop: agendador e tela

- O `SyncScheduler` roda 3 s depois de uma mudança local, a cada 60 s e na
  hora com "Sincronizar agora". É execução única. Falha passageira espera com
  *full jitter* entre 2 s e 5 min. Bloqueado (login vencido, dispositivo
  revogado, app velho), ele só acorda com um aviso.
- A janela "Sincronização…" mostra conta, estado, conflitos, falhas, comandos a
  revisar e dispositivos. A janela da tarefa mostra a origem.
- O `DeviceId` fica em `%LOCALAPPDATA%`, fora do banco: um banco copiado para
  outra máquina não se passa pela original.

## Consequências

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
