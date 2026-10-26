# ADR-006 — AwesomeAssertions no lugar de FluentAssertions

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | testes |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | AwesomeAssertions, FluentAssertions, licença, Xceed, asserções |
| **Relações** | — |
| **Código** | `Directory.Packages.props`, `tests/Directory.Build.props` |

**Decisão:** `AwesomeAssertions` 9.6.0 (Apache-2.0).

**Por quê:** FluentAssertions 8.x passou a usar a *Xceed Community License*,
que autoriza apenas uso **não comercial** (verificado no texto publicado no
NuGet). AwesomeAssertions é fork com API idêntica e licença permissiva, o que
mantém o projeto livre de dúvida jurídica caso ele cresça.
