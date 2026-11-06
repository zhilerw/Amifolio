# Amifolio Codex integration bundle

This is a local integration bundle, not a marketplace registration or a claim of an official plugin manifest. It contains a skill, an opt-in skill installer and a manual stdio MCP connection template. It never changes global AGENTS.md, Codex configuration, or host permissions automatically.

## Build and connect

From a source checkout, use PowerShell 7 and the prerequisites in the root README:

```powershell
.\tools\Build.ps1
# Published bridge: artifacts/Amifolio/bridge/Amifolio.Bridge.exe
```

In a downloaded runtime ZIP, the same executable is `bridge/Amifolio.Bridge.exe` and this bundle is `codex/`. Replace `plugins/codex/` in the installation examples below with `codex/` when using that ZIP. Use the published executable, or `dotnet` with the already-built DLL. Do not use `dotnet run` as a host connection command: build/SDK output is not MCP traffic. For framework-dependent output the .NET 10 runtime is required.

Review [mcp-config.example.toml](mcp-config.example.toml), replace both absolute paths, and manually add the entry through your host's supported MCP settings. Alternatively, explicitly run the following after substituting the published executable and the same data root selected in the desktop application:

```powershell
codex mcp add amifolio -- 'E:\path\Amifolio.Bridge.exe' --data-root 'E:\path\AmifolioData' mcp
```

That command is an optional, user-invoked host configuration change; the installer does not execute it. Omitting `--data-root` uses `LocalApplicationData/Amifolio`. Do not run two connections with different roots and expect one shared library.

## Install only the skill

Choose an explicit destination, such as a repository's `.agents/skills` or the user skill directory supported by your host. The current official Codex documentation lists `$HOME/.agents/skills` for user skills. Preview first:

```powershell
.\plugins\codex\Install-Skill.ps1 -SkillsRoot 'E:\my-project\.agents\skills' -DryRun
.\plugins\codex\Install-Skill.ps1 -SkillsRoot 'E:\my-project\.agents\skills' -Install
```

Without `-Install` the script only reports its plan. `-WhatIf` also prevents writes. An existing `amifolio` skill folder is preserved under `.amifolio-skill-backups` beside the chosen skills directory before replacement; the JSON response names the backup. No other skill is changed. Symlinks and junctions in installation paths are rejected. To restore, close any active skill use, move the current `amifolio` folder aside and move the reported backup back to the exact destination. Installation does not connect MCP; connection does not install the skill. A skill cannot guarantee capture of all generated files.

## CLI

All successful commands emit one newline-terminated JSON object. Errors emit one JSON error and a nonzero exit code; diagnostic text goes to stderr. Global `--data-root PATH` may appear before or after the command. Quote paths and values containing spaces.

```powershell
& $bridge --data-root $dataRoot submit 'E:\work\final report.docx' --project $projectId --source Codex --tags 'report,final'
& $bridge --data-root $dataRoot repo 'E:\work\repository' --project $projectId
& $bridge --data-root $dataRoot list --search report --project $projectId
& $bridge --data-root $dataRoot projects
& $bridge --data-root $dataRoot status $artifactId
& $bridge --data-root $dataRoot context --project $projectId
& $bridge --data-root $dataRoot mcp
```

`$bridge` is the absolute executable path. Project IDs must already exist; this bridge does not create projects. `submit` defaults to source `Agent`. `repo` is a Core-filtered repository snapshot, not a Git-history backup; no repository scripts are run by the bridge. A submit receipt has exactly `id`, `path` (the verified archived copy), and `status: archived`; extraction status is checked separately with `status`. Check status before reporting success, and check a known ID before retrying an uncertain submission.

## MCP contract and limits

The server supports newline-delimited UTF-8 JSON-RPC 2.0 over stdio, `initialize`, `ping`, notifications, `tools/list` and `tools/call`. It negotiates `2025-11-25` or `2025-06-18`; an unsupported requested version receives the supported `2025-11-25` version so the client can accept or disconnect. Send `notifications/initialized` after initialization. Notifications never receive replies and cannot invoke artifact writes. Close stdin for shutdown. Only the tools capability is advertised.

| Tool | Arguments | Result boundary |
| --- | --- | --- |
| `artifact_submit` | required `path`; optional `projectId`, `source`, `tags` | One explicitly authorized file; receipt after Core commit and archive-copy verification |
| `artifact_status` | required `id` | Archive/extraction status only |
| `artifact_search` | optional `search`, `projectId`, `limit` (1–50) | Metadata only; no preview, raw content, object path or hash |
| `context_read` | optional `projectId`, `layer` (`overview`, `memory`, `style`, `summary`) | Enabled documents in exactly one scope; no explicit publish operation, sources index, arbitrary path or original-file read |
| `projects_list` | none | Up to 50 project IDs/names; no root paths |

Tool results include text JSON and equivalent `structuredContent`. Invalid calls receive JSON-RPC errors; execution failures return `isError: true` with a bounded error. Unknown methods, malformed JSON, duplicate keys, invalid types, null IDs, batches and excessive nesting are rejected. No arbitrary-open, configuration-edit, context-enable, context-publish or raw-artifact tool exists.

Connection permission is local full-portfolio access: `artifact_search` without a project filter can discover metadata across the library, and `projects_list` enumerates project names/IDs. A project filter is a query constraint, not per-agent authentication or a tenant boundary. Connect only to a host authorized for that local portfolio. Context documents are still selected by exact scope and enabled state; `CandidateDocuments` (unconfirmed suggestions) are never returned. MCP rejects the reserved source `应用` to prevent agents impersonating application-generated artifacts. CLI `--source` remains a user-controlled label.

`context_read` delegates to `ContextService.ReadAsync` and does not invoke `PublishAsync` or enable sharing. The context service may reconcile its own managed state and user edits during a read, according to that service's contract; the bridge never edits host configuration.

Input is capped at 64 KiB per line and JSON depth 32. Oversized lines are drained without unbounded buffering and receive a parse error; subsequent lines remain usable. Replies are capped at 256 KiB. Search/project lists are capped at 50 with `truncated`; individual metadata fields are clipped. A context read defaults to `START.md` (4096 characters) and `overview.md` (12000); other requested layers return only that document (12000), with `truncated` when clipped. Omitted context project means global only; a supplied unknown project fails without global fallback. Omitted search project means library-wide metadata. Limits on actual imported files/repository snapshots are enforced by Core; a large file is never sent inline through MCP. Sequential requests are supported; this first bridge does not implement parallel task cancellation or an HTTP transport.

## Validate

```powershell
dotnet run --project tests/Amifolio.Bridge.Tests/Amifolio.Bridge.Tests.csproj -c Release
```

The executable suite starts real child processes against disposable data, exercises JSON-RPC and CLI, and never connects to the live Codex host. Skill installation checks also use a temporary destination.

Protocol and connection sources: [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports), [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle), [MCP tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [official Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp), [official skill discovery](https://learn.chatgpt.com/docs/build-skills). These references establish protocol/host conventions; this bundle makes no marketplace compatibility claim.
