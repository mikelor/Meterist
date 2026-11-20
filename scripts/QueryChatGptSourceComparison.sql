-- Compares, day by day, the two ChatGPT sources Meterist stores side by side in
-- RawDailyExtractionRecords: the Daily Usage API row (the source of record) and the
-- COSTS compliance-log rows (a cross-check, 29-day retention). Both are in USD, using
-- the vendor's own estimated_cost_usd. Read-only. Used for the Monday discrepancy in
-- issue #10.
--
-- Usage (PowerShell):
--   sqlite3 -header -column "$env:LOCALAPPDATA\Meterist\meterist.db" ".read scripts/QueryChatGptSourceComparison.sql"
--
-- A day appears only if both sources are stored for it. 'diff' is costs minus api.
WITH raw AS (
    SELECT r.TenantId AS tenant, r.Date AS day, j.value AS row
    FROM RawDailyExtractionRecords r, json_each(r.RecordsJson) j
    WHERE UPPER(r.VendorId) = '3D6F1C2E-8A4B-4E3A-8B2F-7C5E9A1D4F33'
),
api AS (
    SELECT tenant, day, SUM(json_extract(row, '$.EstimatedCostUsd')) AS api_usd
    FROM raw WHERE json_extract(row, '$.Source') = 'DailyUsage' GROUP BY tenant, day
),
costs AS (
    SELECT tenant, day, SUM(json_extract(row, '$.EstimatedCostUsdValue')) AS costs_usd, COUNT(*) AS costs_rows
    FROM raw WHERE json_extract(row, '$.Source') = 'CostsExport' GROUP BY tenant, day
)
SELECT api.tenant,
       api.day,
       CASE strftime('%w', api.day) WHEN '0' THEN 'Sun' WHEN '1' THEN 'Mon' WHEN '2' THEN 'Tue'
            WHEN '3' THEN 'Wed' WHEN '4' THEN 'Thu' WHEN '5' THEN 'Fri' ELSE 'Sat' END AS dow,
       ROUND(api.api_usd, 2) AS api_usd,
       ROUND(costs.costs_usd, 2) AS costs_usd,
       ROUND(costs.costs_usd - api.api_usd, 2) AS diff,
       costs.costs_rows
FROM api JOIN costs ON costs.tenant = api.tenant AND costs.day = api.day
ORDER BY api.tenant, api.day;
