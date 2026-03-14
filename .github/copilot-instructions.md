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
7. Merge all contents into single markdown with YAML frontmatter (`title`, `date`) for Word cover page
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
- `DfmConverter`: regex-based, converts :::image:::, [!NOTE], [!div], :::zone:::, [!VIDEO], :::code:::, etc.
- `MarkdownMerger`: YAML frontmatter generation + heading normalization (Module = H1, Unit = H2, content = H3+)
- `ContentDownloader`: orchestrates download of paths/modules/units/media
- `PandocRunner`: invokes pandoc with `--toc`, `--toc-depth`, `--reference-doc`, `--resource-path`
- `CachingHandler`: file-based HTTP cache (24h TTL), caches 200 OK only, SHA256 hashed URLs as keys

### Multi-URL Support
- CLI accepts multiple positional URL arguments
- Each URL is downloaded independently (path or module)
- All `DownloadedContent` objects are merged into a single markdown + DOCX
- `--title` flag overrides the auto-derived document title
- `--output` / `-o` overrides the auto-generated output directory
- `--format` / `-f` selects output format: `docx` (default) or `md` (markdown only, no pandoc required)

### Heading Hierarchy
- Module title = H1
- Unit title = H2
- Content headings within each unit are shifted so minimum = H3
- YAML frontmatter (`title`, `date`) renders as pandoc title block (Word cover page)

### Docker
- Multi-stage Dockerfile: SDK build → alpine runtime + pandoc
- Docker Compose for convenience (`docker-compose.yml`)
- Output directory defaults to `/output` (Docker VOLUME)
- `GITHUB_TOKEN` passed via `-e` flag or `.env`

### Dependencies
- `YamlDotNet` for YAML parsing
- External `pandoc` for DOCX conversion
- `GITHUB_TOKEN` optional (env var) for higher GitHub API rate limits
