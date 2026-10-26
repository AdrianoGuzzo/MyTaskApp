# ADR-078 — O servidor de sincronização é do usuário: nenhum embutido, e a troca é explícita

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-20, commit `7453854` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SyncServerUrl, ISyncServerAddress, SwitchServerAsync, server_changed, ISyncServerProbe, Configurar servidor, AllowHttpLoopback, SyncServerViewModel |
| **Relações** | relacionado: [ADR-018](018-instalador-inno-setup-e-dados-do-usuario.md), [ADR-062](062-lista-por-computador-onde-aparece.md), [ADR-067](067-celular-dispositivo-da-sincronizacao.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md) |
| **Código** | `src/MyTaskApp.Application/Sync/SyncServerUrl.cs`, `src/MyTaskApp.Application/Sync/SyncUseCases.cs`, `src/MyTaskApp.Application/Sync/SyncEngine.cs`, `src/MyTaskApp.Mobile.Core/Presentation/SyncServerSettings.cs` |

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

## Alternativas

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

## Consequências

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
