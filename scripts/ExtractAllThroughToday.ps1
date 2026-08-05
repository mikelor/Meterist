<#
.SYNOPSIS
    Re-runs extraction for all four vendors, both tenants, catching each one
    up from its established start date through -To (defaults to today).
    Extraction is idempotent (upsert by tenant/vendor/date natural key), so
    it's safe to re-run this repeatedly as the reporting window advances.

.PARAMETER To
    End date for every extraction, yyyy-MM-dd. Defaults to today.

.NOTES
    All four vendors now run from Jul 1 for both tenants. zelleri's ChatGPT
    Enterprise previously started 2026-07-13 because no VendorRateConfig row
    covered dates before its then-current rate's effective date — backfilled
    2026-08-03 with the real prior contract (100 seats @ $396/seat/yr,
    effective 2025-07-14, invoice-confirmed), which now covers Jul 1 onward
    and auto-closed cleanly at the real renewal boundary (2026-07-13/14).
    ecosync's ChatGPT Enterprise was never actually blocked — its rate
    already covered back to 2026-01-01 — Jul 14 was just an overly
    conservative starting point on this script's part.
#>
param(
    [string]$To = (Get-Date).ToString("yyyy-MM-dd")
)

$ErrorActionPreference = "Stop"

function Invoke-Extract {
    param([string]$Tenant, [string]$Vendor, [string]$From)

    Write-Host "`n=== $Tenant / $Vendor : $From to $To ===" -ForegroundColor Cyan
    dotnet run --project ../src/Meterist.Cli -- extract --tenant $Tenant --from $From --to $To --vendor $Vendor
}

Invoke-Extract -Tenant zelleri -Vendor chatgpt-enterprise  -From 2026-07-01
Invoke-Extract -Tenant zelleri -Vendor claude-enterprise   -From 2026-07-01
Invoke-Extract -Tenant zelleri -Vendor gemini-enterprise   -From 2026-07-01
Invoke-Extract -Tenant zelleri -Vendor claude-api-platform -From 2026-07-01

Invoke-Extract -Tenant ecosync -Vendor chatgpt-enterprise  -From 2026-07-01
Invoke-Extract -Tenant ecosync -Vendor claude-enterprise   -From 2026-07-01
Invoke-Extract -Tenant ecosync -Vendor gemini-enterprise   -From 2026-07-01
Invoke-Extract -Tenant ecosync -Vendor claude-api-platform -From 2026-07-01
