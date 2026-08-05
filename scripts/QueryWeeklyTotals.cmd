@echo off
setlocal

rem Buckets DailySpendRecords into Wed-Tue weeks anchored at 2026-07-01
rem (confirmed Wednesday), one query per vendor/tenant pair. Reports
rem week_start, week_end, seat fee, usage/overage, total spend, and
rem days_present so partial weeks (the trailing incomplete week, or a
rem vendor's mid-week extraction start) can be told apart from genuinely
rem complete 7-day weeks.
rem
rem Usage: QueryWeeklyTotals.cmd [yyyy-MM-dd]
rem   With no argument, uses today's date as the window's end.

set TO_DATE=%~1
if "%TO_DATE%"=="" (
    for /f %%d in ('powershell -NoProfile -Command "(Get-Date).ToString('yyyy-MM-dd')"') do set TO_DATE=%%d
)

set DB=%LOCALAPPDATA%\Meterist\meterist.db
set ANCHOR=2026-07-01

set CHATGPT=3d6f1c2e-8a4b-4e3a-8b2f-7c5e9a1d4f33
set CLAUDE_ENT=8f14e45f-ceea-467e-adde-3f82edcd1a11
set CLAUDE_API=c9e1a1a0-3b1e-4b8a-9d9e-2f6a1b0c9d22
set GEMINI=a27b6d3c-1f9e-4a7d-9c3b-6e2d8f0a5b44

echo Using end date: %TO_DATE%
echo Week anchor (Wednesday): %ANCHOR%
echo.

set WEEKQUERY=SELECT CAST((julianday(Date) - julianday('%ANCHOR%')) / 7 AS INTEGER) AS week_index, date('%ANCHOR%', '+' ^|^| (CAST((julianday(Date) - julianday('%ANCHOR%')) / 7 AS INTEGER) * 7) ^|^| ' days') AS week_start, date('%ANCHOR%', '+' ^|^| (CAST((julianday(Date) - julianday('%ANCHOR%')) / 7 AS INTEGER) * 7 + 6) ^|^| ' days') AS week_end, ROUND(SUM(SeatFee),2) AS seat_fee, ROUND(SUM(UsageOrOverage),2) AS usage_overage, ROUND(SUM(GrossSpend),2) AS total, COUNT(*) AS days_present

echo === zelleri / ChatGPT Enterprise ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%CHATGPT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === zelleri / Claude Enterprise ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%CLAUDE_ENT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === zelleri / Gemini Enterprise ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%GEMINI%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === zelleri / Claude API Platform ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='zelleri' AND UPPER(VendorId)=UPPER('%CLAUDE_API%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === ecosync / ChatGPT Enterprise ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%CHATGPT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === ecosync / Claude Enterprise ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%CLAUDE_ENT%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === ecosync / Gemini Enterprise ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%GEMINI%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

echo === ecosync / Claude API Platform ===
sqlite3 "%DB%" "%WEEKQUERY% FROM DailySpendRecords WHERE TenantId='ecosync' AND UPPER(VendorId)=UPPER('%CLAUDE_API%') AND Date BETWEEN '2026-07-01' AND '%TO_DATE%' GROUP BY week_index ORDER BY week_index;"

endlocal
