using System.Diagnostics;

namespace Meterist.Core.Tests.Scripts;

/// <summary>
/// Pins the exit-code matrix of scripts/RunScheduledExtraction.ps1 (0 clean, 1 extraction
/// failure, 2 coverage gap, 3 both), which is what Task Scheduler's "Last Run Result" shows.
/// The mapping lives in ScheduledRunResult.ps1 so it can be run here without an extraction.
/// </summary>
public class ScheduledRunResultTests
{
    [Theory]
    [InlineData(false, false, 0, "OK")]
    [InlineData(true, false, 1, "FAILED: one or more vendor extractions failed")]
    [InlineData(false, true, 2, "FAILED: extraction succeeded but the coverage check found missing days")]
    [InlineData(true, true, 3, "FAILED: extraction failures and missing days")]
    public void ExitCodeMatrix(bool extractFailed, bool coverageFailed, int expectedCode, string summaryStart)
    {
        var script = FindScript("ScheduledRunResult.ps1");
        var command =
            $". '{script}'; $r = Get-ScheduledRunResult -ExtractFailed ${extractFailed} -CoverageFailed ${coverageFailed}; "
            + "Write-Output \"$($r.Code)|$($r.Summary)\"";

        var output = RunPwsh(command);
        var parts = output.Split('|', 2);

        Assert.Equal(expectedCode, int.Parse(parts[0]));
        Assert.StartsWith(summaryStart, parts[1]);
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
