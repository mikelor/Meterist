using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>One week's figures for one (tenant, vendor) pair as a report printed them.</summary>
public sealed record ManifestWeek(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    decimal SeatFee,
    decimal UsageOrOverage,
    decimal GrossSpend,
    int DaysPresent);

public sealed record ManifestVendor(string Vendor, IReadOnlyList<ManifestWeek> Weeks);

public sealed record ManifestTenant(string TenantId, IReadOnlyList<ManifestVendor> Vendors);

/// <summary>
/// The weekly figures of one generated report, written beside it so the next report can be compared with this one
/// (see <see cref="ReportDiffer"/>). It holds the same client financial data as the report, so it is as sensitive as the
/// report is: it lives under the git-ignored <c>artifacts/</c> folder and is never committed.
/// <para>
/// Versioned: a reader refuses a version it does not know rather than guessing, so a later change to what a manifest
/// holds can never be mistaken for a change in the figures. Only the weekly totals are kept, not the projections, which
/// legitimately move with the choice of which weeks feed them.
/// </para>
/// </summary>
public sealed record ReportManifest(
    int Version,
    DateTime GeneratedAt,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    IReadOnlyList<ManifestTenant> Tenants)
{
    /// <summary>The version this build writes and understands.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

        // A missing constructor parameter is an error, not a default: a week with no "grossSpend" would read as $0 and look like
        // a collapse in the figures instead of a damaged file.
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Builds the manifest for a report: its period and, per tenant and vendor, every week it shows.</summary>
    public static ReportManifest From(ReportData data) => new(
        CurrentVersion,
        data.GeneratedAt,
        data.Period.Start,
        data.Period.End,
        data.Tenants
            .Select(t => new ManifestTenant(
                t.TenantId,
                t.Vendors
                    .Select(v => new ManifestVendor(
                        v.Vendor.ShortName,
                        v.Weeks
                            .OrderBy(w => w.WeekStart)
                            .Select(w => new ManifestWeek(w.WeekStart, w.WeekEnd, w.SeatFee, w.UsageOrOverage, w.GrossSpend, w.DaysPresent))
                            .ToList()))
                    .OrderBy(v => v.Vendor, StringComparer.Ordinal)
                    .ToList()))
            .OrderBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList());

    // Every level present and usable, so a damaged file is refused here and shown as "not compared" instead of failing later.
    private static bool IsWellFormed(ReportManifest manifest) =>
        manifest.Tenants is not null
        && manifest.Tenants.All(t =>
            t is not null
            && !string.IsNullOrWhiteSpace(t.TenantId)
            && t.Vendors is not null
            && t.Vendors.All(v =>
                v is not null
                && !string.IsNullOrWhiteSpace(v.Vendor)
                && v.Weeks is not null
                && v.Weeks.All(w => w is not null && w.WeekEnd >= w.WeekStart && w.DaysPresent >= 0)));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Reads a manifest. False with a reason when the text is not valid JSON, lacks a field this version requires, or
    /// carries a different version. The reason never quotes the file's content.
    /// </summary>
    public static bool TryParse(string json, out ReportManifest? manifest, out string? problem)
    {
        manifest = null;
        problem = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version))
            {
                problem = "it has no readable version number";
                return false;
            }

            if (version != CurrentVersion)
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture,
                    $"it is manifest version {version} and this build reads version {CurrentVersion}");
                return false;
            }

            var read = document.RootElement.Deserialize<ReportManifest>(JsonOptions);
            if (read is null || !IsWellFormed(read))
            {
                problem = "it is missing required fields or has an entry that is not well formed";
                return false;
            }

            if (read.PeriodEnd < read.PeriodStart)
            {
                problem = "its period ends before it starts";
                return false;
            }

            manifest = read;
            return true;
        }
        catch (JsonException)
        {
            problem = "it is not valid manifest JSON";
            return false;
        }
    }
}
