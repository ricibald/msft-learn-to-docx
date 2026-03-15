# MsftLearnToDocx

.NET 8 console app that converts Microsoft Learn training paths and modules into a unified Markdown document and a Word (DOCX) file via **pandoc**.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [pandoc](https://pandoc.org/installing.html) in system PATH
- [rsvg-convert](https://wiki.gnome.org/Projects/LibRsvg) (optional, for SVG images in DOCX) — `apt install librsvg2-bin` on Debian/Ubuntu, `brew install librsvg` on macOS, `choco install rsvg-convert` on Windows
- (Optional) `GITHUB_TOKEN` environment variable for higher GitHub API rate limits

## Usage

```bash
# Single learning path
dotnet run -- "https://learn.microsoft.com/en-us/training/paths/copilot/"

# Single module
dotnet run -- "https://learn.microsoft.com/en-us/training/modules/introduction-to-github-copilot/"

# Multiple URLs merged into one document
dotnet run -- "https://learn.microsoft.com/en-us/training/paths/copilot/" "https://learn.microsoft.com/en-us/training/paths/gh-copilot-2/"

# With custom title and DOCX template
dotnet run -- "https://learn.microsoft.com/.../paths/copilot/" --title "GitHub Copilot Guide" --template custom.docx

# Markdown only (no pandoc required)
dotnet run -- "https://learn.microsoft.com/.../paths/copilot/" --format md

# Custom output directory
dotnet run -- "https://learn.microsoft.com/.../paths/copilot/" -o ./my-output

# Help
dotnet run -- --help
```

### Docker

Build and run without installing .NET or pandoc locally:

```bash
# Build the image
docker build -t msft-learn-to-docx .

# Run (output mounted to current directory)
docker run --rm -v "$(pwd)/output:/output" \
  -e GITHUB_TOKEN="$GITHUB_TOKEN" \
  msft-learn-to-docx \
  "https://learn.microsoft.com/en-us/training/paths/copilot/"

# Multiple URLs with custom title
docker run --rm -v "$(pwd)/output:/output" \
  msft-learn-to-docx \
  "https://learn.microsoft.com/.../paths/copilot/" \
  "https://learn.microsoft.com/.../modules/mod/" \
  --title "My Training Guide"
```

Pre-built image from DockerHub:

```bash
docker pull ricibald/msft-learn-to-docx
docker run --rm -v "$(pwd)/output:/output" ricibald/msft-learn-to-docx "<url>"
```

#### Docker Compose

```bash
# Run with docker compose
docker compose run msft-learn-to-docx "https://learn.microsoft.com/.../paths/copilot/" --title "My Guide"
```

## Output

Generated files are saved under `output/{slug}_{timestamp}/`:

```
output/copilot_20260314-120000/
├── media/           # Downloaded images (prefixed with M{n}_ per module)
├── copilot.md       # Unified Markdown (with YAML frontmatter cover page)
└── copilot.docx     # Word document (with Table of Contents)
```

### Heading Hierarchy

- Module title = H1
- Unit title = H2
- Content headings within each unit = H3+
- YAML frontmatter block (`title`, `date`) is used by pandoc to generate a Word cover page

### DOCX Template

The pandoc template is auto-detected from `Templates/template.docx` in the working directory. To use a different template:

```bash
dotnet run -- "<url>" --template path/to/custom-template.docx
```

## Architecture

### Data Flow

```mermaid
graph TD
    A[Input URLs] --> B{Parse each URL}
    B --> C[paths/slug or modules/slug]
    C -->|Path| D[Download path index.yml from GitHub]
    C -->|Module| E[Find module in learn-pr/]
    D --> F[For each module UID]
    F --> G[Learn Catalog API → dir name]
    G --> H[GitHub scan → parent dir]
    H --> I[Download module index.yml]
    I --> J[For each unit → download YAML]
    J --> K{Unit type}
    K -->|Include MD| L[Download markdown from includes/]
    K -->|Quiz| M[Parse quiz YAML → Markdown]
    K -->|Empty| N[Skip]
    L --> O[Convert DFM → Standard Markdown]
    O --> P[Download media]
    E --> I
    P --> Q[Merge all contents with YAML frontmatter]
    M --> Q
    Q --> R[pandoc → DOCX]
```

### Module Path Resolution

The mapping between module UID and GitHub path is non-deterministic. Known exceptions:

| UID | GitHub Directory | Notes |
|-----|-----------------|-------|
| `learn.github.copilot-spaces` | `introduction-copilot-spaces` | slug ≠ uid |
| `learn.github-copilot-with-javascript` | `introduction-copilot-javascript` | slug ≠ uid, no provider |
| `learn.wwl.*` | `learn-pr/wwl-azure/` | wwl ≠ wwl-azure |
| `learn.advanced-github-copilot` | `learn-pr/github/` | no provider in uid |

**Strategy**: Learn Catalog API (`url` field) → real directory name → GitHub Contents API scan for parent directory.

### DFM → Standard Markdown Conversion

Handled Docs-Flavored Markdown syntax:

- `:::image type="content" source="..." alt-text="...":::` → `![alt-text](source)`
- `> [!NOTE]`, `> [!TIP]`, `> [!WARNING]`, `> [!IMPORTANT]`, `> [!CAUTION]` → blockquote with bold label
- `> [!div class="nextstepaction"]`, `> [!div class="checklist"]` → removed
- `:::zone target="...":::` / `:::zone-end:::` → removed
- `:::row:::`, `:::column:::` → removed
- `[!VIDEO url]` → link
- `:::code language="..." source="...":::` → downloads source from GitHub, inlines with `range` support
- `[!INCLUDE[](path)]` residuals → removed

## Project Structure

```
├── MsftLearnToDocx.csproj     # .NET 8 project
├── Program.cs                  # Entry point and orchestration
├── Dockerfile                  # Multi-stage Docker build (SDK → runtime + pandoc)
├── .dockerignore               # Docker build exclusions
├── docker-compose.yml          # Docker Compose convenience config
├── Models/
│   └── LearnModels.cs          # YAML, Catalog API, and downloaded content models
├── Services/
│   ├── GitHubRawClient.cs      # Raw content download + Contents API
│   ├── LearnCatalogClient.cs   # Microsoft Learn Catalog API
│   ├── ModuleResolver.cs       # UID → GitHub path resolution
│   ├── ContentDownloader.cs    # Full download orchestration
│   ├── DfmConverter.cs         # DFM → standard Markdown
│   ├── MarkdownMerger.cs       # Merge + YAML frontmatter + heading normalization
│   ├── PandocRunner.cs         # pandoc → DOCX conversion (with TOC)
│   ├── RetryHandler.cs         # HTTP retry with exponential backoff
│   └── CachingHandler.cs       # HTTP response cache (24h TTL, file-based)
└── Templates/
    └── template.docx           # Default pandoc reference-doc template
```

## Dependencies

- [YamlDotNet](https://github.com/aaubry/YamlDotNet) – YAML parsing
- [pandoc](https://pandoc.org/) – Markdown → DOCX conversion (external)

## HTTP Resilience

`RetryHandler` (DelegatingHandler) automatically handles:
- HTTP 429 (Too Many Requests) respecting the `Retry-After` header
- HTTP 5xx / timeouts with exponential backoff (2s, 4s, 8s)
- Network errors with 3 automatic retries

## HTTP Caching

`CachingHandler` (DelegatingHandler) caches all successful (200 OK) GET responses to disk with a 24-hour TTL.

- Cache location: `%LOCALAPPDATA%/MsftLearnToDocx/cache/` (Windows) or `~/.local/share/MsftLearnToDocx/cache/` (Linux/macOS)
- Only 200 OK responses are cached; 404 and errors are never cached
- Repeated runs for the same content reuse cached data, avoiding redundant API calls
- To clear the cache, delete the cache directory

## CI/CD

A GitHub Actions workflow (`.github/workflows/docker-publish.yml`) automatically builds and pushes the Docker image to DockerHub on every git push.

**Required repository secrets:**
- `DOCKERHUB_USERNAME` — your DockerHub username
- `DOCKERHUB_TOKEN` — a DockerHub access token (Settings → Security → Access Tokens)
