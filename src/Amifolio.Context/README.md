# Context and AGENTS services

This component depends only on the fixed `Amifolio.Core` records/store interface and the .NET runtime. It never edits AGENTS on its own, and never opens artifact originals. All file mutation tests use unique disposable directories.

## UI / bridge API

```csharp
new ContextService(IPortfolioStore store)
new ContextService(IPortfolioStore store, HttpClient httpClient)
Task<ContextSnapshot> ReadAsync(string? projectId = null, CancellationToken cancellationToken = default)
Task SaveDocumentAsync(string? projectId, string fileName, string content, CancellationToken cancellationToken = default)
Task DeleteDocumentAsync(string? projectId, string fileName, CancellationToken cancellationToken = default)
Task SetEnabledAsync(string? projectId, bool enabled, CancellationToken cancellationToken = default)
Task SetOriginalAccessAsync(string? projectId, bool allowed, CancellationToken cancellationToken = default)
Task PublishAsync(string? projectId = null, CancellationToken cancellationToken = default)
Task GenerateAsync(string? projectId, ModelOptions options, string apiKey, CancellationToken cancellationToken = default)
Task<string> PreviewGenerationAsync(string? projectId = null, CancellationToken cancellationToken = default)
```

`ContextSnapshot` is a record with `bool Enabled`, `string Directory`, `IReadOnlyDictionary<string,string> Documents` and `CandidateDocuments`, and an init-only `bool OriginalAccessAllowed`. Returned maps are detached, read-only copies. `ModelOptions(string Endpoint, string Model, bool RemoteAllowed)` is a record. Sharing and original-path access default **off**. Initial reads return honest manual/empty defaults, and no public START entry. No model-derived memory or personality is invented at initialization.

`Documents` contains formal `START.md`, `overview.md`, `memory.md`, `style.md`, `summary.md`, `sources.md`, minus persistent deletions. Candidate keys are `candidate-summary.md`, optionally `candidate-overview.md`, `candidate-memory.md`, `candidate-style.md`, in `CandidateDocuments` only. Candidate saving is intentionally rejected. The explicit adopt action calls `SaveDocumentAsync(projectId, "memory.md", selectedCandidateContent)`, for example. Candidate deletion is supported by `DeleteDocumentAsync`. Both formal and candidate tombstones survive restart and later model calls. Explicitly saving a formal document recreates only that formal document.

`Directory` always denotes the stable absolute export directory, never a generation directory:

```text
<DataRoot>/context/global/START.md
<DataRoot>/context/project-<SHA256 of exact project ID>/START.md
```

Only the root START entry is stable. It links to complete `generations/<unique-id>/{overview,memory,style,summary,sources}.md` documents. Read START, then follow its exact links within that generation. There are no root-level aliases for the five documents. `ReadAsync().Documents["START.md"]` returns the current public pointer when one exists; other values are the editable formal contents.

`PublishAsync` creates/flushed files first and replaces START atomically. A persisted pending-publication journal reconciles an interrupted pointer switch on the next operation. Ordinary publication leaves previous immutable generations intact. Deletion, correction and source withdrawal remove retired exports before republishing; disabling withdraws all scoped exports and clears candidates. The persistent private state under `<DataRoot>/context-state/` is distinct from the export. Disabled manual drafts remain in that private app state. This is file organization, not an OS access-control boundary.

Scope filtering is repeated after store queries: null includes only records with `ProjectId == null`; project IDs compare ordinally and exactly. App-generated `Source == "应用"` artifacts and known secret filenames are excluded. Metadata refresh has no semantic claims. Only explicit `SetOriginalAccessAsync(scope, true)` adds exact, safe-to-represent absolute `ArtifactRecord.ObjectPath` values to the generated `sources.md`; default metadata and remote model payloads omit those paths. The source instructions direct the agent to open only the necessary original fragment after context proves insufficient. Revocation purges old generations containing paths and survives restart. It cannot retract external copies or restrict a process that already knows the path. User-authored text may itself contain paths and is not treated as an automatic source projection.

User edits made through Save are protected from refresh/model calls. Changes to currently published generation documents are imported as manual edits, missing documents become tombstones, and an externally edited root START causes a conflict. Old/unreferenced generation edits are not watched. Filesystem links/junctions are rejected.

## Model adapter

Known credential filenames use the same deny rules as Core repository snapshots, including `credentials.*`, `secrets.*`, `.env*`, package-manager credential files and private-key/keystore extensions. They are excluded even when individually imported as artifacts. Common credential assignments, including `refresh_token`, are redacted in model previews and provider snippets. These rules remain heuristics rather than a general secret detector.

Generate performs actual HTTP POST to a configurable compatible `/chat/completions` endpoint (a base ending in `/v1` or the full endpoint both work). `RemoteAllowed` is independent of scope sharing and must be true on every call. HTTPS is required, except HTTP loopback for local development. URL credentials/query/fragment are refused. The default client refuses redirects; an injected HttpClient remains caller-owned and its redirect policy must also be safe.

At most 24 scoped, non-app artifacts are sent, with 1,200 characters per preview and a 24,000-byte serialized snippet budget. The final request is capped at 64 KiB; responses at 128 KiB; a call times out after 60 seconds and requests at most 2,048 completion tokens. `PreviewGenerationAsync` returns the bounded source JSON, without credentials. Obvious credential assignments/private keys are redacted and known secret files excluded. These heuristics are not a complete sensitive-content classifier: the caller should show the preview and require sharing consent.

The prompt requires a JSON object with a nonempty `summary.md` string and permits nonempty `overview.md`, `memory.md`, `style.md` strings. Missing/unsupported optional fields are omitted. An empty eligible source set is rejected before any request. Only evidence-grounded claims, source citations and explicit constraints/preferences are requested; agent prose is not evidence of the user's style. Every candidate receives scope, source IDs, UTC timestamp, generation method and unconfirmed-status metadata. This metadata does not certify model claims. Candidate text never enters a public generation or overwrites a confirmed document without explicit adoption. Unknown response keys are ignored. Session keys are used only in the request header and are not stored, logged or echoed through provider error bodies.

Model work holds no scope lock during the request. A persistent revision and a current-source fingerprint reject results after deletion, disabling, edits, or a changed source. Existing candidates are invalidated when their sources change. There is no unattended scheduler, background retry, OCR/original-file extraction, provider-specific dialect negotiation, or live paid-provider validation in this component. Full source-linked removal of copies archived by another component is the store/integrator's responsibility; this service never silently deletes artifacts from the store.

## AGENTS API

```csharp
new AgentsFileService()
Task<AgentsFileSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
string PreviewManagedBlock(AgentsFileSnapshot snapshot, string contextDirectory, bool enabled)
Task<string?> SaveAsync(AgentsFileSnapshot snapshot, string newContent, CancellationToken cancellationToken = default)
```

`AgentsFileSnapshot(string Path, string Content, string Hash, bool Exists)` is a record. `snapshot with { Content = editorText }` can be passed to Preview; Save uses original byte hash and existence for the optimistic concurrency check, not Content equality. Preview returns the full proposed document. It manages one `<!-- folio:begin scope=<stable-id> -->` / `<!-- folio:end -->` block and preserves surrounding text. The template only carries an exact START path and fixed progressive-reading rules. No source text or model output is interpolated into those instructions. Control characters and markup characters in context paths are refused.

Malformed, nested, inline or duplicate markers are rejected by Preview and Save. Stale hash/existence raises IOException, without overwriting the external content. Existing writes retain the exact original bytes in a unique `.bak` via atomic replacement; the returned string is that backup path. Creation or a no-op returns null. Cooperating writers use an adjacent `.amifolio.lock`; an open original handle also denies ordinary concurrent byte writes during replacement. Arbitrary external rename/replace writers cannot be given transactional filesystem CAS guarantees by this API; a final existence/hash check narrows that race.

UTF-8 and BOM-tagged UTF-16/32 are decoded strictly and written with the same encoding/BOM. Managed templates follow the existing CRLF/LF convention; user text is not normalized. New files are UTF-8 without BOM. Unknown legacy encodings are rejected for manual resolution. Size limit is 4 MiB. Cancellation is checked before commit; a completed atomic replace is a durable commit, not something later cancellation can undo.

No global path discovery, override-precedence diagnostic, host configuration modification, or claim that a running Codex session has loaded the rules is made here. AGENTS rules guide reading; they do not enforce filesystem isolation or retract external copies/already-read text.

## Verification

Run the console integration suite (no extra test framework/package):

```powershell
dotnet run --project tests/Amifolio.Context.Tests -c Release
```

The suite uses a fake IPortfolioStore deliberately returning an overly broad result so the service's filtering is exercised, real temporary filesystem publication/backup/encoding operations, an injected HTTP transport for deterministic model responses and delayed-request revocation, and a real TCP loopback HTTP provider for default-client summary-only compatibility. The provider protocol follows the [Chat Completions reference](https://developers.openai.com/api/reference/resources/chat).
