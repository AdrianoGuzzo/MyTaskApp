# ADR-096 — Com o app fechado, o celular verifica o controle remoto de tempos em tempos e avisa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | verificação periódica, JobScheduler, RemoteCheckJob, notificação local, POST_NOTIFICATIONS, RemoteBackgroundCheck, push, FCM, APNs |
| **Relações** | exceção a: [ADR-067](067-celular-dispositivo-da-sincronizacao.md); relacionado: [ADR-094](094-controladores-ouvem-o-hub.md), [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md) |
| **Código** | `src/MyTaskApp.Mobile.Core/Remote/RemoteBackgroundCheck.cs`, `src/MyTaskApp.Mobile.Core/Ports/PlatformPorts.cs` (`ILocalNotifier`, `IBackgroundChecks`), `src/MyTaskApp.Mobile.Core/Composition/MobileLifecycle.cs` (`PrepareAsync`, `IsAwake`), `src/MyTaskApp.Mobile/Platforms/Android/RemoteBackground.cs` |

**Contexto:** com o app aberto, o celular vê a aprovação nova e a resposta do
Claude na hora, pelo hub (ADR-094). Com o app fechado, não vê nada: o ADR-067
deixou o celular só em primeiro plano, sem notificações. Quem manda um prompt
longo do celular e guarda o aparelho não sabe quando o Claude respondeu, e a
aprovação pedida nesse meio tempo vence em 10 minutos, negada.

Push de verdade (FCM no Android, APNs no iOS) avisaria na hora, mas exige uma
conta do Firebase e uma chave do serviço do Google no servidor do usuário, e,
no iOS, uma conta paga da Apple. O servidor é do usuário (ADR-078), e o
celular hoje não depende de nenhum serviço de terceiro.

**Decisão:**

1. **Verificação periódica do sistema, no Android.** Um `JobService`
   (`RemoteCheckJob`) agendado no `JobScheduler` a cada 15 minutos, o mínimo do
   Android, só com rede, e de volta depois de reiniciar (`SetPersisted`, com
   `RECEIVE_BOOT_COMPLETED`). O sistema decide a hora exata, e o Doze adia.
2. **Ligada só com o controle remoto ligado.** `RemoteBackgroundCheck.ApplyAsync`
   liga o agendamento com o remoto ligado e um servidor salvo, e desliga no
   contrário. Roda ao abrir o app e ao ligar o remoto na aba.
3. **Só lê.** Cada volta prepara o banco e o servidor salvo
   (`MobileLifecycle.PrepareAsync`), sem ligar a sincronização, e lê com os
   mesmos handlers da aba Remoto: as aprovações em aberto e as conversas.
   Responder pede o destravamento, que só existe com a tela.
4. **Avisa duas coisas, uma vez cada.** A aprovação nova, com o resumo, a
   máquina e o prazo. A conversa cuja versão mudou desde a última volta e
   que agora espera alguém: "O Claude respondeu e espera o próximo prompt." ou
   "O Claude parou com erro.". O que já foi avisado fica nas preferências: os
   ids das aprovações em aberto e a versão de cada conversa. Na primeira volta,
   a conversa que já tinha acabado não é novidade.
5. **Com a tela na frente, só anota.** O que a aba já mostra não vira aviso
   depois (`MobileLifecycle.IsAwake`).
6. **O resumo fica escondido na tela bloqueada.** O aviso é `VisibilityPrivate`
   e tem uma versão pública sem conteúdo: "Novidade no controle remoto". O
   toque abre a aba Remoto. A `MainActivity` é a tela de entrada, que qualquer
   app pode chamar, e só aceita essa rota no extra.
7. **A permissão é pedida com a tela, uma vez.** No Android 13+, a aba Remoto
   pede `POST_NOTIFICATIONS` ao abrir com o remoto pronto. Quem negou não é
   perguntado de novo.
8. **iOS: só com o app aberto.** A Mobile.Core fica com as portas que não fazem
   nada (`NoLocalNotifier`, `NoBackgroundChecks`). O `BGAppRefreshTask` do iOS
   roda quando o sistema quer, às vezes nunca, e não justifica o custo agora.

**Por quê:** é o que dá para fazer sem serviço de terceiro, sem chave nova no
servidor e sem mudar o fio. A verificação reaproveita os handlers da aba
Remoto, então o que ela avisa é o que a aba mostraria.

## Alternativas

- **FCM (Firebase Cloud Messaging).** Avisa na hora, mas o servidor passa a
  depender de uma conta do Google e guarda a chave dela; o aparelho precisa do
  Google Play Services. Fica para quando o usuário quiser essa dependência.
- **UnifiedPush ou ntfy.** Sem Google, mas pede um distribuidor instalado no
  celular ou mais um serviço ao lado do servidor.
- **Serviço em primeiro plano com o hub ligado.** Avisaria na hora, ao custo de
  uma notificação fixa e de bateria o tempo todo, e o Android 14 restringe esse
  tipo de serviço a usos declarados.
- **`WorkManager`.** Faz o mesmo que o `JobScheduler` com mais uma dependência
  do AndroidX; o mínimo do app (Android 7) já tem tudo no `JobScheduler`.
- **Aprovação com prazo maior.** O prazo de 10 minutos é uma barreira contra
  aprovação por cansaço (ADR-089), e não muda por causa do aviso.

## Consequências

- O Claude que terminou com o celular guardado aparece numa notificação em até
  uns 15 minutos, mais o que o Doze adiar.
- **A aprovação pode vencer antes do aviso.** Ela vale 10 minutos e a volta
  vem a cada 15 no melhor caso: parte das aprovações pedidas com o app fechado
  vence sem aviso, negada. O aviso de que o Claude parou (com a negação) chega
  na volta seguinte.
- O processo do app acorda a cada 15 minutos para quem liga o remoto. Para
  quem não liga, o agendamento nem existe.
- O resumo do pedido vai para a notificação, que fica no aparelho. Na tela
  bloqueada, ele não aparece.

## Fora de escopo, de propósito

- Push na hora (FCM, APNs, UnifiedPush): pede uma decisão sobre a dependência.
- Responder pela notificação: assinar pede o destravamento e a tela de
  confirmação (ADR-087, RS-13).
- Avisar no iOS com o app fechado.
- Sincronizar as tarefas em segundo plano: o ADR-067 segue valendo para a
  sincronização.
