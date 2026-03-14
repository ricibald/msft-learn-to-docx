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
8. Every unit becomes an H1 section; content headings start at H2
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
- `MarkdownMerger`: YAML frontmatter generation + heading normalization (every unit = H1, content = H2+)
- `ContentDownloader`: orchestrates download of paths/modules/units/media
- `PandocRunner`: invokes pandoc with `--toc`, `--reference-doc`, `--resource-path`

### Multi-URL Support
- CLI accepts multiple positional URL arguments
- Each URL is downloaded independently (path or module)
- All `DownloadedContent` objects are merged into a single markdown + DOCX
- `--title` flag overrides the auto-derived document title

### Heading Hierarchy
- Every unit is H1 (flat structure for clean Word sections)
- Content headings within each unit are shifted so minimum = H2
- YAML frontmatter (`title`, `date`) renders as pandoc title block (Word cover page)

### Docker
- Multi-stage Dockerfile: SDK build → alpine runtime + pandoc
- Output directory defaults to `/output` (Docker VOLUME)
- `GITHUB_TOKEN` passed via `-e` flag

### Dependencies
- `YamlDotNet` for YAML parsing
- External `pandoc` for DOCX conversion
- `GITHUB_TOKEN` optional (env var) for higher GitHub API rate limits
