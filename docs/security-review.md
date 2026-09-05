# Security review

Two passes over everything that touches credentials or leaves the machine. Findings and what was
done about them are recorded here so the next review starts from a known baseline.

## The promise

Nothing leaves this PC. The only outbound connections are to the SQL Servers you registered
yourself. No telemetry, no update check, no error reporting, no cloud anything.

`tests/SqlHelper.App.Tests/NoNetworkEgressTests.cs` asserts this against the source on every
build: no HTTP/socket/mail API may appear anywhere in `src/`, no hard-coded outbound URL may
appear, and the package list is pinned to six local-processing libraries. Adding any of those
fails the build rather than shipping quietly.

## Where secrets live

| What | Where | How it is protected |
|---|---|---|
| SQL login passwords | `%APPDATA%\SqlHelper\registry.sqlhelper.dat` | Each password encrypted with Windows DPAPI (CurrentUser), then the whole file encrypted again the same way |
| Optional master passphrase | never stored | Used only to derive DPAPI entropy (PBKDF2-SHA256, 600k iterations); without it the file will not open even for the same Windows account |
| A decrypted password | process memory, for the duration of one connection | Buffers zeroed with `CryptographicOperations.ZeroMemory` |
| Shared export | wherever you save it | AES-256-GCM under a passphrase you choose (PBKDF2-SHA256, 600k iterations, fresh random salt and nonce per export) |

DPAPI ties the registry to one Windows account on one machine, so a copied `registry.sqlhelper.dat`
is useless elsewhere. That is why sharing uses its own encrypted format instead.

## Where secrets deliberately do *not* appear

Checked by reading every write path:

- **Connection strings** are built inside `ConnectionStringFactory.Build` and handed straight to
  `SqlConnection`. They are never logged, never put in an exception message, never displayed.
  Failures name the client, not the credential.
- **Crash logs** (`%APPDATA%\SqlHelper\logs\crash-*.log`) record the exception and the operator
  name. No connection string reaches them.
- **Backup files** carry a header with client, server, database, environment, object, timestamp
  and checksum — no credentials.
- **The audit log** records who ran what, where, and what happened. It records the statement
  verbatim, which is the point of an audit trail — but a statement can itself carry a secret, so
  `SecretRedactor` replaces the literal after `PASSWORD` / `OLD_PASSWORD` / `SECRET` / `PWD`
  (quoted, `N`-prefixed, or a `0x` hash) before it is written.
- **Excel exports** contain the client name and the query's own result columns. Nothing else.

## Sharing the database list

`Export…` writes one file containing every database *and* its saved password, so the person who
imports it does nothing but pick the file. Because it carries credentials it is never written in
the clear:

- AES-256-GCM, 256-bit key, 96-bit nonce, 128-bit tag — fresh salt and nonce on every export, so
  two exports of the same list share no ciphertext.
- Key from PBKDF2-SHA256, 600,000 iterations. On import the file's own iteration count is honoured
  so older files keep opening, but never below 100,000 — a file claiming a weak count cannot
  weaken the check.
- GCM is authenticated: a file altered by one byte fails to open rather than yielding altered
  connection details. A wrong passphrase and a tampered file give the same message, since the
  cipher genuinely cannot tell them apart.

Send the passphrase by some route other than the file itself and the file is safe to send however
you like.

## Fixed during this review

1. **A client with two databases could receive the wrong password.** Export and import matched a
   database by client name alone, so `Acme / AcmeApp` and `Acme / AcmeReporting` collided: only one
   password survived the export, and on import it was applied to whichever entry matched first.
   Identity is now client + server + database. This also stops an import silently repointing a
   local entry at a different server.
2. **A password typed into a T-SQL statement was written to the audit log in the clear.** Now
   redacted (see `SecretRedactor`).
3. **"Open last backup folder" handed an arbitrary path to the shell**, which would have *run* it
   had it been an executable rather than a folder. It now verifies the path is a directory and
   passes it to Explorer as an argument.
4. **A patch could be applied to a client that changed after the plan was built.** The reviewed
   diff would no longer describe what was live. The apply step now compares the definition against
   the one the plan was built from and refuses that client rather than overwriting someone else's
   change.

## Residual notes

- Anyone who can already run code as your Windows account can read the registry, with or without
  this tool — that is what DPAPI's per-user scope means. A master passphrase raises that bar.
- The exported file is only as strong as the passphrase chosen for it. The minimum is 8
  characters; longer is better, and it must not travel with the file.
