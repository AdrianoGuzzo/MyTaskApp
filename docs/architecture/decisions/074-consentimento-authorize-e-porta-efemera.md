# ADR-074 — Consentimento no `/connect/authorize` fora do login recém-feito, e loopback com porta efêmera

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-18, commit `44dadc5` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | /connect/authorize, consentimento, mytaskapp_authorize_flow, porta efêmera, loopback, RFC 8252, CallbackPort, SEC-018 |
| **Relações** | revisa: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md); relacionado: [ADR-067](067-celular-dispositivo-da-sincronizacao.md) |
| **Código** | `src/MyTaskApp.Server.Api/ConnectEndpoints.cs`, `src/MyTaskApp.Server.Api/ServerSupport.cs`, `src/MyTaskApp.Infrastructure/Sync/Http/SyncAuthorizationRedirect.cs`, `src/MyTaskApp.Infrastructure/Sync/Http/SyncOptions.cs` |

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

## Alternativas

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

## Consequências

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
