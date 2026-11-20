# Durable archive API (0.1)

`new PortfolioStore(string dataRoot)` implements the unchanged `IPortfolioStore` in `Models.cs`. Data roots and source paths must be local filesystem paths. No scripts, Office macros, external relationships, repository configuration, Git commands, or hooks are executed. SQLite uses parameterized values, WAL, `synchronous=FULL`, foreign keys, and a 15-second busy timeout on every connection. Database work runs on worker threads because Microsoft.Data.Sqlite's underlying operations are synchronous.

## Consumer contract

- `ListAsync(projectId: null)` means **all** active artifacts, including project artifacts. A global context publisher must explicitly filter `ProjectId is null`. List results return only the first 2,048 SQLite characters of each preview, projected in SQL before materializing managed records. `GetAsync` and import results retain the complete stored preview. `kind` is an exact optional filter; search is a literal case-insensitive substring over title, original name, tags, source, and the **complete stored preview**, including text beyond the list prefix. SQLite's built-in case folding covers ASCII; Chinese text matches literally. No semantic search is claimed.
- Generated artifacts have the exact source `应用`; context publication must exclude this source. `SaveGeneratedAsync` takes a filename, not a path. Importing any file/directory inside the data root is rejected to avoid feedback loops. Metadata edits cannot change source.
- Logical identity in this task is case-insensitive original filename + current project ID + exact trimmed source. Identical bytes within that scope return an existing active record, even if an older version contains those bytes. Different bytes allocate a monotonically increasing version; historical objects are never overwritten. Title edits do not change identity. Moving a project changes deduplication scope and reserves the existing version in the destination; historical records are not renumbered. A move into an existing lineage can retain equal historical version numbers; subsequent imports advance beyond both.
- Acknowledgement follows a complete, independently owned copy, SHA-256 calculation, disk flush, rename, and database commit. Objects have unique names, preserve a safe extension, and are marked read-only. `ObjectPath` and `GetOriginalPathAsync` are full local paths to the **archived object**, never the mutable external source. The latter rejects missing, size-modified, or redirected objects. This is not a security boundary against another process with the same filesystem permissions.
- `RemoveArtifactAsync` persistently tombstones an item, clears its searchable preview/tags, and excludes it from lists, searches, lookup, and opening across restarts. It is idempotent and never deletes the original or other versions. A metadata update cannot revive a tombstone. Only explicit reimport can create a new record/version. Archived bytes are retained; removal is not secure erasure, and garbage collection is not implemented.
- Projects persist independently, including an optional normalized root path. Unknown project IDs fail before publication. Root paths may be registered before the directory exists, but existing reparse ancestors are refused.

## Types and extraction statuses

Kinds: `文档`, `表格`, `演示`, `PDF`, `笔记`, `文本`, `代码`, `代码库`, `图片`, `其他`.

Statuses are human-readable strings, not enum codes: `已提取`, `已提取（截断）`, `不支持提取：…`, `提取失败：…`, `提取受限：…`, `已生成快照清单` (repository). Consumers should display the complete string rather than assume successful extraction from file presence.

DOCX extracts body paragraphs and table-cell paragraphs; XLSX resolves shared/inline strings and cached values with worksheet/cell locations, without calculating formulas or following external relationships; PPTX uses presentation relationship order with slide/paragraph locations. UTF-8 and BOM-detected text/code/Markdown/CSV/TSV are read with line locations. Unsupported encodings/binary control characters do not produce invented text. PDF (including text PDFs and scans), image OCR, old binary Office files, encrypted files and deep previews are unsupported. Damaged or limited Office files still retain the original bytes with an honest extraction status. Only the preview is indexed, not a hidden full-document extraction.

## Limits

| Boundary | Limit |
| --- | --- |
| Regular/generated file | 256 MiB |
| Concurrent expensive imports | 2 per `PortfolioStore` instance; cancellable queue |
| Copy buffer | 64 KiB |
| Preview | 65,536 UTF-16 characters, including location labels |
| List result preview | First 2,048 SQLite characters, capped in SQL; full stored preview remains searchable |
| Office ZIP | 2,048 entries, 8 MiB per expanded entry, 32 MiB total expanded bytes |
| Office compressed ratio | 200:1 for entries larger than 1 MiB |
| XML | DTD/entities prohibited, depth 128, 100,000 reader nodes per part, 250,000 cumulative nodes, 32 MiB cumulative part reads (including repeated references) |
| Repository | 20,000 visited entries (including exclusions), 64 directory levels, 256 MiB per file, 512 MiB aggregate source bytes |
| Metadata | source 128, project name 512, title 1,024, tags 4,096 characters; filenames 240 |
| Database contention | up to 15 seconds per SQLite busy operation; cancellation checked around synchronous DB calls |

Copies, hashing, repository traversal and XML work check cancellation. Cancelled or failed copies remove their own `.partial` file. Cancellation/failure after object rename but before commit can leave an unreferenced read-only object; it is never listed as a successful import. Crash-time orphan cleanup is deferred to avoid deleting another process's in-flight copy. No power-loss or disk-controller fault testing is claimed.

## Repository snapshots

Repository inventory is built before source copying. The resulting deterministic ZIP contains source files plus `__amifolio__/manifest.json`, with included relative paths, actual-byte SHA-256 digests, byte counts, and excluded paths/reasons. The artifact preview is the bounded inventory; `ImportResult.Warnings` reports every excluded entry (an excluded directory represents its whole subtree). Fixed timestamps and ordinal ordering make unchanged imports idempotent; original timestamps/permissions and Git history are not retained. Source changes detected between inventory and copying fail the import. A live multi-file repository is not a guaranteed coherent point-in-time snapshot.

Excluded: `.git/.hg/.svn`, `node_modules`, `bin`, `obj`, `venv/.venv`, `__pycache__`, known credential directories/files, `.env*`, private key extensions/names, reparse points/devices, the reserved manifest directory, and the data root itself even when nested in the repository. Dependency manifests and lockfiles remain included. Path-component and Windows opened-handle checks prevent selected symlinks/junctions from redirecting source reads. Path-based secret rules cannot identify secrets embedded in arbitrary source files.

The fixed interface has no separate preview/confirm method: UI can display the returned preview/manifest after import, but a pre-copy review screen would need a parent-approved interface extension. No arbitrary repository include/config references are followed.

## Verification

From the work root:

```powershell
. tools/environment/Enter-DevEnvironment.ps1 -UseExistingLocalProxy
dotnet restore tests/Amifolio.Core.Tests/Amifolio.Core.Tests.csproj
dotnet run --project tests/Amifolio.Core.Tests --no-restore
```

The executable suite uses disposable fixtures below its own output directory; no test-framework packages or real host configuration are used. Evidence and rulings are retained under `tests/Amifolio.Core.Tests/evidence/`.
