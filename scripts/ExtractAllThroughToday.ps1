<#
.SYNOPSIS
    Re-runs extraction for every tenant, catching each one up from -From
    through -To (defaults to today). Extraction is idempotent (upsert by
    tenant/vendor/date natural key), so it's safe to re-run this repeatedly
    as the reporting window advances.

.DESCRIPTION
    Each tenant is extracted with --configured-only, so a tenant that uses
    only some of the vendors (it has credentials for them alone) runs just
    those and shows the others as Skipped, rather than failing on the rest.
    The script exits 1 if any vendor extraction failed for any tenant, so a
    scheduled run can tell. See RunScheduledExtraction.ps1 for the unattended
    wrapper (dated log file plus coverage check).

.PARAMETER To
    End date for every extraction, yyyy-MM-dd. Defaults to today.

.PARAMETER From
    Start date for every extraction, yyyy-MM-dd. Defaults to 2026-07-01.

.PARAMETER Tenants
    Tenant ids to extract. Defaults to the two onboarded tenants. Add a new
    tenant here (or pass it) once its credentials are stored.

.NOTES
    Run from the scripts folder or anywhere: paths resolve from the script's
    own location.
#>
param(
    [string]$To = (Get-Date).ToString("yyyy-MM-dd"),
    [string]$From = "2026-07-01",
    [string[]]$Tenants = @("zelleri", "ecosync")
)

$ErrorActionPreference = "Stop"
$cli = Join-Path $PSScriptRoot "..\src\Meterist.Cli"
$anyFailed = $false

foreach ($tenant in $Tenants) {
    Write-Host "`n=== $tenant : $From to $To ===" -ForegroundColor Cyan
    dotnet run --project $cli -- extract --tenant $tenant --from $From --to $To --configured-only
    if ($LASTEXITCODE -ne 0) {
        $anyFailed = $true
        Write-Host "Extraction reported failures for $tenant (exit code $LASTEXITCODE)." -ForegroundColor Red
    }
}

if ($anyFailed) {
    exit 1
}
