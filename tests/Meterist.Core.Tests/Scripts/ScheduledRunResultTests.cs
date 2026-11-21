using System.Diagnostics;

namespace Meterist.Core.Tests.Scripts;

/// <summary>
/// Pins the exit-code matrix of scripts/RunScheduledExtraction.ps1 (a sum of independent failures: 1 extraction, 2 coverage
/// gap, 4 roster snapshot, 8 spend-limit snapshot; so 0 to 15), which is what Task Scheduler's "Last Run Result" shows.
/// The mapping lives in ScheduledRunResult.ps1 so it can be run here without an extraction.
/// </summary>
public class ScheduledRunResultTests
{
    // One row per combination of the four failures. The code is the sum of the bits, so the matrix is generated from them.
    public static TheoryData<bool, bool, bool, bool, int> Matrix()
    {
        var data = new TheoryData<bool, bool, bool, bool, int>();
        for (var code = 0; code <= 15; code++)
        {
            data.Add((code & 1) != 0, (code & 2) != 0, (code & 4) != 0, (code & 8) != 0, code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ExitCodeMatrix_IsTheSumOfTheIndependentFailures(
        bool extractFailed, bool coverageFailed, bool rosterFailed, bool limitsFailed, int expectedCode)
    {
        var output = Map(extractFailed, coverageFailed, rosterFailed, limitsFailed);

        Assert.Equal(expectedCode, output.Code);
        Assert.Equal(expectedCode == 0, output.Summary.StartsWith("OK", StringComparison.Ordinal));
        Assert.Equal(expectedCode != 0, output.Summary.StartsWith("FAILED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, false, false, "OK")]
    [InlineData(true, false, false, false, "FAILED: one or more vendor extractions failed")]
    [InlineData(false, true, false, false, "FAILED: extraction succeeded but the coverage check found missing days")]
    [InlineData(true, true, false, false, "FAILED: extraction failures and missing days")]
    [InlineData(false, false, true, false, "FAILED: extraction and coverage were clean but one or more roster snapshots failed")]
    [InlineData(true, false, true, false, "FAILED: extraction failures and roster snapshot failures")]
    [InlineData(false, true, true, false, "FAILED: missing days and roster snapshot failures")]
    [InlineData(true, true, true, false, "FAILED: extraction failures, missing days and roster snapshot failures")]
    [InlineData(false, false, false, true, "FAILED: extraction, coverage and roster snapshots were clean but one or more spend-limit snapshots failed")]
    [InlineData(true, false, false, true, "FAILED: extraction failures and spend-limit snapshot failures")]
    [InlineData(false, false, true, true, "FAILED: roster snapshot failures and spend-limit snapshot failures")]
    [InlineData(true, true, true, true, "FAILED: extraction failures, missing days, roster snapshot failures and spend-limit snapshot failures")]
    public void TheSummaryNamesWhatFailed(
        bool extractFailed, bool coverageFailed, bool rosterFailed, bool limitsFailed, string summaryStart)
    {
        Assert.StartsWith(summaryStart, Map(extractFailed, coverageFailed, rosterFailed, limitsFailed).Summary);
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

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 2)]
    [InlineData(false, false, true, 4)]
    [InlineData(true, true, true, 7)]
    public void ACallerWrittenBeforeSpendLimitSnapshotsExisted_GetsTheSameCodesAsBefore(
        bool extractFailed, bool coverageFailed, bool rosterFailed, int expectedCode)
    {
        var script = FindScript("ScheduledRunResult.ps1");
        var command =
            $". '{script}'; $r = Get-ScheduledRunResult -ExtractFailed ${extractFailed} -CoverageFailed ${coverageFailed} "
            + $"-RosterFailed ${rosterFailed}; Write-Output \"$($r.Code)\"";

        Assert.Equal(expectedCode, int.Parse(RunPwsh(command)));
    }

    [Fact]
    public void TheWrapperTakesRosterThenSpendLimitSnapshotsAfterTheCoverageCheck_AndFeedsBothToTheExitCode()
    {
        // The wrapper is an unattended script that cannot be run here without extracting for real, so pin its wiring by text.
        var text = File.ReadAllText(FindScript("RunScheduledExtraction.ps1"));

        var coverage = text.IndexOf("audit coverage", StringComparison.Ordinal);
        var roster = text.IndexOf("roster snapshot-all", StringComparison.Ordinal);
        var limits = text.IndexOf("limits snapshot-all", StringComparison.Ordinal);
        var result = text.IndexOf("Get-ScheduledRunResult", StringComparison.Ordinal);

        Assert.True(
            coverage >= 0 && roster > coverage && limits > roster && result > limits,
            "extract, then coverage, then roster, then spend limits, then the result");
        Assert.Contains("-RosterFailed $rosterFailed", text);
        Assert.Contains("-LimitsFailed $limitsFailed", text);
        Assert.Contains("$rosterFailed = $LASTEXITCODE -ne 0", text);
        Assert.Contains("$limitsFailed = $LASTEXITCODE -ne 0", text);
        // Both must run for every tenant the wrapper was given, like the coverage check does.
        Assert.Contains("roster snapshot-all @tenantArgs", text);
        Assert.Contains("limits snapshot-all @tenantArgs", text);
    }

    [Fact]
    public void TheWrapperDoesNotRunTheSeatAudit_WhichNamesPeopleAndDriftsForADayAfterEverySeatChange()
    {
        var text = File.ReadAllText(FindScript("RunScheduledExtraction.ps1"));

        // A mention in the explanatory header is fine; a command is not.
        var commands = string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && l.Contains("dotnet run", StringComparison.Ordinal)));
        Assert.DoesNotContain("audit seats", commands);
    }

    private static (int Code, string Summary) Map(bool extractFailed, bool coverageFailed, bool rosterFailed, bool limitsFailed)
    {
        var script = FindScript("ScheduledRunResult.ps1");
        var command =
            $". '{script}'; $r = Get-ScheduledRunResult -ExtractFailed ${extractFailed} -CoverageFailed ${coverageFailed} "
            + $"-RosterFailed ${rosterFailed} -LimitsFailed ${limitsFailed}; Write-Output \"$($r.Code)|$($r.Summary)\"";

        var parts = RunPwsh(command).Split('|', 2);
        return (int.Parse(parts[0]), parts[1]);
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
