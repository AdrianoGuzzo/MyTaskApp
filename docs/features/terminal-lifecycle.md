# Acompanhar terminais de outras máquinas

Os terminais de Claude e Codex vinculados às tarefas publicam seu estado quando a máquina aceita controle remoto. A task mostra o provedor, a máquina e se está trabalhando, esperando você, pronta para revisão, encerrada ou com erro. Máquina desconectada significa ausência de atualização; não significa que o processo terminou.

A resposta pronta e o encerramento do processo são eventos diferentes. Nenhum deles conclui automaticamente a tarefa. A lista conserva o encerramento por 24 horas; o detalhe conserva o histórico disponível por trinta dias. A entrada remota continua exigindo a autorização de terminal remoto. O estado pode ser compartilhado sem liberar o teclado ou o conteúdo do terminal.

As transições ficam numa fila local e são reenviadas com o mesmo identificador após reconectar. O servidor retém o histórico em PostgreSQL, inclusive depois de reiniciar. O MCP oferece `terminal_sessions_list` e `terminal_session_events`; o segundo pagina até cem eventos por sequência.

## Avisos imediatos

Na aba Remoto do celular, use **Ativar notificações**. O app avisa quando há pedido de atenção, resposta pronta, falha ou encerramento. O toque abre a task após verificar a conta. Os avisos não incluem prompts nem saída do terminal.

- **Android:** o MyTaskApp usa Firebase Cloud Messaging (FCM). Instale o app, entre na conta, habilite o remoto e permita notificações; não há aplicativo auxiliar. O registro é automático ao abrir a aba Remoto e é renovado ao reabrir o app. O aparelho precisa dos serviços Google.
- **iOS:** use uma build assinada com `EnableRemotePush=true`, `ApnsEnvironment=development` ou `production` conforme o perfil, e a capacidade Push Notifications. A variante gratuita sem essa capacidade continua mostrando estados com o app aberto.

No servidor, configure por variáveis de ambiente ou cofre de segredos:

| Chave | Valor esperado |
|---|---|
| `RemotePush__FcmProjectId` | Identificador do projeto Firebase |
| `RemotePush__FcmServiceAccountFile` | Caminho privado da credencial JSON da conta de serviço com permissão de envio FCM |
| `RemotePush__ApnsBaseAddress` | Endpoint HTTPS oficial APNs do ambiente correspondente à assinatura |
| `RemotePush__ApnsKeyFile` | Caminho privado da chave de assinatura APNs, montada no servidor |
| `RemotePush__ApnsKeyId` | Identificador da chave Apple |
| `RemotePush__ApnsTeamId` | Identificador da equipe Apple |
| `RemotePush__ApnsTopic` | Bundle identifier da variante instalada |

Nunca versione credenciais ou endereços de sua instalação. Reinicie o servidor após configurar o ambiente. A entrega depende de permissão de notificações, rede e políticas do sistema. O servidor tenta novamente falhas temporárias por até 24 horas e remove destinos que o provedor informa como inválidos.

Consulte a [decisão técnica](../architecture/decisions/107-ciclo-de-vida-remoto-dos-terminais.md), o [guia remoto](../remote.md) e o [guia mobile](../mobile.md).

## Configuração única do Firebase para Android

1. No Firebase Console, crie ou selecione o projeto e registre o app Android com o ApplicationId usado na distribuição do MyTaskApp.
2. Baixe o `google-services.json` desse app e guarde fora do repositório. Gere a build passando `-p:FirebaseConfigFile="CAMINHO_DO_ARQUIVO"`. O mesmo arquivo serve para distribuir o app aos demais celulares dessa instalação.
3. Habilite a API Firebase Cloud Messaging HTTP v1 no projeto. Crie uma conta de serviço com a permissão de envio FCM e monte seu arquivo JSON privado no servidor; nunca o inclua no aplicativo.
4. Configure `RemotePush__FcmProjectId` e `RemotePush__FcmServiceAccountFile` no servidor e reinicie-o. Não é necessário usar Cloud Functions, Firestore ou Analytics.
5. Instale a versão configurada no Android e permita notificações. Use uma tarefa com Claude/Codex em outra máquina para validar atenção, resposta pronta e encerramento com o app fechado e aberto.

FCM não cobra pelo envio de notificações, conforme a [página oficial de preços](https://firebase.google.com/pricing). A hospedagem do servidor e produtos opcionais do Google Cloud podem ter custos próprios. Celulares adicionais não exigem repetir a configuração do projeto ou da conta de serviço.

Instalações anteriores com UnifiedPush precisam atualizar servidor e app: a migration descarta as inscrições antigas; o SDK Firebase obtém um registro novo. O iPhone mantém sua configuração APNs. Veja o [ADR-108](../architecture/decisions/108-fcm-no-android-sem-aplicativo-auxiliar.md).
