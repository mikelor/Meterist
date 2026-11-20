<#
.SYNOPSIS
    Registers (or replaces) a Windows Task Scheduler job that runs
    RunScheduledExtraction.ps1 once a day.

.DESCRIPTION
    Runs as the current user, only while that user is logged on. That is
    deliberate: credentials are DPAPI-encrypted per user, so the task must run
    as the same account that stored them, and "run whether logged on or not"
    would need the account's password stored in the task. If the machine is
    off at the scheduled time, the task runs at the next opportunity
    (StartWhenAvailable).

    Check the result in Task Scheduler ("Last Run Result": 0 = OK, see
    RunScheduledExtraction.ps1 for the other codes) or in the dated log under
    %LOCALAPPDATA%\Meterist\logs.

.PARAMETER Time
    Daily start time, HH:mm (24h). Defaults to 06:00.

.PARAMETER Remove
    Unregister the task instead.

.EXAMPLE
    ./Register-DailyExtractionTask.ps1 -Time 05:30
#>
param(
    [string]$Time = "06:00",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$taskName = "Meterist daily extraction"

if ($Remove) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed '$taskName'."
    return
}

$script = (Resolve-Path (Join-Path $PSScriptRoot "RunScheduledExtraction.ps1")).Path
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source

$action = New-ScheduledTaskAction -Execute $pwsh `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" `
    -WorkingDirectory (Split-Path $script)
$trigger = New-ScheduledTaskTrigger -Daily -At $Time
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "Registered '$taskName' to run daily at $Time as $env:USERNAME."
Write-Host "Test it now with:  Start-ScheduledTask -TaskName '$taskName'"
