# Migrating Meterist to a new machine

Everything Meterist needs that *isn't* checked into git — the live SQLite
database, raw vendor credentials, generated reports, and (optionally) this
project's Claude Code context — can be packaged into one zip and restored on
a new machine with `scripts/Export-MigrationPackage.ps1` and
`scripts/Import-MigrationPackage.ps1`.

## What gets migrated, and what doesn't

| Item | Migrates? | Notes |
|---|---|---|
| `%LOCALAPPDATA%\Meterist\meterist.db` | Yes, cleanly | Just a data file, no machine binding. |
| `env/` (raw vendor credentials, invoices) | Yes, cleanly | Gitignored on purpose — real API keys. |
| `artifacts/` (generated reports) | Yes, cleanly | Gitignored on purpose — real financial data. |
| `%LOCALAPPDATA%\Meterist\secrets\` and `\keys\` | **No** | DPAPI-encrypted, tied to the current Windows user profile *on this machine* by design (see `src/Meterist.Secrets/DataProtectionSecretStore.cs`'s doc comment) — included in the package anyway as an inert backup, but don't rely on it. |
| Vendor credentials, functionally | Yes, via `credentials set` | Re-provisioned on the new machine from `env/`'s raw files — see below. |
| Claude Code project memory, global settings/MCP config | Yes, cleanly | Optional bundle, on by default. |
| Claude Code session transcript (chat history) | Best-effort | Claude Code's own internal format — not a guaranteed-stable public format. Try `claude --resume` after restoring; no promises. |
| `~/.claude/.credentials.json` (login token) | **Never** | Deliberately excluded. Log in fresh on the new machine instead. |
| Everything else in this repo | Yes, via `git clone` | Source code and docs are already in git — nothing to do. |

## Backup (on the old machine)

```powershell
scripts\Export-MigrationPackage.ps1 -OutputPath E:\Meterist-Migration.zip
```

Point `-OutputPath` directly at your thumbdrive, or somewhere local and copy
it over yourself afterward. The script prints a summary and the resulting
file size when it's done.

Useful switches:
- `-IncludeClaudeCodeContext:$false` — skip the Claude Code bundle (memory,
  settings, session transcript) if you only want the Meterist app data.

## Restore (on the new machine)

1. Install prerequisites: git, the **.NET 10 preview SDK** (matching what
   `dotnet --list-sdks` shows on the old machine — this is a preview SDK, not
   something the script installs for you), and the `gh` CLI if you use the
   PR workflow.
2. Clone the repo and copy the zip off the thumbdrive.
3. Run:

   ```powershell
   scripts\Import-MigrationPackage.ps1 -PackagePath E:\Meterist-Migration.zip
   ```

   This verifies checksums, restores the database/`env/`/`artifacts/` and
   the Claude Code bundle, then prints (but does not run) the
   `credentials set` commands needed to re-provision each tenant's vendor
   credentials from the restored `env/` files.
4. **Review the printed credential commands**, then re-run with
   `-ApplyCredentials` to actually execute them:

   ```powershell
   scripts\Import-MigrationPackage.ps1 -PackagePath E:\Meterist-Migration.zip -ApplyCredentials
   ```

   (Safe to re-run — checksum verification and the restore steps are
   idempotent; pass `-Force` if a file already exists at the destination
   from a prior attempt.)
5. Log into Claude Code fresh (`.credentials.json` is never migrated).
6. If the Claude Code bundle was included, try `claude --resume` from the
   repo root to see if the old session picks back up. If it doesn't, that's
   expected — the project's memory files (which *are* meant to survive this)
   will still be loaded automatically by a fresh session.

## Validation

The import script finishes with `dotnet build`, `dotnet test`, and a
database header/row-count sanity check by default. Skip it with
`-SkipValidation` if you're in a hurry and want to validate manually later.

To confirm credentials actually landed correctly, run a small extraction
against real vendor data once everything's restored:

```powershell
dotnet run --project src\Meterist.Cli -- extract --tenant zelleri --vendor gemini-enterprise --from 2026-07-01 --to 2026-07-07
```

## If something goes wrong

- **Checksum mismatch on import**: the zip was corrupted in transit (common
  with older/flaky thumbdrives). Re-copy it and try again — don't trust a
  partial extract.
- **"already exists" errors on import**: the script won't silently
  overwrite an existing database/`env/`/`artifacts/`/Claude Code files on
  the new machine. Pass `-Force` once you've confirmed there's nothing there
  worth keeping.
- **A vendor credential doesn't work after `-ApplyCredentials`**: double
  check which `env/` file was used for that vendor against the printed
  command — `credentials set` stores whatever raw file content it's given
  verbatim, so a wrong file (e.g. a bare API key where the app expects a
  JSON envelope with an org ID) will store cleanly but fail at extraction
  time, not at `credentials set` time.
