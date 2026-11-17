# ADR-108 — Android recebe notificações pelo Firebase Cloud Messaging

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-11-15 |
| **Palavras-chave** | FCM, Firebase, FcmMessagingService, FcmAuthorization, notificações Android |
| **Relações** | revisa: [ADR-107](107-ciclo-de-vida-remoto-dos-terminais.md); relacionado: [ADR-096](096-verificacao-periodica-no-celular.md) |
| **Código** | `src/MyTaskApp.Mobile/Platforms/Android/FcmPush.cs`, `src/MyTaskApp.Server.Infrastructure/FcmAuthorization.cs`, `src/MyTaskApp.Server.Infrastructure/RemotePushWorker.cs` |

**Contexto:** o distribuidor UnifiedPush acrescentava instalação e configuração em cada celular. O usuário escolheu a experiência de instalar o MyTaskApp e permitir notificações, aceitando a dependência do Google.

**Decisão:** substituir UnifiedPush por FCM no Android. O SDK recebe e renova o registro do aparelho; o aplicativo o associa à conta autenticada. O servidor usa a API HTTP v1 com OAuth e uma conta de serviço, restrita ao escopo de envio FCM. A biblioteca oficial Google Auth renova o access token. APNs continua no iOS.

O servidor envia notificação com texto genérico e dados de navegação: o sistema mostra o aviso com o app em segundo plano; com o app na frente, o serviço do MyTaskApp apresenta o aviso local. O toque abre a tarefa após autenticação. Não são enviados prompts ou saída do terminal. Identificador do evento e etiqueta estável permitem deduplicação. Retentativas, revogação e outbox seguem o ADR-107.

A configuração Firebase do aplicativo entra na build por `FirebaseConfigFile`, fora do repositório. A credencial da conta de serviço fica exclusivamente no servidor. Builds sem configuração continuam compilando e informam que as notificações não estão disponíveis. O registro ocorre ao ativar o remoto/abrir sua aba, e é renovado ao reabrir o app. Não há distribuidor externo.

**Por quê:** reduz a configuração por aparelho e usa o mecanismo padrão dos Android com serviços Google. FCM é um produto sem cobrança por envio; hospedagem e outros produtos opcionais têm preços próprios.

## Alternativas

- **Manter UnifiedPush:** mantém a configuração adicional rejeitada pelo usuário.
- **Cloud Functions:** desnecessário, pois o servidor existente já envia os avisos.

## Consequências

- Android precisa dos serviços Google e da configuração Firebase na distribuição do aplicativo.
- Uma migration remove inscrições antigas do distribuidor, sem reinterpretá-las como tokens FCM. O app atualizado registra novamente o aparelho.
- `UNREGISTERED` remove inscrições FCM inválidas; falhas de projeto/credenciais não apagam os aparelhos.
- O aceite com celular físico depende do projeto Firebase e da conta de serviço da instalação. Esse aceite ainda está pendente.

## Fora de escopo, de propósito

- Trocar APNs por FCM no iPhone; Analytics, Firestore e Functions.
