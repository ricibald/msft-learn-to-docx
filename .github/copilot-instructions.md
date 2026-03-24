# Copilot Instructions — MsftLearnToDocx

## Project Architecture

.NET 8 console application that converts Microsoft Learn paths/modules and Microsoft documentation sites into Markdown + DOCX.

### Supported URL Types
1. **Learn training**: `learn.microsoft.com/.../training/paths/{slug}` or `.../modules/{slug}` → existing Learn flow (Catalog API, unit YAML, DFM conversion)
2. **VS Code docs**: `code.visualstudio.com/docs/{path}` → `microsoft/vscode-docs` repo, uses Git LFS for images
3. **Microsoft Docs sites**: `learn.microsoft.com/{locale?}/{product}/{path}` (non-training) → auto-mapped to GitHub repos via `DocsUrlParser` known mapping table

### Data Flow — Learn Training (existing)
1. Input: one or more learn.microsoft.com training URLs + optional `--title` + optional DOCX template
2. Parse each URL → type (paths/modules) + slug
3. Download index.yml from GitHub raw (`learn-pr/paths/{slug}/index.yml` or module search)
4. For each module: Catalog API → directory name → GitHub scan parent dirs → download unit YAML
5. For each unit: download markdown from includes/ + download media from media/
6. DFM → standard Markdown conversion (regex-based)
7. Merge all contents into single markdown with YAML frontmatter + CC BY 4.0 attribution
8. Module title = H1, Unit title = H2, content headings = H3+
9. pandoc → DOCX

### Data Flow — Docs Sites (new)
1. Input: one or more docs site URLs (code.visualstudio.com or learn.microsoft.com non-training)
2. `DocsUrlParser.Parse()` → `DocsSiteUrl` with `DocsRepoInfo` (owner, repo, branch, basePath, contentPath, usesLfs, liveSiteHost, liveSitePathPrefix)
3. `DocsDownloader.DownloadAsync()`:
   a. Try to download `toc.yml` from the target directory for page ordering **with hierarchical depth tracking**
      - Case-insensitive: tries both `toc.yml` and `TOC.yml` (GitHub raw URLs are case-sensitive, azure-docs uses uppercase)
      - Supports both root-level sequence format (`[{name, href, items}]`) and root-level mapping format (`{items: [{name, href, items}]}`)
   b. If no toc.yml, recursively list directory and sort alphabetically (index.md/overview.md first)
   c. If path resolves to neither a directory nor a toc.yml, try appending `.md` for single-file download
   d. **TOC hierarchy**: section-only entries (name + items, no href) become section header units; pages carry their nesting depth
   e. Download each .md file, strip YAML frontmatter, remap image paths to `media/` output dir
   f. Apply DFM conversion + strip HTML blocks (`<video>`, `<div>`)
   g. Download images (with batched Git LFS Batch API for LFS-enabled repos)
   h. **Image fallback**: for non-LFS repos, if GitHub returns 404, try downloading from live site URL (e.g., `learn.microsoft.com/en-us/...`)
4. Content is merged with **TOC-based heading hierarchy**: section headers → headings at TOC depth, page titles → headings at depth+1, content headings shifted accordingly
5. Duplicate first H1 removed from page content when it matches the TOC-provided title
6. Pages separated by horizontal rules in merged markdown
7. pandoc → DOCX

### URL → Repo Mapping (`DocsUrlParser`)
- Each mapping includes `LiveSiteHost` and `LiveSitePathPrefix` for fallback image downloads from the live site
- `code.visualstudio.com/docs/*` → `microsoft/vscode-docs`, branch `main`, docs path `docs/`, **LFS enabled**
- `learn.microsoft.com/*/azure/devops/*` → `MicrosoftDocs/azure-devops-docs`, `docs/`
- `learn.microsoft.com/*/dotnet/*` → `dotnet/docs`, `docs/`
- `learn.microsoft.com/*/azure/*` → `MicrosoftDocs/azure-docs`, `articles/`
- `learn.microsoft.com/*/sql/*` → `MicrosoftDocs/sql-docs`, `docs/`
- `learn.microsoft.com/*/powershell/*` → `MicrosoftDocs/PowerShell-Docs`, `reference/`
- `learn.microsoft.com/*/visualstudio/*` → `MicrosoftDocs/visualstudio-docs`, `docs/`
- `learn.microsoft.com/*/windows/*` → `MicrosoftDocs/windows-dev-docs`, `uwp/`
- Locale segment (e.g., `en-us/`) is automatically stripped from learn.microsoft.com paths
- Training URLs (`training/paths/` or `training/modules/`) always route to the existing Learn flow

### Git LFS Support
- Some repos (notably `microsoft/vscode-docs`) use Git LFS for images
- `GitHubRawClient.DownloadFileAsync(DocsRepoInfo, ...)` detects LFS pointer files (starts with `version https://git-lfs.github.com/spec/v1`)
- When detected, parses the OID (sha256) and size from the pointer, then uses the **Git LFS Batch API** (`POST https://github.com/{owner}/{repo}.git/info/lfs/objects/batch`) to obtain the actual download URL
- **IMPORTANT**: The LFS Batch API must be called **without** the `Authorization: Bearer` header used for the GitHub API — a separate `HttpClient` (`_lfsHttp`) is used for this purpose. Sending a Bearer token causes 403 Forbidden on public repos
- `media.githubusercontent.com` fallback was removed because it returns 404 for most LFS objects in `vscode-docs`
- LFS detection is only active when `DocsRepoInfo.UsesLfs == true` (set per mapping)
- The `_lfsHttp` client also handles the final binary download using auth headers returned by the LFS server in the batch response
- `DownloadLfsFilesAsync` returns failed repo paths so callers can try live site fallback

### Known Structural Exceptions in the MicrosoftDocs/learn Repo
- **UID ≠ directory name**: `learn.github.copilot-spaces` → `introduction-copilot-spaces`, `learn.github-copilot-with-javascript` → `introduction-copilot-javascript`
- **Parent dir ≠ uid prefix**: `learn.wwl.*` → `learn-pr/wwl-azure/` (not `wwl/`); modules without provider (e.g., `learn.advanced-github-copilot`) may live in `learn-pr/github/`
- **Cross-repo modules**: some learning paths reference modules from private repos (e.g., `learn-bizapps.*` → `MicrosoftDocs/learn-bizapps-pr`, which is not publicly accessible). `ContentDownloader.DownloadModuleByUidSafeAsync` handles these by creating a placeholder module with a warning blockquote in the output document, plus a console warning. The module title is fetched from the Catalog API when possible
- **Media dir naming**: most modules store images in `media/`, but some (e.g., `intro-to-azure-load-balancer`, `intro-to-azure-application-gateway`, `intro-to-azure-network-watcher`) use `images/` instead. `ContentDownloader.ProcessUnitAsync` tracks the original source directory from DFM `:::image source="../images/...":::` references via `mediaOrigins` dictionary, and `DownloadMediaAsync` uses this to download from the correct GitHub directory
- **Numbered unit YAML files**: `1-introduction.yml`, `2-xxx.yml`; matched by slug in filename
- **Knowledge check units**: `quiz:` field appears as a root-level YAML key (not inside `content: |`) — handled via `Quiz` property in `UnitYaml`
- **Units without content**: sandbox exercises may have empty content → skipped

### Key Services
- `DocsUrlParser`: static class that parses all URL types → `LearnTrainingUrl | DocsSiteUrl`. Known mapping table + locale stripping
- `DocsDownloader`: downloads generic docs from GitHub repos recursively. Uses `toc.yml` for page ordering (including recursive sub-TOC resolution for nested `toc.yml` references), handles image remapping, strips frontmatter/HTML blocks. Supports single-file path resolution (appends `.md`)
- `GitHubRawClient`: raw.githubusercontent.com for content + api.github.com/contents for directory listing. Supports both default `MicrosoftDocs/learn` repo and arbitrary repos via `DocsRepoInfo` overloads. LFS-aware downloads via Git LFS Batch API
- `LearnCatalogClient`: `https://learn.microsoft.com/api/catalog/?uid=...&type=modules` — no authentication required
- `ContentDownloader`: downloads Learn training content (paths + modules + units). Accepts `LearnCatalogClient` for Catalog API lookups. Handles unresolvable modules gracefully via `DownloadModuleByUidSafeAsync` (placeholder with warning in document)
- `ModuleResolver`: heuristic parent dir (from uid prefix) + fallback full scan of learn-pr/
- `DfmConverter`: regex-based, converts :::image:::, [!NOTE], [!div], :::zone:::, [!VIDEO], :::code:::, etc. Also runs `EnsureBlankLineBeforeLists` to inject a blank line before any list block that immediately follows a paragraph (prevents pandoc from rendering bullets as inline text). Also runs `EnsureBlankLinesAroundHorizontalRules` to ensure pandoc correctly treats `---` as horizontal rules (not YAML or setext headings). Zone pivot markers (`:::zone pivot="...":::` / `:::zone-end:::`) are stripped while keeping all pivot content.
- `MarkdownMerger`: YAML frontmatter generation + CC BY 4.0 attribution + download summary section. Two merge modes:
  - **Learn training**: Module = H1, Unit = H2, content = H3+ (heading shift applied)
  - **Docs site**: TOC-based heading hierarchy — section headers become headings at TOC depth, page titles at depth+1, content headings shifted accordingly. Duplicate leading H1 removed when matching TOC title. Pages separated by `---` horizontal rules
  - `subject` field is dynamic based on content types present
  - **Download Summary** section after attribution: module/unit counts + list of unavailable modules (detected by `.unavailable` unit UID suffix)
- `DownloadedContent`: model with `Title`, `IsPath`, `Type` (`LearnTraining | DocsSite`), `Modules` list
- `PandocRunner`: invokes pandoc with `--toc`, `--toc-depth`, `--reference-doc`, `--resource-path`
- `CachingHandler`: file-based HTTP cache (24h TTL), caches 200 OK only, SHA256 hashed URLs as keys
  - **Body-read retry**: if `ReadAsByteArrayAsync` fails with `IOException` (socket reset after 200 OK), CachingHandler retries the full request up to 3 times via `CloneGetRequest`. RetryHandler cannot re-intercept body-read failures because the response has already been returned.
  - **LFS OID cache**: `lfs/` subdirectory stores LFS objects keyed by sha256 OID. Since OID is a content-addressed hash, cached objects never expire. Located in the same base cache directory

### Multi-URL Support
- CLI accepts multiple positional URL arguments (can mix training URLs, VS Code docs, and Microsoft Docs site URLs)
- Each URL is downloaded independently via the appropriate downloader (Learn training or Docs site)
- All `DownloadedContent` objects are merged into a single markdown + DOCX
- `--title` flag overrides the auto-derived document title
- `inputUrls` (the original CLI URL arguments) are passed to `merger.Merge()` as `sourceUrls` for CC BY 4.0 attribution
- `--output` / `-o` overrides the auto-generated output directory
- `--format` / `-f` selects output format: `docx` (default) or `md` (markdown only, no pandoc required)

### Heading Hierarchy
- **Learn training mode**: Module title = H1, Unit title = H2, content headings shifted so minimum = H3
- **Docs site mode**: TOC-based heading hierarchy — section headers (TOC entries without href) become headings at their nesting depth (depth 0 = H1, depth 1 = H2, etc.), page titles become headings at depth+1, content headings shifted to start at depth+2. Pages separated by horizontal rules. If content starts with an H1 matching the TOC title, the duplicate is removed
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

### Testing
- xUnit test project at `Tests/MsftLearnToDocx.Tests.csproj`, added to solution
- **IMPORTANT**: Main `MsftLearnToDocx.csproj` excludes `Tests\**` via `DefaultItemExcludes` to prevent SDK glob from including test files in the main compilation
- Test files: `DocsUrlParserTests.cs` (URL parsing + LiveSiteUrl, 15 tests), `GitHubRawClientTests.cs` (LFS pointer detection/parsing via reflection), `TocEntryTests.cs` (model tests), `DfmConverterTests.cs` (DFM→MD conversion, zone pivots with spaces, HR blank lines, image title stripping, 27 tests), `MarkdownMergerTests.cs` (heading hierarchy, TOC-based docs-site hierarchy, frontmatter, subject, attribution, download summary, placeholders, duplicate title removal, 23 tests), `DocsDownloaderTests.cs` (StripFrontmatter, StripHtmlBlocks, SanitizeImageFileName, ResolveRelativePath, DeriveTitleFromPath, trailing HR removal, 13 tests)
- Run tests: `dotnet test Tests/MsftLearnToDocx.Tests.csproj`
- CI: `.github/workflows/ci.yml` — .NET 8 build + test on every push/PR

### Dependencies
- `YamlDotNet` for YAML parsing
- External `pandoc` for DOCX conversion
- `GITHUB_TOKEN` optional (env var) for higher GitHub API rate limits
