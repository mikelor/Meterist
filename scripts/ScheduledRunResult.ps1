<#
.SYNOPSIS
    Turns the outcomes of a scheduled run into its exit code and summary line.
    Dot-sourced by RunScheduledExtraction.ps1; kept separate so the mapping can be
    tested without running an extraction.

    The code is a sum of independent failures, so it says exactly what went wrong:
      1 = extraction failure
      2 = coverage gap
      4 = roster snapshot failure
      8 = spend-limit snapshot failure
    0 = clean; any other value is the sum of the failures that happened, for example
    3 = extraction and coverage, 5 = extraction and roster, 12 = roster and spend limits,
    15 = all four.

    `audit seats` (seat-count drift and idle seats) is deliberately not part of the run or
    its exit code: drift is expected for a day or two after any seat change while the vendor's
    figure catches up, and the idle list names people, which should not go into a log file.
#>
function Get-ScheduledRunResult {
    param(
        [Parameter(Mandatory)][bool]$ExtractFailed,
        [Parameter(Mandatory)][bool]$CoverageFailed,
        # Optional so a caller written before roster snapshots existed keeps working unchanged.
        [bool]$RosterFailed = $false,
        # Likewise for spend-limit snapshots.
        [bool]$LimitsFailed = $false
    )

    $code = 0
    if ($ExtractFailed) { $code += 1 }
    if ($CoverageFailed) { $code += 2 }
    if ($RosterFailed) { $code += 4 }
    if ($LimitsFailed) { $code += 8 }

    $summary = switch ($code) {
        0 { "OK: all extractions succeeded, coverage is complete and roster and spend-limit snapshots were taken." }
        1 { "FAILED: one or more vendor extractions failed." }
        2 { "FAILED: extraction succeeded but the coverage check found missing days." }
        3 { "FAILED: extraction failures and missing days." }
        4 { "FAILED: extraction and coverage were clean but one or more roster snapshots failed." }
        5 { "FAILED: extraction failures and roster snapshot failures." }
        6 { "FAILED: missing days and roster snapshot failures." }
        7 { "FAILED: extraction failures, missing days and roster snapshot failures." }
        8 { "FAILED: extraction, coverage and roster snapshots were clean but one or more spend-limit snapshots failed." }
        default {
            # Every combination that includes a spend-limit failure along with another: name each part.
            $parts = @()
            if ($ExtractFailed) { $parts += "extraction failures" }
            if ($CoverageFailed) { $parts += "missing days" }
            if ($RosterFailed) { $parts += "roster snapshot failures" }
            if ($LimitsFailed) { $parts += "spend-limit snapshot failures" }
            $last = $parts[-1]
            $head = $parts[0..($parts.Count - 2)] -join ", "
            "FAILED: $head and $last."
        }
    }

    [pscustomobject]@{ Code = $code; Summary = $summary }
}
