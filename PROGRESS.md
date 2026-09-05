# SqlHelper — build status

A local-only Windows tool to run queries and deploy programmable objects across many
identical client databases. See `docs/rnd-report.html` for the R&D that led here.

## Stack
- .NET 10, `net10.0-windows`
- `SqlHelper.Core` — all SQL logic, UI-agnostic, unit + integration tested
- `SqlHelper.App` — WPF (MVVM, CommunityToolkit.Mvvm)
- `SqlHelper.Core.Tests` — xUnit unit tests, no database (177 tests)
- `SqlHelper.Integration.Tests` — xUnit, runs against a real SQL Server (21 tests, all passing
  against SQL Server 2022 Developer Edition)

## Status: functionally complete, all five screens working end to end

| Area | State |
|---|---|
| `Credentials` | DPAPI CurrentUser + optional PBKDF2 passphrase entropy |
| `Registry` | Encrypted file, per-secret + whole-file, atomic save, validation, clipboard/Excel import |
| `Guard` | Token-stream `GO` splitter; ScriptDom AST allowlist for read-only enforcement |
| `Execution` | Fan-out runner (bounded concurrency, retry, cancel), dry-run rowcounts, connection probe |
| `Scripting` | Object definition + signature capture from parsed T-SQL (not `sys.parameters` — it doesn't carry T-SQL defaults), normalizer (semantic/exact hashing, USE/SET stripping) |
| `Backup` | Timestamped folder, per-client capture, auto-generated rollback scripts, JSON manifest |
| `Patching` | Cohort clustering, anchor (exact) / fuzzy (diff-match-patch) tiers, validation gate, signature-diff warnings |
| `Auditing` | Append-only monthly JSONL, serialized writes |
| `Export` | Excel (ClosedXML) — per-client sheets or one combined sheet |
| `Orchestration` | `DeploymentEngine` — the façade every screen calls; ties everything above together, proven end-to-end |
| `App` (WPF) | Unlock flow, Registry / Select / Update-Delete / Deploy / Patch screens, shared target picker, diff viewer |
| `install.cmd` / `install.ps1` | One-script installer: checks/installs .NET SDK, builds, publishes self-contained single-file exe, Start Menu shortcut. Idempotent. |

## Verified

- Full unit + integration suite green (198 tests).
- End-to-end integration test: capture live definitions from two real databases with genuine
  drift, cluster into cohorts, author a patch from a before/after pair, plan (one clean exact
  match, one fuzzy match), apply, verify the live database, confirm the rollback script contains
  the drifted client's original customisation.
- App builds and runs: unlock → main shell → all five screens navigate and render without
  exceptions; text entry, patch authoring and the diff viewer confirmed working against real
  input.
- `install.cmd` run end-to-end on a clean state: SDK detected, build succeeded, self-contained
  publish succeeded (~40s), Start Menu shortcut created.

## Decisions
- Pure WPF UI (no WebView2) — chosen by the user for simplicity/robustness over the Monaco-based
  hybrid option.
- Read-only enforcement is an allowlist, not a keyword blocklist.
- No atomic cross-server transaction — per-DB transactions + rollback scripts + re-run-failed.
- Patch signatures are read by parsing the module definition, not `sys.parameters` — SQL Server
  does not expose T-SQL default-value info in that view.
- `.ps1`/`.cmd` files are kept pure ASCII — Windows PowerShell 5.1 has been observed to
  mis-tokenize UTF-8-without-BOM files containing characters like em dashes, corrupting later
  parsing in ways that don't point at the real line.

## Not yet built (candidates for a next pass)
- In-app editor for the "manual required" patch tier (currently: the tool tells you which client
  and shows the diff; the hand edit itself happens outside the tool).
- Cross-client table/column schema-drift report (flagged in the R&D report as a later feature).
- `.xlsx` file import for the registry (clipboard TSV paste — the primary path — already works).
