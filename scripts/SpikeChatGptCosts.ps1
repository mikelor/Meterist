<#
.SYNOPSIS
    Diagnostic-only spike script for the ChatGPT Enterprise COSTS compliance log export.
    Not part of the shipped extractor — just proves out the real response shape before
    ChatGptEnterpriseSpendExtractor is written, same role BigQuery diagnostic queries
    played for the Gemini adapter.

.PARAMETER OrganizationId
    API Platform Organization ID (org-...), found in ChatGPT workspace Settings ->
    General -> "Organization ID". NOT the Workspace ID.

.PARAMETER AdminKeyFile
    Path to a local text file containing the raw Admin API key (Costs: Read scope).
    Never pass the key on the command line or paste it into chat.

.PARAMETER AfterDays
    How many days back to start the query window. Defaults to 29 to stay inside the
    30-day compliance log retention window.
#>
param(
    [string]$OrganizationId = "org-QfdtNaipyr41iIOkKRw3fa7y",
    [string]$AdminKeyFile = "..\env\zelleri-chatgpt-admin-key.txt",
    [int]$AfterDays = 29
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $AdminKeyFile)) {
    throw "Admin key file not found: $AdminKeyFile"
}
$adminKey = (Get-Content $AdminKeyFile -Raw).Trim()

$after = (Get-Date).ToUniversalTime().AddDays(-$AfterDays).ToString("yyyy-MM-ddTHH:mm:ssZ")

Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.DefaultRequestHeaders.Authorization =
    New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $adminKey)

$listUrl = "https://api.chatgpt.com/v1/compliance/organizations/$OrganizationId/logs?event_type=COSTS&after=$after&limit=10"
Write-Host "Listing COSTS files:"
Write-Host "  $listUrl"

$listResp = $client.GetAsync($listUrl).GetAwaiter().GetResult()
$listBody = $listResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()

if ([int]$listResp.StatusCode -ge 300) {
    Write-Host "Non-success status: $([int]$listResp.StatusCode)"
    Write-Host $listBody
    exit 1
}

$list = $listBody | ConvertFrom-Json
Write-Host ""
Write-Host "has_more:       $($list.has_more)"
Write-Host "last_end_time:  $($list.last_end_time)"
Write-Host "file count:     $($list.data.Count)"
Write-Host ""
$list.data | Select-Object id, event_type, end_time, file_name, file_size | Format-Table -AutoSize

if ($list.data.Count -eq 0) {
    Write-Host "No COSTS files in this window — nothing to download. Try a larger -AfterDays, or confirm the org actually has ChatGPT Enterprise usage in range."
    exit 0
}

$firstFile = $list.data[0]
$downloadUrl = "https://api.chatgpt.com/v1/compliance/organizations/$OrganizationId/logs/$($firstFile.id)"
Write-Host ""
Write-Host "Downloading first file: $($firstFile.file_name) ($($firstFile.file_size) bytes)"
Write-Host "  $downloadUrl"

$downloadResp = $client.GetAsync($downloadUrl).GetAwaiter().GetResult()

if ([int]$downloadResp.StatusCode -ne 307) {
    Write-Host "Expected a 307 redirect, got $([int]$downloadResp.StatusCode) instead."
    Write-Host $downloadResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    exit 1
}

$signedUrl = $downloadResp.Headers.Location.ToString()
Write-Host "Got signed URL (expires shortly, not logging it in full): $($signedUrl.Substring(0, [Math]::Min(60, $signedUrl.Length)))..."

# Deliberately a fresh client with NO Authorization header — the signed URL is
# pre-authenticated and the admin bearer token has no business being sent to
# whatever host issued it.
$plainClient = New-Object System.Net.Http.HttpClient
$fileResp = $plainClient.GetAsync($signedUrl).GetAwaiter().GetResult()
$fileContent = $fileResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()

$outFile = Join-Path (Split-Path $AdminKeyFile -Parent) "chatgpt-costs-sample.jsonl"
$fileContent | Out-File -FilePath $outFile -Encoding utf8
Write-Host ""
Write-Host "Saved full file to: $outFile"
Write-Host "Line count: $((Get-Content $outFile | Measure-Object -Line).Lines)"
Write-Host ""
Write-Host "--- first 3 lines ---"
Get-Content $outFile -TotalCount 3
