# Context integration suite

2026-11-06 release review added a 28th scenario covering credential filename variants in previews, published context and provider payloads, and extended the existing credential-format scenario with refresh-token redaction. Both new checks failed against the previous implementation before the repair. The 27/27 observation below is historical; see [the current release check](../../docs/releases/0.1.3-github-readiness.md) for final results.

Run from the repository root:

```powershell
dotnet run --project tests/Amifolio.Context.Tests -c Release
```

Optional focused reproduction:

```powershell
dotnet run --project tests/Amifolio.Context.Tests -c Release -- --filter ordinary
```

No test framework dependencies. Tests own and remove unique disposable directories. No actual global or project AGENTS is modified. The fake store intentionally returns cross-project records so isolation is verified in ContextService rather than assumed from a storage query. Real filesystem bytes, generations, backups and encodings are exercised. HTTP tests include deterministic delayed transports and a real local TCP HTTP provider using the default HttpClient.

Observed on 2026-11-05: initial service skeleton failed all 18 baseline scenarios at the unimplemented methods. New optional-candidate/source-withdrawal cases failed before their implementation; empty-evidence and header/quoted-secret cases then failed before the corresponding guard/redaction repairs. The two original-path cases failed before the new API implementation. The independent-review ordinary-prose regression failed with InvalidDataException before HTML-comment-only marker detection. Final console suite: **27/27 passed**.

Coverage includes disabled honest defaults, exact null/project scope, no generated-source feedback, stable START links to complete immutable generations, formal/manual protection, direct published edits/deletions, persistent formal/candidate tombstones, full retired-export withdrawal, source changes, original-path consent/revocation, HTTP consent/endpoint/payload bounds/credential filtering, optional evidence candidates and explicit adoption, stale-request revocation, source removal during generation, provider errors/cancellation, real summary-only HTTP compatibility, marker text preservation and idempotence, ordinary prose, malformed/duplicate markers, optimistic conflicts, exact UTF-16 backups/BOM/newlines and missing-file races.
