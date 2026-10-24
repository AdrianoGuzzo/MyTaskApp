# ADR-010 — Classificação da tela "Hoje" é função pura no domínio

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | dominio |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TodayClassifier, Classify, tela Hoje, ATRASADAS, AGORA, IsLate, NowWindowBeforeMinutes, NowWindowAfterMinutes |
| **Relações** | relacionado: [ADR-004](004-lembretes-sobrevivem-ao-processo.md), [ADR-022](022-ordem-manual-por-arrasto.md) |
| **Código** | `src/MyTaskApp.Domain/Planning/TodayClassifier.cs`, `src/MyTaskApp.Application/Configuration/ApplicationOptions.cs` |

**Decisão:** `TodayClassifier.Classify(candidato, hoje, agora, janela)` decide a
seção. Não lê relógio nem banco; recebe tudo por parâmetro.

**Por quê:** "o que está atrasado" é regra de negócio, não detalhe de consulta.
Como função pura, cada linha do §9 vira um teste direto — inclusive as bordas que
passariam despercebidas, como a janela de "agora" dando a volta na meia-noite.

**Interpretação registrada do §9:** o exemplo do briefing mostra `Daily 09:00`
sob **HOJE** enquanto **AGORA** marca 14:00. Ou seja, tarefa de hoje com horário
vencido **não** migra para ATRASADAS — lá ficam só as de dias anteriores. Seguimos
o exemplo e sinalizamos essas com `IsLate`, para a UI destacar sem trocar de
seção. Mudar de ideia é alterar uma linha do classificador e os testes que a
fixam.

**"Agora" é configurável:** `NowWindowBeforeMinutes`/`NowWindowAfterMinutes` no
appsettings, padrão −15/+60. Assimétrico de propósito: o que está chegando pesa
mais que o que acabou de vencer.
