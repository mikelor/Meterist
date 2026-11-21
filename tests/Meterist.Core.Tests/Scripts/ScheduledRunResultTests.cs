using System.Diagnostics;

namespace Meterist.Core.Tests.Scripts;

/// <summary>
/// Pins the exit-code matrix of scripts/RunScheduledExtraction.ps1 (a sum of independent failures: 1 extraction,
/// 2 coverage gap, 4 roster snapshot; so 0 to 7), which is what Task Scheduler's "Last Run Result" shows.
/// The mapping lives in ScheduledRunResult.ps1 so it can be run here without an extraction.
/// </summary>
public class ScheduledRunResultTests
{
    [Theory]
    [InlineData(false, false, false, 0, "OK")]
    [InlineData(true, false, false, 1, "FAILED: one or more vendor extractions failed")]
    [InlineData(false, true, false, 2, "FAILED: extraction succeeded but the coverage check found missing days")]
    [InlineData(true, true, false, 3, "FAILED: extraction failures and missing days")]
    [InlineData(false, false, true, 4, "FAILED: extraction and coverage were clean but one or more roster snapshots failed")]
    [InlineData(true, false, true, 5, "FAILED: extraction failures and roster snapshot failures")]
    [InlineData(false, true, true, 6, "FAILED: missing days and roster snapshot failures")]
    [InlineData(true, true, true, 7, "FAILED: extraction failures, missing days and roster snapshot failures")]
    public void ExitCodeMatrix(bool extractFailed, bool coverageFailed, bool rosterFailed, int expectedCode, string summaryStart)
    {
        var script = FindScript("ScheduledRunResult.ps1");
        var command =
            $". '{script}'; $r = Get-ScheduledRunResult -ExtractFailed ${extractFailed} -CoverageFailed ${coverageFailed} "
            + $"-RosterFailed ${rosterFailed}; Write-Output \"$($r.Code)|$($r.Summary)\"";

        var output = RunPwsh(command);
        var parts = output.Split('|', 2);

        Assert.Equal(expectedCode, int.Parse(parts[0]));
        Assert.StartsWith(summaryStart, parts[1]);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 3)]
    public void ACallerWrittenBeforeRosterSnapshotsExisted_GetsTheSameCodesAsBefore(bool extractFailed, bool coverageFailed, int expectedCode)
    {
        var script = FindScript("ScheduledRunResult.ps1");
        var command =
            $". '{script}'; $r = Get-ScheduledRunResult -ExtractFailed ${extractFailed} -CoverageFailed ${coverageFailed}; "
            + "Write-Output \"$($r.Code)\"";

        Assert.Equal(expectedCode, int.Parse(RunPwsh(command)));
    }

    [Fact]
    public void TheWrapperTakesRosterSnapshotsAfterTheCoverageCheck_AndFeedsTheResultToTheExitCode()
    {
        // The wrapper is an unattended script that cannot be run here without extracting for real, so pin its wiring by text.
        var text = File.ReadAllText(FindScript("RunScheduledExtraction.ps1"));

        var coverage = text.IndexOf("audit coverage", StringComparison.Ordinal);
        var roster = text.IndexOf("roster snapshot-all", StringComparison.Ordinal);
        var result = text.IndexOf("Get-ScheduledRunResult", StringComparison.Ordinal);

        Assert.True(coverage >= 0 && roster > coverage && result > roster, "extract, then coverage, then roster, then the result");
        Assert.Contains("-RosterFailed $rosterFailed", text);
        Assert.Contains("$rosterFailed = $LASTEXITCODE -ne 0", text);
        // It must run for every tenant the wrapper was given, like the coverage check does.
        Assert.Contains("roster snapshot-all @tenantArgs", text);
    }
    private static string FindScript(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find scripts/{name} above {AppContext.BaseDirectory}.");
    }

    private static string RunPwsh(string command)
    {
        var info = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("PowerShell 7 (pwsh) is not installed, so the script mapping can't be exercised.");
            throw;
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"pwsh failed: {error}");
            return output.Trim();
        }
    }
}
