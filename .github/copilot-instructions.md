# Copilot Instructions — MsftLearnToDocx

## Project Architecture

.NET 8 console application that converts Microsoft Learn paths/modules into Markdown + DOCX.

### Data Flow
1. Input: one or more learn.microsoft.com URLs (paths or modules) + optional `--title` + optional DOCX template
2. Parse each URL → type (paths/modules) + slug
3. Download index.yml from GitHub raw (`learn-pr/paths/{slug}/index.yml` or module search)
4. For each module: Catalog API → directory name → GitHub scan parent dirs → download unit YAML
5. For each unit: download markdown from includes/ + download media from media/
6. DFM → standard Markdown conversion (regex-based)
7. Merge all contents into single markdown with YAML frontmatter (`title`, `author`, `date`, `subject`, `description`) for Word cover page + CC BY 4.0 attribution blockquote
8. Module title = H1, Unit title = H2, content headings = H3+
9. pandoc → DOCX

### Known Structural Exceptions in the MicrosoftDocs/learn Repo
- **UID ≠ directory name**: `learn.github.copilot-spaces` → `introduction-copilot-spaces`, `learn.github-copilot-with-javascript` → `introduction-copilot-javascript`
- **Parent dir ≠ uid prefix**: `learn.wwl.*` → `learn-pr/wwl-azure/` (not `wwl/`); modules without provider (e.g., `learn.advanced-github-copilot`) may live in `learn-pr/github/`
- **Numbered unit YAML files**: `1-introduction.yml`, `2-xxx.yml`; matched by slug in filename
- **Knowledge check units**: `quiz:` field appears as a root-level YAML key (not inside `content: |`) — handled via `Quiz` property in `UnitYaml`
- **Units without content**: sandbox exercises may have empty content → skipped

### Key Services
- `GitHubRawClient`: raw.githubusercontent.com for content + api.github.com/contents for directory listing
- `LearnCatalogClient`: `https://learn.microsoft.com/api/catalog/?uid=...&type=modules` — no authentication required
- `ModuleResolver`: heuristic parent dir (from uid prefix) + fallback full scan of learn-pr/
- `DfmConverter`: regex-based, converts :::image:::, [!NOTE], [!div], :::zone:::, [!VIDEO], :::code:::, etc. Also runs `EnsureBlankLineBeforeLists` to inject a blank line before any list block that immediately follows a paragraph (prevents pandoc from rendering bullets as inline text).
- `MarkdownMerger`: YAML frontmatter generation (title, date, keywords, subject, description with CC BY 4.0 attribution) + dedicated **# Attribution** H1 section at document top + heading normalization (Module = H1, Unit = H2, content = H3+). `Merge()` accepts optional `sourceUrls` parameter passed from `Program.cs` to embed original learn.microsoft.com URLs in the attribution.
  - `keywords`: list of module titles (topics covered)
- `DownloadedContent`: plain model with `Title`, `IsPath`, `Modules` list
- `PandocRunner`: invokes pandoc with `--toc`, `--toc-depth`, `--reference-doc`, `--resource-path`
- `CachingHandler`: file-based HTTP cache (24h TTL), caches 200 OK only, SHA256 hashed URLs as keys
  - **Body-read retry**: if `ReadAsByteArrayAsync` fails with `IOException` (socket reset after 200 OK), CachingHandler retries the full request up to 3 times via `CloneGetRequest`. RetryHandler cannot re-intercept body-read failures because the response has already been returned.

### Multi-URL Support
- CLI accepts multiple positional URL arguments
- Each URL is downloaded independently (path or module)
- All `DownloadedContent` objects are merged into a single markdown + DOCX
- `--title` flag overrides the auto-derived document title
- `inputUrls` (the original CLI URL arguments) are passed to `merger.Merge()` as `sourceUrls` for CC BY 4.0 attribution
- `--output` / `-o` overrides the auto-generated output directory
- `--format` / `-f` selects output format: `docx` (default) or `md` (markdown only, no pandoc required)

### Heading Hierarchy
- Module title = H1
- Unit title = H2
- Content headings within each unit are shifted so minimum = H3
- YAML frontmatter (`title`, `date`, `keywords`, `subject`, `description`) renders as pandoc title block (Word cover page + document properties)

### Content License
- Source repo `MicrosoftDocs/learn` is licensed under **CC BY 4.0** — confirmed from `https://raw.githubusercontent.com/MicrosoftDocs/learn/main/LICENSE`
- Every generated document must include attribution per CC BY 4.0 §3(a): creator ID, copyright notice, license URI, and source URI
- Attribution is injected by `MarkdownMerger.Merge()` in two places:
  1. YAML frontmatter fields (`author`, `subject`, `description`) → embedded in Word document properties
  2. Visible blockquote at the top of the document body
- Fetching via GitHub API / raw URLs is **not scraping** — it is standard API usage of publicly available content

### Docker
- Multi-stage Dockerfile: SDK build → alpine runtime + pandoc + `rsvg-convert`
  - **Alpine package name**: `rsvg-convert` (NOT `librsvg` — that installs only the `.so` library without the CLI, since Alpine ~3.18)
- Docker Compose for convenience (`docker-compose.yml`)
- Output directory: `/output` (VOLUME) — `Program.cs` detects `DOTNET_RUNNING_IN_CONTAINER=true` and writes there instead of `./output/`
- Cache directory: `/cache` (VOLUME) — mount as named volume `msftlearn-cache` to persist HTTP cache across runs
- `GITHUB_TOKEN` passed via `-e` flag or `.env`

### Dependencies
- `YamlDotNet` for YAML parsing
- External `pandoc` for DOCX conversion
- `GITHUB_TOKEN` optional (env var) for higher GitHub API rate limits
