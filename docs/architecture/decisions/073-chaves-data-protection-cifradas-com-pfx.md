# ADR-073 — As chaves do Data Protection cifradas no banco, com um PFX fora dele

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-18, commit `d8cbc04` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Data Protection, ProtectKeysWithCertificate, PlaintextKeyRevoker, DataProtectionCertificate, DataProtectionKeys, rotação de certificado, SEC-011 |
| **Relações** | relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-070](070-servidor-sync-production-tls.md) |
| **Código** | `src/MyTaskApp.Server.Api/ServerSetup.cs`, `src/MyTaskApp.Server.Api/ServerSupport.cs`, `src/MyTaskApp.Server.Api/SyncServerOptions.cs` |

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

## Alternativas

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

## Consequências

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
