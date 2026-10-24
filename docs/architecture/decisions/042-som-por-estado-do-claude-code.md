# ADR-042 — Um som por estado do Claude Code, com sons do app e do usuário

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-29, commit `a31bd3e` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SoundSynthesizer, WindowsAudioPlayer, MCI, AgentAlertSounds, sons do usuário, WAV, MP3, AlertSoundFailed |
| **Relações** | relacionado: [ADR-004](004-lembretes-sobrevivem-ao-processo.md), [ADR-014](014-padrao-de-lembrete-no-banco.md), [ADR-018](018-instalador-inno-setup-e-dados-do-usuario.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Sounds/SoundSynthesizer.cs`, `src/MyTaskApp.Infrastructure/Sounds/SoundLibrary.cs`, `src/MyTaskApp.Desktop/Reminders/WindowsAudioPlayer.cs`, `src/MyTaskApp.Application/Agents/AgentAlertSounds.cs` |

**Contexto:** o ADR-037 avisa na tela e pisca o terminal quando o Claude para
esperando o usuário, termina a resposta ou falha. Mas o aviso é mudo, e quem
está em outra janela (ou longe da tela) não percebe. O pedido: tocar um som por
estado, ligar e desligar cada um, escolher o som, com alguns sons que já vêm no
app e a possibilidade de subir os próprios.

**Decisão:** cada estado que avisa ganha um som próprio. A escolha é feita em
"Sons do Claude Code…", no menu do painel.

| Estado (`AgentActivity`) | Na tela | Som de fábrica |
|---|---|---|
| `WaitingForUser` | Aguardando você | Chamada |
| `WaitingReview` | Aguardando revisão | Concluído |
| `Failed` | Erro na resposta | Alerta |

- **Os estados são os do aviso** (`AgentSession.NeedsAttention`). "Voltou a
  trabalhar" não toca: não interrompe ninguém.
- **Toca onde o aviso aparece, e só ali.** `RecordAgentEventHandler` toca junto
  com o aviso do canto, depois de piscar o terminal. As regras do ADR-037 valem
  para o som: só na mudança de estado, e nada com o terminal do agente em
  primeiro plano, porque o usuário já está olhando.
- **Nasce ligado, com um som diferente por estado.** Dá para saber, sem olhar,
  se o Claude perguntou algo ou só terminou.
- **Tudo vale na hora.** A janela não tem "Salvar": marcar, escolher, adicionar
  e excluir já gravam. "Restaurar padrão" volta os três estados ao de fábrica.

**Os sons do app são sintetizados, e não arquivos.** `SoundSynthesizer` monta
cada som a partir de uma receita de notas: parciais com ataque de 5 ms e
decaimento exponencial, normalizados e gravados como WAV PCM 16 bits mono a
44,1 kHz. São sete: Chamada, Concluído, Alerta, Sino, Dois toques, Suave e
Bolha. Assim não há licença de áudio de terceiros nem asset para esquecer no
instalador. O mesmo id sempre gera os mesmos bytes. O WAV vai para uma pasta
descartável (`%TEMP%\MyTaskApp\sounds`) na primeira vez que toca, e só é
reescrito se a receita mudar. O player toca arquivo, então os sons do app e os
do usuário seguem o mesmo caminho.

**Os sons do usuário são copiados.** "Adicionar som…" copia o arquivo para
`%APPDATA%\MyTaskApp\sounds` (ou `MYTASKAPP_DATA_DIR\sounds`). Não guarda uma
referência para onde ele estava: mover ou apagar o original não emudece o
aviso, e a pasta sobrevive à atualização como o banco (ADR-018). A pasta é o
catálogo, sem tabela: o id é `custom:<arquivo>`, e o nome mostrado é o nome
do arquivo sem extensão. Nome repetido ganha " (2)", mesmo com outra extensão.

- **Aceita WAV e MP3, até 10 MB.** A assinatura é conferida, e não só a
  extensão (`RIFF…WAVE`, `ID3` ou um quadro MPEG). Um arquivo renomeado seria
  aceito e ficaria mudo justo na hora do aviso.
- **O id vem do banco e é conferido.** Um `custom:..\..\algo` não sai da pasta.
- **Excluir** pede confirmação. Os estados que usavam o som voltam ao de
  fábrica no banco, e não só na tela.
- **Sumiu por fora do app?** O aviso toca o som de fábrica daquele estado, em
  vez de ficar mudo, e a tela mostra o de fábrica.

**Tocar: MCI do Windows, numa thread STA só dele.** O `MessageBeep` dos
lembretes não toca arquivo. O `PlaySound` só toca WAV. Um pacote de áudio seria
dependência nova para um recurso só do Windows. O MCI (`winmm.dll`,
`mciSendStringW`, dispositivo `mpegvideo`) toca WAV e MP3 com o que o Windows
já tem. Conferido na máquina:

- numa thread **MTA** o `open` falha com o erro 266 (o `mpegvideo` é DirectShow
  e quer STA). Por isso o `WindowsAudioPlayer` tem uma thread STA própria, de
  fundo, criada no primeiro som. Abrir, tocar e fechar moram nela, porque o
  dispositivo MCI é da thread que o abriu;
- **um som por vez:** um aviso novo interrompe o que ainda estiver tocando;
- quando o som acaba, a thread fecha o dispositivo e solta o arquivo. Ela
  confere o estado a cada 200 ms;
- como o `ISoundPlayer`, **nunca lança**. Uma falha do MCI vai para o log
  (`AlertSoundFailed`), e o aviso na tela continua.

**Persistência.** A migration `AgentAlertSounds` cria a tabela de mesmo nome,
com uma linha por estado: `Activity` (o número do enum, chave), `IsEnabled` e
`SoundId`. Sem linha, vale o de fábrica (ADR-014). Os sons são globais, e não
por agente. Hoje só há o Claude Code, e um segundo agente pode ganhar a coluna
`ProviderId` quando existir.

**Limites aceitos:**

- sem volume próprio: vale o volume do Windows para o app. Um MP3 alto entra
  alto;
- um som longo toca inteiro, a menos que outro aviso o interrompa;
- fora do Windows, o `IAudioPlayer` é o objeto nulo, e nada toca;
- os lembretes continuam com o `MessageBeep` da escada (ADR-004). Este ADR é só
  dos avisos do agente.
