# MsftLearnToDocx

[![Docker Pulls](https://img.shields.io/docker/pulls/ricibald/msft-learn-to-docx)](https://hub.docker.com/r/ricibald/msft-learn-to-docx)
[![Docker Image Size](https://img.shields.io/docker/image-size/ricibald/msft-learn-to-docx/latest)](https://hub.docker.com/r/ricibald/msft-learn-to-docx)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ricibald/msft-learn-to-docx/blob/main/LICENSE)

Convert Microsoft Learn training paths/modules **and** Microsoft documentation sites (VS Code docs, Azure DevOps, .NET, Azure, etc.) into Word (DOCX) or Markdown — no local install of .NET or pandoc required.

> **Find learning paths**: <https://learn.microsoft.com/en-us/training/browse/?resource_type=learning%20path>

## Quick start

```bash
docker run --rm \
  -v "$(pwd)/output:/output" \
  -v msftlearn-cache:/cache \
  -e GITHUB_TOKEN="$GITHUB_TOKEN" \
  ricibald/msft-learn-to-docx \
  "https://learn.microsoft.com/en-us/training/paths/copilot/"
```

Output is written to `./output/{slug}_{timestamp}/` on the host.

## Why Use This?

**Turn Microsoft Learn & docs into enterprise or personal knowledge** — offline, versioned, distributable.

- 📚 **Offline reading** — complete learning paths or docs sections as standalone DOCX/Markdown  
- 🌐 **Multiple sources** — Learn training, VS Code docs, Azure DevOps, .NET, Azure, SQL, PowerShell docs  
- 🏢 **Enterprise-ready** — Word templates, metadata, CC BY 4.0 attribution included  
- 🤖 **AI-ready** — structured content for embeddings and RAG systems  
- 🔗 **Merge multiple URLs** — combine training + docs into one unified document  
- 🐳 **Zero dependencies** — Docker handles pandoc, .NET, all requirements  
- ♻️ **Smart caching** — repeated runs reuse cached data, instant subsequent conversions  

For full details and use cases → [GitHub repo](https://github.com/ricibald/msft-learn-to-docx)

## Documentation Sites

```bash
# VS Code Copilot documentation
docker run --rm \
  -v "$(pwd)/output:/output" \
  -v msftlearn-cache:/cache \
  -e GITHUB_TOKEN="$GITHUB_TOKEN" \
  ricibald/msft-learn-to-docx \
  "https://code.visualstudio.com/docs/copilot/"

# Azure DevOps Repos documentation
docker run --rm \
  -v "$(pwd)/output:/output" \
  -v msftlearn-cache:/cache \
  -e GITHUB_TOKEN="$GITHUB_TOKEN" \
  ricibald/msft-learn-to-docx \
  "https://learn.microsoft.com/en-us/azure/devops/repos/get-started"
```

## Multiple URLs

```bash
docker run --rm \
  -v "$(pwd)/output:/output" \
  -v msftlearn-cache:/cache \
  -e GITHUB_TOKEN="$GITHUB_TOKEN" \
  ricibald/msft-learn-to-docx \
  "https://learn.microsoft.com/en-us/training/paths/copilot/" \
  "https://code.visualstudio.com/docs/copilot/" \
  --title "GitHub Copilot Complete Guide"
```

## Volumes

| Volume | Mount | Purpose |
| --- | --- | --- |
| output | `-v $(pwd)/output:/output` | Output directory (DOCX + MD + media) |
| `/cache` | `-v msftlearn-cache:/cache` | HTTP response cache — reuse across runs |

## Environment variables

| Variable | Required | Description |
| --- | --- | --- |
| `GITHUB_TOKEN` | Recommended | GitHub Personal Access Token — raises rate limit from 60 to 5000 req/h |

## Options

```text
<url> [<url> ...]              One or more URLs (Learn training, VS Code docs, Microsoft Docs)
--title "My Title"            Override the document title
--template path/to/ref.docx   Custom pandoc reference DOCX template
--format md                   Generate Markdown only (no pandoc needed)
--output, -o <dir>            Custom output directory
--toc-depth N                 Table of Contents depth (default: 3)
--help                        Show help
```

## Source

[github.com/ricibald/msft-learn-to-docx](https://github.com/ricibald/msft-learn-to-docx)

## Supported Sources

| Source | Example URL |
| --- | --- |
| Learn training paths | `https://learn.microsoft.com/en-us/training/paths/copilot/` |
| Learn training modules | `https://learn.microsoft.com/en-us/training/modules/introduction-to-github-copilot/` |
| VS Code docs | `https://code.visualstudio.com/docs/copilot/` |
| Azure DevOps docs | `https://learn.microsoft.com/en-us/azure/devops/repos/get-started` |
| .NET docs | `https://learn.microsoft.com/en-us/dotnet/core/introduction` |
| Azure docs | `https://learn.microsoft.com/en-us/azure/...` |
| SQL Server docs | `https://learn.microsoft.com/en-us/sql/...` |
| PowerShell docs | `https://learn.microsoft.com/en-us/powershell/...` |

## Content License & Legal Notice

Content is fetched from public GitHub repositories (MicrosoftDocs/learn, microsoft/vscode-docs, etc.) via the official GitHub API — **not web scraping**.

Microsoft documentation content is licensed under **[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)**. Every generated document automatically includes the required attribution (author, copyright notice, license URI, and source URL) in both the document properties (YAML frontmatter → Word metadata) and as a visible blockquote at the top of the document body.
