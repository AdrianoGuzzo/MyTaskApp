# MyTaskApp no celular (Android e iOS)

O app do celular é feito em .NET MAUI. Ele é **mais um dispositivo da
sincronização**: roda a mesma Application do desktop sobre um SQLite próprio
e conversa só com o servidor de sincronização. A decisão e as alternativas
estão no [ADR-067](ARCHITECTURE.md#adr-067--o-celular-como-mais-um-dispositivo-da-sincronização-net-maui).

| Plataforma | Estado |
|---|---|
| Android | compila (Debug e Release) e foi validado no emulador (API 36). |
| iOS | configurado, **não validado**: não houve Mac. Veja [iOS](#ios). |

## O que o app faz

- **Tarefas:** todas as tarefas de todos os dispositivos numa lista só, em
  páginas.
  - Busca por texto.
  - Atalhos: Todas, Pendentes, Vencidas, Concluídas, Canceladas.
  - Filtros por prioridade, etiquetas e origem; ordenação.
  - Concluir e reabrir pela linha.
  - Puxar para atualizar sincroniza.
- **Detalhe:**
  - descrição, status, prioridade, origem, etiquetas com cor, prazo e datas;
  - em que dispositivo foi criada e alterada;
  - o histórico deste celular;
  - os repositórios em que a tarefa é desenvolvida (só leitura).
  - Ações conforme o estado: concluir, reabrir, cancelar, arquivar, restaurar,
    lixeira.
- **Nova tarefa e edição:**
  - título (obrigatório, até 200 caracteres), descrição, prioridade, status,
    origem, etiquetas existentes e prazo;
  - perguntar antes de descartar alterações.
- **Conta:**
  - entrar com Google, o estado da sincronização e os dispositivos;
  - conflitos ("Reaplicar o meu" ou "Manter o do servidor");
  - alterações recusadas (tentar de novo ou descartar);
  - pausar e sair.
- **Funciona sem rede:** o que for feito fica guardado no celular e sobe quando
  a conexão voltar. O indicador no topo diferencia:
  - Sincronizado
  - Pendente (N)
  - Sem conexão
  - Atenção
  - Só neste celular

### Origem, projeto e etiquetas

- **Origem** é onde a tarefa aparece (ADR-062): compartilhada, só neste
  celular, ou só num computador da conta.
  - O filtro é exato: "Notebook" mostra só as do notebook.
  - Tarefa criada no celular nasce **compartilhada**, para aparecer na visão
    padrão do desktop.
- **Etiquetas** são as do desktop. O celular escolhe entre elas, mas não as cria
  nem as edita. Use etiquetas para contextos como Trabalho, Estudos ou Pessoal.
- **Projeto** não existe no domínio do MyTaskApp. Não há filtro de projeto; o
  detalhe mostra o desenvolvimento (repositórios e worktrees).

## Projetos

```
src/MyTaskApp.Mobile.Core   net10.0                    ViewModels, apresentação, composição, portas da plataforma
src/MyTaskApp.Mobile        net10.0-android (+ios)     telas XAML, Shell, adaptadores MAUI, arquivos de Android e iOS
tests/MyTaskApp.Mobile.Core.Tests                      ViewModels sobre a pilha de verdade, com SQLite temporário
MyTaskApp.Mobile.slnx                                  a casca MAUI e o que ela usa
```

- O núcleo (`Mobile.Core`) não depende de MAUI; um teste de arquitetura garante
  isso. Ele está no `MyTaskApp.slnx` e entra no portão de cobertura do CI.
- A casca MAUI só está no `MyTaskApp.Mobile.slnx`. Assim, quem trabalha no
  desktop não precisa de workload.
- O que o celular reaproveita:
  - os handlers da Application: os mesmos do desktop e do MCP;
  - o `DbContext`, as migrations, a captura do outbox e a aplicação do que chega
    do servidor, via `AddLocalData`;
  - o `SyncEngine`, o `SyncScheduler` e o `SyncAccountService`.

## Autenticação

O login é o mesmo do desktop (ADR-061): Google pelo servidor, OpenIddict,
authorization code com PKCE S256, refresh rotativo. Só mudam o cliente e a
volta:

| | Desktop | Celular |
|---|---|---|
| Cliente OAuth | `mytaskapp-desktop` | `mytaskapp-mobile` |
| Volta do login | `http://127.0.0.1:<porta efêmera>/callback` (loopback, ADR-074) | `io.github.adrianoguzzo.mytaskapp://oauth/callback` (esquema do app) |
| Navegador | o padrão do sistema | Custom Tabs (Android) / `ASWebAuthenticationSession` (iOS), pelo `WebAuthenticator` |
| Refresh token | DPAPI / secret-tool | `SecureStorage` (Keystore / Keychain) |
| Access token | só na memória | só na memória |

1. "Entrar com Google" abre o `/connect/authorize` no navegador do sistema, com
   `client_id=mytaskapp-mobile`, PKCE, `device_id` e o nome do aparelho.
2. O servidor manda para o Google, cria ou acha a conta e devolve um código para
   o esquema do app.
3. O Android ou o iOS entrega a volta ao app. O `AppLinkAuthorizationRedirect`
   confere o `state`, e o mesmo `SyncAuthenticationService` do desktop troca o
   código por tokens.
4. O app pede `/me` e `/workspaces`, registra o dispositivo e começa a
   sincronizar.

**Sobre o esquema próprio:**

- O servidor aceita esse redirect **só** para `mytaskapp-mobile`.
- Um código interceptado não serve sem o verificador do PKCE, que nunca sai do
  app.

**Expiração:**

- Refresh recusado (vencido, revogado, ou certificado do servidor trocado): o
  app apaga o segredo e mostra "Atenção — o login venceu".
- Dispositivo revogado: entrar de novo registra o celular como um dispositivo
  novo.

## API usada pelo celular

Nenhuma rota nova: as do servidor de sincronização (`/api/v1`, versionada no
caminho e por `SyncSchema.Current`), descritas em
[sync-server.md](sync-server.md). O celular usa:

| Rota | Para quê |
|---|---|
| `GET /connect/authorize`, `POST /connect/token`, `POST /connect/revoke` | login, renovação, sair |
| `GET /api/v1/me`, `GET /api/v1/workspaces` | a conta e o workspace pessoal |
| `PUT /api/v1/devices/current`, `GET /api/v1/devices`, `PATCH /api/v1/devices/{id}` | registrar, listar e renomear |
| `POST /api/v1/workspaces/{id}/sync/push` | enviar o outbox (lotes de 100, `OpId` como chave de idempotência) |
| `GET /api/v1/workspaces/{id}/sync/pull?cursor=` | trazer o que mudou (páginas de 200) |
| `POST /api/v1/workspaces/{id}/sync/fetch` | reler entidades (tentar de novo, descartar) |

Erros vêm como ProblemDetails com `code`. O cliente os traduz em
`SyncFailureKind`: transitório (tenta sozinho, com espera crescente), precisa
reconectar, revogado, versão antiga.

## Sincronização

É a do desktop, sem diferença no protocolo (ADR-061):

- **Gravar é local.** Cada `SaveChanges` grava a mudança e, na mesma transação,
  a operação no outbox. Sem rede, nada se perde.
- **Ciclo:** olá → enviar o outbox → trazer do servidor. Roda 3 s depois de uma
  gravação, a cada minuto parado, ao puxar para atualizar, ao voltar a rede e
  ao reabrir o app.
- **Reenvio seguro:** cada operação tem um `OpId`. Se a resposta se perder, o
  reenvio devolve o mesmo resultado em vez de gravar de novo.
- **Conflitos:** o servidor decide por campo. Campos diferentes nunca
  conflitam; no mesmo campo, vence quem chegou primeiro, e o perdedor vê o
  conflito na aba Conta.
- **Só em primeiro plano.** Em segundo plano o laço para (o sistema congelaria o
  processo de qualquer jeito). O que estiver no meio é reenviado na volta com os
  mesmos ids.

## Ambiente de desenvolvimento

**Requisitos:**

- O SDK do .NET do `global.json` (10.0.4xx).
- O workload do Android: `dotnet workload install maui-android`.
  - Numa instalação por usuário, o workload vai para o mesmo
    `%LOCALAPPDATA%\Microsoft\dotnet`, com `DOTNET_ROOT` apontando para lá.
- JDK 21. A instalação do Visual Studio traz um em
  `C:\Program Files (x86)\Android\openjdk\jdk-21.*`.
- Android SDK com a plataforma 36 e build-tools. O que faltar, o próprio .NET
  instala:

  ```bash
  dotnet build src/MyTaskApp.Mobile -t:InstallAndroidDependencies -f net10.0-android \
    -p:AndroidSdkDirectory="<pasta do SDK>" -p:JavaSdkDirectory="<pasta do JDK>" \
    -p:AcceptAndroidSDKLicenses=True
  ```

- Um emulador (AVD) com Google Play, ou um celular com depuração USB.

### O servidor de sincronização no desenvolvimento

O build Debug aponta para `http://localhost:5280/`. No celular, `localhost` é o
próprio celular, não o computador. O `adb reverse` leva a porta do celular até
o computador:

```bash
adb reverse tcp:5280 tcp:5280
```

Notas:

- **Cleartext:** HTTP em claro só é aceito para `localhost` e `127.0.0.1`
  (`network_security_config.xml` no Android, `NSAllowsLocalNetworking` no iOS).
  O app recusa HTTP fora do loopback (`SyncOptions`), em qualquer build.
- **O servidor precisa conhecer o cliente do celular.** A versão do servidor que
  semeia o `mytaskapp-mobile` é a desta mudança. Um servidor antigo responde
  `invalid_client` no login do celular; atualize-o antes (ver
  [sync-server.md](sync-server.md)).
- **Servidor numa porta diferente** (por exemplo, a 5280 do computador já
  ocupada): `adb reverse tcp:5280 tcp:<porta>`.
  - O redirect do Google cadastrado no console é por porta; o login só funciona
    na porta registrada lá.

### Rodar no Android

```bash
# a partir da raiz do repositório
dotnet build src/MyTaskApp.Mobile/MyTaskApp.Mobile.csproj -t:Run -f net10.0-android \
  -p:AndroidSdkDirectory="<pasta do SDK>" -p:JavaSdkDirectory="<pasta do JDK>"
```

- O `-t:Run` instala e abre no emulador ou no celular conectado. Para instalar
  sem abrir, use `-t:Install`.
- O build Debug usa *Fast Deployment*: os assemblies vão por fora do APK.
  Instalar o `.apk` do Debug com `adb install` não funciona (o app fecha na
  hora com "No assemblies found"). Use `-t:Install`, `-t:Run` ou o Release.
- Os dados ficam em `FileSystem.AppDataDirectory`: `mytaskapp.db` e
  `device.json`, o id deste dispositivo. Desinstalar apaga tudo.

### Release e servidor de produção

```bash
dotnet build src/MyTaskApp.Mobile/MyTaskApp.Mobile.csproj -f net10.0-android -c Release \
  -p:SyncApiBaseUrl=https://sync.seudominio.com/
```

- O endereço do servidor é gravado no app na hora da build (como no desktop) e
  precisa ser HTTPS.
- **Assinatura:** sem configuração, o Release sai assinado com a chave de debug.
  Para publicar, configure `AndroidKeyStore`, `AndroidSigningKeyStore` e as
  senhas por variável de ambiente, nunca no repositório.
- **Play Store:** exige alinhamento de 16 KB das bibliotecas nativas a partir do
  Android 15. Confira o `libe_sqlite3.so` antes de publicar.

### iOS

Configurado e **não validado**: este ambiente não tem Mac.

**O que está pronto:**

- `net10.0-ios` entra no build no macOS ou com `-p:IncludeIos=true`.
- `Info.plist` com o esquema de volta (`CFBundleURLTypes`) e
  `NSAllowsLocalNetworking`.
- `Entitlements.plist` com o grupo do Keychain, usado pelo `SecureStorage`.
- `UseInterpreter` ligado: o EF Core precisa dele sem JIT.
- A pasta de dados fora do backup do iCloud.

**O que falta para validar:**

1. Um Mac com Xcode na versão que o workload `ios` do .NET 10 exige, e
   `dotnet workload install maui-ios`.
2. Compilar e rodar no simulador:

   ```bash
   dotnet build src/MyTaskApp.Mobile/MyTaskApp.Mobile.csproj -t:Run -f net10.0-ios
   ```

   Ou rodar o workflow manual *Mobile iOS (manual)*
   (`.github/workflows/mobile-ios.yml`), que compila para o simulador num
   runner macOS.
3. Conferir no simulador:
   - o login (volta pelo esquema);
   - o Keychain guardando o refresh;
   - o SQLite com o interpretador.
4. Para um iPhone de verdade: conta de desenvolvedor Apple, certificado e perfil
   de provisionamento.

## Testes

```bash
dotnet test tests/MyTaskApp.Mobile.Core.Tests                                   # o app sem MAUI
dotnet test tests/MyTaskApp.Infrastructure.Tests                                # login do celular, AddLocalData, origem
dotnet test tests/MyTaskApp.Server.IntegrationTests --filter "PhoneSyncTests"   # ponta a ponta, precisa de Docker
```

- **Testes do Mobile.Core:**
  - composição real e SQLite numa pasta temporária;
  - só as portas da plataforma são falsas (navegação, diálogos, navegador).
- **`PhoneSyncTests` (ponta a ponta):**
  - o celular entra pela tela Entrar contra a API de verdade (Google falso);
  - sincroniza com um desktop nos dois sentidos;
  - fica sem rede e volta;
  - filtra por origem.
- **CI:** o job `mobile-android` compila o APK em Release e confere que ele leva
  o SQLite nativo. Não há emulador no CI.

## Limitações conhecidas

- **Sem sincronização em segundo plano;** sem push. Abrir o app, puxar para
  atualizar ou a rede voltar disparam um ciclo.
- **Sem lembretes nem notificações** no celular.
- **Fora do celular:** cronômetro, post-its, Jira, comandos e ambientes de
  desenvolvimento ficam no desktop. Os dados deles sincronizam, mas não
  aparecem.
- **Etiquetas:** não se criam nem se editam no celular.
- **Projeto** não existe no domínio.
- **Servidor fixo por build.** Não há campo para trocar o servidor no app.
- **"Computador" no desktop:** o desktop ainda chama o celular de "computador"
  em "Enviar para…".
- **Histórico local:** o do detalhe é só deste celular; a auditoria não
  sincroniza.
- **Edição sem transação única.** Se um passo for recusado (por exemplo,
  concluir e mudar o prazo), o que veio antes já foi gravado. O formulário relê
  e mostra o estado real.
- **Avisos curtos no iOS:** o iOS não tem um equivalente nativo; o resultado
  aparece na própria tela.

## Integração futura com agentes de IA

Fora do escopo: o celular não roda comandos, não abre terminal e não conversa
com agentes. O que fica pronto:

- O desenvolvimento da tarefa (repositórios, branches, worktrees) já chega ao
  celular pela sincronização e aparece no detalhe, só para leitura.
- Status de tarefa nunca é status de agente: o celular não mostra sessões.

Uma versão futura que consulte ou acione agentes deve passar por uma API
autenticada própria, com autorização explícita, validação de comandos e
auditoria. Isso é uma funcionalidade separada, não uma extensão da
sincronização.
