<#
.SYNOPSIS
    Unattended daily run: extract every tenant through yesterday, then check
    that no seat-priced vendor has missing days. Meant for Windows Task
    Scheduler (see Register-DailyExtractionTask.ps1).

.DESCRIPTION
    Writes everything to a dated log file, extract-yyyy-MM-dd.log in -LogDir
    (a second run on the same day appends). Exits 0 only if every extraction
    succeeded AND the coverage check found no gaps, so Task Scheduler's
    "Last Run Result" is a real pass/fail: 0 = clean, 1 = extraction failure,
    2 = coverage gap, 3 = both.

    Runs through yesterday, not today: today's data is not complete yet, and a
    run through today would report today as a gap on the vendors that publish
    a day late.

    Why daily matters: ChatGPT Enterprise's cost export keeps only ~29 days,
    so a stretch of missed runs can lose data that can no longer be recovered.

.PARAMETER From
    Start of the window, yyyy-MM-dd. Defaults to 2026-07-01.

.PARAMETER Tenants
    Tenant ids to run. Defaults to the two onboarded tenants.

.PARAMETER LogDir
    Where log files go. Defaults to %LOCALAPPDATA%\Meterist\logs.
#>
param(
    [string]$From = "2026-07-01",
    [string[]]$Tenants = @("zelleri", "ecosync"),
    [string]$LogDir = (Join-Path $env:LOCALAPPDATA "Meterist\logs")
)

$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$to = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$log = Join-Path $LogDir ("extract-{0}.log" -f (Get-Date).ToString("yyyy-MM-dd"))
$cli = Join-Path $PSScriptRoot "..\src\Meterist.Cli"

Start-Transcript -Path $log -Append | Out-Null
try {
    Write-Host ("[{0}] Meterist scheduled run: {1} to {2}, tenants: {3}" -f (Get-Date -Format "s"), $From, $to, ($Tenants -join ", "))

    & (Join-Path $PSScriptRoot "ExtractAllThroughToday.ps1") -From $From -To $to -Tenants $Tenants
    $extractFailed = $LASTEXITCODE -ne 0

    Write-Host "`n=== Coverage check ===" -ForegroundColor Cyan
    $tenantArgs = $Tenants | ForEach-Object { "--tenant"; $_ }
    dotnet run --project $cli -- audit coverage @tenantArgs --from $From --to $to
    $coverageFailed = $LASTEXITCODE -ne 0

    $code = 0
    if ($extractFailed) { $code += 1 }
    if ($coverageFailed) { $code += 2 }

    $summary = switch ($code) {
        0 { "OK: all extractions succeeded and coverage is complete." }
        1 { "FAILED: one or more vendor extractions failed." }
        2 { "FAILED: extraction succeeded but the coverage check found missing days." }
        3 { "FAILED: extraction failures and missing days." }
    }
    Write-Host ("`n[{0}] {1} (log: {2})" -f (Get-Date -Format "s"), $summary, $log)
}
finally {
    Stop-Transcript | Out-Null
}

exit $code
