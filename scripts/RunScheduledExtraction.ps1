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

.PARAMETER CostsCrossCheckDays
    How many of the most recent days the ChatGPT Enterprise COSTS cross-check reads each day (1 to 29). The cross-check is
    hundreds of file downloads over a full window and only corroborates the Daily Usage figures, so a daily run needs
    just the last few days. Defaults to 7. 0 reads the whole retention window, as the app does by default. Ignored if the
    environment variable Meterist__ChatGpt__CostsCrossCheck__RecentDays is already set. Days outside the window keep their
    Daily Usage figures but get no fresh COSTS rows in raw storage; see the user guide.

.PARAMETER LogDir
    Where log files go. Defaults to %LOCALAPPDATA%\Meterist\logs.
#>
param(
    [string]$From = "2026-07-01",
    [string[]]$Tenants = @("zelleri", "ecosync"),
    [ValidateRange(0, 29)][int]$CostsCrossCheckDays = 7,
    [string]$LogDir = (Join-Path $env:LOCALAPPDATA "Meterist\logs")
)

. (Join-Path $PSScriptRoot "ScheduledRunResult.ps1")

$ErrorActionPreference = "Continue"
$setRecentDays = $false
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$to = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$log = Join-Path $LogDir ("extract-{0}.log" -f (Get-Date).ToString("yyyy-MM-dd"))
$cli = Join-Path $PSScriptRoot "..\src\Meterist.Cli"

Start-Transcript -Path $log -Append | Out-Null
try {
    Write-Host ("[{0}] Meterist scheduled run: {1} to {2}, tenants: {3}" -f (Get-Date -Format "s"), $From, $to, ($Tenants -join ", "))

    # The ChatGPT COSTS cross-check is hundreds of sequential-looking file downloads over a full window and only corroborates
    # the Daily Usage figures, so the daily run reads just the last few days. An explicit setting in the environment wins.
    if ($CostsCrossCheckDays -gt 0 -and -not $env:Meterist__ChatGpt__CostsCrossCheck__RecentDays) {
        $env:Meterist__ChatGpt__CostsCrossCheck__RecentDays = [string]$CostsCrossCheckDays
        $setRecentDays = $true
        Write-Host ("COSTS cross-check limited to the last {0} day(s) for this run." -f $CostsCrossCheckDays)
    }

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
    # Leave the caller's session as it was found: an environment variable set here would outlive a manual run.
    if ($setRecentDays) { Remove-Item Env:Meterist__ChatGpt__CostsCrossCheck__RecentDays -ErrorAction SilentlyContinue }
    Stop-Transcript | Out-Null
}

exit $result.Code
