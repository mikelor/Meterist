<#
.SYNOPSIS
    Restores a Meterist migration package (from Export-MigrationPackage.ps1)
    on a new machine: the database, env/, artifacts/, and (dry-run by
    default) the commands to re-provision vendor credentials.

.DESCRIPTION
    Run this AFTER cloning the repo and installing the .NET 10 preview SDK
    on the new machine. By default this only PRINTS the 'credentials set'
    commands it would run -- pass -ApplyCredentials to actually execute
    them, since these are live vendor API keys and the mapping is worth a
    quick eyeball first.

.PARAMETER PackagePath
    Path to the zip produced by Export-MigrationPackage.ps1.

.PARAMETER RepoRoot
    Repo root to restore env/ and artifacts/ into. Defaults to this
    script's parent directory (i.e. run it from a freshly-cloned repo's
    scripts/ folder with no argument needed).

.PARAMETER ApplyCredentials
    Actually run 'credentials set' for each entry in the package's
    restore mapping, instead of just printing the commands.

.PARAMETER Force
    Overwrite an existing database/env/artifacts on this machine without
    prompting. Without this, the script stops rather than clobber
    something already here.

.PARAMETER SkipValidation
    Skip the build/test/database sanity check at the end.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')),

    [switch]$ApplyCredentials,
    [switch]$Force,
    [switch]$SkipValidation,

    # Override for testing against fixture data -- leave unset for a real
    # import, which restores to the real %LOCALAPPDATA%\Meterist.
    [string]$MeteristDataDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PackagePath)) {
    throw "Package not found: '$PackagePath'"
}

Write-Host "Meterist migration import" -ForegroundColor Cyan
Write-Host "Repo root: $RepoRoot"
Write-Host ""

$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Select-String '^10\.')) {
    Write-Host "Warning: no .NET 10 SDK found via 'dotnet --list-sdks' -- 'dotnet build' below will likely fail until it's installed." -ForegroundColor Yellow
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "MeteristMigrationRestore-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Write-Host "Extracting package..."
    Expand-Archive -Path $PackagePath -DestinationPath $staging

    $manifestPath = Join-Path $staging 'manifest.json'
    if (-not (Test-Path $manifestPath)) {
        throw "manifest.json not found in package -- is this a package produced by Export-MigrationPackage.ps1?"
    }
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

    Write-Host "Verifying checksums (exported $($manifest.ExportedAtUtc) from $($manifest.SourceMachine))..."
    $mismatches = @()
    foreach ($entry in $manifest.Checksums) {
        $filePath = Join-Path $staging $entry.RelativePath
        if (-not (Test-Path $filePath)) {
            $mismatches += "$($entry.RelativePath): missing after extraction"
            continue
        }
        $actual = (Get-FileHash $filePath -Algorithm SHA256).Hash
        if ($actual -ne $entry.Sha256) {
            $mismatches += "$($entry.RelativePath): checksum mismatch (thumbdrive transfer may have corrupted it)"
        }
    }
    if ($mismatches.Count -gt 0) {
        $mismatches | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "Checksum verification failed -- re-copy the zip and try again rather than trusting a partial extract."
    }
    Write-Host "  All $($manifest.Checksums.Count) files verified." -ForegroundColor Green

    function Restore-Item {
        param([string]$Source, [string]$Destination, [string]$Label)

        if (-not (Test-Path $Source)) { return }

        if ((Test-Path $Destination) -and -not $Force) {
            throw "$Label already exists at '$Destination'. Pass -Force to overwrite, or move it aside first."
        }
        if (Test-Path $Destination) {
            Remove-Item $Destination -Recurse -Force -Confirm:$false
        }

        $destParent = Split-Path $Destination -Parent
        if ($destParent -and -not (Test-Path $destParent)) {
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null
        }
        Copy-Item $Source $Destination -Recurse
        Write-Host "  Restored $Label -> $Destination"
    }

    $meteristDataDir = if ($MeteristDataDir) {
        $MeteristDataDir
    } else {
        Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Meterist'
    }

    Write-Host "Restoring database..."
    Restore-Item (Join-Path $staging 'database\meterist.db') (Join-Path $meteristDataDir 'meterist.db') 'meterist.db'

    if (Test-Path (Join-Path $staging 'database\secrets')) {
        Write-Host "Restoring encrypted secrets store (almost certainly won't decrypt here -- see script header)..." -ForegroundColor Yellow
        Restore-Item (Join-Path $staging 'database\secrets') (Join-Path $meteristDataDir 'secrets') 'secrets/'
        Restore-Item (Join-Path $staging 'database\keys') (Join-Path $meteristDataDir 'keys') 'keys/'
    }

    Write-Host "Restoring env/ and artifacts/..."
    Restore-Item (Join-Path $staging 'env') (Join-Path $RepoRoot 'env') 'env/'
    Restore-Item (Join-Path $staging 'artifacts') (Join-Path $RepoRoot 'artifacts') 'artifacts/'

    Write-Host ""
    if ($manifest.RestoreCredentials.Count -eq 0) {
        Write-Host "No credential-restore entries in this package's manifest." -ForegroundColor Yellow
    } elseif ($ApplyCredentials) {
        Write-Host "Applying credentials via 'meterist credentials set'..." -ForegroundColor Cyan
        foreach ($entry in $manifest.RestoreCredentials) {
            $fromFile = Join-Path $RepoRoot $entry.File
            Write-Host "  $($entry.TenantId) / $($entry.Vendor)"
            & dotnet run --project (Join-Path $RepoRoot 'src\Meterist.Cli') -- credentials set `
                --tenant $entry.TenantId --vendor $entry.Vendor --from-file $fromFile
        }
    } else {
        Write-Host "Credential-restore commands (dry run -- pass -ApplyCredentials to actually run these):" -ForegroundColor Cyan
        foreach ($entry in $manifest.RestoreCredentials) {
            $fromFile = Join-Path $RepoRoot $entry.File
            Write-Host "  dotnet run --project src\Meterist.Cli -- credentials set --tenant $($entry.TenantId) --vendor $($entry.Vendor) --from-file `"$fromFile`""
        }
    }

    if (-not $SkipValidation) {
        Write-Host ""
        Write-Host "Validating..." -ForegroundColor Cyan

        Write-Host "  dotnet build..."
        & dotnet build (Join-Path $RepoRoot 'Meterist.slnx') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

        Write-Host "  dotnet test..."
        & dotnet test (Join-Path $RepoRoot 'Meterist.slnx') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }

        $restoredDb = Join-Path $meteristDataDir 'meterist.db'
        $header = New-Object byte[] 16
        $stream = [System.IO.File]::OpenRead($restoredDb)
        try { [void]$stream.Read($header, 0, 16) } finally { $stream.Close() }
        $magic = [System.Text.Encoding]::ASCII.GetString($header, 0, 15)
        if ($magic -ne 'SQLite format 3') {
            throw "Restored database doesn't look like a valid SQLite file (bad header)."
        }
        Write-Host "  Database header OK." -ForegroundColor Green

        if (Get-Command sqlite3 -ErrorAction SilentlyContinue) {
            Write-Host "  Row counts by tenant/vendor:"
            & sqlite3 $restoredDb "SELECT TenantId, VendorId, COUNT(*), MAX(Date) FROM DailySpendRecords GROUP BY TenantId, VendorId;"
        } else {
            Write-Host "  (sqlite3 not on PATH -- skipping row-count check; header check above already confirms the file is intact)" -ForegroundColor Yellow
        }

        Write-Host ""
        Write-Host "Validation passed." -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Done." -ForegroundColor Green
    if (-not $ApplyCredentials -and $manifest.RestoreCredentials.Count -gt 0) {
        Write-Host "Re-run with -ApplyCredentials once you've reviewed the mapping above." -ForegroundColor Yellow
    }
}
finally {
    Remove-Item $staging -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}
