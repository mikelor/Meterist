<#
.SYNOPSIS
    Read-only live probe of the Claude Enterprise analytics endpoints (spike #21): the legacy
    user_cost_report against the new /analytics/cost and /analytics/usage (beta header), for one
    closed window. Prints and writes dates, dollars and counts only, never a name or email.
    Not part of the shipped extractor.

.EXAMPLE
    ./scripts/SpikeClaudeAnalytics.ps1 -Tenant <tenant> -Start 2026-10-01 -EndExclusive 2026-11-01 -Out claude-probe.json
    Reads the Analytics API key from env/<tenant>-claude-enterprise.txt (never pass it on the command line).
#>
param([string]$Tenant, [string]$Start = '2026-10-01', [string]$EndExclusive = '2026-11-01', [string]$Out)

$key = (Get-Content "D:\repos\meterist\env\$Tenant-claude-enterprise.txt" -Raw).Trim()
$base = 'https://api.anthropic.com/v1/organizations/analytics'
$beta = 'analytics-usage-cost-2026-08-24'

function Call([string]$path, [string]$query, [bool]$useBeta) {
    $h = @{ 'x-api-key' = $key; 'anthropic-version' = '2023-06-01' }
    if ($useBeta) { $h['anthropic-beta'] = $beta }
    $r = Invoke-WebRequest -Uri "$base/${path}?$query" -Headers $h -Method Get -SkipHttpErrorCheck
    $json = $null
    try { $json = $r.Content | ConvertFrom-Json -Depth 20 } catch { }
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json; Raw = $r.Content }
}

function GetAll([string]$path, [string]$query, [bool]$useBeta) {
    $rows = @(); $page = $null; $n = 0; $first = $null; $refreshed = $null
    do {
        $q = if ($page) { "$query&page=$([uri]::EscapeDataString($page))" } else { $query }
        $r = Call $path $q $useBeta
        if ($r.Status -ne 200) { return [pscustomobject]@{ Status = $r.Status; Rows = @(); Raw = $r.Raw; Refreshed = $null } }
        $rows += $r.Json.data; $n++
        $refreshed = $r.Json.data_refreshed_at
        $page = if ($r.Json.has_more) { $r.Json.next_page } else { $null }
    } while ($page -and $n -lt 100)
    [pscustomobject]@{ Status = 200; Rows = $rows; Raw = $null; Refreshed = $refreshed }
}

$s = "$($Start)T00:00:00Z"; $e = "$($EndExclusive)T00:00:00Z"
$sq = [uri]::EscapeDataString($s); $eq = [uri]::EscapeDataString($e)
$result = [ordered]@{ tenant = $Tenant; window = "$Start..$EndExclusive" }

# 1. Does the beta header work with the existing key, and what do errors look like without it?
$noBeta = Call 'cost' "starting_at=$sq&ending_at=$eq&time_bucket_width=1d&limit=5" $false
$withBeta = Call 'cost' "starting_at=$sq&ending_at=$eq&time_bucket_width=1d&limit=5" $true
$result.newCost_withoutBetaHeader_status = $noBeta.Status
$result.newCost_withoutBetaHeader_error = if ($noBeta.Status -ne 200) { ($noBeta.Raw -replace '\s+', ' ').Substring(0, [Math]::Min(200, $noBeta.Raw.Length)) } else { $null }
$result.newCost_withBetaHeader_status = $withBeta.Status
$result.newCost_withBetaHeader_error = if ($withBeta.Status -ne 200) { ($withBeta.Raw -replace '\s+', ' ').Substring(0, [Math]::Min(300, $withBeta.Raw.Length)) } else { $null }

# 2. ending_at required with a bucket width?
$noEnd = Call 'cost' "starting_at=$sq&time_bucket_width=1d&limit=5" $true
$result.newCost_bucketWithoutEndingAt_status = $noEnd.Status
$result.newCost_bucketWithoutEndingAt_error = if ($noEnd.Status -ne 200) { ($noEnd.Raw -replace '\s+', ' ').Substring(0, [Math]::Min(200, $noEnd.Raw.Length)) } else { 'accepted' }

# 3. Legacy per-user daily cost (cents -> dollars)
$legacy = GetAll 'user_cost_report' "starting_at=$sq&ending_at=$eq&bucket_width=1d&limit=1000" $false
$result.legacy_status = $legacy.Status
$legacyDaily = @{}
if ($legacy.Status -eq 200) {
    foreach ($row in $legacy.Rows) {
        $d = ([datetime]$row.starting_at).ToUniversalTime().ToString('yyyy-MM-dd')
        $legacyDaily[$d] = [double]$legacyDaily[$d] + [double]$row.amount / 100.0
    }
    $result.legacy_rows = $legacy.Rows.Count
    $result.legacy_total_usd = [math]::Round(($legacyDaily.Values | Measure-Object -Sum).Sum, 2)
    $result.legacy_days_with_rows = $legacyDaily.Count
    $result.legacy_distinct_users = @($legacy.Rows | ForEach-Object { $_.actor.user_id } | Sort-Object -Unique).Count
}
else { $result.legacy_error = ($legacy.Raw -replace '\s+', ' ').Substring(0, [Math]::Min(300, $legacy.Raw.Length)) }

# 4. New cost endpoint: whole org per day, and per actor per day
$newOrg = GetAll 'cost' "starting_at=$sq&ending_at=$eq&time_bucket_width=1d&limit=1000" $true
$result.newCostOrg_status = $newOrg.Status
$newOrgDaily = @{}
if ($newOrg.Status -eq 200) {
    foreach ($row in $newOrg.Rows) {
        $d = ([datetime]$row.starting_at).ToUniversalTime().ToString('yyyy-MM-dd')
        $newOrgDaily[$d] = [double]$newOrgDaily[$d] + [double]$row.cost.amount / 100.0
    }
    $result.newCostOrg_rows = $newOrg.Rows.Count
    $result.newCostOrg_total_usd = [math]::Round(($newOrgDaily.Values | Measure-Object -Sum).Sum, 2)
    $result.newCostOrg_days_with_rows = $newOrgDaily.Count
    $result.newCostOrg_data_refreshed_at = $newOrg.Refreshed
    $result.newCostOrg_row_keys = ($newOrg.Rows[0].PSObject.Properties.Name -join ',')
    if ($newOrg.Rows[0].cost) { $result.newCost_cost_keys = ($newOrg.Rows[0].cost.PSObject.Properties.Name -join ',') }
}

$newActor = GetAll 'cost' "starting_at=$sq&ending_at=$eq&time_bucket_width=1d&group_by%5B%5D=actor&actor_types%5B%5D=user_actor&limit=1000" $true
$result.newCostActor_status = $newActor.Status
$newActorDaily = @{}
if ($newActor.Status -eq 200) {
    foreach ($row in $newActor.Rows) {
        $d = ([datetime]$row.starting_at).ToUniversalTime().ToString('yyyy-MM-dd')
        $newActorDaily[$d] = [double]$newActorDaily[$d] + [double]$row.cost.amount / 100.0
    }
    $result.newCostActor_rows = $newActor.Rows.Count
    $result.newCostActor_total_usd = [math]::Round(($newActorDaily.Values | Measure-Object -Sum).Sum, 2)
    $result.newCostActor_distinct_users = @($newActor.Rows | ForEach-Object { $_.group.actor.user_id } | Where-Object { $_ } | Sort-Object -Unique).Count
    if ($newActor.Rows.Count) { $result.newCostActor_actor_keys = if ($newActor.Rows[0].group.actor) { ($newActor.Rows[0].group.actor.PSObject.Properties.Name -join ',') } else { '(null actor)' } }
}

# 5. Usage endpoint: requests per day, and does it cover days where cost is zero (seat-included usage)?
$usage = GetAll 'usage' "starting_at=$sq&ending_at=$eq&time_bucket_width=1d&limit=1000" $true
$result.usageOrg_status = $usage.Status
$usageDaily = @{}
if ($usage.Status -eq 200) {
    foreach ($row in $usage.Rows) {
        $d = ([datetime]$row.starting_at).ToUniversalTime().ToString('yyyy-MM-dd')
        $usageDaily[$d] = [double]$usageDaily[$d] + [double]$row.usage.requests
    }
    $result.usageOrg_rows = $usage.Rows.Count
    $result.usageOrg_total_requests = ($usageDaily.Values | Measure-Object -Sum).Sum
    $result.usageOrg_days_with_rows = $usageDaily.Count
    $result.usageOrg_total_tokens = ($usage.Rows | ForEach-Object { [double]$_.usage.total_tokens } | Measure-Object -Sum).Sum
    $result.usageOrg_usage_keys = ($usage.Rows[0].usage.PSObject.Properties.Name -join ',')
    $result.usageOrg_data_refreshed_at = $usage.Refreshed
}
else { $result.usageOrg_error = ($usage.Raw -replace '\s+', ' ').Substring(0, [Math]::Min(300, $usage.Raw.Length)) }

# 6. Day-by-day comparison table (dates and dollars only)
$days = New-Object System.Collections.Generic.SortedSet[string]
foreach ($k in @($legacyDaily.Keys) + @($newOrgDaily.Keys) + @($usageDaily.Keys)) { [void]$days.Add($k) }
$table = foreach ($d in $days) {
    [pscustomobject]@{
        day = $d
        legacy_usd = [math]::Round([double]$legacyDaily[$d], 2)
        newOrg_usd = [math]::Round([double]$newOrgDaily[$d], 2)
        newActor_usd = [math]::Round([double]$newActorDaily[$d], 2)
        requests = [double]$usageDaily[$d]
    }
}
$result.days = @($table)
$result | ConvertTo-Json -Depth 6 | Set-Content $Out
"done: $Tenant legacy=$($result.legacy_status) newCost=$($result.newCostOrg_status) usage=$($result.usageOrg_status)"
