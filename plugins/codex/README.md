# Amifolio Codex integration bundle

Amifolio discovers real local Codex projects and references files in their original directories. The default CLI, MCP connection and Stop hook enable reference mode using the effective `CODEX_HOME` (otherwise the current user's `.codex`). They never copy project files, take repository/ZIP snapshots, upload content, manually create projects, edit originals, or automatically parse files. The application stores its own metadata/context separately. Existing archive data is preserved for recovery, not silently migrated or treated as current project files.

## Install the Amifolio plugin

The Amifolio 1.1.2 Windows candidate includes the 1.1.2 Codex plugin with its manifest, hooks, skill and local MCP server. Add the bundled marketplace, then install Amifolio under Codex Plugins. From the CLI, the equivalent steps are:

~~~powershell
codex plugin marketplace add 'E:\path\Amifolio\codex\marketplace'
codex plugin add amifolio@amifolio-local
~~~

Trust the Amifolio hooks in Codex after reviewing them. In the Amifolio desktop app, open **Codex 作品**, choose a project, and enable automatic work sync for that project. Project learning has its own consent controls. In **项目偏好**, explicitly enable delivery of saved settings to Codex. Preferences are available to the trusted plugin while the Amifolio window is closed. Disable the plugin from Codex or remove it with `codex plugin remove amifolio@amifolio-local`.

For offline staging, run `tools/New-CodexPluginPackage.ps1` against the published Windows bridge directory. The marketplace is a directory containing `.agents/plugins/marketplace.json` and `plugins/amifolio`. Keep both under the same root. Match the effective `CODEX_HOME` and Amifolio data root between the app and bridge.

### Migrating from the standalone integration

If Codex already has a standalone Amifolio MCP server or a separately trusted Amifolio Stop hook, disable those Amifolio entries before enabling the plugin to keep one active integration. The standalone Stop status message is `Amifolio 项目扫描` or `Amifolio 收纳核对`. Once the plugin skill is visible, remove a duplicate standalone Amifolio skill from the scope where it was installed. Use Codex's existing trust flow again if an update changes a hook command.

## Standalone integration for older installations

The desktop Agent integration page shows the connection/configuration preview and explicit installation actions. Review those previews before applying them. Existing conflicting configuration and unrelated hooks are preserved. Host configuration, skill installation and necessary AGENTS.md changes require explicit setup actions; project discovery/reference registration does not authorize them. Restart Codex manually after changing its MCP connection.

Use the same application data root and effective `CODEX_HOME` in the app and bridge. Project discovery reads bounded local project metadata and session headers, never dialogue bodies or authentication/configuration secrets. This local adapter is not a stable official host protocol: absent, malformed, limited or unsupported metadata can produce an empty/incomplete discovery result. Do not fall back to manual project creation or arbitrary directory access.

An explicitly installed and trusted Stop hook defaults to one bounded refresh of discovered projects and existing reference metadata. Separate per-project learning `Capture` consent (default off) permits the missing-receipt fallback below. Skill installation, MCP connection, old output-directory grants and hook trust do not grant that consent. The hook never opens a caller's `transcript_path`, parses content, starts model work, relaunches itself, writes project originals or runs a persistent watcher. Quiet completion is not an archive, registration, parsing or learning-activation receipt. Actual host dispatch remains a separate acceptance check.

Updating the application bundle does not automatically update an existing host installation (skills, MCP configuration, AGENTS.md entries or Stop hooks). The current Stop hook uses the status message `Amifolio 项目扫描`. An existing `Amifolio 收纳核对` hook, a differently configured current hook, or duplicate Amifolio hooks cause installation to stop without overwriting them. Review `hooks.json`, manually remove only the old Amifolio Stop block while preserving other hooks, then explicitly install and trust the new hook. Reinstalling an identical single current hook is a no-op.

## Build and connect

From a source checkout, use the prerequisites and build procedure in the root README:

```powershell
.\tools\Build.ps1
# Published bridge: artifacts/Amifolio/bridge/Amifolio.Bridge.exe
```

In a runtime package the executable is `bridge/Amifolio.Bridge.exe` and this bundle is `codex/`. Replace `plugins/codex/` below with `codex/` for that package. Use the published executable or `dotnet` with an already-built DLL; `dotnet run` is unsuitable as the host connection command because build output is not MCP traffic. Framework-dependent output requires the .NET 10 runtime.

Review [mcp-config.example.toml](mcp-config.example.toml) and substitute the absolute executable and application data paths. An explicitly requested setup may add that entry through the host's MCP settings, or run this user-invoked command:

```powershell
codex mcp add amifolio -- 'E:\path\Amifolio.Bridge.exe' --data-root 'E:\path\AmifolioData' mcp
```

The installer does not run that command. Omitting `--data-root` uses `LocalApplicationData/Amifolio`. Two roots are separate libraries. The bridge inherits `CODEX_HOME` from its process environment and never changes it or writes Codex configuration to repair discovery.

## Install only the skill

Choose an explicit skill destination supported by your host. Preview before installing:

```powershell
.\plugins\codex\Install-Skill.ps1 -SkillsRoot 'E:\my-project\.agents\skills' -DryRun
.\plugins\codex\Install-Skill.ps1 -SkillsRoot 'E:\my-project\.agents\skills' -Install
```

Without `-Install`, the script only reports its plan; `-WhatIf` also prevents writes. An existing `amifolio` folder is backed up under `.amifolio-skill-backups` beside the selected skills directory before replacement; the JSON receipt identifies the backup. Other skills remain unchanged. Installation paths reject symlinks/junctions. Restore only the reported selected folder after stopping its active use. Skill installation is an explicit setup operation, separate from reference mode; this task does not install or modify a live host.

## CLI

Commands emit one newline-terminated JSON object. Failures emit one bounded JSON error and a nonzero exit code; diagnostics go to stderr. Global `--data-root PATH` may appear before or after the command. Quote paths/values containing spaces.

```powershell
& $bridge --data-root $dataRoot projects
& $bridge --data-root $dataRoot submit 'E:\discovered-project\report.docx' --project $projectId
& $bridge --data-root $dataRoot list --search report --project $projectId
& $bridge --data-root $dataRoot status $artifactId
& $bridge --data-root $dataRoot context --project $projectId
& $bridge --data-root $dataRoot tasks
& $bridge --data-root $dataRoot task-cancel $taskId
& $bridge --data-root $dataRoot mcp
```

`$bridge` is the absolute executable path. Project IDs come only from currently discovered valid local roots. `submit` registers one existing file, inferring its discovered project when `--project` is omitted. Optional legacy `--source`/`--tags` arguments are validated but do not edit source or project metadata; Core records discovered references as `Codex`.

A submission receipt has exactly `id`, `path` (the actual original file), and `status: referenced`. It never means archived/copied/backed up/parsed. `status` checks metadata and reports `referenced`, `changed`, `missing` or `unavailable`, plus extraction status/version. Registration schedules no parsing. Only a separate explicit user action in the desktop app may start parsing; the bridge exposes no parse command or tool. Check a known ID after an uncertain submission instead of blindly retrying.

Compatibility commands `repo`, `backup` and `restore` are rejected: this product bridge cannot create project snapshots or read/write arbitrary backup paths. `task-retry` and `process-tasks` return `confirmation_required`, for extraction and model work alike. `tasks`/`task-cancel` query or cancel application tasks without starting work. `collect --retention-days N` remains cleanup of application-owned legacy object storage; it never collects project files or removes reference sources. There are no project-create, artifact-edit or source-delete commands.

## MCP contract and limits

The server uses newline-delimited UTF-8 JSON-RPC 2.0 over stdio. It supports `initialize`, `ping`, notifications, `tools/list` and `tools/call`, negotiating `2025-11-25` or `2025-06-18`; unsupported versions receive `2025-11-25` for client acceptance/disconnection. Send `notifications/initialized` before using tools. Notifications receive no response and cannot register references. Close stdin to shut down. Only tools capability is advertised.

The server exposes eleven tools. The five existing names retain their reference semantics:

| Tool | Arguments | Result boundary |
| --- | --- | --- |
| `artifact_submit` | required `path`; optional `projectId`, `source`, `tags` | Reference one existing allowed project file; receipt after metadata commit, `status: referenced`; no copying, parsing, source edits or contents returned |
| `artifact_status` | required `id` | Current reference/source metadata and extraction state; no raw contents or implicit parsing |
| `artifact_search` | optional `search`, `projectId`, `limit` (1–50) | Metadata of references in currently discovered projects; no preview, raw content, original path or hash; search matches metadata only |
| `context_read` | optional `projectId`, `layer` (`overview`, `memory`, `style`, `summary`, `preferences`) | Enabled formal context in exactly one scope; preferences is global-only and contains confirmed generic wording. Disabled documents and private candidates/provenance are excluded; no arbitrary file open, parsing or model request |
| `projects_list` | none | Up to 50 discovered project IDs/names; no root paths or manual project creation |

The six additional tools use application-managed learning state; none grants consent:

| Tool | Arguments | Result boundary |
| --- | --- | --- |
| `project_resolve` | actual `cwd` | Exact discovered project ID/name; no arbitrary workspace access or scope fallback |
| `learning_status` | `projectId` | Consent, processor, queue/watermark, receipt metadata and bounded verified recent/missing turn IDs; unsupported evidence stays unknown; `sessionHint` is not caller authentication |
| `learning_capture` | project/session/turn IDs, `summary`, `outcome`; optional `quotes`, `outputs` | Durable Agent-reported summary with independently checked user evidence; pending evidence cannot activate memory |
| `learning_claim` | `projectId` | One bounded owned chat lease when permission/mode/threshold permit; otherwise pending; no full-history retrieval |
| `learning_complete` | `jobId`, `token`, `proposals` | Commit at most 24 proposals citing supplied verified user quote IDs; server checks consent, sources, scope, locks, suppression and lease validity |
| `learning_context` | optional `projectId`, `layer`, `condition` | Minimal enabled applicable context, overview first; sharing and master context must be on; no private candidates, arbitrary source bodies or permission changes |

Capture, learning, activation, sharing, remote sending and original-path consent are separate and default off. The Agent resolves its actual project, checks status, reads applicable learning context overview first, captures one known current user turn and checks its receipt. On a later turn it can claim/complete an authorized ready chat batch. Never capture tools, progress, assistant echoes or subagent messages as independent user input. A summary is an Agent report; only verified exact user clauses support preferences. Unknown current identity means skip capture, never guess or claim verification. Existing authorized batches can still be integrated. Background processing requires separate permission; the chat workflow needs no API Key.

### Opt-in Stop learning fallback

After metadata refresh, the hook checks the exact discovered project's current `LearningPolicy.Capture` before opening user-turn evidence. It requires explicit valid `session_id`, never an environment hint. Supplied `turn_id` must verify exactly. If omitted, only a uniquely latest verified turn in that session and exact project root is eligible, after the consent grant and within ten minutes (at most two minutes of future clock skew). Tied latest timestamps, wrong roots, stale records and unsupported schemas remain pending. Existing receipts, including user-deleted tombstones, are skipped; the hook does not backfill an older turn instead.

Fallback outcome is `partial`, with no output files and at most four exact preference clauses of 160 characters each accepted by a small grammar and the shared sensitive-text filter. A safe `last_assistant_message` of at most 1200 characters is prefixed `Agent 自述（未核验完成事实）：`; absent, oversized, unsafe or path-bearing reports become `回合结束，概述等待Agent补充`. These heuristic filters do not guarantee detection of every possible secret. No content is sent remotely. Context rechecks source and consent before committing; the next authorized chat can inspect the receipt and integrate a ready batch.

Stop input remains capped at 64 KiB/depth 16. The learning branch has a cooperative 15-second deadline within the existing 50-second Stop limit. Each Reader call retains its own limits: 128 KiB header, 2 MiB tail, 16 MiB total reads and a five-second cooperative deadline. `stop_hook_active` continuations are ignored. Stdout remains one continuing-hook JSON response with `suppressOutput: true`, no injected system message, and a concise `amifolioLearning` state/reason (`disabled`, `already_recorded`, `recorded` or `pending_evidence`). No quotes, paths or exception messages are emitted. Missing learning evidence does not block Codex or relaunch the hook.

The current Reader requires correlated top-level `event_msg.user_message`. Read-only investigation of this task's Desktop `0.169.0-alpha.11.1` child session found annotated `response_item` messages plus `item_completed.item.type="UserMessage"`, without that legacy event. This format remains unsupported. Per-part annotations distinguish injected AGENTS/skills/tools, but subagent input also carries `user.text/user` and `composer` labels; those alone cannot prove direct human authorship. Sources remain unknown/pending without a trusted adapter, never upgraded from model self-report. This observation does not establish every Desktop or CLI version's behavior.

Real-host acceptance can separately run an actual installed Codex CLI with isolated `CODEX_HOME`, a synthetic read-only workspace and a deterministic local Responses endpoint. Such a run can establish config/skill/AGENTS dispatch and receipts when supported user-event provenance is present. It does not establish real-model reasoning; paid HTTP model validation is a separate synthetic-only check. These fixture tests claim neither result.

Discovery is refreshed per operation, including long-lived MCP connections. Removed/invalid projects do not remain authorized by an old ID. A file merely existing on disk is insufficient: Core validates membership in discovered roots, regular-file status, links, credentials/config exclusions and size. Directories and repository snapshots are rejected. An explicitly supplied unknown/mismatched project never falls back to another/global scope.

Tool results include text JSON and equivalent `structuredContent`. Invalid calls receive JSON-RPC errors; execution failures use `isError: true` and a bounded error. Malformed JSON, duplicate keys, wrong types, null IDs, batches, unknown methods and excessive nesting are rejected. There are no arbitrary-open, project-edit, parse, model-send, configuration-edit, context-enable or explicit publish tools. MCP reserves source `应用` for the application.

This is local library access, not per-agent authentication or tenant isolation. Unfiltered metadata search covers currently discovered projects; a project filter is a query constraint. `context_read` uses `ContextService.ReadAsync`, selecting enabled documents in the exact scope and excluding unconfirmed candidate documents. That service may reconcile its own managed context state during reads; the bridge does not enable sharing or call `PublishAsync`. Original-path permission and any remote-send permission remain separate from discovery/registration.

Input is capped at 64 KiB per line and depth 32. Oversized lines are drained with bounded buffering, rejected, and followed by usable subsequent requests. Replies are capped at 256 KiB. Search/project results cap at 50 and expose `truncated`; metadata fields are clipped. Default context returns `START.md` (4096 characters) and `overview.md` (12000); another requested layer returns only its document (12000), with `truncated` when clipped. Omitted context project means global only; an unknown supplied ID fails. Sequential requests are supported; there is no HTTP transport or parallel task cancellation. Project file bytes are never sent inline through these tools.

## Validate

After the coordinated build is available:

```powershell
dotnet run --project tests/Amifolio.Bridge.Tests/Amifolio.Bridge.Tests.csproj -c Release --no-build --no-restore
```

The suite starts real bridge child processes. Each test has a disposable `CODEX_HOME`, `local-projects` metadata, synthetic project directories and application data; every child inherits that fake home. It checks reference-only behavior, forbidden paths, no source writes/copies/parsing, revoked discovery, metadata refresh on Stop despite old grants, JSON-RPC validation/lifecycle, context privacy and limits. Hook learning cases cover default-off consent, exact/ambiguous identities, recent-window limits, duplicate/deleted receipts, unsafe reports/quotes, caller transcript-path refusal and unsupported Desktop records. Installer checks use a temporary destination. No live host configuration or paid model calls are needed.

Protocol references: [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports), [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle), [MCP tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools). These establish protocol conventions, not marketplace compatibility.
