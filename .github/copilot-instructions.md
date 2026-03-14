# Copilot Instructions — MsftLearnToDocx

## Architettura progetto

Applicazione console .NET 8 che converte Microsoft Learn paths/modules in Markdown + DOCX.

### Flusso dati
1. Input: URL learn.microsoft.com (path o modulo) + opzionale template DOCX
2. Parse URL → tipo (paths/modules) + slug
3. Download index.yml da GitHub raw (`learn-pr/paths/{slug}/index.yml` o ricerca modulo)
4. Per ogni modulo: Catalog API → directory name → GitHub scan parent dirs → download unit YAML
5. Per ogni unit: download markdown da includes/ + download media da media/
6. Conversione DFM → Markdown standard (regex-based)
7. Merge markdown con heading level adjustment
8. pandoc → DOCX

### Eccezioni strutturali note nel repo MicrosoftDocs/learn
- **UID ≠ directory name**: `learn.github.copilot-spaces` → `introduction-copilot-spaces`, `learn.github-copilot-with-javascript` → `introduction-copilot-javascript`
- **Parent dir ≠ uid prefix**: `learn.wwl.*` → `learn-pr/wwl-azure/` (non `wwl/`); moduli senza provider (es. `learn.advanced-github-copilot`) possono stare in `learn-pr/github/`
- **Unit YAML numerati**: `1-introduction.yml`, `2-xxx.yml`; match per slug nel nome file
- **Knowledge check units**: il campo `quiz:` finisce come chiave YAML root-level (non dentro `content: |`) — gestito con proprietà `Quiz` nel modello `UnitYaml`
- **Unit senza contenuto**: sandbox exercises possono avere content vuoto → skip

### Servizi chiave
- `GitHubRawClient`: raw.githubusercontent.com per contenuti + api.github.com/contents per listing directory
- `LearnCatalogClient`: `https://learn.microsoft.com/api/catalog/?uid=...&type=modules` — nessuna autenticazione richiesta
- `ModuleResolver`: heuristic parent dir (da uid prefix) + fallback scan completo learn-pr/
- `DfmConverter`: regex-based, converte :::image:::, [!NOTE], [!div], :::zone:::, [!VIDEO], ecc.
- `MarkdownMerger`: heading shift basato su min heading trovato nel contenuto

### Dipendenze
- `YamlDotNet` per parsing YAML
- `pandoc` esterno per conversione DOCX
- `GITHUB_TOKEN` opzionale (env var) per rate limit GitHub API più alti
