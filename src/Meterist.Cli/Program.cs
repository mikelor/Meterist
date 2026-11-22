using System.Globalization;
using System.CommandLine;
using System.Net.Http;
using Meterist.Core.Auditing;
using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Reporting;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Meterist.Data;
using Meterist.Secrets;
using Meterist.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.ClaudeApiPlatform;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.GeminiEnterprise;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

// ContentRootPath must be pinned to the compiled app's own directory, not the
// default (the caller's current working directory) — otherwise appsettings.json
// silently fails to load whenever this is invoked from anywhere other than
// this exact folder (e.g. a wrapper script, or `dotnet run --project` from the
// repo root, both of which this tool is meant to support).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.AddServiceDefaults();

builder.Services.AddMeteristData();
builder.Services.AddMeteristSecretStore();

// Which billing lines count as Gemini Enterprise spend is configuration (appsettings), with the
// verified defaults used when the section is absent.
var geminiBillingSection = builder.Configuration.GetSection(GeminiBillingFilter.ConfigurationSection);
builder.Services.AddSingleton(
    geminiBillingSection.Exists()
        ? geminiBillingSection.Get<GeminiBillingFilter>() ?? GeminiBillingFilter.Default
        : GeminiBillingFilter.Default);
builder.Services.AddSingleton<IGeminiBillingQueryRepository, BigQueryGeminiBillingRepository>();
builder.Services.AddSingleton<IVendorSpendNormalizer, GeminiEnterpriseSpendNormalizer>();
builder.Services.AddSingleton<IVendorSpendBreakdownNormalizer, GeminiEnterpriseBreakdownNormalizer>();

// AllowAutoRedirect must stay false: the download endpoint's 307 points to a
// pre-signed URL that must never receive our Authorization header — see
// HttpChatGptCostLogRepository's doc comment.
builder.Services.AddHttpClient<IChatGptCostLogRepository, HttpChatGptCostLogRepository>(client =>
    {
        client.BaseAddress = new Uri("https://api.chatgpt.com/");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IChatGptDailyUsageRepository, HttpChatGptDailyUsageRepository>(client =>
    {
        client.BaseAddress = new Uri("https://api.chatgpt.com/");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.Configure<ChatGptCostsCrossCheckOptions>(
    builder.Configuration.GetSection(ChatGptCostsCrossCheckOptions.ConfigurationSection));
builder.Services.AddSingleton<IVendorSpendNormalizer, ChatGptEnterpriseSpendNormalizer>();
builder.Services.AddSingleton<IVendorUserUsageNormalizer, ChatGptEnterpriseUserUsageNormalizer>();
// The member list uses the same admin key and workspace ID as the Daily Usage API. Its HTTP client is registered through
// RosterHttpClients, which also keeps its URLs (they carry a member id as the cursor) out of the factory's request logging.
builder.Services.AddChatGptRosterHttpClient();
builder.Services.AddSingleton<IVendorRosterSource, ChatGptEnterpriseRosterSource>();

// Claude API Platform cost comes from the newer /analytics/cost endpoint by default (same Admin key, per-actor
// attribution), with the legacy cost_report behind Meterist:ClaudeApiPlatform:UseLegacyCostReport.
builder.Services.Configure<ClaudeApiPlatformOptions>(
    builder.Configuration.GetSection(ClaudeApiPlatformOptions.ConfigurationSection));
builder.Services.AddHttpClient<HttpClaudeCostReportRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddHttpClient<HttpClaudeApiAnalyticsCostRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddTransient<IClaudeCostReportRepository>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClaudeApiPlatformOptions>>().Value.UseLegacyCostReport
        ? sp.GetRequiredService<HttpClaudeCostReportRepository>()
        : sp.GetRequiredService<HttpClaudeApiAnalyticsCostRepository>());
builder.Services.AddSingleton<IVendorSpendNormalizer, ClaudeApiPlatformSpendNormalizer>();
builder.Services.AddSingleton<IVendorUserUsageNormalizer, ClaudeApiPlatformUserUsageNormalizer>();

// Claude Enterprise cost comes from the newer /analytics/cost endpoint by default, with the legacy
// user_cost_report behind Meterist:ClaudeEnterprise:UseLegacyCostReport. Both typed clients are
// registered and one is chosen from the setting.
builder.Services.Configure<ClaudeEnterpriseOptions>(
    builder.Configuration.GetSection(ClaudeEnterpriseOptions.ConfigurationSection));
builder.Services.AddHttpClient<HttpClaudeAnalyticsCostReportRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddHttpClient<HttpClaudeAnalyticsCostRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddTransient<IClaudeAnalyticsCostReportRepository>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClaudeEnterpriseOptions>>().Value.UseLegacyCostReport
        ? sp.GetRequiredService<HttpClaudeAnalyticsCostReportRepository>()
        : sp.GetRequiredService<HttpClaudeAnalyticsCostRepository>());
builder.Services.AddSingleton<IVendorSpendNormalizer, ClaudeEnterpriseSpendNormalizer>();
builder.Services.AddSingleton<IVendorUserUsageNormalizer, ClaudeEnterpriseUserUsageNormalizer>();
builder.Services.AddClaudeRosterHttpClient();
builder.Services.AddGeminiUserLicenseHttpClient();
builder.Services.AddSingleton<IVendorRosterSource, GeminiEnterpriseRosterSource>();
builder.Services.AddSingleton<IVendorRosterSource, ClaudeEnterpriseRosterSource>();
builder.Services.AddHttpClient<IClaudeActivitySummaryRepository, HttpClaudeActivitySummaryRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddSingleton<IVendorActivitySummarySource, ClaudeEnterpriseActivitySummarySource>();
builder.Services.AddHttpClient<IClaudeSpendLimitRepository, HttpClaudeSpendLimitRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddSingleton<IVendorSpendLimitSource, ClaudeEnterpriseSpendLimitSource>();
builder.Services.AddClaudeUserActivityHttpClient();
builder.Services.AddSingleton<IVendorUserActivitySource, ClaudeEnterpriseUserActivitySource>();
builder.Services.AddChatGptSpendControlsHttpClient();
builder.Services.AddSingleton<IVendorSpendLimitSource, ChatGptEnterpriseSpendLimitSource>();

builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeApiPlatformSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ChatGptEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, GeminiEnterpriseSpendExtractor>();

builder.Services.AddScoped<SpendExtractionService>();
builder.Services.AddScoped<ConfiguredVendorExtraction>();
builder.Services.AddScoped<ReportDataAggregator>();
builder.Services.AddScoped<CoverageChecker>();
builder.Services.AddScoped<SeatAuditor>();
builder.Services.AddScoped<UserUsageBackfill>();
builder.Services.AddScoped<SpendBreakdownBackfill>();
builder.Services.AddScoped<RosterSnapshotService>();
builder.Services.AddScoped<SpendLimitSnapshotService>();
builder.Services.AddScoped<ConfiguredSpendLimitSnapshots>();
builder.Services.AddScoped<ConfiguredRosterSnapshots>();
builder.Services.AddScoped<UserUsageReporter>();

using var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MeteristDbContext>().Database.MigrateAsync();
}

var rootCommand = new RootCommand("Meterist — AI vendor spend extraction tool");
rootCommand.Subcommands.Add(BuildCredentialsCommand(host));
rootCommand.Subcommands.Add(BuildRatesCommand(host));
rootCommand.Subcommands.Add(BuildExtractCommand(host));
rootCommand.Subcommands.Add(BuildReportCommand(host));
rootCommand.Subcommands.Add(BuildAuditCommand(host));
rootCommand.Subcommands.Add(BuildBackfillCommand(host));
rootCommand.Subcommands.Add(BuildUsersCommand(host));
rootCommand.Subcommands.Add(BuildRosterCommand(host));
rootCommand.Subcommands.Add(BuildActivityCommand(host));
rootCommand.Subcommands.Add(BuildLimitsCommand(host));

return await rootCommand.Parse(args).InvokeAsync();

static Command BuildCredentialsCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var vendorOption = new Option<string>("--vendor")
    {
        Description = "Vendor short name (e.g. gemini-enterprise)", Required = true,
    };
    var fromFileOption = new Option<FileInfo>("--from-file")
    {
        Description = "Path to a file whose raw content becomes the stored credential. "
            + "Shape is vendor-specific (a bare API key, or a JSON envelope for vendors "
            + "like Gemini Enterprise) — construct it yourself before pointing here.",
        Required = true,
    };

    var setCommand = new Command("set", "Store a tenant's vendor credential");
    setCommand.Options.Add(tenantOption);
    setCommand.Options.Add(vendorOption);
    setCommand.Options.Add(fromFileOption);

    setCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var vendorShortName = parseResult.GetValue(vendorOption)!;
        var file = parseResult.GetValue(fromFileOption)!;

        var vendor = VendorCatalog.FindByShortName(vendorShortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{vendorShortName}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return 1;
        }

        var credential = await File.ReadAllTextAsync(file.FullName, cancellationToken);

        if (vendor.Id == VendorCatalog.ChatGptEnterprise.Id
            && !ChatGptCredential.TryParse(credential, out _, out var credentialError))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Not stored.[/] {credentialError}");
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secretStore.SetCredentialAsync(tenantId, vendor.Id, credential, cancellationToken);

        AnsiConsole.MarkupLine($"[green]Stored credential for tenant '{tenantId}', vendor '{vendor.DisplayName}'.[/]");
        return 0;
    });

    var credentialsCommand = new Command("credentials", "Manage per-tenant vendor credentials");
    credentialsCommand.Subcommands.Add(setCommand);
    return credentialsCommand;
}

static Command BuildRatesCommand(IHost host)
{
    var vendorOption = new Option<string>("--vendor")
    {
        Description = "Vendor short name (e.g. chatgpt-enterprise)", Required = true,
    };
    var tenantOption = new Option<string?>("--tenant")
    {
        Description = "Tenant identifier. Omit to set the public default rate "
            + "(applies to any tenant without its own override).",
    };
    var rateTypeOption = new Option<string>("--rate-type")
    {
        Description = "Rate kind, e.g. 'per-seat' or 'credit-to-usd' — vendor-defined, "
            + "see docs/user-guide.md's Configuring rates section.",
        Required = true,
    };
    var modelOrSkuOption = new Option<string?>("--model-or-sku")
    {
        Description = "Pricing dimension key the vendor's normalizer looks up by, e.g. 'seat' or 'credit-usd'.",
    };
    var rateOption = new Option<decimal>("--rate")
    {
        Description = "The rate value (e.g. dollars per seat per month, or dollars per credit)", Required = true,
    };
    var seatsOption = new Option<int?>("--seats")
    {
        Description = "Billed seat count — only meaningful for per-seat rate types.",
    };
    var cadenceOption = new Option<string?>("--cadence")
    {
        Description = "Billing cadence for per-seat proration: Monthly, Annual, or OneTime.",
    };
    var effectiveFromOption = new Option<DateTime>("--effective-from")
    {
        Description = "Date this rate takes effect", Required = true,
    };
    var effectiveToOption = new Option<DateTime?>("--effective-to")
    {
        Description = "Date this rate stops applying (omit for open-ended)",
    };

    var setCommand = new Command("set", "Store a versioned per-vendor rate (seat fee, credit conversion, etc.)");
    setCommand.Options.Add(vendorOption);
    setCommand.Options.Add(tenantOption);
    setCommand.Options.Add(rateTypeOption);
    setCommand.Options.Add(modelOrSkuOption);
    setCommand.Options.Add(rateOption);
    setCommand.Options.Add(seatsOption);
    setCommand.Options.Add(cadenceOption);
    setCommand.Options.Add(effectiveFromOption);
    setCommand.Options.Add(effectiveToOption);

    setCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var vendorShortName = parseResult.GetValue(vendorOption)!;
        var vendor = VendorCatalog.FindByShortName(vendorShortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{vendorShortName}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return 1;
        }

        var cadenceText = parseResult.GetValue(cadenceOption);
        BillingCadence? cadence = null;
        if (!string.IsNullOrWhiteSpace(cadenceText))
        {
            if (!Enum.TryParse<BillingCadence>(cadenceText, ignoreCase: true, out var parsedCadence))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Unknown --cadence '{cadenceText}'.[/] Expected Monthly, Annual, or OneTime.");
                return 1;
            }

            cadence = parsedCadence;
        }

        var tenantIdValue = parseResult.GetValue(tenantOption);
        var modelOrSkuValue = parseResult.GetValue(modelOrSkuOption);
        var effectiveFromDate = DateOnly.FromDateTime(parseResult.GetValue(effectiveFromOption));
        var effectiveToValue = parseResult.GetValue(effectiveToOption);

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IVendorRateConfigRepository>();

        DateOnly? resolvedEffectiveTo = effectiveToValue is { } effectiveTo
            ? DateOnly.FromDateTime(effectiveTo)
            : null;

        // A backdated rate (no --effective-to) that predates a rate already on
        // file for this scope should end the day before that later rate starts,
        // rather than requiring the caller to compute that boundary by hand.
        if (resolvedEffectiveTo is null)
        {
            var nextEffectiveFrom = await repository.FindNextEffectiveFromAsync(
                tenantIdValue, vendor.Id, modelOrSkuValue, effectiveFromDate, cancellationToken);
            if (nextEffectiveFrom is { } next)
            {
                resolvedEffectiveTo = next.AddDays(-1);
                AnsiConsole.MarkupLine(
                    $"[yellow]Capped this rate's EffectiveTo to {resolvedEffectiveTo:yyyy-MM-dd} -- a later "
                    + $"rate for this scope already starts {next:yyyy-MM-dd}.[/]");
            }
        }

        var rate = new VendorRateConfig
        {
            TenantId = tenantIdValue,
            VendorId = vendor.Id,
            RateType = parseResult.GetValue(rateTypeOption)!,
            ModelOrSku = modelOrSkuValue,
            Rate = parseResult.GetValue(rateOption),
            SeatCount = parseResult.GetValue(seatsOption),
            BillingCadence = cadence,
            EffectiveFrom = effectiveFromDate,
            EffectiveTo = resolvedEffectiveTo,
        };

        // Closes out any previous open-ended rate in the same scope before
        // adding the new one, so a contract renewal never leaves two
        // overlapping windows for the same (tenant-or-public, vendor,
        // model-or-sku) — see IVendorRateConfigRepository's doc comment.
        var closedCount = await repository.CloseOpenEndedRateAsync(
            rate.TenantId, rate.VendorId, rate.ModelOrSku, rate.EffectiveFrom, cancellationToken);

        // Runs after the close above, not before -- a legitimate renewal's
        // superseded row needs its EffectiveTo already updated, or every
        // ordinary renewal would false-positive against its own predecessor.
        var overlaps = await repository.FindOverlappingRatesAsync(
            rate.TenantId, rate.VendorId, rate.ModelOrSku, rate.EffectiveFrom, rate.EffectiveTo, cancellationToken);
        if (overlaps.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]This rate would overlap existing row(s) for this scope:[/]");
            foreach (var overlap in overlaps)
            {
                AnsiConsole.MarkupLine(
                    $"  {overlap.EffectiveFrom:yyyy-MM-dd} to "
                    + $"{overlap.EffectiveTo?.ToString("yyyy-MM-dd") ?? "open-ended"}");
            }

            return 1;
        }

        await repository.AddAsync(rate, cancellationToken);

        var scopeLabel = rate.TenantId is null ? "public default" : $"tenant '{rate.TenantId}'";
        if (closedCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Closed {closedCount} previous open-ended rate(s) for this scope, ending "
                + $"{rate.EffectiveFrom.AddDays(-1):yyyy-MM-dd}.[/]");
        }

        AnsiConsole.MarkupLine(
            $"[green]Stored '{rate.RateType}' rate for {scopeLabel}, vendor '{vendor.DisplayName}', "
            + $"effective {rate.EffectiveFrom:yyyy-MM-dd}.[/]");
        return 0;
    });

    var listTenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var listVendorOption = new Option<string>("--vendor") { Description = "Vendor short name", Required = true };

    var listCommand = new Command("list", "List stored rates for a tenant/vendor");
    listCommand.Options.Add(listTenantOption);
    listCommand.Options.Add(listVendorOption);

    listCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(listTenantOption)!;
        var vendorShortName = parseResult.GetValue(listVendorOption)!;
        var vendor = VendorCatalog.FindByShortName(vendorShortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{vendorShortName}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IVendorRateConfigRepository>();
        var rates = await repository.GetApplicableRatesAsync(
            tenantId, vendor.Id, new DateRange(DateOnly.MinValue, DateOnly.MaxValue), cancellationToken);

        var table = new Table()
            .AddColumn("Scope")
            .AddColumn("RateType")
            .AddColumn("ModelOrSku")
            .AddColumn("Rate")
            .AddColumn("Seats")
            .AddColumn("Cadence")
            .AddColumn("EffectiveFrom")
            .AddColumn("EffectiveTo");

        foreach (var rate in rates.OrderBy(r => r.ModelOrSku).ThenBy(r => r.EffectiveFrom))
        {
            table.AddRow(
                rate.TenantId is null ? "public default" : $"tenant '{rate.TenantId}'",
                rate.RateType,
                rate.ModelOrSku ?? string.Empty,
                rate.Rate.ToString("0.######"),
                rate.SeatCount?.ToString() ?? string.Empty,
                rate.BillingCadence?.ToString() ?? string.Empty,
                rate.EffectiveFrom.ToString("yyyy-MM-dd"),
                rate.EffectiveTo?.ToString("yyyy-MM-dd") ?? string.Empty);
        }

        AnsiConsole.Write(table);
        return 0;
    });

    var ratesCommand = new Command("rates", "Manage per-tenant/public-default vendor pricing rates");
    ratesCommand.Subcommands.Add(setCommand);
    ratesCommand.Subcommands.Add(listCommand);
    return ratesCommand;
}

static Command BuildExtractCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier to extract spend for", Required = true };
    var fromOption = new Option<DateTime>("--from") { Description = "Start of the extraction period", Required = true };
    var toOption = new Option<DateTime>("--to") { Description = "End of the extraction period (inclusive)", Required = true };
    var vendorOption = new Option<string?>("--vendor")
    {
        Description = "Restrict extraction to one vendor short name (default: all registered vendors)",
    };
    var dryRunOption = new Option<bool>("--dry-run")
    {
        Description = "Query the vendors and show what would change, without writing anything "
            + "(no raw data and no daily records are stored)",
    };
    var configuredOnlyOption = new Option<bool>("--configured-only")
    {
        Description = "Skip vendors the tenant has no stored credential for, instead of reporting them "
            + "as failed (for tenants that use only some of the vendors)",
    };

    var extractCommand = new Command("extract", "Extract spend for a tenant across a date range");
    extractCommand.Options.Add(tenantOption);
    extractCommand.Options.Add(fromOption);
    extractCommand.Options.Add(toOption);
    extractCommand.Options.Add(vendorOption);
    extractCommand.Options.Add(dryRunOption);
    extractCommand.Options.Add(configuredOnlyOption);

    extractCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var vendorShortName = parseResult.GetValue(vendorOption);
        var dryRun = parseResult.GetValue(dryRunOption);
        var configuredOnly = parseResult.GetValue(configuredOnlyOption);

        Guid? vendorFilter = null;
        if (vendorShortName is not null)
        {
            var vendor = VendorCatalog.FindByShortName(vendorShortName);
            if (vendor is null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{vendorShortName}'.[/] Known vendors: "
                    + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
                return 1;
            }

            vendorFilter = vendor.Id;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — {(dryRun ? "dry run for" : "extracting for")} tenant [yellow]{tenantId}[/], "
            + $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        if (dryRun)
        {
            AnsiConsole.MarkupLine("[yellow]DRY RUN — vendors are queried, but nothing is written.[/]");
        }

        using var scope = host.Services.CreateScope();
        var period = new DateRange(from, to);
        var results = configuredOnly
            ? await scope.ServiceProvider.GetRequiredService<ConfiguredVendorExtraction>()
                .ExtractAsync(tenantId, period, vendorFilter, cancellationToken, dryRun)
            : await scope.ServiceProvider.GetRequiredService<SpendExtractionService>()
                .ExtractAsync(tenantId, period, vendorFilter, cancellationToken, dryRun);
        var table = new Table()
            .AddColumn("Vendor")
            .AddColumn("Status")
            .AddColumn("Records")
            .AddColumn("Detail");

        foreach (var result in results)
        {
            var statusMarkup = result.Status switch
            {
                VendorExtractionStatus.Succeeded => "[green]Succeeded[/]",
                VendorExtractionStatus.NotImplemented => "[grey]Not implemented[/]",
                VendorExtractionStatus.Skipped => "[grey]Skipped[/]",
                _ => "[red]Failed[/]",
            };

            table.AddRow(
                result.DisplayName, statusMarkup, result.RecordCount.ToString(), Markup.Escape(result.Detail ?? string.Empty));
        }

        AnsiConsole.Write(table);

        if (dryRun)
        {
            WriteDryRunChanges(results);
        }

        // A scheduled run has to be able to tell that something broke, so a failed vendor
        // makes the whole command fail. Skipped and not-implemented vendors are not failures.
        var exitCode = ExtractionExitCode.For(results);
        if (exitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]{results.Count(r => r.Status == VendorExtractionStatus.Failed)} vendor extraction(s) failed.[/]");
        }

        return exitCode;
    });

    return extractCommand;
}

static void WriteDryRunChanges(IReadOnlyList<VendorExtractionResult> results)
{
    const int MaxRows = 60;

    var any = false;
    var relabelled = 0;

    foreach (var result in results)
    {
        // A day whose figures are identical and only its source label changes is
        // counted, not listed: listing them would bury the days that really change.
        relabelled += (result.Changes ?? []).Count(c => c.Kind == DailySpendChangeKind.Relabelled);

        var changed = (result.Changes ?? [])
            .Where(c => c.Kind is not (DailySpendChangeKind.Unchanged or DailySpendChangeKind.Relabelled))
            .OrderBy(c => c.Date)
            .ToList();
        if (changed.Count == 0)
        {
            continue;
        }

        any = true;

        // Gross spend is shown as well as usage: for a seat-priced vendor like
        // Gemini, nearly all of a day's spend is the seat fee, so a usage-only
        // table would read as zero for every new day.
        var table = new Table()
            .Title($"{Markup.Escape(result.DisplayName)} - days that would change")
            .AddColumn(new TableColumn("Date").NoWrap())
            .AddColumn("Change")
            .AddColumn(new TableColumn("Stored gross").RightAligned())
            .AddColumn(new TableColumn("Pulled gross").RightAligned())
            .AddColumn(new TableColumn("Stored usage").RightAligned())
            .AddColumn(new TableColumn("Pulled usage").RightAligned())
            .AddColumn("Source");

        foreach (var change in changed.Take(MaxRows))
        {
            var changeMarkup = change.Kind switch
            {
                DailySpendChangeKind.Inserted => "[green]new day[/]",
                DailySpendChangeKind.Updated => "[yellow]updated[/]",
                _ => "[red]kept higher usage[/]",
            };

            table.AddRow(
                change.Date.ToString("yyyy-MM-dd"),
                changeMarkup,
                change.Previous is null ? "-" : change.Previous.GrossSpend.ToString("F2"),
                change.Incoming.GrossSpend.ToString("F2"),
                change.Previous is null ? "-" : change.Previous.UsageOrOverage.ToString("F2"),
                change.Incoming.UsageOrOverage.ToString("F2"),
                Markup.Escape(change.Stored.Source ?? "-"));
        }

        AnsiConsole.Write(table);

        if (changed.Count > MaxRows)
        {
            AnsiConsole.MarkupLine($"[grey]...and {changed.Count - MaxRows} more day(s) not shown.[/]");
        }
    }

    if (relabelled > 0)
    {
        AnsiConsole.MarkupLine(
            $"[grey]{relabelled} day(s) would keep identical figures and only change their source label.[/]");
    }

    if (!any && relabelled == 0)
    {
        // "Nothing" must mean nothing: the per-user, breakdown, activity-summary and per-person activity rows are written too.
        AnsiConsole.MarkupLine(results.Any(r => r.WouldChangeOtherStoredData())
            ? "[green]No day of spend would change.[/] [yellow]Other stored data would (per-user, breakdown, activity): see the Detail column above.[/]"
            : "[green]Nothing would change.[/]");
    }
}

static Command BuildRosterCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var vendorOption = new Option<string>("--vendor")
    {
        Description = "Vendor short name (a vendor with a member list, such as claude-enterprise)",
        Required = true,
    };

    static (VendorIdentity? Vendor, int ExitCode) ResolveVendor(string shortName)
    {
        var vendor = VendorCatalog.FindByShortName(shortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(shortName)}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return (null, 1);
        }

        return (vendor, 0);
    }

    // ---- roster snapshot
    var dryRunOption = new Option<bool>("--dry-run")
    {
        Description = "Fetch the member list and show what would be stored, without writing anything",
    };

    var snapshotCommand = new Command(
        "snapshot",
        "Take a dated snapshot of a vendor's member list (who holds a seat today) and store it. Run it regularly: "
        + "vendors expose no membership history, so each snapshot is the only record of who was on the list that day. "
        + "Taking another on the same day replaces that day's snapshot.");
    snapshotCommand.Options.Add(tenantOption);
    snapshotCommand.Options.Add(vendorOption);
    snapshotCommand.Options.Add(dryRunOption);

    snapshotCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var dryRun = parseResult.GetValue(dryRunOption);
        var (vendor, exit) = ResolveVendor(parseResult.GetValue(vendorOption)!);
        if (vendor is null)
        {
            return exit;
        }

        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RosterSnapshotService>();

        try
        {
            var outcome = await service.TakeAsync(tenantId, vendor.Id, dryRun, cancellationToken);

            AnsiConsole.MarkupLine(
                $"[bold]Meterist[/] — roster snapshot for [yellow]{Markup.Escape(tenantId)}[/] / {Markup.Escape(vendor.DisplayName)}, "
                + $"{outcome.SnapshotDate:yyyy-MM-dd}" + (dryRun ? " [grey](dry run: nothing written)[/]" : string.Empty));
            if (outcome.EmptyNotStored)
            {
                AnsiConsole.MarkupLine(
                    "  The list is empty (nobody holds a licence or has tried to sign in yet), so nothing was stored. "
                    + "An earlier snapshot, if there is one, stays the latest.");
                return 0;
            }

            AnsiConsole.MarkupLine(
                $"  {outcome.MemberCount} member(s): {string.Join(", ", outcome.RoleCounts.Select(kv => $"{Markup.Escape(kv.Key)} {kv.Value}"))}.");

            var stored = outcome.Stored;
            var parts = new List<string>();
            if (stored.Inserted > 0) parts.Add($"{stored.Inserted} added");
            if (stored.Updated > 0) parts.Add($"{stored.Updated} changed");
            if (stored.Removed > 0) parts.Add($"{stored.Removed} removed");
            if (stored.Unchanged > 0) parts.Add($"{stored.Unchanged} unchanged");
            AnsiConsole.MarkupLine(
                $"  {(dryRun ? "Would store" : "Stored")} the snapshot: {(parts.Count == 0 ? "nothing" : string.Join(", ", parts))}"
                + (outcome.Stored.Inserted + outcome.Stored.Updated + outcome.Stored.Removed == 0 && stored.Unchanged > 0
                    ? " (identical to the snapshot already stored for today)"
                    : string.Empty)
                + ".");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    });

    // ---- roster snapshot-all: what the scheduled run calls
    var tenantsOption = new Option<string[]>("--tenant")
    {
        Description = "Tenant identifier to snapshot (repeat for several tenants)",
        Required = true,
    };

    var snapshotAllCommand = new Command(
        "snapshot-all",
        "Take the roster snapshot for every vendor that has a member list and that each tenant has a credential for, "
        + "skipping the rest. One vendor or tenant failing does not stop the others. Exits 1 if any snapshot failed, "
        + "so a scheduled run can tell. This is what the daily task runs.");
    snapshotAllCommand.Options.Add(tenantsOption);
    snapshotAllCommand.Options.Add(dryRunOption);

    snapshotAllCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantsOption)!;
        var dryRun = parseResult.GetValue(dryRunOption);

        var all = new List<RosterRunResult>();

        foreach (var tenantId in tenantIds)
        {
            // A scope, and so a database context, per tenant: a write that fails for one tenant cannot affect the next.
            using var scope = host.Services.CreateScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<ConfiguredRosterSnapshots>();
            var results = await snapshots.TakeAsync(tenantId, dryRun, cancellationToken);
            all.AddRange(results);

            var table = new Table()
                .Title($"{Markup.Escape(tenantId)} roster snapshot{(dryRun ? " (dry run)" : string.Empty)}")
                .AddColumn("Vendor")
                .AddColumn("Status")
                .AddColumn("Detail");

            foreach (var r in results)
            {
                var detail = r.Status == RosterRunStatus.Succeeded && r.Outcome is { EmptyNotStored: true }
                    ? r.Detail
                    : r.Status == RosterRunStatus.Succeeded && r.Outcome is { } o
                    ? $"{o.MemberCount} member(s):{string.Join(", ", o.RoleCounts.Select(kv => $"{kv.Key} {kv.Value}"))}; "
                        + $"{(dryRun ? "would store" : "stored")} {o.Stored.Inserted} added, {o.Stored.Updated} changed, "
                        + $"{o.Stored.Removed} removed, {o.Stored.Unchanged} unchanged"
                    : r.Detail;
                var status = r.Status switch
                {
                    RosterRunStatus.Succeeded => "[green]Succeeded[/]",
                    RosterRunStatus.Skipped => "[grey]Skipped[/]",
                    _ => "[red]Failed[/]",
                };
                table.AddRow(Markup.Escape(r.DisplayName), status, Markup.Escape(detail));
            }

            AnsiConsole.Write(table);
        }

        var failed = all.Count(r => r.Status == RosterRunStatus.Failed);
        AnsiConsole.MarkupLine(failed == 0
            ? $"[green]{all.Count(r => r.Status == RosterRunStatus.Succeeded)} roster snapshot(s) taken, "
                + $"{all.Count(r => r.Status == RosterRunStatus.Skipped)} skipped.[/]"
            : $"[red]{failed} roster snapshot(s) failed.[/]");

        return RosterExitCode.For(all);
    });

    // ---- roster show
    var dateOption = new Option<DateTime?>("--on-or-before")
    {
        Description = "Show the latest snapshot taken on or before this date (default: the latest of all)",
    };

    var showCommand = new Command(
        "show",
        "Show a stored roster snapshot: the members, their roles and when they were added. Read-only.");
    showCommand.Options.Add(tenantOption);
    showCommand.Options.Add(vendorOption);
    showCommand.Options.Add(dateOption);

    showCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var (vendor, exit) = ResolveVendor(parseResult.GetValue(vendorOption)!);
        if (vendor is null)
        {
            return exit;
        }

        var onOrBefore = parseResult.GetValue(dateOption) is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMemberRosterRepository>();
        var snapshot = await repository.GetLatestSnapshotAsync(tenantId, vendor.Id, onOrBefore, cancellationToken);
        var dates = await repository.GetSnapshotDatesAsync(tenantId, vendor.Id, cancellationToken);

        if (snapshot is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No roster snapshot is stored for {Markup.Escape(tenantId)} / {Markup.Escape(vendor.DisplayName)}"
                + (onOrBefore is { } b ? $" on or before {b:yyyy-MM-dd}" : string.Empty)
                + ".[/] Run roster snapshot first.");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — roster for [yellow]{Markup.Escape(tenantId)}[/] / {Markup.Escape(vendor.DisplayName)}, "
            + $"snapshot of {snapshot.Date:yyyy-MM-dd} ({dates.Count} snapshot(s) stored)");

        var table = new Table().AddColumns("Member", "Role", "Seat type", "SCIM", "Added");
        foreach (var member in snapshot.Members)
        {
            var who = !string.IsNullOrWhiteSpace(member.Name) && !string.IsNullOrWhiteSpace(member.Email)
                ? $"{member.Name} <{member.Email}>"
                : member.Name ?? member.Email ?? member.MemberId;
            table.AddRow(
                Markup.Escape(who),
                Markup.Escape(member.Role ?? "-"),
                Markup.Escape(member.SeatType ?? "-"),
                member.IsScimManaged switch { true => "yes", false => "no", null => "-" },
                member.AddedAt is { } added ? added.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"  {snapshot.Members.Count} member(s): "
            + string.Join(", ", snapshot.Members
                .GroupBy(m => string.IsNullOrWhiteSpace(m.Role) ? "(none)" : m.Role!, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{Markup.Escape(g.Key)} {g.Count()}")) + ".");
        return 0;
    });

    var rosterCommand = new Command("roster", "Vendor member lists: who holds a seat");
    rosterCommand.Subcommands.Add(snapshotCommand);
    rosterCommand.Subcommands.Add(snapshotAllCommand);
    rosterCommand.Subcommands.Add(showCommand);
    return rosterCommand;
}

static Command BuildActivityCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var vendorOption = new Option<string>("--vendor") { Description = "Vendor short name, e.g. claude-enterprise", Required = true };
    var fromOption = new Option<DateTime>("--from") { Description = "First day of the period", Required = true };
    var toOption = new Option<DateTime>("--to") { Description = "Last day of the period (inclusive)", Required = true };
    var productOption = new Option<string?>("--product")
    {
        Description = "Show one product's counts (for Claude: chat, claude_code, cowork, ...) instead of the whole organisation",
    };

    var showCommand = new Command(
        "show",
        "Show the vendor's own stored daily active-user and seat counts. Aggregate figures only, no names. Read-only.");
    showCommand.Options.Add(tenantOption);
    showCommand.Options.Add(vendorOption);
    showCommand.Options.Add(fromOption);
    showCommand.Options.Add(toOption);
    showCommand.Options.Add(productOption);

    showCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var vendorName = parseResult.GetValue(vendorOption)!;
        var vendor = VendorCatalog.FindByShortName(vendorName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(vendorName)}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return 1;
        }

        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var product = parseResult.GetValue(productOption) ?? ActivitySummaryRecord.AllProducts;

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IActivitySummaryRepository>();
        var rows = (await repository.GetAsync(tenantId, vendor.Id, new DateRange(from, to), cancellationToken))
            .Where(r => string.Equals(r.Product, product, StringComparison.Ordinal))
            .ToList();

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No activity summaries are stored for {Markup.Escape(tenantId)} / {Markup.Escape(vendor.DisplayName)} "
                + $"({Markup.Escape(product)}) from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.[/] Run extract first "
                + "(only vendors that report them have any).");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — activity for [yellow]{Markup.Escape(tenantId)}[/] / {Markup.Escape(vendor.DisplayName)}, "
            + $"{Markup.Escape(product)}, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");

        static string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
        static string Pct(decimal? value) => value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "-";

        var table = new Table().AddColumns("Date", "Daily", "Weekly", "Monthly", "Seats", "Pending", "Daily adoption");
        foreach (var row in rows)
        {
            table.AddRow(
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                N(row.DailyActiveUsers), N(row.WeeklyActiveUsers), N(row.MonthlyActiveUsers),
                N(row.AssignedSeatCount), N(row.PendingInviteCount), Pct(row.DailyAdoptionRate));
        }

        AnsiConsole.Write(table);
        return 0;
    });

    var activityCommand = new Command("activity", "The vendor's own active-user and seat counts");
    activityCommand.Subcommands.Add(showCommand);
    return activityCommand;
}

static Command BuildLimitsCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var vendorOption = new Option<string>("--vendor")
    {
        Description = "Vendor short name (a vendor with spend limits, such as claude-enterprise)",
        Required = true,
    };

    static VendorIdentity? ResolveVendor(string shortName)
    {
        var vendor = VendorCatalog.FindByShortName(shortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(shortName)}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
        }

        return vendor;
    }

    // ---- limits snapshot
    var dryRunOption = new Option<bool>("--dry-run")
    {
        Description = "Fetch the limits and show what would be stored, without writing anything",
    };

    var snapshotCommand = new Command(
        "snapshot",
        "Take a dated snapshot of each member's spend limit and what they have spent against it, and store it. The "
        + "vendor keeps no history of these, so each snapshot is the only record of where a member stood that day. "
        + "Taking another on the same day replaces that day's snapshot.");
    snapshotCommand.Options.Add(tenantOption);
    snapshotCommand.Options.Add(vendorOption);
    snapshotCommand.Options.Add(dryRunOption);

    snapshotCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var dryRun = parseResult.GetValue(dryRunOption);
        var vendor = ResolveVendor(parseResult.GetValue(vendorOption)!);
        if (vendor is null)
        {
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SpendLimitSnapshotService>();

        try
        {
            var outcome = await service.TakeAsync(tenantId, vendor.Id, dryRun, cancellationToken);

            AnsiConsole.MarkupLine(
                $"[bold]Meterist[/] — spend limits for [yellow]{Markup.Escape(tenantId)}[/] / {Markup.Escape(vendor.DisplayName)}, "
                + $"{outcome.SnapshotDate:yyyy-MM-dd}" + (dryRun ? " [grey](dry run: nothing written)[/]" : string.Empty));
            AnsiConsole.MarkupLine(
                $"  {outcome.MemberCount} member(s), {outcome.WithLimitCount} with a limit and "
                + $"{outcome.MemberCount - outcome.WithLimitCount} without.");

            var stored = outcome.Stored;
            var parts = new List<string>();
            if (stored.Inserted > 0) parts.Add($"{stored.Inserted} added");
            if (stored.Updated > 0) parts.Add($"{stored.Updated} changed");
            if (stored.Removed > 0) parts.Add($"{stored.Removed} removed");
            if (stored.Unchanged > 0) parts.Add($"{stored.Unchanged} unchanged");
            AnsiConsole.MarkupLine(
                $"  {(dryRun ? "Would store" : "Stored")} the snapshot: {(parts.Count == 0 ? "nothing" : string.Join(", ", parts))}.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    });

    // ---- limits show
    var onOrBeforeOption = new Option<DateTime?>("--on-or-before")
    {
        Description = "Show the latest snapshot taken on or before this date (default: the latest of all)",
    };

    var showCommand = new Command(
        "show",
        "Show a stored spend-limit snapshot as headroom: each member's limit, what they have spent against it, the "
        + "share used and where the limit comes from, most used first. Read-only. The vendor calls the spend "
        + "informational, so treat it as a signal, not a ledger.");
    showCommand.Options.Add(tenantOption);
    showCommand.Options.Add(vendorOption);
    showCommand.Options.Add(onOrBeforeOption);

    showCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var vendor = ResolveVendor(parseResult.GetValue(vendorOption)!);
        if (vendor is null)
        {
            return 1;
        }

        var onOrBefore = parseResult.GetValue(onOrBeforeOption) is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpendLimitRepository>();
        var snapshot = await repository.GetLatestSnapshotAsync(tenantId, vendor.Id, onOrBefore, cancellationToken);

        if (snapshot is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No spend-limit snapshot is stored for {Markup.Escape(tenantId)} / {Markup.Escape(vendor.DisplayName)}"
                + (onOrBefore is { } b ? $" on or before {b:yyyy-MM-dd}" : string.Empty)
                + ".[/] Run limits snapshot first.");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — spend limits for [yellow]{Markup.Escape(tenantId)}[/] / {Markup.Escape(vendor.DisplayName)}, "
            + $"snapshot of {snapshot.Date:yyyy-MM-dd}");

        static string Money(decimal? value, string? currency) =>
            value is { } v ? v.ToString("N2", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(currency) ? string.Empty : " " + currency) : "-";

        // Most constrained first (a zero limit with spend, then by share of the limit used), members with no limit
        // last. A zero limit is a limit, so it is ranked and shown, not treated as the absence of one.
        var ordered = snapshot.Limits
            .Select(l => (Row: l, Headroom: SpendLimitHeadroom.Of(l)))
            .OrderByDescending(x => x.Headroom.Pressure)
            .ThenByDescending(x => x.Row.PeriodToDateSpend ?? 0m)
            .ThenBy(x => x.Row.Name ?? x.Row.Email ?? x.Row.MemberId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Last month is shown only when the vendor reports it (ChatGPT does when available; Claude never does).
        var showPrior = ordered.Any(x => x.Row.PriorPeriodSpend is not null);
        var table = new Table().AddColumns("Member", "Limit", "Spent", "Used", "Left");
        if (showPrior)
        {
            table.AddColumn("Last month");
        }

        table.AddColumn("Source");
        foreach (var (l, headroom) in ordered)
        {
            var who = !string.IsNullOrWhiteSpace(l.Name) && !string.IsNullOrWhiteSpace(l.Email)
                ? $"{l.Name} <{l.Email}>"
                : l.Name ?? l.Email ?? l.MemberId;
            var used = headroom.UsedFraction;
            var left = headroom.Left;
            var source = l.SourceType is null ? "-" : l.SourceDetail is null ? l.SourceType : $"{l.SourceType} ({l.SourceDetail})";

            var cells = new List<string>
            {
                Markup.Escape(who),
                l.LimitAmount is null ? "no limit" : Money(l.LimitAmount, l.Currency),
                Money(l.PeriodToDateSpend, l.Currency),
                used is { } u ? (u * 100m).ToString("0", CultureInfo.InvariantCulture) + "%" : headroom.Kind == SpendLimitKind.Zero ? "n/a" : "-",
                left is { } lf ? Money(lf, l.Currency) : "-",
            };
            if (showPrior)
            {
                cells.Add(Money(l.PriorPeriodSpend, l.Currency));
            }

            cells.Add(Markup.Escape(source));
            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
        var withLimit = snapshot.Limits.Count(l => l.LimitAmount is not null);
        AnsiConsole.MarkupLine(
            $"  {snapshot.Limits.Count} member(s), {withLimit} with a limit. The vendor reports spend to date as informational: "
            + "a headroom signal, not a ledger.");
        return 0;
    });

    // ---- limits snapshot-all: what the scheduled run calls
    var tenantsOption = new Option<string[]>("--tenant")
    {
        Description = "Tenant identifier to snapshot (repeat for several tenants)",
        Required = true,
    };

    var snapshotAllCommand = new Command(
        "snapshot-all",
        "Take the spend-limit snapshot for every vendor that has one and that each tenant has a credential for, skipping the "
        + "rest. One vendor or tenant failing does not stop the others. Exits 1 if any snapshot failed (a key without the "
        + "spend-limit scope is a failure, not a skip), so a scheduled run can tell. This is what the daily task runs.");
    snapshotAllCommand.Options.Add(tenantsOption);
    snapshotAllCommand.Options.Add(dryRunOption);

    snapshotAllCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantsOption)!;
        var dryRun = parseResult.GetValue(dryRunOption);

        var all = new List<SpendLimitRunResult>();

        foreach (var tenantId in tenantIds)
        {
            // A scope, and so a database context, per tenant: a write that fails for one tenant cannot affect the next.
            using var scope = host.Services.CreateScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<ConfiguredSpendLimitSnapshots>();
            var results = await snapshots.TakeAsync(tenantId, dryRun, cancellationToken);
            all.AddRange(results);

            var table = new Table()
                .Title($"{Markup.Escape(tenantId)} spend-limit snapshot{(dryRun ? " (dry run)" : string.Empty)}")
                .AddColumn("Vendor")
                .AddColumn("Status")
                .AddColumn("Detail");

            foreach (var r in results)
            {
                var detail = r.Status == SpendLimitRunStatus.Succeeded && r.Outcome is { } o
                    ? $"{o.MemberCount} member(s), {o.WithLimitCount} with a limit; "
                        + $"{(dryRun ? "would store" : "stored")} {o.Stored.Inserted} added, {o.Stored.Updated} changed, "
                        + $"{o.Stored.Removed} removed, {o.Stored.Unchanged} unchanged"
                    : r.Detail;
                var status = r.Status switch
                {
                    SpendLimitRunStatus.Succeeded => "[green]Succeeded[/]",
                    SpendLimitRunStatus.Skipped => "[grey]Skipped[/]",
                    _ => "[red]Failed[/]",
                };
                table.AddRow(Markup.Escape(r.DisplayName), status, Markup.Escape(detail));
            }

            AnsiConsole.Write(table);
        }

        var failed = all.Count(r => r.Status == SpendLimitRunStatus.Failed);
        AnsiConsole.MarkupLine(failed == 0
            ? $"[green]{all.Count(r => r.Status == SpendLimitRunStatus.Succeeded)} spend-limit snapshot(s) taken, "
                + $"{all.Count(r => r.Status == SpendLimitRunStatus.Skipped)} skipped.[/]"
            : $"[red]{failed} spend-limit snapshot(s) failed.[/]");

        return SpendLimitExitCode.For(all);
    });

    var limitsCommand = new Command("limits", "Per-member spend limits and headroom");
    limitsCommand.Subcommands.Add(snapshotCommand);
    limitsCommand.Subcommands.Add(snapshotAllCommand);
    limitsCommand.Subcommands.Add(showCommand);
    return limitsCommand;
}

static Command BuildUsersCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var fromOption = new Option<DateTime>("--from") { Description = "First day of the period", Required = true };
    var toOption = new Option<DateTime>("--to") { Description = "Last day of the period (inclusive)", Required = true };
    var vendorOption = new Option<string?>("--vendor")
    {
        Description = "Show only one vendor short name (default: every vendor that has per-user rows)",
    };
    var topOption = new Option<int?>("--top")
    {
        Description = "Show only the N highest-spending users per vendor, then a line for the rest",
    };

    var usageCommand = new Command(
        "usage",
        "Per-user usage over a period (named by name or email): dollars, credits, requests and days active, "
        + "followed by the vendor's aggregate for the same period so the two can be compared. Read-only.");
    usageCommand.Options.Add(tenantOption);
    usageCommand.Options.Add(fromOption);
    usageCommand.Options.Add(toOption);
    usageCommand.Options.Add(vendorOption);
    usageCommand.Options.Add(topOption);

    usageCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var period = new DateRange(
            DateOnly.FromDateTime(parseResult.GetValue(fromOption)),
            DateOnly.FromDateTime(parseResult.GetValue(toOption)));
        var vendorShortName = parseResult.GetValue(vendorOption);
        var top = parseResult.GetValue(topOption);

        if (period.Start > period.End)
        {
            AnsiConsole.MarkupLine("[red]--from must not be after --to.[/]");
            return 1;
        }

        if (top is <= 0)
        {
            AnsiConsole.MarkupLine("[red]--top must be a positive number.[/]");
            return 1;
        }

        Guid? vendorFilter = null;
        if (vendorShortName is not null)
        {
            var vendor = VendorCatalog.FindByShortName(vendorShortName);
            if (vendor is null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(vendorShortName)}'.[/] Known vendors: "
                    + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
                return 1;
            }

            vendorFilter = vendor.Id;
        }

        using var scope = host.Services.CreateScope();
        var reporter = scope.ServiceProvider.GetRequiredService<UserUsageReporter>();
        var report = await reporter.BuildAsync(tenantId, period, vendorFilter, cancellationToken);

        WriteUserUsageReport(report, top);
        return 0;
    });

    var activityTopOption = new Option<int?>("--top") { Description = "Show only the N most active people, then a line for the rest" };

    var activityCommand = new Command(
        "activity",
        "Per-person product activity over a period (named by email): days active, last active day, messages, Claude Code "
        + "sessions, commits and pull requests, Cowork sessions and web searches, from the stored activity rows. Activity is "
        + "not cost: a person can be busy and cost nothing on a seat plan. Read-only.");
    activityCommand.Options.Add(tenantOption);
    activityCommand.Options.Add(fromOption);
    activityCommand.Options.Add(toOption);
    activityCommand.Options.Add(activityTopOption);

    activityCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var period = new DateRange(
            DateOnly.FromDateTime(parseResult.GetValue(fromOption)), DateOnly.FromDateTime(parseResult.GetValue(toOption)));
        var top = parseResult.GetValue(activityTopOption);

        if (period.Start > period.End)
        {
            AnsiConsole.MarkupLine("[red]--from must not be after --to.[/]");
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUserActivityRepository>();
        var rows = await repository.GetAsync(tenantId, VendorCatalog.ClaudeEnterprise.Id, period, cancellationToken);

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No activity rows are stored for {Markup.Escape(tenantId)} / Claude Enterprise from {period.Start:yyyy-MM-dd} "
                + $"to {period.End:yyyy-MM-dd}.[/] Run extract first (it stores them for the days it has not read yet).");
            return 0;
        }

        var people = rows
            .GroupBy(r => r.UserId, StringComparer.Ordinal)
            .Select(g => new
            {
                Id = g.Key,
                Email = g.OrderByDescending(r => r.Date).Select(r => r.UserEmail).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
                Days = g.Select(r => r.Date).Distinct().Count(),
                Last = g.Max(r => r.Date),
                Messages = g.Sum(r => r.ChatMessages + r.CoworkMessages + r.OfficeMessages + r.DesignMessages + r.ScienceMessages),
                CodeSessions = g.Sum(r => r.ClaudeCodeSessions),
                Commits = g.Sum(r => r.ClaudeCodeCommits),
                PullRequests = g.Sum(r => r.ClaudeCodePullRequests),
                CoworkSessions = g.Sum(r => r.CoworkSessions),
                Searches = g.Sum(r => r.WebSearches),
            })
            .OrderByDescending(p => p.Days)
            .ThenByDescending(p => p.Messages)
            .ThenBy(p => p.Email ?? p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — activity for [yellow]{Markup.Escape(tenantId)}[/] / Claude Enterprise, "
            + $"{period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd} ({rows.Select(r => r.Date).Distinct().Count()} day(s) with activity stored, "
            + $"{people.Count} active person(s))");

        // The person column never wraps (an email split over three lines is unreadable); the numeric headings are short so the
        // numbers can share the rest of a normal-width terminal. Messages is chat, Cowork, Office, Design and Science together.
        var table = new Table()
            .AddColumn(new TableColumn("Person").NoWrap())
            .AddColumn(new TableColumn("Days").RightAligned())
            .AddColumn(new TableColumn("Last active").NoWrap())
            .AddColumn(new TableColumn("Msgs").RightAligned())
            .AddColumn(new TableColumn("Code").RightAligned())
            .AddColumn(new TableColumn("Commits").RightAligned())
            .AddColumn(new TableColumn("PRs").RightAligned())
            .AddColumn(new TableColumn("Cowork").RightAligned())
            .AddColumn(new TableColumn("Searches").RightAligned());
        foreach (var p in people.Take(top ?? people.Count))
        {
            table.AddRow(
                Markup.Escape(p.Email ?? p.Id),
                p.Days.ToString(CultureInfo.InvariantCulture),
                p.Last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                p.Messages.ToString("N0", CultureInfo.InvariantCulture),
                p.CodeSessions.ToString("N0", CultureInfo.InvariantCulture),
                p.Commits.ToString("N0", CultureInfo.InvariantCulture),
                p.PullRequests.ToString("N0", CultureInfo.InvariantCulture),
                p.CoworkSessions.ToString("N0", CultureInfo.InvariantCulture),
                p.Searches.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        if (top is { } n && people.Count > n)
        {
            AnsiConsole.MarkupLine($"  {people.Count - n} more person(s) not shown.");
        }

        AnsiConsole.MarkupLine(
            "  Days = days active. Msgs = chat, Cowork, Office, Design and Science messages together. Code = Claude Code sessions. "
            + "Cowork = Cowork sessions. Activity is counts of what people did, not model requests and not cost. The output names "
            + "people, so keep it out of anything you commit.");
        return 0;
    });

    var usersCommand = new Command("users", "Per-user views of the stored usage data");
    usersCommand.Subcommands.Add(usageCommand);
    usersCommand.Subcommands.Add(activityCommand);
    return usersCommand;
}

static void WriteUserUsageReport(UserUsageReport report, int? top)
{
    var us = CultureInfo.GetCultureInfo("en-US");
    static string Money(decimal value, IFormatProvider culture) => value.ToString("C2", culture);

    AnsiConsole.MarkupLine(
        $"[bold]Meterist[/] — per-user usage for [yellow]{Markup.Escape(report.TenantId)}[/], "
        + $"{report.Period.Start:yyyy-MM-dd} to {report.Period.End:yyyy-MM-dd}");

    if (report.Vendors.Count == 0)
    {
        AnsiConsole.MarkupLine(
            "[yellow]No per-user rows are stored for this tenant in this period.[/] Run extract for ChatGPT or Claude "
            + "Enterprise, or backfill user-usage from raw records already stored.");
        return;
    }

    foreach (var vendor in report.Vendors)
    {
        var anyEstimated = vendor.Users.Any(u => u.IsEstimated);
        var anyKind = vendor.Users.Any(u => u.Attribution != UsageAttribution.Person);

        var table = new Table()
            .Title(Markup.Escape(vendor.Vendor.DisplayName))
            .AddColumn("User")
            .AddColumn(new TableColumn("Amount").RightAligned())
            .AddColumn(new TableColumn("Credits").RightAligned())
            .AddColumn(new TableColumn("Requests").RightAligned())
            .AddColumn(new TableColumn("Days active").RightAligned());

        var shown = top is { } n ? vendor.Users.Take(n).ToList() : vendor.Users.ToList();
        foreach (var user in shown)
        {
            var kind = user.Attribution switch
            {
                UsageAttribution.Person => string.Empty,
                UsageAttribution.ApiKey => " [grey](API key)[/]",
                UsageAttribution.Allocated => " [grey](allocated)[/]",
                _ => " [grey](not a person)[/]",
            };

            table.AddRow(
                Markup.Escape(user.DisplayName) + kind,
                (user.IsEstimated ? "~" : string.Empty) + Money(user.Amount, us),
                user.Credits is { } credits ? credits.ToString("N0", us) : "-",
                user.Requests is { } requests ? requests.ToString("N0", us) : "-",
                user.DaysActive.ToString(us));
        }

        var rest = vendor.Users.Skip(shown.Count).ToList();
        if (rest.Count > 0)
        {
            table.AddRow(
                $"[grey]and {rest.Count} more user(s)[/]",
                Money(rest.Sum(u => u.Amount), us),
                rest.Any(u => u.Credits is not null) ? rest.Sum(u => u.Credits ?? 0m).ToString("N0", us) : "-",
                rest.Any(u => u.Requests is not null) ? rest.Sum(u => u.Requests ?? 0L).ToString("N0", us) : "-",
                string.Empty);
        }

        table.AddRow(
            "[bold]All users[/]",
            "[bold]" + Money(vendor.TotalAmount, us) + "[/]",
            vendor.TotalCredits is { } totalCredits ? totalCredits.ToString("N0", us) : "-",
            vendor.TotalRequests is { } totalRequests ? totalRequests.ToString("N0", us) : "-",
            string.Empty);

        AnsiConsole.Write(table);

        var recon = vendor.Reconciliation;
        AnsiConsole.MarkupLine(
            $"  Aggregate usage on the {recon.DaysCompared} day(s) with per-user rows: {Money(recon.AggregateUsage, us)}"
            + (recon.AggregateUsage == vendor.AggregateUsage
                ? string.Empty
                : $" (all days in the period: {Money(vendor.AggregateUsage, us)})")
            + $". Seat fees for the period ({Money(vendor.AggregateSeatFee, us)}) are not part of per-user figures.");

        if (recon.Reconciles)
        {
            AnsiConsole.MarkupLine($"  [green]Per-user total matches the aggregate[/] (difference {Money(recon.Difference, us)}).");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]Per-user total {Money(recon.PerUserAmount, us)} does not match the aggregate "
                + $"{Money(recon.AggregateUsage, us)} (difference {Money(recon.Difference, us)}).[/] Usually a user the "
                + "vendor has since deleted, a day whose stored aggregate kept a higher earlier figure, or per-user "
                + "rows stored from a different pull than the daily total.");
        }

        if (recon.UsageDaysWithoutUserRows > 0)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]{recon.UsageDaysWithoutUserRows} day(s) have aggregate usage but no per-user rows[/] "
                + "(not included above); backfill or re-extract them.");
        }

        if (anyEstimated)
        {
            AnsiConsole.MarkupLine(
                "  [grey]~ estimated: dollars derived from credits, not a billed per-user amount.[/]");
        }

        if (anyKind)
        {
            AnsiConsole.MarkupLine(
                "  [grey](API key) and (not a person) rows are usage the vendor did not attribute to a named person.[/]");
        }

        AnsiConsole.WriteLine();
    }
}

static Command BuildBackfillCommand(IHost host)
{
    var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier", Required = true };
    var vendorOption = new Option<string>("--vendor")
    {
        Description = "Vendor short name (a vendor that stores per-user usage, such as claude-enterprise)",
        Required = true,
    };
    var fromOption = new Option<DateTime>("--from") { Description = "First day to rebuild", Required = true };
    var toOption = new Option<DateTime>("--to") { Description = "Last day to rebuild (inclusive)", Required = true };
    var dryRunOption = new Option<bool>("--dry-run")
    {
        Description = "Show what would be stored without writing anything",
    };

    var userUsageCommand = new Command(
        "user-usage",
        "Rebuild per-user daily usage from raw extraction records already in the database, without calling "
        + "the vendor's API. Safe to repeat.");
    userUsageCommand.Options.Add(tenantOption);
    userUsageCommand.Options.Add(vendorOption);
    userUsageCommand.Options.Add(fromOption);
    userUsageCommand.Options.Add(toOption);
    userUsageCommand.Options.Add(dryRunOption);

    userUsageCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var vendorShortName = parseResult.GetValue(vendorOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var dryRun = parseResult.GetValue(dryRunOption);

        var vendor = VendorCatalog.FindByShortName(vendorShortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(vendorShortName)}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return 1;
        }

        if (from > to)
        {
            AnsiConsole.MarkupLine("[red]--from must not be after --to.[/]");
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<UserUsageBackfill>();

        try
        {
            var result = await backfill.RunAsync(tenantId, vendor.Id, new DateRange(from, to), dryRun, cancellationToken);

            AnsiConsole.MarkupLine(
                $"[bold]Meterist[/] — per-user usage backfill for [yellow]{Markup.Escape(tenantId)}[/] / "
                + $"{Markup.Escape(vendor.DisplayName)}, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}"
                + (dryRun ? " [grey](dry run: nothing written)[/]" : string.Empty));
            AnsiConsole.MarkupLine(
                $"  Read {result.DaysRead} stored day(s), found {result.RowsNormalized} per-user row(s): "
                + $"{result.Upsert.Summarize()}.");
            if (result.DaysRead == 0)
            {
                AnsiConsole.MarkupLine("  [yellow]No raw records are stored for this tenant, vendor and period.[/]");
            }
            else if (result.RowsNormalized == 0)
            {
                AnsiConsole.MarkupLine(
                    "  [yellow]The stored raw rows carry no per-user detail for this vendor; re-run extract for these days "
                    + "to fetch it.[/]");
            }

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    });

    var breakdownBackfillCommand = new Command(
        "spend-breakdown",
        "Rebuild the per-kind spend breakdown from raw extraction records already in the database, without calling "
        + "the vendor's API. Safe to repeat. Charges added to a vendor's query later (Gemini tax and support) are "
        + "only in days pulled since; re-run extract for older days.");
    breakdownBackfillCommand.Options.Add(tenantOption);
    breakdownBackfillCommand.Options.Add(vendorOption);
    breakdownBackfillCommand.Options.Add(fromOption);
    breakdownBackfillCommand.Options.Add(toOption);
    breakdownBackfillCommand.Options.Add(dryRunOption);

    breakdownBackfillCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var vendorShortName = parseResult.GetValue(vendorOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var dryRun = parseResult.GetValue(dryRunOption);

        var vendor = VendorCatalog.FindByShortName(vendorShortName);
        if (vendor is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(vendorShortName)}'.[/] Known vendors: "
                + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
            return 1;
        }

        if (from > to)
        {
            AnsiConsole.MarkupLine("[red]--from must not be after --to.[/]");
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<SpendBreakdownBackfill>();

        try
        {
            var result = await backfill.RunAsync(tenantId, vendor.Id, new DateRange(from, to), dryRun, cancellationToken);

            AnsiConsole.MarkupLine(
                $"[bold]Meterist[/] — spend breakdown backfill for [yellow]{Markup.Escape(tenantId)}[/] / "
                + $"{Markup.Escape(vendor.DisplayName)}, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}"
                + (dryRun ? " [grey](dry run: nothing written)[/]" : string.Empty));
            AnsiConsole.MarkupLine(
                $"  Read {result.DaysRead} stored day(s), found {result.RowsNormalized} breakdown row(s): "
                + $"{result.Replace.Summarize()}.");
            if (result.DaysRead == 0)
            {
                AnsiConsole.MarkupLine("  [yellow]No raw records are stored for this tenant, vendor and period.[/]");
            }

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    });

    var backfillCommand = new Command("backfill", "Rebuild derived data from what is already stored");
    backfillCommand.Subcommands.Add(userUsageCommand);
    backfillCommand.Subcommands.Add(breakdownBackfillCommand);
    return backfillCommand;
}

static void WriteSeatAudit(TenantSeatAudit audit)
{
    foreach (var v in audit.Vendors)
    {
        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — seats, [yellow]{Markup.Escape(audit.TenantId)}[/] / {Markup.Escape(v.Vendor.DisplayName)}, as of {v.AsOf:yyyy-MM-dd}");

        static string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

        var table = new Table().AddColumns("Measure", "Seats", "From");
        table.AddRow("Billed (rate configuration)", N(v.BilledSeats), v.BilledSeats is null ? "-" : "rates");
        table.AddRow(
            "Assigned (vendor)", N(v.AssignedSeats), v.AssignedDate is { } ad ? $"activity summary {ad:yyyy-MM-dd}" : "not reported");
        table.AddRow(
            "Roster members", N(v.RosterCount),
            v.RosterDate is { } rd ? $"snapshot {rd:yyyy-MM-dd}" : "no snapshot");
        AnsiConsole.Write(table);

        if (v.RosterBySeatType.Count > 0)
        {
            AnsiConsole.MarkupLine(
                "  Roster by seat type: " + string.Join(", ", v.RosterBySeatType.Select(kv => $"{Markup.Escape(kv.Key)} {kv.Value}")) + ".");
        }

        if (v.HasDrift)
        {
            AnsiConsole.MarkupLine(
                $"  [red]Drift:[/] {v.BilledSeats} seat(s) are billed in the rate configuration but the vendor reports "
                + $"{v.VendorReportedSeats} ({v.VendorReportedSource}). Check the latest invoice and, if it changed, run "
                + "'rates set' for the new count ('activity show' lists the assigned seats day by day, to date the change). "
                + "Nothing was changed.");
        }
        else if (v.UnusedBilledSeats is { } unused)
        {
            AnsiConsole.MarkupLine(
                $"  [green]No drift:[/] no more members than billed seats. [grey]Info:[/] {unused} of the {v.BilledSeats} billed seat(s) "
                + "have no member on the roster (committed but unused, or not yet provisioned). Not drift.");
        }
        else if (v.BilledSeats is not null && v.VendorReportedSeats is not null)
        {
            AnsiConsole.MarkupLine($"  [green]No drift:[/] billed and vendor-reported seats agree ({v.BilledSeats}).");
        }

        foreach (var note in v.Notes)
        {
            AnsiConsole.MarkupLine($"  [yellow]Note:[/] {Markup.Escape(note)}");
        }

        var idle = v.Idle;
        if (!idle.Assessable)
        {
            AnsiConsole.MarkupLine($"  [yellow]Idle seats not assessed:[/] {Markup.Escape(idle.Reason ?? "no data")}");
            AnsiConsole.WriteLine();
            continue;
        }

        AnsiConsole.MarkupLine(
            $"  Idle in the last {idle.Window.End.DayNumber - idle.Window.Start.DayNumber + 1} day(s) "
            + $"({idle.Window.Start:yyyy-MM-dd} to {idle.Window.End:yyyy-MM-dd}): [bold]{idle.IdleSeats.Count}[/] of "
            + $"{N(v.RosterCount)} rostered; {idle.ActiveCount} active"
            + (idle.NewMemberCount > 0 ? $", {idle.NewMemberCount} added inside the window (not counted as idle)" : string.Empty)
            + (idle.ActiveNotOnRoster > 0 ? $"; {idle.ActiveNotOnRoster} active user(s) not on the roster (left, or not matched)" : string.Empty)
            + ".");

        if (idle.Reason is not null)
        {
            AnsiConsole.MarkupLine($"  [yellow]Caveat:[/] {Markup.Escape(idle.Reason)}");
        }

        if (idle.IdleSeats.Count > 0)
        {
            var idleTable = new Table().AddColumns("Member", "Role", "Seat type", "Last active before the window");
            foreach (var seat in idle.IdleSeats)
            {
                var who = !string.IsNullOrWhiteSpace(seat.Name) && !string.IsNullOrWhiteSpace(seat.Email)
                    ? $"{seat.Name} <{seat.Email}>"
                    : seat.Name ?? seat.Email ?? seat.MemberId;
                idleTable.AddRow(
                    Markup.Escape(who),
                    Markup.Escape(seat.Role ?? "-"),
                    Markup.Escape(seat.SeatType ?? "-"),
                    seat.LastActiveBeforeWindow is { } last ? last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "none stored");
            }

            AnsiConsole.Write(idleTable);
        }

        AnsiConsole.WriteLine();
    }
}

static Command BuildAuditCommand(IHost host)
{
    var tenantOption = new Option<string[]>("--tenant")
    {
        Description = "Tenant identifier to check (repeat for multiple tenants)",
        Required = true,
    };
    var fromOption = new Option<DateTime>("--from") { Description = "First day to check", Required = true };
    var toOption = new Option<DateTime>("--to")
    {
        Description = "Last day to check (inclusive). Leave a day or two behind today: vendors publish with "
            + "a delay, and a day they haven't published yet would read as a gap.",
        Required = true,
    };
    var vendorOption = new Option<string?>("--vendor")
    {
        Description = "Check only one vendor short name (default: all vendors)",
    };

    var coverageCommand = new Command(
        "coverage",
        "Report days with no stored spend record, per tenant and vendor. Exits with a non-zero code when a "
        + "seat-priced vendor has gaps, so a scheduled run can alert.");
    coverageCommand.Options.Add(tenantOption);
    coverageCommand.Options.Add(fromOption);
    coverageCommand.Options.Add(toOption);
    coverageCommand.Options.Add(vendorOption);

    coverageCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var vendorShortName = parseResult.GetValue(vendorOption);

        if (from > to)
        {
            AnsiConsole.MarkupLine("[red]--from must not be after --to.[/]");
            return 1;
        }

        Guid? vendorFilter = null;
        if (vendorShortName is not null)
        {
            var vendor = VendorCatalog.FindByShortName(vendorShortName);
            if (vendor is null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(vendorShortName)}'.[/] Known vendors: "
                    + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
                return 1;
            }

            vendorFilter = vendor.Id;
        }

        using var scope = host.Services.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<CoverageChecker>();
        var report = await checker.CheckAsync(tenantIds, new DateRange(from, to), vendorFilter, cancellationToken);

        WriteCoverageReport(report);

        // Non-zero means "look at this": a gap in a vendor that should have every day. Quiet days
        // of a usage-priced vendor, unbackfilled history and unused vendors never set it.
        return report.HasProblems ? 1 : 0;
    });

    var breakdownCommand = new Command(
        "gemini-breakdown",
        "Show stored Gemini Enterprise spend by kind of charge (seat, overage, Agent Platform, model usage, "
        + "pay-as-you-go) and the non-usage charges kept out of the totals (tax, support), from the stored "
        + "breakdown. Read-only.");
    breakdownCommand.Options.Add(tenantOption);
    breakdownCommand.Options.Add(fromOption);
    breakdownCommand.Options.Add(toOption);

    breakdownCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantOption)!;
        var period = new DateRange(
            DateOnly.FromDateTime(parseResult.GetValue(fromOption)),
            DateOnly.FromDateTime(parseResult.GetValue(toOption)));

        if (period.Start > period.End)
        {
            AnsiConsole.MarkupLine("[red]--from must not be after --to.[/]");
            return 1;
        }

        using var scope = host.Services.CreateScope();
        var breakdownRepository = scope.ServiceProvider.GetRequiredService<IDailySpendBreakdownRepository>();
        var dailyRepository = scope.ServiceProvider.GetRequiredService<IDailySpendRepository>();
        var us = CultureInfo.GetCultureInfo("en-US");

        foreach (var tenantId in tenantIds)
        {
            var rows = await breakdownRepository.GetAsync(
                tenantId, VendorCatalog.GeminiEnterprise.Id, period, cancellationToken);
            var stored = await dailyRepository.GetAsync(
                tenantId, VendorCatalog.GeminiEnterprise.Id, period, cancellationToken);

            AnsiConsole.MarkupLine(
                $"[bold]Meterist[/] — Gemini Enterprise spend by kind, [yellow]{Markup.Escape(tenantId)}[/], "
                + $"{period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd}");

            var totals = SpendBreakdownSummary.Summarize(rows);
            if (totals.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "  [grey]No stored breakdown rows in this period. Run extract, or backfill spend-breakdown "
                    + "from raw records already stored.[/]");
                continue;
            }

            var table = new Table().AddColumns("Kind", "In totals?", "Days with charge", "Amount", "SKUs");
            foreach (var total in totals.Where(t => !t.IsNegligible))
            {
                table.AddRow(
                    Markup.Escape(total.Category),
                    total.IsUsage ? "yes" : "[grey]no (not usage)[/]",
                    total.Days.ToString(CultureInfo.InvariantCulture),
                    total.Amount.ToString("C2", us),
                    Markup.Escape(string.Join("; ", total.Details)));
            }

            AnsiConsole.Write(table);

            var usageTotal = totals.Where(t => t.IsUsage).Sum(t => t.Amount);
            var nonUsageTotal = totals.Where(t => !t.IsUsage).Sum(t => t.Amount);
            var storedGross = stored.Sum(r => r.GrossSpend);
            var daysWithBreakdown = rows.Select(r => r.Date).Distinct().ToHashSet();
            var storedGrossOnThoseDays = stored.Where(r => daysWithBreakdown.Contains(r.Date)).Sum(r => r.GrossSpend);

            AnsiConsole.MarkupLine(
                $"  Usage categories total {usageTotal.ToString("C2", us)}; stored gross spend on the same days "
                + $"{storedGrossOnThoseDays.ToString("C2", us)}"
                + (storedGross == storedGrossOnThoseDays ? string.Empty : $" ({storedGross.ToString("C2", us)} over the whole period)")
                + "."
                + (Math.Abs(usageTotal - storedGrossOnThoseDays) > 0.005m
                    ? " [yellow]They differ: the breakdown and the daily records came from different extractions "
                      + "or rules; re-run extract for this period.[/]"
                    : string.Empty));

            var negligible = totals.Where(t => t.IsNegligible).ToList();
            if (negligible.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]{negligible.Count} kind(s) under half a cent are not listed ("
                    + string.Join(", ", negligible.Select(t => Markup.Escape(t.Category)))
                    + "); they are included in the totals.[/]");
            }

            if (nonUsageTotal != 0m)
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]Not usage, not in any total: {nonUsageTotal.ToString("C2", us)} (tax, support and similar). "
                    + "A Google invoice for this period would be about this much higher.[/]");
            }

            var daysWithoutBreakdown = stored.Count(r => r.GrossSpend != 0m && !daysWithBreakdown.Contains(r.Date));
            if (daysWithoutBreakdown > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]{daysWithoutBreakdown} day(s) have spend but no breakdown rows[/] "
                    + "(backfill spend-breakdown, or re-run extract).");
            }
        }

        return 0;
    });
    var idleDaysOption = new Option<int>("--idle-days")
    {
        Description = "A seat counts as idle when its member shows no activity in this many days",
        DefaultValueFactory = _ => 30,
    };
    var asOfOption = new Option<DateTime?>("--as-of")
    {
        Description = "The day to audit (default: today, UTC). The rate in force, the latest roster on or before it and the "
            + "window ending on it are used.",
    };
    var seatsVendorOption = new Option<string?>("--vendor")
    {
        Description = "Check only one vendor short name (default: every vendor that has a roster)",
    };

    var seatsCommand = new Command(
        "seats",
        "Compare the billed seat count in the rate configuration with what each vendor reports (assigned seats and the "
        + "roster), and list rostered members with no activity lately. Flags drift, never corrects it. Exits non-zero "
        + "when a billed count differs from the vendor's, so a scheduled run can alert. Idle seats never set the exit "
        + "code. Names people, so keep the output out of anything you commit.");
    seatsCommand.Options.Add(tenantOption);
    seatsCommand.Options.Add(idleDaysOption);
    seatsCommand.Options.Add(asOfOption);
    seatsCommand.Options.Add(seatsVendorOption);

    seatsCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantOption)!;
        var idleDays = parseResult.GetValue(idleDaysOption);
        var vendorShortName = parseResult.GetValue(seatsVendorOption);
        var asOf = parseResult.GetValue(asOfOption) is { } d
            ? DateOnly.FromDateTime(d)
            : DateOnly.FromDateTime(DateTime.UtcNow);

        if (idleDays < 1 || idleDays > SeatAuditor.MaxIdleDays)
        {
            AnsiConsole.MarkupLine($"[red]--idle-days must be between 1 and {SeatAuditor.MaxIdleDays}.[/]");
            return 1;
        }

        Guid? vendorFilter = null;
        if (vendorShortName is not null)
        {
            var vendor = VendorCatalog.FindByShortName(vendorShortName);
            if (vendor is null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown vendor short name '{Markup.Escape(vendorShortName)}'.[/] Known vendors: "
                    + string.Join(", ", VendorCatalog.All.Select(v => v.ShortName)));
                return 1;
            }

            vendorFilter = vendor.Id;
        }

        var anyDrift = false;
        foreach (var tenantId in tenantIds)
        {
            // A scope, and so a database context, per tenant.
            using var scope = host.Services.CreateScope();
            var auditor = scope.ServiceProvider.GetRequiredService<SeatAuditor>();
            var audit = await auditor.AuditAsync(tenantId, asOf, idleDays, vendorFilter, cancellationToken);
            anyDrift |= audit.HasDrift;
            WriteSeatAudit(audit);
        }

        return anyDrift ? 1 : 0;
    });

    var auditCommand = new Command("audit", "Data-quality checks on the stored spend data");
    auditCommand.Subcommands.Add(coverageCommand);
    auditCommand.Subcommands.Add(breakdownCommand);
    auditCommand.Subcommands.Add(seatsCommand);
    return auditCommand;
}

static void WriteCoverageReport(CoverageReport report)
{
    AnsiConsole.MarkupLine(
        $"[bold]Meterist[/] — coverage check, {report.Period.Start:yyyy-MM-dd} to {report.Period.End:yyyy-MM-dd}");

    static string Describe(DateRange range)
    {
        var days = range.End.DayNumber - range.Start.DayNumber + 1;
        var label = days == 1 ? "1 day" : $"{days} days";
        return range.Start == range.End
            ? $"{range.Start:yyyy-MM-dd} ({label})"
            : $"{range.Start:yyyy-MM-dd} – {range.End:yyyy-MM-dd} ({label})";
    }

    foreach (var tenant in report.Tenants)
    {
        var table = new Table()
            .Title($"{Markup.Escape(tenant.TenantId)}")
            .AddColumn("Vendor")
            .AddColumn(new TableColumn("Days stored").RightAligned())
            .AddColumn("Status")
            .AddColumn("Detail");

        foreach (var v in tenant.Vendors)
        {
            var stored = $"{v.DaysStored} / {v.DaysInPeriod}";
            var notes = new List<string>();

            string status;
            if (!v.HasAnyRecord)
            {
                status = "[grey]no data[/]";
                notes.Add("[grey]no records at all for this tenant: unused, or never extracted[/]");
            }
            else if (v.HasProblem)
            {
                status = "[red]GAPS[/]";
                notes.AddRange(v.Gaps.Select(g => $"[red]missing {Describe(g)}[/]"));
            }
            else if (!v.Vendor.ExpectsRecordEveryDay)
            {
                status = "[green]OK[/]";
                if (v.GapDays > 0)
                {
                    notes.Add($"[grey]{v.GapDays} day(s) without a record: normal for a usage-priced vendor[/]");
                }
            }
            else
            {
                status = "[green]OK[/]";
            }

            if (v.NotYetBackfilled is { } leading)
            {
                notes.Add($"[yellow]not yet backfilled: {Describe(leading)}[/]");
            }

            table.AddRow(
                Markup.Escape(v.Vendor.DisplayName), stored, status, string.Join("\n", notes));
        }

        AnsiConsole.Write(table);
    }

    AnsiConsole.MarkupLine(report.HasProblems
        ? "[red]Coverage problems found: a vendor that should have a record every day is missing some.[/]"
        : "[green]No coverage problems.[/]");
}

static Command BuildReportCommand(IHost host)
{
    var tenantOption = new Option<string[]>("--tenant")
    {
        Description = "Tenant identifier to include (repeat for multiple tenants)",
        Required = true,
    };
    var fromOption = new Option<DateTime>("--from") { Description = "Start of the report period", Required = true };
    var toOption = new Option<DateTime>("--to") { Description = "End of the report period (inclusive)", Required = true };
    var outOption = new Option<string>("--out")
    {
        Description = "Output HTML file path",
        DefaultValueFactory = _ => "artifacts/client-spend-benchmark-report.html",
    };

    var compareToOption = new Option<string?>("--compare-to")
    {
        Description = "A manifest from an earlier report to compare against (default: the newest one beside the output file)",
    };
    var toleranceOption = new Option<decimal>("--diff-tolerance")
    {
        Description = "Dollars a weekly figure may move before it counts as changed",
        DefaultValueFactory = _ => ReportDiffer.DefaultTolerance,
    };
    var failOnDecreaseOption = new Option<bool>("--fail-on-decrease")
    {
        Description = "Exit non-zero when any weekly figure fell since the previous report (the report is still written)",
    };

    var generateCommand = new Command("generate", "Generate the Client Spend & Benchmark Report as HTML");
    generateCommand.Options.Add(tenantOption);
    generateCommand.Options.Add(fromOption);
    generateCommand.Options.Add(toOption);
    generateCommand.Options.Add(outOption);
    generateCommand.Options.Add(compareToOption);
    generateCommand.Options.Add(toleranceOption);
    generateCommand.Options.Add(failOnDecreaseOption);

    generateCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var outPath = parseResult.GetValue(outOption)!;
        var compareTo = parseResult.GetValue(compareToOption);
        var tolerance = parseResult.GetValue(toleranceOption);
        var failOnDecrease = parseResult.GetValue(failOnDecreaseOption);

        if (tolerance < 0m)
        {
            AnsiConsole.MarkupLine("[red]--diff-tolerance cannot be negative.[/]");
            return 1;
        }

        // A baseline the user named must exist: silently comparing with nothing would look like a clean report.
        if (compareTo is not null && !File.Exists(compareTo))
        {
            AnsiConsole.MarkupLine($"[red]The manifest named by --compare-to does not exist:[/] {Markup.Escape(compareTo)}");
            return 1;
        }


        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — generating report for [yellow]{string.Join(", ", tenantIds)}[/], "
            + $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}");

        using var scope = host.Services.CreateScope();
        var aggregator = scope.ServiceProvider.GetRequiredService<ReportDataAggregator>();
        var data = await aggregator.AggregateAsync(
            tenantIds, new DateRange(from, to), cancellationToken: cancellationToken);

        var manifest = ReportManifest.From(data);
        // A named baseline is used as given; otherwise the newest manifest in the output folder for the same tenants.
        ReportManifestStore.Baseline baseline;
        if (compareTo is not null)
        {
            baseline = ReportManifestStore.TryLoad(compareTo, out var named, out var namedProblem)
                ? new ReportManifestStore.Baseline(compareTo, named, null)
                : new ReportManifestStore.Baseline(compareTo, null, namedProblem);
        }
        else
        {
            baseline = ReportManifestStore.ResolveBaseline(outPath, manifest);
        }

        var baselinePath = baseline.Path;
        ReportDiff? diff = null;
        if (baselinePath is not null)
        {
            diff = baseline.Manifest is not null
                ? ReportDiffer.Compare(baseline.Manifest, manifest, tolerance)
                : ReportDiff.NotCompared($"the previous manifest ({Path.GetFileName(baselinePath)}) cannot be used: {baseline.Problem}", tolerance);
        }

        var htmlContent = ClientBenchmarkReportRenderer.Render(data, diff);

        var outDirectory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDirectory))
        {
            Directory.CreateDirectory(outDirectory);
        }
        await File.WriteAllTextAsync(outPath, htmlContent, cancellationToken);

        // Never overwrites an earlier manifest: the baseline that looked right has to outlive a run that shows it gone.
        var manifestPath = await ReportManifestStore.WriteAsync(
            manifest, ReportManifestStore.PathFor(outPath, data.GeneratedAt), cancellationToken);

        AnsiConsole.MarkupLine($"[green]Wrote[/] {outPath}");
        AnsiConsole.MarkupLine($"[green]Wrote[/] {manifestPath} [dim](weekly figures, for comparing the next report; financial data, keep out of git)[/]");

        if (diff is null)
        {
            AnsiConsole.MarkupLine("[dim]No previous report manifest to compare with.[/]");
            return 0;
        }

        if (!diff.Compared)
        {
            AnsiConsole.MarkupLine($"[yellow]Not compared with the previous report:[/] {Markup.Escape(diff.NotComparedReason ?? string.Empty)}.");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[dim]Compared with {Markup.Escape(Path.GetFileName(baselinePath!))}: {diff.WeeksCompared} vendor week(s) compared; "
            + $"{diff.WeeksOnlyInCurrent} new, {diff.WeeksOnlyInPrevious} only in the previous, {diff.WeeksCoveringDifferentDays} not comparable (different days).[/]");

        if (diff.Changes.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No weekly figure moved since the previous report.[/]");
            return 0;
        }

        foreach (var change in diff.Changes)
        {
            var colour = change.Kind == WeekChangeKind.Decrease ? "red" : "yellow";
            AnsiConsole.MarkupLine(
                $"[{colour}]{change.Kind.ToString().ToUpperInvariant()}[/] {Markup.Escape(change.TenantId)} / {Markup.Escape(change.Vendor)} "
                + $"week {change.WeekStart:yyyy-MM-dd}{(change.WeekMissing ? " (MISSING from this report)" : string.Empty)}: {change.Previous.GrossSpend:N2} -> {change.Current.GrossSpend:N2} "
                + $"(seat {change.Previous.SeatFee:N2} -> {change.Current.SeatFee:N2}, usage {change.Previous.UsageOrOverage:N2} -> {change.Current.UsageOrOverage:N2})");
        }

        if (diff.Decreases > 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]{diff.Decreases} week(s) went down since the previous report.[/] Stored usage should not fall: find out why before trusting this report.");
            return failOnDecrease ? 1 : 0;
        }

        return 0;
    });

    var reportCommand = new Command("report", "Report generation commands");
    reportCommand.Subcommands.Add(generateCommand);
    return reportCommand;
}
