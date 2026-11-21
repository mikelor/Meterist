<#
.SYNOPSIS
    Unattended daily run: extract every tenant through yesterday, check that no
    seat-priced vendor has missing days, then take each tenant's roster snapshots
    and spend-limit snapshots. Meant for Windows Task Scheduler (see
    Register-DailyExtractionTask.ps1).

.DESCRIPTION
    Writes everything to a dated log file, extract-yyyy-MM-dd.log in -LogDir
    (a second run on the same day appends). Exits 0 only if every extraction
    succeeded, the coverage check found no gaps AND every roster and spend-limit
    snapshot was taken, so Task Scheduler's "Last Run Result" is a real pass/fail.
    The code is a sum of independent failures: 1 = extraction failure, 2 = coverage
    gap, 4 = roster snapshot failure, 8 = spend-limit snapshot failure (so 3 =
    extraction and coverage, 5 = extraction and roster, 12 = roster and spend limits,
    15 = all four).

    The roster and spend-limit snapshots run after extraction whether or not
    extraction failed: they are independent of it, and the vendors keep no history
    of either (who held a seat, and how close each member was to their limit), so a
    day skipped is a day nobody can later reconstruct. A tenant with no credential
    for a vendor is skipped for that vendor, not failed, and one vendor failing
    does not stop the others. A key that the vendor rejects for spend limits (missing
    scope, usage credits off) is a failure, not a skip, so it is noticed; see the user
    guide.

    `audit seats` is not part of the run: drift is expected for a day or two after any
    seat change, and the idle list names people, which should not go into a log.

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

. (Join-Path $PSScriptRoot "ScheduledRunResult.ps1")

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

    Write-Host "`n=== Roster snapshots ===" -ForegroundColor Cyan
    dotnet run --project $cli -- roster snapshot-all @tenantArgs
    $rosterFailed = $LASTEXITCODE -ne 0

    Write-Host "`n=== Spend-limit snapshots ===" -ForegroundColor Cyan
    dotnet run --project $cli -- limits snapshot-all @tenantArgs
    $limitsFailed = $LASTEXITCODE -ne 0

    $result = Get-ScheduledRunResult -ExtractFailed $extractFailed -CoverageFailed $coverageFailed -RosterFailed $rosterFailed -LimitsFailed $limitsFailed
    Write-Host ("`n[{0}] {1} (log: {2})" -f (Get-Date -Format "s"), $result.Summary, $log)
}
finally {
    Stop-Transcript | Out-Null
}

exit $result.Code
