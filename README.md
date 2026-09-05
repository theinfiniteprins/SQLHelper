# SqlHelper

A local-only Windows tool for running queries and deploying stored procedures / functions across
many client databases with the same schema — built so the annoying part of a hotfix ("now go run
this on all 30 client DBs") stops being manual.

Everything runs on your own PC. The only network traffic is to the SQL Servers you register —
no telemetry, no auto-update, no cloud dependency.

## Install

```
install.cmd
```

Double-click it (or run from a terminal). It's safe to run again any time — it checks the .NET
SDK, rebuilds, republishes, and refreshes the Start Menu shortcut. First run installs the .NET
SDK via `winget` if it isn't already present. See [install.ps1](install.ps1) for exactly what it
does.

## What it does

| Screen | Purpose |
|---|---|
| **Databases** | The connection registry — server, database, auth, environment. Paste a range straight from Excel or add rows by hand. Passwords are encrypted with Windows DPAPI, per Windows account and machine. |
| **Select Query** | Read-only SELECT across any subset of databases, client-wise results, export to Excel. Enforced read-only three ways: an AST allowlist (not a keyword blocklist), a transaction that's always rolled back, and (recommended) a reader-only SQL login. |
| **Update / Delete** | Any script, with a dry run (runs inside a transaction, shows row counts, rolls back) before committing for real. Production targets require typing `DEPLOY` to confirm. |
| **Deploy Object** | Full replace of a stored procedure / function / view / trigger. Backs up every target's current definition first (with a generated rollback script), applies inside a transaction, verifies, and writes an audit manifest. |
| **Patch Divergent Copies** | For when 30 clients don't all have the same copy of a procedure. Clusters clients into their real distinct versions, then applies a small change — authored from a before/after reference pair — against whatever each client actually has: an exact line match where possible, a tolerant (diff-match-patch) match where the surroundings drifted, and a clear flag for a manual edit otherwise. Every result is shown as a diff before it can be approved. |

## Project layout

```
src/SqlHelper.Core   All SQL logic — connection registry, execution, T-SQL parsing/guarding,
                      object scripting, backup/rollback, the patch engine, auditing. No UI
                      dependency; this is what's actually tested.
src/SqlHelper.App    WPF (MVVM, CommunityToolkit.Mvvm). Thin — screens call into Core's
                      DeploymentEngine and render the result.
tests/SqlHelper.Core.Tests          xUnit, no database required.
tests/SqlHelper.Integration.Tests   xUnit, runs against a real SQL Server (defaults to
                                     `localhost` with Windows auth; override with the
                                     SQLHELPER_TEST_SERVER environment variable). Creates and
                                     drops its own throwaway databases.
docs/                 The original R&D report this project was built from.
```

Run the tests with:

```
dotnet test tests/SqlHelper.Core.Tests
dotnet test tests/SqlHelper.Integration.Tests
```

## Data on disk

Everything lives under `%APPDATA%\SqlHelper`:

- `registry.sqlhelper.dat` — the encrypted connection registry
- `audit\yyyy-MM.jsonl` — append-only log of every operation (who, what, where, when)
- `DeploymentBackups\` (default) — one timestamped folder per deployment, with each client's
  prior definition, a per-client rollback script, and a manifest

## Known limits

- No single atomic transaction across every target server — each database gets its own
  transaction, with a generated rollback script and a "re-run failed only" path instead.
- A target whose code has genuinely diverged at the exact point being changed needs a manual
  edit; the tool narrows that down to one client and one region rather than guessing.
- The Update/Delete screen can run anything; the discipline of dry-run-first and typed
  production confirmation is the safety net, not a syntax restriction like the Select screen has.

See [PROGRESS.md](PROGRESS.md) for build status and design decisions.
