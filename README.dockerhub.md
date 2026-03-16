# MsftLearnToDocx

[![Docker Pulls](https://img.shields.io/docker/pulls/ricibald/msft-learn-to-docx)](https://hub.docker.com/r/ricibald/msft-learn-to-docx)
[![Docker Image Size](https://img.shields.io/docker/image-size/ricibald/msft-learn-to-docx/latest)](https://hub.docker.com/r/ricibald/msft-learn-to-docx)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ricibald/msft-learn-to-docx/blob/main/LICENSE)

Convert Microsoft Learn training paths and modules into a Word document (DOCX) or Markdown — no local install of .NET or pandoc required.

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

## Multiple URLs

```bash
docker run --rm \
  -v "$(pwd)/output:/output" \
  -v msftlearn-cache:/cache \
  -e GITHUB_TOKEN="$GITHUB_TOKEN" \
  ricibald/msft-learn-to-docx \
  "https://learn.microsoft.com/en-us/training/paths/copilot/" \
  "https://learn.microsoft.com/en-us/training/paths/gh-copilot-2/" \
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

```
<url> [<url> ...]              One or more learn.microsoft.com paths or modules URLs
--title "My Title"            Override the document title
--template path/to/ref.docx   Custom pandoc reference DOCX template
--format md                   Generate Markdown only (no pandoc needed)
--output, -o <dir>            Custom output directory
--toc-depth N                 Table of Contents depth (default: 3)
--help                        Show help
```

## Source

[github.com/ricibald/msft-learn-to-docx](https://github.com/ricibald/msft-learn-to-docx)

## Content License & Legal Notice

Training content is fetched from the **[MicrosoftDocs/learn](https://github.com/MicrosoftDocs/learn)** public GitHub repository via the official GitHub API — **not web scraping**.

The Microsoft Learn content is licensed under **[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)**. Every generated document automatically includes the required attribution (author, copyright notice, license URI, and source URL) in both the document properties (YAML frontmatter → Word metadata) and as a visible blockquote at the top of the document body.
