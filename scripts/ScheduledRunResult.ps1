<#
.SYNOPSIS
    Turns the outcomes of a scheduled run into its exit code and summary line.
    Dot-sourced by RunScheduledExtraction.ps1; kept separate so the mapping can be
    tested without running an extraction.

    The code is a sum of independent failures, so it says exactly what went wrong:
      1 = extraction failure
      2 = coverage gap
      4 = roster snapshot failure
    0 = clean, 1 or 2 or 4 = one thing failed, 3 = extraction and coverage,
    5 = extraction and roster, 6 = coverage and roster, 7 = all three.
#>
function Get-ScheduledRunResult {
    param(
        [Parameter(Mandatory)][bool]$ExtractFailed,
        [Parameter(Mandatory)][bool]$CoverageFailed,
        # Optional so a caller written before roster snapshots existed keeps working unchanged.
        [bool]$RosterFailed = $false
    )

    $code = 0
    if ($ExtractFailed) { $code += 1 }
    if ($CoverageFailed) { $code += 2 }
    if ($RosterFailed) { $code += 4 }

    $summary = switch ($code) {
        0 { "OK: all extractions succeeded, coverage is complete and roster snapshots were taken." }
        1 { "FAILED: one or more vendor extractions failed." }
        2 { "FAILED: extraction succeeded but the coverage check found missing days." }
        3 { "FAILED: extraction failures and missing days." }
        4 { "FAILED: extraction and coverage were clean but one or more roster snapshots failed." }
        5 { "FAILED: extraction failures and roster snapshot failures." }
        6 { "FAILED: missing days and roster snapshot failures." }
        7 { "FAILED: extraction failures, missing days and roster snapshot failures." }
    }

    [pscustomobject]@{ Code = $code; Summary = $summary }
}
