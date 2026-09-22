<#
.SYNOPSIS
    Packages everything Meterist needs that isn't in git -- the SQLite
    database, the raw vendor-credential materials under env/, and any
    generated reports under artifacts/ -- into one zip for moving to a new
    computer.

.DESCRIPTION
    Run this on the OLD machine, from the repo root or scripts/. The
    resulting zip also includes the current DPAPI-encrypted secrets store
    (%LOCALAPPDATA%\Meterist\secrets and \keys) purely as an inert backup --
    see the NOTES below. It will NOT decrypt on a different machine by
    design (DataProtectionSecretStore.cs's own doc comment says so
    explicitly). The real credential-recovery path is env/'s raw files,
    replayed via 'credentials set' on the new machine -- Import-MigrationPackage.ps1
    prints (or, with -ApplyCredentials, runs) those commands for you.

.PARAMETER OutputPath
    Where to write the zip. Defaults to a timestamped file in the current
    directory. Point this directly at your thumbdrive if you want
    (e.g. -OutputPath E:\Meterist-Migration.zip).

.PARAMETER IncludeClaudeCodeContext
    Also bundle this project's Claude Code memory, this session's raw
    transcript (best-effort -- Claude Code's own internal format, not
    guaranteed stable across versions), and global settings.json/mcp.json.
    Deliberately excludes ~/.claude/.credentials.json (your login token) --
    log in fresh on the new machine instead. Default true.

.NOTES
    Why the encrypted secrets store is included anyway: it's already
    ciphertext, so bundling it costs nothing and is a harmless fallback in
    the rare case the new machine can somehow still decrypt it (e.g. a
    domain-roamed profile). Don't rely on it -- rely on env/ + credentials set.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Get-Location) "Meterist-Migration-$(Get-Date -Format 'yyyyMMdd-HHmmss').zip"),

    # Overrides below are for testing the packaging logic against fixture
    # data -- leave them unset for a real export, which uses the real
    # %LOCALAPPDATA%\Meterist paths and this repo's real env/artifacts.
    [string]$MeteristDataDir,
    [string]$EnvDir,
    [string]$ArtifactsDir,

    [bool]$IncludeClaudeCodeContext = $true,
    [string]$ClaudeHomeDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $MeteristDataDir) {
    $MeteristDataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Meterist'
}
$dbPath = Join-Path $MeteristDataDir 'meterist.db'
$secretsDir = Join-Path $MeteristDataDir 'secrets'
$keysDir = Join-Path $MeteristDataDir 'keys'
$envDir = if ($EnvDir) { $EnvDir } else { Join-Path $repoRoot 'env' }
$artifactsDir = if ($ArtifactsDir) { $ArtifactsDir } else { Join-Path $repoRoot 'artifacts' }
if (-not $ClaudeHomeDir) { $ClaudeHomeDir = Join-Path $HOME '.claude' }
# Claude Code derives a project folder name from the repo's absolute path by
# replacing ':' and '\' with '-' -- e.g. C:\Users\me\repo -> C--Users-me-repo.
$claudeProjectSlug = ($repoRoot.Path -replace '[:\\]', '-')
$claudeProjectDir = Join-Path $ClaudeHomeDir "projects\$claudeProjectSlug"

Write-Host "Meterist migration export" -ForegroundColor Cyan
Write-Host "Repo root: $repoRoot"
Write-Host ""

if (-not (Test-Path $dbPath)) {
    throw "Database not found at '$dbPath'. Nothing to export -- has Meterist ever been run on this machine?"
}
if (-not (Test-Path $envDir)) {
    throw "'$envDir' not found. This holds the raw vendor credentials env/ is supposed to contain -- without it, credentials can't be restored on the new machine short of pulling fresh keys from each vendor console."
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "MeteristMigrationStaging-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $staging | Out-Null

# The known-correct file for each (tenant, vendor) pair, resolved by
# inspecting each candidate file's actual shape (not guessed from naming) --
# see the conversation history / commit message for how each was confirmed.
# A tenant is skipped automatically below if it has no env/ files at all.
$credentialMap = @(
    @{ Vendor = 'chatgpt-enterprise'; FilePattern = '{0}-chatgpt-credential.json' }
    @{ Vendor = 'claude-enterprise'; FilePattern = '{0}-claude-enterprise.txt' }
    @{ Vendor = 'claude-api-platform'; FilePattern = '{0}-claude-platform.txt' }
    @{ Vendor = 'gemini-enterprise'; FilePattern = '{0}-gemini-credentials.json' }
)

try {
    Write-Host "Copying database..."
    New-Item -ItemType Directory -Path (Join-Path $staging 'database') | Out-Null
    Copy-Item $dbPath (Join-Path $staging 'database\meterist.db')

    if (Test-Path $secretsDir) {
        Write-Host "Copying encrypted secrets store (inert backup -- see NOTES)..."
        Copy-Item $secretsDir (Join-Path $staging 'database\secrets') -Recurse
    }
    if (Test-Path $keysDir) {
        Copy-Item $keysDir (Join-Path $staging 'database\keys') -Recurse
    }

    Write-Host "Copying env/ (raw vendor credentials + invoices)..."
    Copy-Item $envDir (Join-Path $staging 'env') -Recurse

    if (Test-Path $artifactsDir) {
        Write-Host "Copying artifacts/ (generated reports)..."
        Copy-Item $artifactsDir (Join-Path $staging 'artifacts') -Recurse
    } else {
        Write-Host "No artifacts/ folder found -- skipping (nothing generated yet)." -ForegroundColor Yellow
    }

    if ($IncludeClaudeCodeContext) {
        Write-Host "Copying Claude Code context (memory, global settings, session transcript)..."
        New-Item -ItemType Directory -Path (Join-Path $staging 'claude-code') | Out-Null

        $projectSettingsLocal = Join-Path $repoRoot '.claude\settings.local.json'
        if (Test-Path $projectSettingsLocal) {
            Copy-Item $projectSettingsLocal (Join-Path $staging 'claude-code\project-settings.local.json')
        }

        $globalSettings = Join-Path $ClaudeHomeDir 'settings.json'
        if (Test-Path $globalSettings) {
            Copy-Item $globalSettings (Join-Path $staging 'claude-code\settings.json')
        }
        $globalMcp = Join-Path $ClaudeHomeDir 'mcp.json'
        if (Test-Path $globalMcp) {
            Copy-Item $globalMcp (Join-Path $staging 'claude-code\mcp.json')
        }

        $memoryDir = Join-Path $claudeProjectDir 'memory'
        if ((Test-Path $memoryDir) -and (Get-ChildItem $memoryDir -ErrorAction SilentlyContinue)) {
            Copy-Item $memoryDir (Join-Path $staging 'claude-code\memory') -Recurse
        } else {
            Write-Host "  (no project memory files found -- skipping)" -ForegroundColor Yellow
        }

        # Best-effort: Claude Code's own session storage, not a stable public
        # format. Copied as-is; whether a fresh install on the new machine can
        # resume from it depends on the Claude Code version there.
        if (Test-Path $claudeProjectDir) {
            $transcripts = Get-ChildItem $claudeProjectDir -Filter '*.jsonl' -ErrorAction SilentlyContinue
            if ($transcripts) {
                New-Item -ItemType Directory -Path (Join-Path $staging 'claude-code\sessions') | Out-Null
                foreach ($t in $transcripts) {
                    Copy-Item $t.FullName (Join-Path $staging "claude-code\sessions\$($t.Name)")
                }
            }
        }
        # Deliberately NOT copied: ~/.claude/.credentials.json (your login
        # token) -- log in fresh on the new machine instead.
    }

    # Discover which tenants actually have credential files, rather than
    # hardcoding zelleri/ecosync -- keeps this working if a tenant is added
    # or removed later.
    $tenantIds = Get-ChildItem $envDir -Filter '*-chatgpt-credential.json' |
        ForEach-Object { $_.Name -replace '-chatgpt-credential\.json$', '' }

    $restoreEntries = foreach ($tenantId in $tenantIds) {
        foreach ($entry in $credentialMap) {
            $fileName = $entry.FilePattern -f $tenantId
            $relativePath = "env/$fileName"
            if (Test-Path (Join-Path $envDir $fileName)) {
                [pscustomobject]@{
                    TenantId = $tenantId
                    Vendor   = $entry.Vendor
                    File     = $relativePath
                }
            } else {
                Write-Host "  (no $fileName found -- skipping $tenantId/$($entry.Vendor))" -ForegroundColor Yellow
            }
        }
    }

    Write-Host "Computing checksums..."
    $checksums = Get-ChildItem $staging -Recurse -File | ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
            Sha256       = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
    }

    $manifest = [pscustomobject]@{
        ExportedAtUtc   = (Get-Date).ToUniversalTime().ToString('o')
        SourceMachine   = $env:COMPUTERNAME
        RestoreCredentials = $restoreEntries
        Checksums       = $checksums
        Notes = @(
            "database/meterist.db is the live SQLite store -- restores cleanly, no machine binding."
            "database/secrets and database/keys are DPAPI-protected and will almost certainly NOT decrypt on a different machine/profile -- do not rely on them."
            "env/ holds the raw credential materials used to re-run 'credentials set' on the new machine -- see RestoreCredentials above for the exact tenant/vendor/file mapping."
            "claude-code/ (if present) holds this project's Claude Code memory, global settings.json/mcp.json, and a best-effort copy of session transcripts. .credentials.json (the login token) is never included -- log in fresh on the new machine."
        )
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $staging 'manifest.json') -Encoding utf8

    Write-Host "Compressing to '$OutputPath'..."
    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Confirm:$false }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $OutputPath

    Write-Host ""
    Write-Host "Done: $OutputPath" -ForegroundColor Green
    Write-Host "Size: $([math]::Round((Get-Item $OutputPath).Length / 1MB, 1)) MB"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Copy this zip to your thumbdrive."
    Write-Host "  2. On the new machine: git clone the repo, install the .NET 10 preview SDK, then run"
    Write-Host "     scripts\Import-MigrationPackage.ps1 -PackagePath <path-to-zip>"
}
finally {
    Remove-Item $staging -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}
