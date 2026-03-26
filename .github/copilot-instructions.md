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
   b. If no toc.yml, try `toc.json` from the docs root (used by vscode-docs). Format: `[{name, area, topics: [[title, path], ...]}]` with nested sections as `["", "", {name, area, topics}]`. **Recursive area search**: when contentPath is a nested sub-area (e.g., `copilot/agents`), `ParseTocJsonSection` searches recursively through top-level sections and their nested topic entries to find the matching `area` property, then returns only that sub-section's topics. **Empty area**: when contentPath is empty (root docs URL), returns ALL sections from the toc.json with top-level section names as section headers at depth 0
   c. If no toc found, recursively list directory and sort alphabetically (index.md/overview.md first)
   d. If path resolves to neither a directory nor a toc, try appending `.md` for single-file download
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
- **GitHub imposes a 100-object limit** on the LFS Batch API: `ResolveLfsBatchAsync` splits large requests into chunks of ≤100 objects to avoid HTTP 413 (`"More than 100 objects specified."`)

### Known Structural Exceptions in the MicrosoftDocs/learn Repo
- **UID ≠ directory name**: `learn.github.copilot-spaces` → `introduction-copilot-spaces`, `learn.github-copilot-with-javascript` → `introduction-copilot-javascript`
- **Parent dir ≠ uid prefix**: `learn.wwl.*` → `learn-pr/wwl-azure/` (not `wwl/`); modules without provider (e.g., `learn.advanced-github-copilot`) may live in `learn-pr/github/`
- **Cross-repo modules**: some learning paths reference modules from private repos (e.g., `learn-bizapps.*` → `MicrosoftDocs/learn-bizapps-pr`, which is not publicly accessible). `ContentDownloader.DownloadModuleByUidSafeAsync` handles these by creating a placeholder module with a warning blockquote in the output document, plus a console warning. The module title is fetched from the Catalog API when possible
- **Media dir naming**: most modules store images in `media/`, but some (e.g., `intro-to-azure-load-balancer`, `intro-to-azure-application-gateway`, `intro-to-azure-network-watcher`) use `images/` instead. `ContentDownloader.ProcessUnitAsync` tracks the original source directory from DFM `:::image source="../images/...":::` references via `mediaOrigins` dictionary, and `DownloadMediaAsync` uses this to download from the correct GitHub directory
- **Numbered unit YAML files**: `1-introduction.yml`, `2-xxx.yml`; matched by slug in filename
- **Knowledge check units**: `quiz:` field appears as a root-level YAML key (not inside `content: |`) — handled via `Quiz` property in `UnitYaml`
- **Units without content**: sandbox exercises may have empty content → skipped

### DocFX / DFM Path Conventions
- **Tilde prefix `~/`**: In DocFX Markdown, `~/` means the root of the repository (not relative to the current file). `DocsDownloader.ResolveRelativePath` detects this prefix and resolves directly from repo root instead of prepending the page's directory. Example: `:::image source="~/reusable-content/ce-skilling/azure/media/img.png":::` resolves to `reusable-content/ce-skilling/azure/media/img.png` (no `articles/` prefix)
- **Zone markers without trailing `:::`**: Some repos (notably `MicrosoftDocs/azure-docs`) use zone markers without the closing `:::` delimiter: `::: zone pivot="..."` and `::: zone-end`. `DfmConverter.ZoneStartRegex`/`ZoneEndRegex` handle both formats (with and without trailing `:::`)
- **Reference-style image links**: Some docs pages use numeric reference-style image links (`![alt][0]` with `[0]: media/img.png` definitions). When merging pages, these numeric IDs collide. `DocsDownloader.InlineRefStyleImageLinks` converts them to inline format (`![alt](media/remapped.png)`) and removes the definitions, solving both duplicate refs and path remapping

### Known Pandoc Warnings (Non-Actionable)
- **SVG conversion**: `rsvg-convert` tool is only available in the Docker image (Alpine package `rsvg-convert`). On local Windows/macOS dev, SVG images produce `Could not convert image ... rsvg-convert: does not exist` — these are harmless (SVG is still embedded)
- **False TeX math**: Content using literal `$` characters (e.g., metric names like `storageaccounts-Capacity-BlobCapacity$`) may trigger `Could not convert TeX math` warnings. This is inherent to the source content, not a pipeline bug

### Key Services
- `DocsUrlParser`: static class that parses all URL types → `LearnTrainingUrl | DocsSiteUrl`. Known mapping table + locale stripping
- `DocsDownloader`: downloads generic docs from GitHub repos recursively. Uses `toc.yml` for page ordering (including recursive sub-TOC resolution for nested `toc.yml` references) or `toc.json` from docs root (vscode-docs format: `[{name, area, topics}]` with nested sections). **Recursive area search** in `toc.json`: `ParseTocJsonSection` searches top-level sections first, then recursively traverses nested topic entries (`["", "", {name, area, topics}]`) to find the matching `area` — enabling sub-area downloads like `copilot/agents` from a single centralized `toc.json`. Handles image remapping, strips frontmatter/HTML blocks. Supports single-file path resolution (appends `.md`). Handles DocFX `~/` repo-root paths in both includes and image references. Converts reference-style image links (`![alt][ref]` + `[ref]: path`) to inline format to avoid duplicate reference IDs when merging multiple pages
- `GitHubRawClient`: raw.githubusercontent.com for content + api.github.com/contents for directory listing. Supports both default `MicrosoftDocs/learn` repo and arbitrary repos via `DocsRepoInfo` overloads. LFS-aware downloads via Git LFS Batch API
- `LearnCatalogClient`: `https://learn.microsoft.com/api/catalog/?uid=...&type=modules` — no authentication required
- `ContentDownloader`: downloads Learn training content (paths + modules + units). Accepts `LearnCatalogClient` for Catalog API lookups. Handles unresolvable modules gracefully via `DownloadModuleByUidSafeAsync` (placeholder with warning in document)
- `ModuleResolver`: heuristic parent dir (from uid prefix) + fallback full scan of learn-pr/
- `DfmConverter`: regex-based, converts :::image:::, [!NOTE], [!div], :::zone:::, [!VIDEO], :::code:::, etc. Also runs `EnsureBlankLineBeforeLists` to inject a blank line before any list block that immediately follows a paragraph (prevents pandoc from rendering bullets as inline text). Also runs `EnsureBlankLinesAroundHorizontalRules` to ensure pandoc correctly treats `---` as horizontal rules (not YAML or setext headings). Zone pivot markers are stripped with or without trailing `:::` (both `:::zone pivot="...":::` and `::: zone pivot="..."` formats supported, matching azure-docs conventions)
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
- Test files: `DocsUrlParserTests.cs` (URL parsing + LiveSiteUrl, 15 tests), `GitHubRawClientTests.cs` (LFS pointer detection/parsing via reflection), `TocEntryTests.cs` (model tests), `DfmConverterTests.cs` (DFM→MD conversion, zone pivots with/without trailing `:::`, HR blank lines, image title stripping, 31 tests), `MarkdownMergerTests.cs` (heading hierarchy, TOC-based docs-site hierarchy, frontmatter, subject, attribution, download summary, placeholders, duplicate title removal, 23 tests), `DocsDownloaderTests.cs` (StripFrontmatter, StripHtmlBlocks, SanitizeImageFileName, ResolveRelativePath including `~/` tilde repo-root, DeriveTitleFromPath, ref-style image link inlining, trailing HR removal, toc.json recursive sub-area search, 19 tests), `E2eAzureBlobsTests.cs` (full pipeline: download azure/storage/blobs → merge → pandoc, 2 tests), `E2eVscodeDocsTests.cs` (single page LFS download: code.visualstudio.com/docs/copilot/getting-started + directory download: docs/copilot with 61 pages and 146 LFS images, 2 tests), `E2eDotnetDocsTests.cs` (dotnet/docs: dependency-injection directory, 1 test), `E2eLearnTrainingTests.cs` (Learn training: single module introduction-to-github-copilot + learning path copilot, 2 tests)
- **E2E test design**: E2E tests use file-based assertions (checking `DownloadedContent` model properties and files on disk) instead of `Console.SetOut`/`Console.SetError` to avoid `ObjectDisposedException` when tests run in parallel. Compare image references in merged markdown against files in `media/` directory
- **Test categories**: all test classes have `[Trait("Category", "Unit")]` or `[Trait("Category", "E2E")]`. Run unit-only: `--filter "Category=Unit"`, E2E-only: `--filter "Category=E2E"`
- E2E tests hit real GitHub API + pandoc and take ~1 minute each; CI runs only unit tests
- Run tests: `dotnet test Tests/MsftLearnToDocx.Tests.csproj`
- CI: `.github/workflows/ci.yml` — .NET 8 build + test on every push/PR

### Dependencies
- `YamlDotNet` for YAML parsing
- External `pandoc` for DOCX conversion
- `GITHUB_TOKEN` optional (env var) for higher GitHub API rate limits
