# MyTaskApp no celular (Android e iOS)

O app do celular é feito em .NET MAUI. Ele é **mais um dispositivo da
sincronização**: roda a mesma Application do desktop sobre um SQLite próprio
e conversa só com o servidor de sincronização. A decisão e as alternativas
estão no [ADR-067](architecture/decisions/067-celular-dispositivo-da-sincronizacao.md).

| Plataforma | Estado |
|---|---|
| Android | validado no emulador (API 36). Cada release traz o APK assinado, `MyTaskApp-<versão>-android.apk`. |
| iOS | configurado, **não validado num aparelho**. O workflow manual *Mobile iOS (manual)* gera um `.ipa` sem assinatura para instalar pelo Windows. Veja [iOS](#ios). |

Para instalar no seu celular sem Play Store, sem conta Apple paga e só com
Windows, veja [Teste pessoal sem custo, só com Windows](#teste-pessoal-sem-custo-só-com-windows).

## O que o app faz

- **Tarefas:** só o que falta fazer, de todos os dispositivos, em páginas
  (ADR-076, ADR-079).
  - **Dispositivo no título:** o topo da lista mostra, grande, o dispositivo
    em foco ("Notebook ⌄").
    - Um toque abre a folha com Todos os dispositivos, Compartilhadas, Este
      celular e cada computador da conta.
    - A escolha **volta na próxima abertura do app** e vale também para o
      Histórico.
    - Sem conta ligada, o título é "Tarefas".
  - Busca por texto, atrás da lupa do topo.
  - Filtros pelo funil do topo, que ganha um ponto quando há algo ligado:
    - só as vencidas;
    - prioridade e etiquetas;
    - ordenação.
  - "＋ Nova tarefa" no rodapé, discreta, sem cobrir a lista.
  - Concluir pela linha tira a tarefa da lista; ela passa para o Histórico.
  - "Limpar filtros" volta às pendentes e a todos os dispositivos.
- **Histórico:** a aba do meio, com as concluídas e as canceladas.
  - Atalhos: Concluídas (o padrão) e Canceladas.
  - Mesma busca, mesmos filtros e o mesmo dispositivo da aba Tarefas.
  - Reabrir uma concluída a devolve para a aba Tarefas.
- **Nas duas listas:**
  - Concluir e reabrir pela linha (ADR-075):
    - pelo círculo à esquerda do cartão;
    - ou arrastando para a direita, que só **revela** o botão "Concluir" e
      não conclui sozinho.

    Depois, a barra "Tarefa concluída · Desfazer" fica 8 s na tela.
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
  - pausar e sair;
  - **Aparência:** os sete temas do desktop (Papel, Sépia, Carvão, Nórdico,
    Ameixa, Verde Pecuária, Alto contraste) e "Automático", que segue o
    claro/escuro do celular. Vale ao tocar e fica só neste aparelho.
- **Teclado:** tocar fora do campo fecha o teclado. No iPhone há também o "OK"
  em cima do teclado, e arrastar a lista para baixo o fecha.
- **Funciona sem rede:** o que for feito fica guardado no celular e sobe quando
  a conexão voltar. A pílula no topo da lista mostra uma bolinha com as cores
  do desktop e a frase ("Sincronizado · há 2 min"); tocar nela abre a Conta.
  Ela diferencia:
  - Sincronizado
  - Pendente (N)
  - Sem conexão
  - Atenção
  - Só neste celular

### Origem, projeto e etiquetas

- **Origem** é onde a tarefa aparece (ADR-062): compartilhada, só neste
  celular, ou só num computador da conta.
  - O filtro de dispositivo (o título da lista) é exato: "Notebook" mostra só
    as do notebook, sem as compartilhadas.
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
caminho e por `SyncSchema.Current`), cuja lista completa é a classe `ApiRoutes`
de `src/MyTaskApp.Sync.Contracts/SyncSchema.cs`; o servidor está em
[sync-server.md](sync-server.md). O celular usa só estas (revogar um
dispositivo, `POST /api/v1/devices/{id}/revoke`, é só do desktop, e
`GET …/sync/status` nenhum cliente chama):

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

## Servidor de sincronização

O app não traz servidor nenhum ([ADR-078](architecture/decisions/078-servidor-de-sync-do-usuario.md)). Cada pessoa usa a
própria instância do servidor ([sync-server.md](sync-server.md)), e o endereço
é escolhido no app.

**Só offline.** Ao abrir pela primeira vez, o app cria o banco local e mostra a
lista. Não pede login nem servidor. Criar, editar, concluir, arquivar e mandar
para a lixeira funcionam sem internet. A aba Conta mostra "Sincronização
desativada" e o botão **Configurar servidor**. Sem servidor, nada é guardado
para enviar.

**Configurar.** *Conta → Configurar servidor* (ou *Configurar servidor* na tela
Entrar):

1. Informe o endereço da sua instância, com `https://` (por exemplo
   `https://sync.seudominio.com/`). Endereço inválido, sem HTTPS, com usuário e
   senha, com `?` ou `#` é recusado na hora, com o motivo.
2. **Testar conexão** lê o `/health` e o discovery do OAuth
   (`/.well-known/openid-configuration`). Não faz login e não envia dados. O
   resultado diz o que deu errado: endereço não encontrado (DNS), servidor
   desligado ou recusando, certificado inválido, tempo esgotado,
   redirecionamento, "não parece um servidor do MyTaskApp" ou erro do servidor.
   Se o servidor anunciar o login em outro endereço, o teste só avisa: o app
   continua no endereço que você digitou.
3. **Salvar.** O endereço fica nas preferências do aparelho
   (`SharedPreferences` no Android, `NSUserDefaults` no iOS, chave
   `sync-server-url`) e volta na próxima abertura. Não é segredo: o token fica
   no `SecureStorage`.
4. **Entrar com sua conta.** Só a partir daqui algo sai do celular. A tela
   Entrar diz para qual servidor as tarefas vão.

**Trocar ou tirar o servidor.** Com uma conta ligada a outro servidor, o app
pergunta antes e diz o que vai acontecer: quantas alterações ainda não chegaram
ao servidor antigo e que nenhuma tarefa é apagada. Ao confirmar:

- sai da conta **no servidor antigo** (revoga o token lá; sem rede, só apaga
  daqui);
- esquece o que era do servidor antigo: outbox, versões, conflitos, cursor, a
  conta e o workspace. Nada disso é reaproveitado no novo;
- as tarefas ficam. As alterações que não chegaram ao antigo não vão mais para
  ele; o estado atual delas continua aqui;
- **nada vai para o servidor novo até você entrar nele.** Ao entrar, o celular
  traz o que o servidor já tem e manda o que só existe aqui, como na primeira
  conexão.

"Cancelar" não muda nada. Tirar o servidor faz o mesmo e deixa a sincronização
desligada.

**Quem atualiza de uma versão antiga.** As versões até a 1.20 traziam o
servidor na build. Se o celular já tinha entrado nele, o endereço da conta
ligada passa a ser o salvo, e a sincronização continua. Se nunca entrou, fica
sem servidor.

**Proteções.**

- HTTPS sempre. HTTP só em `localhost`/`127.0.0.1` e só no build Debug.
- O certificado é validado pelo sistema; não há como desligar.
- O cliente não segue redirecionamento: um `3xx` vira erro, e o token não vai
  para outro endereço.
- O refresh token fica amarrado ao servidor que o emitiu. Um token de outro
  servidor nunca é enviado.
- Se o servidor mudar por fora da tela (um backup restaurado, por exemplo), a
  sincronização para com "O servidor mudou" antes de qualquer pedido.

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

No build Debug, a tela do servidor já vem preenchida com `http://localhost:5280/`
(`SyncServerSuggestion` no `.csproj`). É só sugestão: toque em **Salvar** uma
vez. No celular, `localhost` é o próprio celular, não o computador. O
`adb reverse` leva a porta do celular até o computador:

```bash
adb reverse tcp:5280 tcp:5280
```

Notas:

- **Cleartext:** HTTP em claro só é aceito para `localhost` e `127.0.0.1`
  (`network_security_config.xml` no Android, `NSAllowsLocalNetworking` no iOS).
  O app recusa HTTP fora do loopback em qualquer build, e no loopback só aceita
  no Debug (`SyncServerUrl`, ADR-078).
- **O servidor precisa conhecer o cliente do celular.** A versão do servidor que
  semeia o `mytaskapp-mobile` é a desta mudança. Um servidor antigo responde
  `invalid_client` no login do celular; atualize-o antes (ver
  [sync-server.md](sync-server.md)).
- **Servidor numa porta diferente** (por exemplo, a 5280 do computador já
  ocupada): `adb reverse tcp:5280 tcp:<porta>`.
  - O redirect do Google cadastrado no console é por porta; o login só funciona
    na porta registrada lá.

### Rodar no Android

Um comando faz tudo no Windows: liga o emulador (se não houver aparelho
conectado), espera o Android iniciar, faz o `adb reverse`, prepara o Chrome
para o login e compila com `-t:Run`:

```powershell
.\scripts\run-android.ps1                                                # servidor local na 5280
.\scripts\run-android.ps1 -SyncApiBaseUrl https://sync.seudominio.com/   # outro servidor
```

`-Avd`, `-Serial`, `-InstallOnly`, `-AndroidSdk` e `-JavaSdk` estão no
`Get-Help .\scripts\run-android.ps1 -Detailed`. O servidor também pode vir da
variável `MYTASKAPP_MOBILE_SYNC_URL`.

Na mão, os passos são:

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

### Release

```bash
dotnet build src/MyTaskApp.Mobile/MyTaskApp.Mobile.csproj -f net10.0-android -c Release
```

- **Nenhum servidor vai na build** (ADR-078). O APK é o mesmo para qualquer
  pessoa: cada uma configura o próprio servidor no app. Distribuir o app não
  depende da infraestrutura de ninguém.
- **Assinatura:** sem configuração, o Release sai assinado com a chave de debug.
  O APK da release é assinado pelo `release.yml` (job `android`) com a keystore
  guardada nos secrets do repositório: `AndroidKeyStore`,
  `AndroidSigningKeyStore` e as senhas pelo prefixo `env:`, nunca no
  repositório nem no log. Como criar a keystore e cadastrar os secrets:
  [Teste pessoal sem custo](#android-apk-assinado-em-cada-release).
- **Play Store:** exige alinhamento de 16 KB das bibliotecas nativas a partir do
  Android 15. Confira o `libe_sqlite3.so` antes de publicar.

### iOS

Configurado e **não validado num aparelho**: este ambiente não tem Mac.

**O que está pronto:**

- `net10.0-ios` entra no build no macOS ou com `-p:IncludeIos=true`.
- `Info.plist` com o esquema de volta (`CFBundleURLTypes`) e
  `NSAllowsLocalNetworking`.
- `Entitlements.plist` com o grupo do Keychain, usado pelo `SecureStorage`:
  `$(AppIdentifierPrefix)io.github.adrianoguzzo.mytaskapp`, sem team ID fixo e
  sem nada que exija conta paga (push, App Groups, iCloud, Associated Domains).
  Assim um Apple ID gratuito consegue reassinar o app.
- `UseInterpreter` ligado: o EF Core precisa dele sem JIT.
- A pasta de dados fora do backup do iCloud.
- O workflow manual *Mobile iOS (manual)* (`.github/workflows/mobile-ios.yml`),
  num runner macOS:
  1. compila para o simulador (valida a configuração);
  2. compila para o aparelho **sem assinatura** e publica
     `MyTaskApp-<versão>-ios-unsigned.ipa` como artifact `ios-ipa`;
  3. com o campo `tag` preenchido, faz o checkout da tag e anexa o `.ipa` ao
     rascunho daquela release (se ainda for rascunho).
- **Xcode e workload fixos, juntos:** Xcode 26.6 e o workload set `10.0.401`
  (iOS 26.5.10318), no topo do `mobile-ios.yml`. Sem versão, o workload viria
  com o iOS 27.0, que exige o Xcode 27. Subir para o Xcode 27 pede antes o
  ciclo de vida UIScene no app: sem ele, um app compilado com o Xcode 27 fecha
  ao abrir.
- **`-p:IncludeAndroid=false`:** o restore avalia todos os alvos do csproj,
  mesmo com `-f`. Sem isso, o Mac pediria o workload do Android.

**O que falta para validar:**

1. Rodar o workflow pela primeira vez e conferir os dois builds.
2. Instalar o `.ipa` num iPhone pelo Windows
   ([iOS sem Mac](#ios-sem-mac-ipa-reassinado-com-um-apple-id-gratuito)) e
   passar a lista de conferência de lá.
3. Com um Mac, também dá para rodar no simulador:

   ```bash
   dotnet build src/MyTaskApp.Mobile/MyTaskApp.Mobile.csproj -t:Run -f net10.0-ios -p:IncludeAndroid=false -p:CodesignKey=-
   ```

   O `-p:CodesignKey=-` assina ad-hoc. Sem ele, o `Entitlements.plist`
   preenchido faz o SDK exigir certificado e perfil até para o simulador.

4. App Store ou TestFlight continuam exigindo a conta de desenvolvedor paga.

## Teste pessoal sem custo, só com Windows

Para usar o app no próprio celular sem Play Store, sem conta Apple Developer
paga e sem Mac. Os comandos são de PowerShell.

### Android: APK assinado em cada release

Cada release traz `MyTaskApp-<versão>-android.apk`, assinado com uma keystore
sua. A keystore é criada uma vez e vale para sempre: o Android só instala uma
versão por cima da outra se as duas tiverem a mesma assinatura.

**1. Criar a keystore (uma vez).** O JDK 21 que vem com o Visual Studio traz o
`keytool`. Guarde a keystore **fora do repositório**:

```powershell
$dir = "$env:USERPROFILE\Documents\MyTaskApp-chaves"
New-Item -ItemType Directory -Force $dir | Out-Null
$keytool = (Resolve-Path "C:\Program Files (x86)\Android\openjdk\jdk-21.*\bin\keytool.exe" | Select-Object -Last 1).Path
& $keytool -genkeypair -v -keystore "$dir\mytaskapp.jks" -alias mytaskapp `
  -keyalg RSA -keysize 4096 -validity 10000
```

O `keytool` pede a senha da keystore, a da chave e o nome no certificado.

> **Guarde a keystore e as senhas num lugar seguro** (gerenciador de senhas,
> backup fora deste computador). Sem elas, a versão seguinte não instala por
> cima: é preciso desinstalar o app, e **desinstalar apaga os dados do
> celular** (o que ainda não tiver sincronizado se perde). Nunca coloque a
> keystore no repositório.

**2. Cadastrar no GitHub.** Em *Settings → Secrets and variables → Actions*:

| Nome | Tipo | Valor |
|---|---|---|
| `ANDROID_KEYSTORE_BASE64` | secret | a keystore em base64 (abaixo) |
| `ANDROID_KEY_ALIAS` | secret | `mytaskapp` (o `-alias` usado acima) |
| `ANDROID_KEYSTORE_PASSWORD` | secret | a senha da keystore |
| `ANDROID_KEY_PASSWORD` | secret | a senha da chave |

O servidor de sincronização **não** é cadastrado aqui: o app não leva servidor
(ADR-078). Uma variável `SYNC_API_BASE_URL` de versões antigas pode ser apagada
(`gh variable delete SYNC_API_BASE_URL`).

A keystore em base64 vai direto para a área de transferência:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$dir\mytaskapp.jks")) | Set-Clipboard
```

Ou, com o `gh`, sem passar pela tela (o `gh secret set` sem `--body` pede o
valor sem ecoá-lo):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$dir\mytaskapp.jks")) | gh secret set ANDROID_KEYSTORE_BASE64
gh secret set ANDROID_KEY_ALIAS --body mytaskapp
gh secret set ANDROID_KEYSTORE_PASSWORD
gh secret set ANDROID_KEY_PASSWORD
```

Sem esses secrets, o `release.yml` **para** antes do build, com a mensagem do
que falta. É de
propósito: um APK com a chave de debug não atualizaria o que já está no
celular.

**3. Instalar no celular** (Android 7.0 ou mais novo):

1. No celular, abra a release no GitHub e baixe o
   `MyTaskApp-<versão>-android.apk`.
2. Abra o arquivo. Na primeira vez, o Android pede para **permitir instalar
   apps desta fonte** (o navegador ou o gerenciador de arquivos): permita.
3. O Play Protect avisa que o app é desconhecido: *Mais detalhes → Instalar
   mesmo assim*.
4. **Atualizar:** baixe o APK da versão nova e instale por cima. Os dados
   ficam.

### iOS sem Mac: .ipa reassinado com um Apple ID gratuito

O `.ipa` sai do CI sem assinatura. O Sideloadly (ou o AltServer) o assina no
Windows com o seu Apple ID e o instala no iPhone pelo cabo.

**1. Gerar o .ipa.** Em *Actions → Mobile iOS (manual) → Run workflow*:

- `tag` vazio: o branch escolhido no disparo;
- `tag` preenchido (ex. `v1.18.0`): o código da tag, e o `.ipa` também é
  anexado ao rascunho daquela release.

Baixe `ios-ipa` em *Artifacts*, no fim da execução (ou o `.ipa` da release) e
extraia o `MyTaskApp-<versão>-ios-unsigned.ipa`. O servidor de sincronização
é configurado depois, no app.

**2. Preparar o Windows (uma vez):**

- **iTunes** e **iCloud** baixados do site da Apple, **não** da Microsoft
  Store: o Sideloadly usa os drivers e as bibliotecas da versão do site.
- **Sideloadly**, de sideloadly.io.

**3. Instalar:**

1. Ligue o iPhone no cabo e toque em *Confiar neste computador*.
2. Abra o Sideloadly, arraste o `.ipa`, escolha o iPhone e entre com o Apple
   ID. De preferência um **Apple ID secundário**, só para isso.
3. No iPhone:
   1. *Ajustes → Privacidade e Segurança → Modo de Desenvolvedor*: ligue. O
      iPhone reinicia e pede confirmação. (A opção só aparece depois que um
      app assinado assim é instalado.)
   2. *Ajustes → Geral → VPN e Gerenciamento de Dispositivos*: toque no perfil
      do seu Apple ID e em *Confiar*.
4. Abra o MyTaskApp.

**Limites do Apple ID gratuito:**

- O app **expira em 7 dias**. Reinstale pelo Sideloadly (os dados ficam,
  desde que seja o mesmo Apple ID e o mesmo identificador do app).
- No máximo **3 apps** assim por Apple ID ao mesmo tempo.
- Não há simulador do iOS no Windows: o teste é sempre no aparelho.
- **Alternativa que renova sozinha:** o AltServer para Windows instala o
  AltStore no iPhone, e o AltStore renova os apps com o PC e o iPhone **na
  mesma rede Wi-Fi** e o AltServer aberto. O AltStore pode instalar com outro
  identificador; nesse caso, é outro app para o iPhone e os dados não passam de
  um para o outro. Escolha um dos dois e fique nele.

**Quando o app fecha sozinho:**

- No Sideloadly, os **Device logs** mostram o log do iPhone ao vivo: deixe
  aberto, reproduza o problema e procure por `MyTaskApp`.
- O relatório do crash também fica no iPhone, em *Ajustes → Privacidade e
  Segurança → Análise e Melhorias → Dados de Análise* (um arquivo
  `MyTaskApp.Mobile-<data>.ips`), e dá para compartilhá-lo para o PC.

**O que conferir no aparelho** (é o que nunca foi validado):

- [ ] **Login com Google:** abre o navegador do sistema e **volta para o app**
  pelo esquema `io.github.adrianoguzzo.mytaskapp://oauth/callback`.
- [ ] **Keychain:** feche o app de vez (deslize na troca de apps) e reabra:
  ele continua conectado, sem pedir login. É o refresh guardado pelo
  `SecureStorage`.
- [ ] **SQLite com o interpretador:** criar, editar, filtrar, buscar e
  concluir tarefas sem erro; os dados continuam lá depois de reabrir.
- [ ] **Sincronização com o desktop:** uma tarefa criada no celular aparece no
  desktop e vice-versa; modo avião, alterar, voltar a rede e ver subir.
- [ ] **Teclado (ADR-075):**
  - o "OK" em cima do teclado fecha o teclado no título, na descrição e na
    busca;
  - arrastar a lista para baixo e tocar fora do campo também fecham.
- [ ] **Concluir:**
  - arrastar só revela o botão;
  - a barra "Desfazer" aparece acima das abas e desfaz;
  - "Tarefa salva" aparece ao salvar o formulário.
- [ ] **Temas:** trocar na aba Conta muda o app na hora, a barra de status e o
  teclado acompanham, e a escolha continua depois de fechar e reabrir.
- [ ] **Dispositivo (ADR-076, ADR-079):**
  - tocar no título e escolher um computador na folha;
  - fechar o app de vez e reabrir: ele continua no título;
  - concluir uma tarefa a leva para o Histórico.
- [ ] **Topo enxuto (ADR-079):**
  - a lupa abre a busca com o teclado, e tocar de novo fecha e limpa;
  - "Só as vencidas" na folha acende o ponto do funil;
  - "＋ Nova tarefa" abre o formulário;
  - a barra Desfazer não cobre a última tarefa.

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
  - filtra pelo dispositivo escolhido;
  - troca de servidor com alteração pendente: só a revogação vai ao antigo,
    nada vai ao novo antes do login, e a tarefa sobe depois de entrar.
- **Servidor configurável** (`SyncServerViewModelTests`, ADR-078): primeira
  abertura sem servidor, uso local completo, salvar e reabrir, endereço
  inválido e HTTP, trocar e tirar com e sem conta, cancelar, adotar o servidor
  de quem atualiza, teste de conexão.
- **CI:** não há emulador. A conferência do APK é uma só,
  `scripts/check-android-apk.sh`:
  - nos PRs, o job `mobile-android` do `ci.yml` compila o APK em Release e
    confere que ele leva o SQLite nativo;
  - em cada release, o job `android` do `release.yml` gera o APK assinado e
    confere também a assinatura (não pode ser a chave de debug) e o
    `versionCode` (o da tag) antes de anexá-lo.
- **iOS:** só no disparo manual do *Mobile iOS (manual)*, fora do CI dos PRs e
  do `release.yml`: o minuto de macOS custa 10x o de Linux.

## Limitações conhecidas

- **Sem sincronização em segundo plano;** sem push. Abrir o app, puxar para
  atualizar ou a rede voltar disparam um ciclo.
- **Sem lembretes nem notificações** no celular.
- **Fora do celular:** cronômetro, post-its e Jira ficam no desktop. Os dados
  deles sincronizam, mas não aparecem. Comandos e ambientes de desenvolvimento
  rodam nos computadores; o celular só os pede, pela aba Remoto.
- **Etiquetas:** não se criam nem se editam no celular.
- **Projeto** não existe no domínio.
- **Um servidor por vez.** Não há perfis de vários servidores; trocar esquece
  o estado de sincronização do anterior (ADR-078).
- **"Computador" no desktop:** o desktop ainda chama o celular de "computador"
  em "Enviar para…".
- **Histórico local:** o do detalhe é só deste celular; a auditoria não
  sincroniza.
- **Edição sem transação única.** Se um passo for recusado (por exemplo,
  concluir e mudar o prazo), o que veio antes já foi gravado. O formulário relê
  e mostra o estado real.
- **"Desfazer" não religa o cronômetro.** Concluir para o tempo que estava
  correndo; desfazer reabre a tarefa, mas não volta a contar.
- **Tema por aparelho:** a escolha não sincroniza com o desktop, que também
  guarda o tema por computador. "Automático" não liga o Alto contraste sozinho.

## Controle remoto (aba Remoto)

O celular é controlador da orquestração remota (fase 2, E7): pede a um
computador pareado que prepare uma tarefa, rode um comando, copie o banco ou
converse com o Claude Code, e aprova o que ele perguntar. Ele não roda nada:
quem executa é o computador, com as regras dele. O guia é o
[`remote.md`](remote.md).

- **Ligar** registra o celular no servidor só como controlador, com uma chave
  ES256 gerada aqui e guardada no Keystore/Keychain. Não é a sincronização:
  passa pelas rotas `/api/v1/remote`, com o escopo `remote`, e cada pedido é
  assinado.
- **A aba** mostra os computadores que aceitam controle, o código do
  pareamento (digitado lá, na janela Máquinas), as aprovações pendentes (lidas
  a cada 1,5 s com o app aberto; a nova avisa e conta no título da aba) e os
  pedidos feitos daqui.
- **A máquina** lista os worktrees de lá: rodar um comando liberado, abrir o
  Claude, apagar o worktree. **A operação** mostra o estado, a saída ao vivo,
  as perguntas dela, cancelar, mandar mais um prompt e parar o Claude. Na
  tarefa, **"Preparar em…"** escolhe o computador, o repositório, a cópia do
  banco e o Claude.
- **Destravar antes de assinar:** todo pedido e toda resposta pedem a
  biometria ou o bloqueio da tela (`BiometricPrompt` com o PIN no Android 10+,
  a tela de confirmação do bloqueio no 9 e anteriores, `LAContext` no iOS).
  Destravado, vale 30 s, e tranca ao ir para segundo plano. Sem bloqueio de
  tela, o celular não assina.
- **Sem push:** as aprovações só chegam com o app aberto. Uma pergunta sem
  resposta em 10 minutos é negada.
