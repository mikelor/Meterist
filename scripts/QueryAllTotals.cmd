@echo off
setlocal

rem Usage: QueryAllTotals.cmd [yyyy-MM-dd]
rem   With no argument, uses today's date as the window's end (matching
rem   ExtractAllThroughToday.ps1's -To default). Batch's %date% is
rem   locale/format-dependent and unsafe to parse directly, so "today" is
rem   computed via a PowerShell subprocess instead.

set TO_DATE=%~1
if "%TO_DATE%"=="" (
    for /f %%d in ('powershell -NoProfile -Command "(Get-Date).ToString('yyyy-MM-dd')"') do set TO_DATE=%%d
)

set DB=%LOCALAPPDATA%\Meterist\meterist.db

set CHATGPT=3d6f1c2e-8a4b-4e3a-8b2f-7c5e9a1d4f33
set CLAUDE_ENT=8f14e45f-ceea-467e-adde-3f82edcd1a11
set CLAUDE_API=c9e1a1a0-3b1e-4b8a-9d9e-2f6a1b0c9d22
set GEMINI=a27b6d3c-1f9e-4a7d-9c3b-6e2d8f0a5b44

echo Using end date: %TO_DATE%
echo.

echo === zelleri / ChatGPT Enterprise (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%CHATGPT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === zelleri / Claude Enterprise (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%CLAUDE_ENT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === zelleri / Gemini Enterprise (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%GEMINI%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === zelleri / Claude API Platform (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%CLAUDE_API%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === ecosync / ChatGPT Enterprise (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%CHATGPT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === ecosync / Claude Enterprise (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%CLAUDE_ENT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === ecosync / Gemini Enterprise (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%GEMINI%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

echo === ecosync / Claude API Platform (2026-07-01 to %TO_DATE%) ===
sqlite3 "%DB%" "SELECT ROUND(SUM(GrossSpend),2), ROUND(SUM(SeatFee),2), ROUND(SUM(UsageOrOverage),2), COUNT(*), MIN(Date), MAX(Date) FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%CLAUDE_API%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%';"

endlocal
