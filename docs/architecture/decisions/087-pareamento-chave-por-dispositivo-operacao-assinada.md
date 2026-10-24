# ADR-087 — Confiança: pareamento na máquina executora, chave por dispositivo e operação assinada

| | |
|---|---|
| **Status** | Proposto |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | DeviceKeyStore, device-key, ES256, PairingCode, ConfirmPairingHandler, TrustedControllers, escopo remote, janela Máquinas |
| **Relações** | relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-069](069-rate-limit-por-conta.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Remote/DeviceKeyStore.cs`, `src/MyTaskApp.Application/Remote/RemotePairing.cs`, `src/MyTaskApp.Remote.Contracts/Pairings.cs`, `src/MyTaskApp.Desktop/Views/MachinesWindow.axaml` |

**Estado:** proposto (fase 2), em implementação: a chave por dispositivo, o escopo `remote`, o pareamento com código digitado no executor, os `TrustedControllers` e a janela "Máquinas" já existem (T-02 a T-06); a operação assinada ainda não.

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

## Alternativas

- **Confiar no servidor e no token** (o modelo do sync). Mais simples, mas um
  servidor comprometido vira execução remota em todas as máquinas.
- **mTLS por dispositivo.** Prende o canal, mas não o pedido: o servidor ainda
  repassaria qualquer coisa. E o proxy TLS (Caddy) teria de repassar o
  certificado do cliente.
- **Aprovar cada operação na máquina executora.** Seguro, mas inútil: não há
  ninguém na frente dela.
- **Pareamento por QR code na tela do executor.** Bom, e pode vir depois; o
  código numérico funciona também pelo CLI, numa máquina Linux sem tela.

## Consequências

- Parear exige estar uma vez na máquina executora.
- O celular precisa destravar (biometria/PIN do sistema) para assinar.
- O servidor confere a assinatura também, para recusar lixo cedo, mas a
  decisão é do agente.

## Fora de escopo, de propósito

- Cifra ponta a ponta dos eventos (o servidor ainda vê saídas e nomes): risco
  residual R-04 do modelo de ameaças.
