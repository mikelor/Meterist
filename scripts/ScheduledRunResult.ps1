<#
.SYNOPSIS
    Turns the two outcomes of a scheduled run into its exit code and summary line.
    Dot-sourced by RunScheduledExtraction.ps1; kept separate so the mapping can be
    tested without running an extraction.

    0 = clean, 1 = extraction failure, 2 = coverage gap, 3 = both.
#>
function Get-ScheduledRunResult {
    param(
        [Parameter(Mandatory)][bool]$ExtractFailed,
        [Parameter(Mandatory)][bool]$CoverageFailed
    )

    $code = 0
    if ($ExtractFailed) { $code += 1 }
    if ($CoverageFailed) { $code += 2 }

    $summary = switch ($code) {
        0 { "OK: all extractions succeeded and coverage is complete." }
        1 { "FAILED: one or more vendor extractions failed." }
        2 { "FAILED: extraction succeeded but the coverage check found missing days." }
        3 { "FAILED: extraction failures and missing days." }
    }

    [pscustomobject]@{ Code = $code; Summary = $summary }
}
