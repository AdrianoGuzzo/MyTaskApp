# Servidor de sincronização

O servidor da [sincronização](sync.md) é um ASP.NET Core com PostgreSQL:
contas pelo Google, OAuth do desktop pelo OpenIddict, e a API `/api/v1`. A
decisão está no [ADR-061](ARCHITECTURE.md), e a publicação em Production
atrás de TLS, no ADR-070.

Quem usa o app só local não precisa disto. Este guia é para quem publica o
servidor (o seu, para você e quem você autorizar), ou para quem desenvolve a
sincronização.

**Não existe servidor padrão** ([ADR-078](ARCHITECTURE.md)). Nenhuma build do
desktop ou do celular traz endereço embutido, e o projeto não mantém uma
instância compartilhada. Cada pessoa sobe a sua e aponta os apps para ela.

## Sua instância

**Requisitos mínimos:**

- um host com Docker (ou qualquer lugar que rode a imagem da API) e um
  PostgreSQL 17 (o compose de produção já traz os dois, §5);
- um nome de domínio e um certificado TLS válido, emitido por uma autoridade
  que o sistema do celular e do computador aceitem. Os apps validam o
  certificado e não aceitam autoassinado nem HTTP pela rede. O Caddy do
  compose obtém o certificado sozinho;
- um cliente OAuth no Google Cloud (§1) e a lista de quem pode entrar
  (§2, ADR-068);
- `Sync__Issuer` igual ao endereço público (§2). É esse endereço que vai nos
  apps;
- para o celular, o redirect `io.github.adrianoguzzo.mytaskapp://oauth/callback`
  em `Sync__MobileRedirectUris` (é o padrão; só mude se distribuir o app com
  outro esquema).

**Conectar os apps:**

| App | Onde vai o endereço |
|---|---|
| Desktop | `Sync:ApiBaseUrl` no `appsettings.user.json` (pasta de dados do usuário, veja o README). Reabra o app e conecte em *☰ → Sincronização…* |
| Celular | *Conta → Configurar servidor*: informe, teste, salve e entre ([mobile.md](mobile.md#servidor-de-sincronização)) |

**Testar a conectividade.** O que o "Testar conexão" do celular faz, à mão:

```bash
curl -fsS https://sync.seudominio.com/health                            # {"status":"ok"}
curl -fsS https://sync.seudominio.com/.well-known/openid-configuration  # o issuer e o authorization_endpoint
```

O `authorization_endpoint` do discovery deve estar no mesmo host que você vai
pôr nos apps. Se estiver em outro, o `Sync__Issuer` não é o endereço público.
Um `3xx` também é problema: os apps não seguem redirecionamento, para o token
não ir para outro endereço; use o endereço final.

**Trocar de servidor com segurança.** No celular, a tela do servidor pergunta
antes, sai da conta no servidor antigo, esquece o estado da sincronização dele
e não envia nada ao novo até você entrar nele. As tarefas ficam
([mobile.md](mobile.md#servidor-de-sincronização)). No desktop, troque o
`Sync:ApiBaseUrl` e reabra: o app percebe que a conta é de outro servidor, para
a sincronização com "O servidor mudou: entre de novo" e só volta depois de
conectar no novo, que é tratado como outra conta (ADR-078). Para sair da conta
no servidor antigo antes, desconecte em *☰ → Sincronização…* ainda apontado
para ele.

## Projetos

O servidor tem uma solution própria, `MyTaskApp.Server.slnx`, separada da do
desktop (`MyTaskApp.slnx`). Abra só ela para trabalhar no servidor:

```powershell
dotnet build MyTaskApp.Server.slnx
```

| Projeto | O que tem |
|---|---|
| `src/MyTaskApp.Sync.Contracts` | rotas, DTOs, registro dos tipos e campos, validação. Compartilhado com o desktop |
| `src/MyTaskApp.Server.Application` | a política de mescla, o push, o pull e os dispositivos, sem banco |
| `src/MyTaskApp.Server.Infrastructure` | `ServerDbContext` (Identity, OpenIddict, chaves do DataProtection, sincronização) e as migrations |
| `src/MyTaskApp.Server.Api` | o host: Google, OpenIddict, endpoints, rate limit, logs, `Dockerfile` |

A solution também traz Domain, Application e Infrastructure do desktop, numa
pasta `desktop`. Só os testes ponta a ponta usam esses projetos, para subir dois
desktops inteiros contra a API. O servidor não referencia nenhum deles.

## 1. Cliente OAuth no Google Cloud

Feito **uma vez**, por quem publica o servidor.

1. Em <https://console.cloud.google.com/>, crie (ou escolha) um projeto.
2. **APIs e serviços → Tela de consentimento OAuth**: tipo **Externo**, nome
   `MyTaskApp`, e-mail de suporte. Escopos: `openid`, `email` e `profile`, que
   não exigem verificação. Enquanto o app estiver em **Teste**, só os e-mails
   cadastrados em "Usuários de teste" entram. Para abrir a todos, publique o
   app.
3. **Credenciais → Criar credenciais → ID do cliente OAuth**, tipo **Aplicativo
   da Web**.
   - **Origens JavaScript autorizadas:** nenhuma.
   - **URIs de redirecionamento autorizados:** `https://<seu-host>/signin-google`.
     Para desenvolvimento, acrescente `http://localhost:5280/signin-google`.
4. Copie o **ID do cliente** e a **Chave secreta**. A chave secreta fica
   **só no servidor**. O desktop nunca a vê: ele é cliente público do
   *nosso* servidor, com PKCE, e não do Google.

O token do Google só abre a sessão no servidor. Ele nunca vira token da API.

## 2. Configuração

Toda configuração vem de `appsettings.json`, de variáveis de ambiente
(`Secao__Chave`), de arquivos em `/run/secrets` ou, em desenvolvimento, do
user-secrets. Em `/run/secrets`, cada arquivo é uma chave: o nome do arquivo é
o da variável, e o conteúdo é o valor. É o que o compose de produção usa (§5).
**Nenhum segredo vai para o repositório nem para a imagem.**

| Variável | Obrigatória | O que é |
|---|---|---|
| `ConnectionStrings__Sync` | sim | connection string do PostgreSQL |
| `Authentication__Google__ClientId` | sim | o ID do cliente do passo 1 |
| `Authentication__Google__ClientSecret` | sim | a chave secreta do passo 1 |
| `Sync__Registration__AllowedEmails__0` … | sim, esta ou a de baixo | e-mails das contas Google que podem usar o servidor (ADR-068). Sem diferença de maiúsculas, e só vale e-mail que o Google diz verificado |
| `Sync__Registration__AllowedSubjects__0` … | sim, esta ou a de cima | o `sub` do Google das contas que podem usar o servidor. Não muda quando o e-mail da conta muda |
| `Sync__SigningCertificate__Path` ou `__Base64` | em produção | PFX que assina os tokens do OpenIddict |
| `Sync__SigningCertificate__Password` | se o PFX tiver | senha do PFX |
| `Sync__EncryptionCertificate__Path` / `__Base64` / `__Password` | em produção | PFX que cifra os tokens |
| `Sync__DataProtectionCertificate__Path` / `__Base64` / `__Password` | em produção | PFX que cifra as chaves do Data Protection (cookies do login) no banco (SEC-011, ADR-073). Fora de Development, sem ele o servidor não sobe |
| `Sync__DataProtectionPreviousCertificates__0__Path` / `__Base64` / `__Password` … | não | PFX de antes da rotação: só decifram as chaves que eles cifraram |
| `Database__MigrateOnStartup` | não | `true` aplica as migrations ao subir. Padrão: só em Development |
| `ForwardedHeaders__KnownProxies__0` | atrás de proxy | IP do proxy reverso, para valer o `X-Forwarded-Proto` |
| `Sync__Issuer` | em produção | o endereço público do servidor, como `https://sync.exemplo.com/`: o issuer do OAuth, no discovery e nos tokens (SEC-007). Fora de Development, sem ele (ou sem `https`) o servidor não sobe. Vazio, o `Host` da requisição decide, e um token emitido por um endereço não vale no outro. Trocá-lo derruba o login de todos |
| `AllowedHosts` | em produção | os nomes aceitos no `Host`, separados por `;`, sem porta. Fora de Development, tem de listar o nome do `Sync__Issuer` e não pode ser `*`: o `Host` forjado leva 400. Em Development, `localhost;127.0.0.1;[::1]` |
| `Development__AllowNonLoopback` | não | `true` deixa Development escutar fora do loopback. Só no contêiner de desenvolvimento (§3) |
| `Sync__DesktopRedirectUris__0` … | não | voltas aceitas do desktop. Padrão: `http://127.0.0.1/callback`, que no loopback aceita qualquer porta (ADR-074) |
| `Sync__MobileRedirectUris__0` … | não | voltas aceitas do celular (cliente `mytaskapp-mobile`, ADR-067). Padrão: `io.github.adrianoguzzo.mytaskapp://oauth/callback` |
| `Sync__AccessTokenLifetime` | não | padrão `00:15:00` |
| `Sync__RefreshTokenLifetime` | não | padrão `30.00:00:00`, deslizante e rotativo |
| `Sync__RefreshTokenReuseLeeway` | não | padrão `00:00:30`: um refresh já trocado ainda vale, para a resposta perdida na rede |
| `Sync__DeviceCacheLifetime` | não | padrão `00:00:30` |
| `Sync__MaxActiveDevicesPerUser` | não | dispositivos não revogados por conta (SEC-015). Padrão 20; acima, o login de um dispositivo novo leva `device_limit_reached` e é preciso revogar um antigo; `0` desliga |
| `Sync__ProcessedOperationRetention` | não | padrão `90.00:00:00`, tempo de guarda dos resultados para o reenvio |
| `Sync__RateLimits__AuthPerMinute` | não | login e token, por IP. Padrão 20 |
| `Sync__RateLimits__SyncPerMinute` / `SyncBurst` | não | push, pull e fetch, por usuário (o `sub` do token). Padrão 60 por minuto, rajada de 20 |
| `Sync__RateLimits__ApiPerMinute` | não | o resto da API, por usuário (o `sub` do token). Padrão 120 |
| `Sync__RateLimits__RemotePerMinute` | não | a orquestração remota (`/api/v1/remote`), por dispositivo da conta, separada da cota da API. Padrão 300 (a saída ao vivo de um comando manda um lote a cada 500 ms) |
| `Sync__RateLimits__AnonymousPerMinute` | não | rotas da API sem token válido (o que vai dar 401), por IP. Padrão 30 |
| `Sync__RateLimits__GlobalPerMinute` | não | teto de proteção de qualquer rota, por IP, somado aos outros. Padrão 600; `0` desliga |
| `Sync__Pull__MaxEntities` | não | entidades por página do pull, contando o que vem de carona (ADR-071). Padrão 1000 |
| `Sync__Pull__MaxBytes` | não | bytes por página do pull, somando o JSON de cada mudança. Padrão 1048576 (1 MiB). Só um item maior que isso, sozinho, passa do teto |
| `Sync__Push__MaxEntityBytes` | não | o JSON de uma entidade, já mesclado, em bytes UTF-8 (ADR-072). Padrão 786432 (768 KiB); acima, `entity_too_large`; `0` desliga |
| `Sync__Push__MaxWorkspaceEntities` | não | entidades vivas por workspace (tombstone não conta). Padrão 100000; acima, `quota_exceeded`; `0` desliga |
| `Sync__Push__MaxWorkspaceBytes` | não | soma do JSON de todas as linhas do workspace. Padrão 268435456 (256 MiB); acima, `quota_exceeded`; `0` desliga |
| `Serilog__MinimumLevel__Default` | não | padrão `Information`. OpenIddict, ASP.NET e EF ficam em `Warning` |

Sem o cliente do Google, o servidor sobe, mas ninguém entra.

### Quem pode entrar

O servidor é fechado (ADR-068): só entra a conta Google que está em
`Sync__Registration__AllowedEmails__*` ou em `Sync__Registration__AllowedSubjects__*`.
Com as duas listas vazias, **ninguém entra**, e esquecer a variável no deploy não
abre o servidor.

- Fora da lista, o `/account/external-callback` responde 403 com o código
  `registration_closed` e não cria conta, workspace nem sessão. O log registra
  `SyncRegistrationRefused` com o `sub` da conta recusada, que é o valor para
  `AllowedSubjects` se ela tiver de entrar.
- O e-mail só conta se o Google disser que é verificado (`email_verified`).
  Para não depender do e-mail, use o `sub`.
- Tirar uma conta da lista bloqueia também quem já tem conta: o `/connect/authorize`
  responde `access_denied` e a renovação no `/connect/token`, `invalid_grant`. O
  access token em mãos vale até vencer (`Sync__AccessTokenLifetime`, 15 minutos).
  A lista é lida ao subir: mudou, reinicie. Nada é apagado, e a conta volta
  com tudo se entrar na lista de novo.

As voltas do desktop padrão continuam valendo mesmo com
`Sync__DesktopRedirectUris__*` na configuração: a lista configurada **soma** à
padrão. Os clientes são nativos: uma volta de loopback registrada **sem
porta** aceita a porta efêmera que o desktop abre a cada login (RFC 8252 §7.3),
e o resto do endereço continua exato (ADR-074). Atualize o servidor **antes**
do desktop. O desktop novo num servidor antigo, que só conhece a 47833, recebe
"redirect_uri inválido" no navegador. O desktop antigo, na 47833, entra no
servidor novo.

O `/connect/authorize` só emite o código sem perguntar logo depois do login
que o próprio pedido começou. Uma sessão do Identity que sobrou (10 minutos)
mostra antes uma tela "Conectar …?", com antiforgery, e por isso exige HTTPS
como o logout. No log: `SyncAuthorizeConsentShown`, `…Given` e `…Denied`.

O servidor semeia dois clientes públicos ao subir, os dois sem secret e com
PKCE obrigatório: `mytaskapp-desktop`, só com as voltas de loopback, e
`mytaskapp-mobile`, só com o esquema do app do celular (`Sync__MobileRedirectUris__*`,
que também soma à padrão). Um servidor de antes desta versão não conhece o
cliente do celular: o login do app responde `invalid_client` até o servidor
ser atualizado. Nada muda no cliente OAuth do Google — o celular entra pelo
mesmo `/account/login` do servidor.

### Certificados do OpenIddict

Em Development, o OpenIddict usa certificados de desenvolvimento gerados na
máquina (`CN = OpenIddict Server Signing Certificate`). Eles são recriados
quando somem ou vencem, e cada recriação derruba os refresh tokens: por isso
Development não serve a rede (ADR-070). Em produção, são obrigatórios dois PFX:
um para assinar e outro para cifrar. Para gerar certificados autoassinados,
que não precisam de uma CA, já na pasta de segredos do compose (§5):

```bash
cd deploy/sync-server/secrets

openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Signing" \
  -keyout signing.key -out signing.crt -addext "keyUsage=digitalSignature"
openssl rand -base64 24 | tr -d '\n' > signing_password
openssl pkcs12 -export -inkey signing.key -in signing.crt -out signing.pfx -passout file:signing_password

openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Encryption" \
  -keyout encryption.key -out encryption.crt -addext "keyUsage=keyEncipherment"
openssl rand -base64 24 | tr -d '\n' > encryption_password
openssl pkcs12 -export -inkey encryption.key -in encryption.crt -out encryption.pfx -passout file:encryption_password

rm signing.key signing.crt encryption.key encryption.crt
```

Fora do compose, o PFX também pode ir numa variável (`base64 -w0 signing.pfx`
→ `Sync__SigningCertificate__Base64`). Evite: a variável aparece para quem roda
`docker inspect` ou `docker service inspect` e fica no painel do orquestrador.
Prefira o arquivo em `/run/secrets` com `__Path` (no Dokploy, §5). Com os dois,
vale o `__Base64`.

Os tokens são assinados e cifrados com esses certificados. Trocar qualquer um
dos dois invalida os tokens em circulação, inclusive os refresh tokens, e cada
computador precisa conectar de novo. Guarde os PFX e as senhas junto com o
backup do banco, e fora do host.

#### Rotação dos PFX

Os certificados acima valem 730 dias. Para saber quando vencem:

```bash
cd deploy/sync-server/secrets
openssl pkcs12 -in signing.pfx -passin file:signing_password -nokeys | openssl x509 -noout -subject -enddate
openssl pkcs12 -in encryption.pfx -passin file:encryption_password -nokeys | openssl x509 -noout -subject -enddate
```

A troca é planejada (antes do vencimento) ou de emergência (um PFX ou uma senha
vazou). Nos dois casos, o roteiro é o mesmo:

1. Avise: depois da troca, cada computador e cada celular mostra "Entrar de
   novo com o Google" uma vez. Os dados ficam; o que estava pendente no
   aparelho sobe depois do login.
2. Troque **os dois** PFX juntos, para o login cair uma vez só. Guarde a dupla
   antiga fora do host até conferir a nova (passo 4), e depois apague.
3. Gere a dupla nova por cima da antiga, com os comandos acima e senhas novas,
   e recrie a API. O arquivo de segredo é montado ao criar o contêiner, por
   isso `restart` não basta:

   ```bash
   docker compose -f docker-compose.prod.yml --env-file prod.env up -d --force-recreate api
   ```

4. Confira que o JWKS publica o certificado novo (o `notBefore` é de agora):

   ```bash
   curl -s https://<SYNC_HOST>/.well-known/jwks | jq -r '.keys[].x5c[0]' \
     | base64 -d | openssl x509 -inform der -noout -subject -dates
   ```

Com os PFX novos, nenhum token antigo é aceito, nem um forjado com a chave que
vazou: na emergência, a troca já basta.

A troca não mexe nas chaves do Data Protection (abaixo) nem no certificado TLS
do Caddy (no volume `caddy-data`, renovado por ele).

### Certificado do Data Protection

As chaves do Data Protection protegem o cookie do login, o cookie do retorno
do Google e o token antiforgery. Elas ficam na tabela `DataProtectionKeys`,
para que várias instâncias possam rodar atrás de um balanceador. Sem
certificado, o XML de cada chave vai em claro para o banco, e quem lê a tabela
(ou um backup) forja o cookie do login de qualquer conta e, com ele, tira
tokens da API (SEC-011). Com o certificado, a chave vai cifrada (o XML leva um
`encryptedSecret`), e só quem tem o PFX a abre (ADR-073).

Em Production, o PFX é obrigatório; em Development e nos testes, opcional. Ele
é um terceiro PFX, separado dos dois do OpenIddict, e é gerado da mesma forma:

```bash
cd deploy/sync-server/secrets

openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Data Protection" \
  -keyout dataprotection.key -out dataprotection.crt -addext "keyUsage=keyEncipherment"
openssl rand -base64 24 | tr -d '\n' > dataprotection_password
openssl pkcs12 -export -inkey dataprotection.key -in dataprotection.crt -out dataprotection.pfx -passout file:dataprotection_password

rm dataprotection.key dataprotection.crt
```

Guarde o PFX e a senha com os outros, fora do host. **Não guarde junto do
backup do banco:** quem tem os dois volta a ter as chaves.

Para conferir, cada `key-…` tem de sair com `cifrada = t`:

```bash
docker compose -f docker-compose.prod.yml --env-file prod.env exec postgres \
  psql -U mytaskapp -d mytaskapp_sync \
  -c "SELECT \"FriendlyName\", \"Xml\" LIKE '%encryptedSecret%' AS cifrada FROM \"DataProtectionKeys\" ORDER BY \"Id\";"
```

As linhas `revocation-…` não têm segredo e não são cifradas.

#### As chaves gravadas antes do certificado

Ligar o certificado não cifra o que já está na tabela. A chave gravada em
claro continua legível no banco e nos backups, e seria a chave em uso por até
90 dias: quem já leu o banco continuaria forjando o cookie. Por isso, ao subir
com o certificado, a API **revoga sozinha** toda chave sem `encryptedSecret`
(log `SyncServerPlaintextKeysRevoked`, em Warning) e gera a próxima já cifrada.
A revogação fica na tabela. Na subida seguinte não há o que revogar, e nada
muda.

- Quem estava no meio de um login (o cookie dura 10 minutos) volta para o
  Google uma vez. Os tokens dos aparelhos não dependem dessas chaves, e
  ninguém precisa conectar de novo.
- Restaurar um backup de antes do certificado traz a chave em claro de volta.
  A primeira subida a revoga de novo.
- A chave revogada continua na tabela, em claro, mas não vale mais para nada.
  Para tirá-la do banco (e dos próximos backups), apague as linhas antigas
  com a API no ar, depois da revogação:

  ```sql
  DELETE FROM "DataProtectionKeys" WHERE "Xml" NOT LIKE '%encryptedSecret%' AND "FriendlyName" LIKE 'key-%';
  ```

  Os backups feitos antes continuam com ela: eles também merecem o cuidado de
  um segredo.

#### Rotação do certificado do Data Protection

O Data Protection gera uma chave nova a cada 90 dias, cifrada com o
certificado atual. A chave antiga continua no banco, cifrada com o certificado
que existia quando ela foi criada.

**Troca planejada** (antes do vencimento do PFX), sem que ninguém perceba:

1. Renomeie o par atual para `dataprotection_previous.pfx` e
   `dataprotection_previous_password`, e gere o novo com os comandos acima.
2. No `docker-compose.prod.yml`, monte o anterior e aponte para ele:

   ```yaml
   # em api.environment:
   Sync__DataProtectionPreviousCertificates__0__Path: /run/secrets/dataprotection_previous.pfx
   # em api.secrets:
   - source: dataprotection_previous_pfx
     target: dataprotection_previous.pfx
   - source: dataprotection_previous_password
     target: Sync__DataProtectionPreviousCertificates__0__Password
   # em secrets:
   dataprotection_previous_pfx:
     file: ./secrets/dataprotection_previous.pfx
   dataprotection_previous_password:
     file: ./secrets/dataprotection_previous_password
   ```

3. Recrie a API (`up -d --force-recreate api`). As chaves novas saem com o
   PFX novo, e o anterior só decifra as que já existiam.
4. A chave em uso pode ser ainda a do PFX anterior, por até 90 dias. Depois
   disso (90 dias mais uma margem), tire o anterior do compose, recrie a API e
   apague o par antigo.

**Troca de emergência** (o PFX ou a senha vazou junto com uma cópia do banco):
gere o novo, **não** configure o anterior e recrie a API. As chaves antigas
ficam ilegíveis: o Data Protection as deixa de lado e gera uma nova, cifrada
com o PFX novo. Um cookie forjado com uma chave antiga não abre mais.
O custo é o mesmo da revogação: quem estava no meio de um login volta para o
Google uma vez. Esse caminho também serve para a troca planejada, se esse
custo não importar.

## 3. Rodar localmente

Com o Docker Desktop:

```powershell
# A senha do banco, só desta máquina, no deploy/sync-server/.env (fora do Git)
$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
$senha = -join ($b | ForEach-Object { $_.ToString("x2") })
"POSTGRES_PASSWORD=$senha" | Set-Content -Encoding ascii deploy/sync-server/.env

docker compose -f deploy/sync-server/docker-compose.yml up -d postgres

cd src/MyTaskApp.Server.Api
dotnet user-secrets set "ConnectionStrings:Sync" "Host=localhost;Port=5432;Database=mytaskapp_sync;Username=mytaskapp;Password=$senha"
dotnet user-secrets set "Authentication:Google:ClientId" "<id>"
dotnet user-secrets set "Authentication:Google:ClientSecret" "<segredo>"
dotnet user-secrets set "Sync:Registration:AllowedEmails:0" "<seu e-mail do Google>"
cd ../..

dotnet run --project src/MyTaskApp.Server.Api --launch-profile http
```

O servidor sobe em `http://localhost:5280`, aplica as migrations sozinho (em
Development) e responde `GET /health`.

**Nenhuma senha do banco fica no repositório** (SEC-012). O compose lê a
`POSTGRES_PASSWORD` do `deploy/sync-server/.env`, que o Git ignora (modelo em
`deploy/sync-server/.env.example`). Sem ela, ele não sobe e diz por quê:
`required variable POSTGRES_PASSWORD is missing a value: defina
POSTGRES_PASSWORD…`. O `dotnet run` lê a connection string do user-secrets
(`UserSecretsId` `mytaskapp-sync-server`, fora do repositório, em
`%APPDATA%\Microsoft\UserSecrets`); sem ela, o servidor não sobe e registra
`Falta ConnectionStrings:Sync`. Para ver ou trocar: `dotnet user-secrets list`
e `dotnet user-secrets set "ConnectionStrings:Sync" "…"`, dentro de
`src/MyTaskApp.Server.Api`.

O PostgreSQL só usa a `POSTGRES_PASSWORD` ao criar o volume `sync-data`. Um
volume criado antes continua com a senha antiga, a que estava versionada, que
deve ser tratada como pública. Troque-a no banco e use a nova no `.env` e no
user-secrets:

```powershell
docker compose -f deploy/sync-server/docker-compose.yml exec postgres `
  psql -U mytaskapp -d mytaskapp_sync -c "ALTER ROLE mytaskapp PASSWORD '<a do .env>'"
```

Ou recrie o volume, apagando os dados: `docker compose -f
deploy/sync-server/docker-compose.yml down -v`.

**Development só escuta no loopback** (ADR-070). Ele anda em HTTP, com os
certificados de desenvolvimento, e não serve a rede. Com `--urls
http://0.0.0.0:5280`, um IP da LAN ou `ASPNETCORE_URLS=http://+:…`, o servidor
registra `SyncServerDevelopmentOnNetwork` e não sobe. Para outra máquina usar
o servidor, publique em Production (§5). O `Host` também só aceita os nomes do
loopback (`AllowedHosts` do `appsettings.Development.json`); o celular chega
como `localhost` pelo `adb reverse` (docs/mobile.md).

Para rodar tudo em contêiner, com a API inclusive (a connection string da API
também é montada a partir da `POSTGRES_PASSWORD` do `.env`):

```powershell
$env:GOOGLE_CLIENT_ID = "<id>"; $env:GOOGLE_CLIENT_SECRET = "<segredo>"; $env:SYNC_ALLOWED_EMAIL = "<seu e-mail do Google>"
docker compose -f deploy/sync-server/docker-compose.yml --profile api up -d --build
```

Nesse caso a API fica em `http://localhost:8080`, e o redirect do Google é
`http://localhost:8080/signin-google`. Dentro do contêiner, a API escuta em
todas as interfaces dele, mas a porta só é publicada no `127.0.0.1` da
máquina. É a única exceção à regra do loopback, e por isso o compose de
desenvolvimento liga `Development__AllowNonLoopback`. Não mude o `ports` dele
para `0.0.0.0`.

### Apontar o desktop para o servidor local

O endereço do servidor vem da seção `Sync` do `appsettings.user.json`; nenhuma
build traz um (ADR-078). Em qualquer endereço de rede o app exige HTTPS. HTTP
só vale no loopback e só no desenvolvimento: a build Debug aceita sozinha; uma
build Release só com `"AllowHttpLoopback": true` explícito.

Para simular dois "computadores" na mesma máquina, use duas pastas de dados.
Cada uma tem seu banco, seu cofre e seu `device.json`, e portanto um DeviceId
próprio:

```powershell
$a = "$env:TEMP\mytaskapp-a"; $b = "$env:TEMP\mytaskapp-b"
foreach ($dir in $a, $b) {
  New-Item -ItemType Directory -Force $dir | Out-Null
  '{ "Sync": { "ApiBaseUrl": "http://localhost:5280/", "AllowHttpLoopback": true } }' | Set-Content -Encoding utf8 "$dir\appsettings.user.json"
}

$env:MYTASKAPP_DATA_DIR = $a; dotnet run --project src/MyTaskApp.Desktop
```

O app tem instância única por sessão do Windows, e o app instalado também
conta. Então os dois não abrem juntos: conecte e mexa no A, feche, e abra o B
com `$env:MYTASKAPP_DATA_DIR = $b`. Cada um sincroniza ao abrir.

## 4. Migrations

As migrations do servidor são do `ServerDbContext`, separadas das do desktop:

```powershell
# Nova migration (o factory de design-time não conecta em banco nenhum)
dotnet ef migrations add <Nome> -p src/MyTaskApp.Server.Infrastructure -s src/MyTaskApp.Server.Infrastructure -o Persistence/Migrations

# Aplicar no banco local
dotnet ef database update -p src/MyTaskApp.Server.Infrastructure -s src/MyTaskApp.Server.Infrastructure `
  --connection "Host=localhost;Database=mytaskapp_sync;Username=mytaskapp;Password=<a do .env>"
```

Regras:

- **Só aditivas.** Coluna nova é anulável ou tem padrão. Nada é renomeado nem
  removido na mesma versão em que deixa de ser usado. Um servidor novo convive
  com desktops velhos.
- Em produção, `Database__MigrateOnStartup` fica `false`. A imagem traz o
  bundle `efbundle`, que roda **antes** de subir a versão nova:

  ```bash
  docker run --rm --entrypoint /app/efbundle mytaskapp-sync \
    --connection "$ConnectionStrings__Sync"
  ```

  No compose de produção, é o serviço `migrate` (§5), que lê a connection
  string do segredo.

## 5. Implantação

O servidor vai para a rede só em **Production, atrás de um proxy com TLS**
(ADR-070). A imagem escuta em `8080`, sem TLS, e roda como usuário sem
privilégio. Em produção, o servidor:

- redireciona HTTP para HTTPS e manda HSTS de 1 ano;
- recusa OAuth sem TLS (o OpenIddict exige);
- marca cookies como `Secure`, `HttpOnly` e `SameSite=Lax` (Strict no antiforgery);
  por isso, sem HTTPS (inclusive em Development), o logout responde 400 dizendo
  que exige HTTPS;
- cifra no banco as chaves que protegem esses cookies (o PFX do Data
  Protection, §2), e não sobe sem ele;
- só volta do login para um caminho do próprio servidor: `returnUrl` externo,
  com `//`, `/\`, espaço ou caractere de controle vira `/`;
- limita o corpo a 1 MB e o push a 100 operações;
- limita a taxa de requisições (veja abaixo);
- manda `Cache-Control: no-store` em `/api/v1`, `/connect` e `/account`, e
  `X-Content-Type-Options: nosniff` em tudo, sem o header `Server`. A página
  do logout tem CSP e `X-Frame-Options: DENY`. O discovery e o JWKS
  continuam cacheáveis;
- não grava token, código, cabeçalho nem corpo no log. É uma linha por
  requisição: método, caminho sem query, status e tempo. O caminho vai
  reescapado (`%0D%0A`, `%1B`), então a requisição não forja linha nem manda
  sequência ANSI para o terminal (SEC-019).

O `deploy/sync-server/docker-compose.prod.yml` monta isso num host com Docker:

| Serviço | O que faz |
|---|---|
| `proxy` | Caddy. É o único com porta publicada (80 e 443): termina o TLS e responde HTTP com 308 para HTTPS |
| `api` | a imagem, em Production, sem porta publicada. Disco só de leitura e sem capabilities. Só aceita o `X-Forwarded-Proto` do IP fixo do Caddy (`ForwardedHeaders__KnownProxies__0`) e só o nome público no `Host` (`AllowedHosts`). O issuer é fixo: `Sync__Issuer=https://<SYNC_HOST>/` |
| `postgres` | numa rede interna: sem porta, sem saída, e só a API e o `migrate` o alcançam |
| `migrate` | o bundle das migrations (§4), rodado sob demanda |

Em outro provedor (um balanceador com TLS, nginx), vale o mesmo: Production,
os três PFX, o `KnownProxies` com o IP de quem fala com a API e nenhuma porta
da API exposta além do proxy.

### Passo a passo

1. **Nome e certificado TLS.** O Google só aceita redirect em HTTPS com um
   nome de domínio público: IP puro não serve, nem nome sem TLD público (`.lan`,
   `.local`). Então `SYNC_HOST` é um nome como `sync.exemplo.com`. Ele pode
   apontar para um IP da LAN. O certificado sai de um destes:
   - `SYNC_TLS=internal`: o Caddy emite com uma CA própria. Serve para a LAN
     com o desktop, que passa a confiar nessa CA (passo 7). O celular só
     confia nas CAs do sistema, e com ela não conecta.
   - `SYNC_TLS=<seu e-mail>`: Let's Encrypt. Exige as portas 80 e 443
     alcançáveis da internet nesse nome. Serve para o desktop e para o celular.
   - `SYNC_TLS=/etc/caddy/tls/cert.pem /etc/caddy/tls/key.pem`: um certificado
     seu (de uma CA pública, por exemplo emitido por DNS-01 em outra máquina),
     copiado para `deploy/sync-server/tls/`.
2. **Cliente do Google.** No passo 1 deste guia, o redirect é
   `https://<SYNC_HOST>/signin-google`.
3. **Configuração.** No host, com o repositório clonado:

   ```bash
   cd deploy/sync-server
   cp prod.env.example prod.env    # SYNC_HOST, SYNC_TLS, GOOGLE_CLIENT_ID e SYNC_ALLOWED_EMAIL
   ```

   O `SYNC_ALLOWED_EMAIL` é a conta que pode entrar ("Quem pode entrar", §2).
   Sem ele, o compose não sobe.

   O issuer é `https://<SYNC_HOST>/`. Com `SYNC_HTTPS_PORT` diferente de 443,
   defina `SYNC_ISSUER` com a porta (`https://sync.exemplo.com:8443/`): ele tem
   de ser o endereço que os clientes usam.

   Se `172.30.80.0/24` colidir com outra rede do host, troque
   `SYNC_EDGE_SUBNET` e `SYNC_PROXY_IP` juntos.
4. **Segredos.** Um arquivo por segredo em `deploy/sync-server/secrets/` (fora
   do Git), sem quebra de linha no fim:

   | Arquivo | Conteúdo | Na API vira |
   |---|---|---|
   | `postgres_password` | a senha do `mytaskapp`, o superusuário do bootstrap. A API não o usa | — (é o `POSTGRES_PASSWORD_FILE` do PostgreSQL) |
   | `postgres_app_password` | a senha do `mytaskapp_app`, o papel da API | — (o `initdb/` cria o papel com ela) |
   | `connection_string` | `Host=postgres;Database=mytaskapp_sync;Username=mytaskapp_app;Password=<a do postgres_app_password>` | `ConnectionStrings__Sync` |
   | `google_client_secret` | a chave secreta do cliente do Google | `Authentication__Google__ClientSecret` |
   | `signing.pfx`, `signing_password` | o PFX de assinatura e a senha dele (§2) | `Sync__SigningCertificate__Path` e `__Password` |
   | `encryption.pfx`, `encryption_password` | o PFX de cifragem e a senha dele (§2) | `Sync__EncryptionCertificate__Path` e `__Password` |
   | `dataprotection.pfx`, `dataprotection_password` | o PFX do Data Protection e a senha dele (§2) | `Sync__DataProtectionCertificate__Path` e `__Password` |

   ```bash
   mkdir -p secrets && cd secrets
   openssl rand -hex 24 | tr -d '\n' > postgres_password
   openssl rand -hex 24 | tr -d '\n' > postgres_app_password
   printf 'Host=postgres;Database=mytaskapp_sync;Username=mytaskapp_app;Password=%s' "$(cat postgres_app_password)" > connection_string
   printf '%s' '<chave secreta do Google>' > google_client_secret
   # os três PFX: os comandos de "Certificados do OpenIddict" e de "Certificado do Data Protection" (§2)
   cd .. && chmod 700 secrets && chmod 444 secrets/*
   ```

   Sem Swarm, o compose monta o arquivo como ele está no host. A API roda com
   o UID 1654 e o PostgreSQL com o 70, e os dois precisam ler o arquivo. O
   `700` na pasta fecha os segredos para os outros usuários do host, e o `444`
   deixa os contêineres lerem. Num editor do Windows, cuidado com o CRLF no
   fim: ele vira parte da senha.

   A API e as migrations entram no banco como `mytaskapp_app`: sem
   superusuário e dono do banco e do schema `public` (SEC-020). Na primeira
   subida, com o volume vazio, o `initdb/10-mytaskapp-app.sql` cria esse papel.
   O `mytaskapp` é o superusuário do bootstrap, e o PostgreSQL não deixa tirar
   isso dele. Ele fica para o healthcheck e a manutenção, só na rede interna.
5. **Subir.** Imagem, migrations e serviços:

   ```bash
   docker compose -f docker-compose.prod.yml --env-file prod.env build
   docker compose -f docker-compose.prod.yml --env-file prod.env run --rm migrate
   docker compose -f docker-compose.prod.yml --env-file prod.env up -d
   ```

6. **Conferir.** Estes são os critérios do G-01 da auditoria de segurança:

   ```bash
   H=https://<SYNC_HOST>
   curl -s -D - -o /dev/null http://<SYNC_HOST>/health  # 308, Location: https://…
   curl -s -D - -o /dev/null $H/health                  # 200 e strict-transport-security: max-age=31536000
   curl -s $H/.well-known/openid-configuration | jq -r .issuer   # https://<SYNC_HOST>/
   curl -s $H/.well-known/jwks | jq -r '.keys[].x5c[0]' | base64 -d \
     | openssl x509 -inform der -noout -subject          # CN = MyTaskApp Sync Signing, nunca "OpenIddict Server Signing Certificate"

   # HTTP direto na API, por dentro da rede do compose (mesmo com X-Forwarded-Proto forjado):
   docker run --rm --network mytaskapp-sync_edge curlimages/curl:8.11.1 -s \
     -H "Host: <SYNC_HOST>" -H "X-Forwarded-Proto: https" \
     -X POST http://api:8080/connect/token -d grant_type=refresh_token
   # 400 invalid_request, "This server only accepts HTTPS requests." (ID2083)

   # Host forjado direto na API (G-07): 400, e o issuer nunca muda
   docker run --rm --network mytaskapp-sync_edge curlimages/curl:8.11.1 -s -o /dev/null -w '%{http_code}\n' \
     -H "Host: evil.example" http://api:8080/.well-known/openid-configuration

   # A API sem superusuário (G-20): pela senha dela, por TCP, como a API conecta
   docker run --rm --network mytaskapp-sync_db -v "$PWD/secrets/postgres_app_password:/pw:ro" \
     postgres:17-alpine sh -c 'PGPASSWORD="$(cat /pw)" psql -h postgres -U mytaskapp_app -d mytaskapp_sync \
     -tAc "SELECT rolsuper FROM pg_roles WHERE rolname = current_user"'   # f
   # e a sessão aberta pela própria API: mytaskapp_app
   docker compose -f docker-compose.prod.yml --env-file prod.env exec postgres psql -U mytaskapp -d mytaskapp_sync \
     -tAc "SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = 'mytaskapp_sync' AND usename <> 'mytaskapp'"
   ```

   Com `SYNC_TLS=internal`, passe `--cacert caddy-root.crt` ao `curl` (passo 7).
   O issuer é fixo e não denuncia o proxy. Se faltar o HSTS, ou se o
   `/connect/token` pelo proxy responder ID2083, o `KnownProxies` não bate com o
   IP do Caddy: a API não acredita no `X-Forwarded-Proto` e recusa o OAuth
   também pelo proxy.
7. **Clientes.** O desktop de release recebe `https://<SYNC_HOST>/` (abaixo),
   ou a seção `Sync` do `appsettings.user.json`. Com `SYNC_TLS=internal`, cada
   computador precisa confiar na CA do Caddy:

   ```bash
   docker compose -f docker-compose.prod.yml --env-file prod.env cp \
     proxy:/data/caddy/pki/authorities/local/root.crt caddy-root.crt
   ```

   ```powershell
   Import-Certificate -FilePath caddy-root.crt -CertStoreLocation Cert:\CurrentUser\Root
   ```

   A CA fica no volume `caddy-data`. Perdido o volume, a CA é outra, e cada
   computador precisa importá-la de novo.

**Ordem de uma atualização:** `git pull` e `build`, depois as migrations
(`run --rm migrate`), depois o servidor (`up -d`), depois os desktops. O
servidor aceita clientes do `SyncSchema.MinimumClient` para cima, e abaixo
disso responde 426 ("Atualize o MyTaskApp").

Para trocar os PFX, veja "Rotação dos PFX" e "Rotação do certificado do Data
Protection" (§2).

**Vindo de uma versão sem o certificado do Data Protection** (antes do
ADR-073): gere o `dataprotection.pfx` e a senha (§2) **antes** de subir a
versão nova, que em Production não sobe sem eles. Na primeira subida, a chave
gravada em claro é revogada ("As chaves gravadas antes do certificado", §2).

**Vindo de uma versão com a API como superusuário** (antes do SEC-020): o
`initdb/` só roda com o volume vazio, então o papel sai à mão. O script pode
rodar mais de uma vez, e passa para o `mytaskapp_app` as tabelas que as
migrations criaram com o `mytaskapp`.

```bash
git pull
openssl rand -hex 24 | tr -d '\n' > secrets/postgres_app_password && chmod 444 secrets/postgres_app_password
# recria o postgres com o segredo novo montado; o volume fica
docker compose -f docker-compose.prod.yml --env-file prod.env up -d postgres
docker compose -f docker-compose.prod.yml --env-file prod.env exec -T postgres \
  psql -U mytaskapp -d mytaskapp_sync < initdb/10-mytaskapp-app.sql
chmod 644 secrets/connection_string
printf 'Host=postgres;Database=mytaskapp_sync;Username=mytaskapp_app;Password=%s' \
  "$(cat secrets/postgres_app_password)" > secrets/connection_string
chmod 444 secrets/connection_string
# o compose não percebe a mudança no conteúdo do segredo: o --force-recreate a aplica
docker compose -f docker-compose.prod.yml --env-file prod.env up -d --force-recreate api
```

Depois, confira com os comandos do G-20 no passo 6. A partir daí, as
migrations (`run --rm migrate`) também rodam com o `mytaskapp_app`.

**Vindo de uma versão sem lista de cadastro** (antes do ADR-068): configure
`Sync__Registration__*` **antes** de subir a versão nova. Sem a lista, quem já
estava conectado perde a renovação do token em até 15 minutos, e conectar de
novo é recusado com `registration_closed`.

### No Dokploy

O Dokploy (v0.25) publica a API como serviço do Swarm. A cada deploy, ele
reescreve o spec inteiro do serviço a partir do painel: env, mounts, portas e
rede. Um secret do Swarm posto à mão (`docker service update --secret-add`)
some no deploy seguinte, e a API sobe sem os PFX e não arranca. Por isso os
segredos ficam numa pasta do host, montada em `/run/secrets` por um **Bind
Mount** do painel, com o mesmo layout do compose (§5, passo 4):

| Arquivo em `/srv/mytaskapp-sync/secrets` | Na API vira |
|---|---|
| `ConnectionStrings__Sync` | a connection string |
| `Authentication__Google__ClientSecret` | a chave secreta do Google |
| `signing.pfx`, `Sync__SigningCertificate__Password` | o PFX de assinatura e a senha |
| `encryption.pfx`, `Sync__EncryptionCertificate__Password` | o PFX de cifragem e a senha |
| `dataprotection.pfx`, `Sync__DataProtectionCertificate__Password` | o PFX do Data Protection e a senha |

- A pasta é `root:1654` com `0750`, e os arquivos, `root:1654` com `0440`. A API
  (UID e GID 1654) lê os arquivos, mas não consegue alterá-los.
- No painel: **Advanced → Volumes → Bind Mount**, host
  `/srv/mytaskapp-sync/secrets`, destino `/run/secrets`. Em **Environment**, só o
  que não é segredo, mais os caminhos:
  `Sync__SigningCertificate__Path=/run/secrets/signing.pfx`,
  `Sync__EncryptionCertificate__Path=/run/secrets/encryption.pfx` e
  `Sync__DataProtectionCertificate__Path=/run/secrets/dataprotection.pfx`.
  Nenhum `__Base64`, `__Password`, `ClientSecret` ou `ConnectionStrings__Sync` no
  env.
- O arquivo é lido ao subir. Mudou um segredo? Faça um Deploy (ou
  `docker service update --force`).
- Ao tirar um segredo do env, o `docker service inspect` ainda o mostra no
  `PreviousSpec`. Um segundo Deploy o limpa. Os contêineres parados das tasks
  antigas também guardam o env (`docker inspect`); apague-os:
  `docker ps -a -f name=<serviço> -f status=exited -q | xargs -r docker rm`.
- Para conferir: `docker service inspect <serviço> | grep -cE
  'Base64|Password|ClientSecret|ConnectionStrings'` dá `0`.

### Rate limit

Os limites contam por minuto e respondem 429 com `Retry-After` e
`"code": "rate_limited"`. São dois estágios (ADR-069):

- **Borda, por IP, antes de qualquer autenticação:** o teto global
  (`GlobalPerMinute`, em toda rota, inclusive `/health` e `/connect/revoke`) e
  o login (`AuthPerMinute`, em `/account/*`, `/connect/authorize` e
  `/connect/token`). Fica antes porque o OpenIddict responde os erros de
  `/connect/*` dentro da autenticação, e eles também contam.
- **API, depois de validar o Bearer:** com token válido, a cota é da conta (o
  `sub`), e não do IP: duas contas atrás do mesmo NAT não dividem a cota. Sem
  token, ou com token inválido, vencido ou revogado, a requisição cai no balde
  anônimo do IP (`AnonymousPerMinute`) e não gasta a cota de nenhuma conta.

**Atrás de proxy, o `ForwardedHeaders__KnownProxies__0` é obrigatório.** Sem
ele, todo cliente aparece com o IP do proxy, e os limites por IP (global, login
e anônimo) viram um só para o servidor inteiro. Os limites por conta continuam
valendo.

Os contadores ficam na memória de cada instância: com duas réplicas, cada uma
conta o seu.

### Tetos de armazenamento

O push só guarda o que está no contrato (ADR-072). Campo fora dele recusa a
operação com `unknown_field`: **campo novo no contrato pede o servidor
atualizado antes dos desktops e do celular**. Uma entidade que, já mesclada,
passa de `Sync__Push__MaxEntityBytes` é recusada com `entity_too_large`. O
workspace tem cota de entidades vivas e de bytes (`quota_exceeded`). Mesmo acima
da cota, excluir, encolher e editar sem crescer continuam passando. Nos três
casos, a operação aparece como falha na janela da sincronização, com a mensagem
do servidor.

Para ver o uso de um workspace:

```sql
SELECT count(*) FILTER (WHERE NOT "IsDeleted") AS vivas, sum("DataBytes") AS bytes
FROM "SyncEntities" WHERE "WorkspaceId" = '…';
```

### Distribuir os apps

Os instaladores e o APK não levam endereço de servidor (ADR-078): o mesmo
build serve para qualquer pessoa, e distribuir o app não depende da
infraestrutura de ninguém. Quem instala aponta para o próprio servidor
([Sua instância](#sua-instância)). Sem servidor, a janela da sincronização diz
"Sem servidor configurado", e o resto funciona igual.

### Backup e restauração

- Faça backup do banco inteiro (`pg_dump -Fc`). Ele tem contas, autorizações,
  chaves do Data Protection (cifradas com o PFX dele) e os dados
  sincronizados. Guarde os PFX à parte: o backup sozinho não abre as chaves.
- Restaurado sem o mesmo PFX do Data Protection, as chaves do backup ficam
  ilegíveis e o servidor gera outra; só quem estava no meio de um login sente.
- Depois de restaurar um backup antigo, o cursor dos desktops pode estar **à
  frente** do servidor. O servidor responde 410, e o desktop refaz o pull desde
  o zero, sem perder o que tem localmente. O que foi criado nos desktops depois
  do backup sobe de novo pelo outbox, ou pela varredura da primeira conexão.
- O push só aceita uma versão base que o servidor deu (SEC-017). Editar no
  desktop algo que o backup levou para uma versão anterior aparece uma vez em
  "falhas", com `invalid_base_version`. O desktop já adota a versão do
  servidor, e o "Tentar de novo" reenvia a edição.
- Restaurar também traz de volta os refresh tokens daquele momento. Um token
  rodado depois do backup não vale mais, e o computador precisa conectar de
  novo.

## 6. Testes

```powershell
dotnet test MyTaskApp.Server.slnx                        # tudo do servidor
dotnet test tests/MyTaskApp.Server.Application.Tests     # sem banco
dotnet test tests/MyTaskApp.Server.IntegrationTests      # PostgreSQL real
```

Os testes de integração sobem um `postgres:17-alpine` pelo Testcontainers, com
o Docker Desktop ligado. Sem Docker, use um PostgreSQL próprio:

```powershell
$env:MYTASKAPP_TEST_SERVER_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=…;Database=postgres"
```

O usuário precisa criar bancos: cada teste cria o seu. Sem Docker e sem a
variável, os testes **pulam** (é o caso do CI do Windows). No CI, o job
`server-integration` roda no ubuntu, com Docker.

O `ApiFactory` põe na lista de cadastro os sujeitos `ana`, `bia` e `google-1`. O
teste que configura `Sync:Registration:*` troca a lista inteira, e o
`RegistrationTests` cobre a recusa.

Além da API, a suíte tem o ponta a ponta `TwoDeviceSyncTests`: dois desktops
inteiros sobre SQLite, com login pelo navegador simulado e volta pela porta de
loopback de verdade.
