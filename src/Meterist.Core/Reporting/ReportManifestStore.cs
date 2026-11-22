using System.Globalization;

namespace Meterist.Core.Reporting;

/// <summary>
/// Where report manifests live and how the previous one is found. A manifest sits beside its report, named from the report's
/// file name and the moment it was generated, and is never overwritten by a later run: the figures that last looked right
/// must survive a run that shows them gone, or the check would erase its own evidence.
/// </summary>
public static class ReportManifestStore
{
    private const string Infix = ".manifest.";

    /// <summary>The manifest path for a report written at <paramref name="reportPath"/> and generated at <paramref name="generatedAt"/>.</summary>
    public static string PathFor(string reportPath, DateTime generatedAt)
    {
        var stamp = generatedAt.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(DirectoryOf(reportPath), $"{StemOf(reportPath)}{Infix}{stamp}.json");
    }

    /// <summary>The baseline picked for a new report: its path and manifest, or why there is none worth comparing with.</summary>
    public sealed record Baseline(string? Path, ReportManifest? Manifest, string? Problem);

    /// <summary>
    /// Picks the earlier manifest in the report's folder to compare <paramref name="current"/> with. Any manifest there counts,
    /// whatever the report was called, because reports are usually saved under dated names. The newest one for the same set
    /// of tenants wins, so a report for one client is not shadowed by a later report for another. When none matches, the
    /// newest manifest is returned anyway (or the reason it cannot be read), so the report can say what it was not compared
    /// with. A null <see cref="Baseline.Path"/> means there is no earlier manifest at all.
    /// </summary>
    public static Baseline ResolveBaseline(string reportPath, ReportManifest current)
    {
        var directory = DirectoryOf(reportPath);
        if (!Directory.Exists(directory))
        {
            return new Baseline(null, null, null);
        }

        // The timestamp is the last dotted part before ".json" and sorts the same as time, whatever the report's own name is.
        var files = Directory
            .EnumerateFiles(directory, $"*{Infix}*.json")
            .OrderByDescending(p => StampOf(p), StringComparer.Ordinal)
            .ThenByDescending(p => p, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            return new Baseline(null, null, null);
        }

        var currentTenants = current.Tenants.Select(t => t.TenantId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Baseline? newest = null;
        foreach (var file in files)
        {
            var loaded = TryLoad(file, out var manifest, out var problem)
                ? new Baseline(file, manifest, null)
                : new Baseline(file, null, problem);
            newest ??= loaded;

            if (loaded.Manifest is not null
                && loaded.Manifest.Version == current.Version
                && loaded.Manifest.Tenants.Select(t => t.TenantId).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(currentTenants))
            {
                return loaded;
            }
        }

        return newest!;
    }

    /// <summary>Reads and validates a manifest file. False with a reason that does not quote the file.</summary>
    public static bool TryLoad(string path, out ReportManifest? manifest, out string? problem)
    {
        manifest = null;
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "it could not be read";
            return false;
        }

        return ReportManifest.TryParse(json, out manifest, out problem);
    }

    /// <summary>Writes the manifest for a report to <paramref name="path"/>, creating the folder. Returns the path.</summary>
    public static async Task<string> WriteAsync(ReportManifest manifest, string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, manifest.ToJson(), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string StemOf(string reportPath) => Path.GetFileNameWithoutExtension(reportPath);

    private static string StampOf(string manifestPath)
    {
        var name = Path.GetFileNameWithoutExtension(manifestPath);
        var at = name.LastIndexOf(Infix, StringComparison.Ordinal);
        return at < 0 ? name : name[(at + Infix.Length)..];
    }

    private static string DirectoryOf(string reportPath) =>
        Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? Directory.GetCurrentDirectory();
}
