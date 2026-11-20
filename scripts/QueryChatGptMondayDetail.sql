-- Per-user detail behind a COSTS-vs-Daily-Usage difference (issue #10): for each listed
-- day, the COSTS export's estimated USD per user and per SKU/unit, from the stored raw
-- rows. Compare the per-user figures with the Daily Usage API's per-user rows for the
-- same day. Read-only. Edit the day list and tenant below.
--
-- Usage (PowerShell):
--   sqlite3 -header -column "$env:LOCALAPPDATA\Meterist\meterist.db" ".read scripts/QueryChatGptMondayDetail.sql"
WITH raw AS (
    SELECT r.TenantId AS tenant, r.Date AS day, j.value AS row
    FROM RawDailyExtractionRecords r, json_each(r.RecordsJson) j
    WHERE UPPER(r.VendorId) = '3D6F1C2E-8A4B-4E3A-8B2F-7C5E9A1D4F33'
      AND r.TenantId = 'zelleri'
      AND r.Date IN ('2026-10-26', '2026-11-16', '2026-11-17')
      AND json_extract(j.value, '$.Source') = 'CostsExport'
)
SELECT day,
       json_extract(row, '$.UserId') AS user_id,
       json_extract(row, '$.Sku') AS sku,
       json_extract(row, '$.CostUnit') AS unit,
       COUNT(*) AS lines,
       ROUND(SUM(json_extract(row, '$.CostValue')), 2) AS cost_value,
       ROUND(SUM(json_extract(row, '$.EstimatedCostUsdValue')), 2) AS est_usd
FROM raw
GROUP BY day, user_id, sku, unit
ORDER BY day, user_id, sku;
