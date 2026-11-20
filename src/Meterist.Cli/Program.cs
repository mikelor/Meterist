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
builder.Services.AddSingleton<IVendorSpendNormalizer, ChatGptEnterpriseSpendNormalizer>();
builder.Services.AddSingleton<IVendorUserUsageNormalizer, ChatGptEnterpriseUserUsageNormalizer>();

builder.Services.AddHttpClient<IClaudeCostReportRepository, HttpClaudeCostReportRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddSingleton<IVendorSpendNormalizer, ClaudeApiPlatformSpendNormalizer>();

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
builder.Services.AddHttpClient<IClaudeMemberRosterRepository, HttpClaudeMemberRosterRepository>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
});
builder.Services.AddSingleton<IVendorRosterSource, ClaudeEnterpriseRosterSource>();

builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeApiPlatformSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ChatGptEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, GeminiEnterpriseSpendExtractor>();

builder.Services.AddScoped<SpendExtractionService>();
builder.Services.AddScoped<ConfiguredVendorExtraction>();
builder.Services.AddScoped<ReportDataAggregator>();
builder.Services.AddScoped<CoverageChecker>();
builder.Services.AddScoped<UserUsageBackfill>();
builder.Services.AddScoped<SpendBreakdownBackfill>();
builder.Services.AddScoped<RosterSnapshotService>();
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
        AnsiConsole.MarkupLine("[green]Nothing would change.[/]");
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

        using var scope = host.Services.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<ConfiguredRosterSnapshots>();
        var all = new List<RosterRunResult>();

        foreach (var tenantId in tenantIds)
        {
            var results = await snapshots.TakeAsync(tenantId, dryRun, cancellationToken);
            all.AddRange(results);

            var table = new Table()
                .Title($"{Markup.Escape(tenantId)} roster snapshot{(dryRun ? " (dry run)" : string.Empty)}")
                .AddColumn("Vendor")
                .AddColumn("Status")
                .AddColumn("Detail");

            foreach (var r in results)
            {
                var detail = r.Status == RosterRunStatus.Succeeded && r.Outcome is { } o
                    ? $"{o.MemberCount} member(s): {string.Join(", ", o.RoleCounts.Select(kv => $"{kv.Key} {kv.Value}"))}; "
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

        var table = new Table().AddColumns("Member", "Role", "Seat type", "Added");
        foreach (var member in snapshot.Members)
        {
            var who = !string.IsNullOrWhiteSpace(member.Name) && !string.IsNullOrWhiteSpace(member.Email)
                ? $"{member.Name} <{member.Email}>"
                : member.Name ?? member.Email ?? member.MemberId;
            table.AddRow(
                Markup.Escape(who),
                Markup.Escape(member.Role ?? "-"),
                Markup.Escape(member.SeatType ?? "-"),
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

    var usersCommand = new Command("users", "Per-user views of the stored usage data");
    usersCommand.Subcommands.Add(usageCommand);
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
    var auditCommand = new Command("audit", "Data-quality checks on the stored spend data");
    auditCommand.Subcommands.Add(coverageCommand);
    auditCommand.Subcommands.Add(breakdownCommand);
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

    var generateCommand = new Command("generate", "Generate the Client Spend & Benchmark Report as HTML");
    generateCommand.Options.Add(tenantOption);
    generateCommand.Options.Add(fromOption);
    generateCommand.Options.Add(toOption);
    generateCommand.Options.Add(outOption);

    generateCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantIds = parseResult.GetValue(tenantOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var outPath = parseResult.GetValue(outOption)!;

        AnsiConsole.MarkupLine(
            $"[bold]Meterist[/] — generating report for [yellow]{string.Join(", ", tenantIds)}[/], "
            + $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}");

        using var scope = host.Services.CreateScope();
        var aggregator = scope.ServiceProvider.GetRequiredService<ReportDataAggregator>();
        var data = await aggregator.AggregateAsync(
            tenantIds, new DateRange(from, to), cancellationToken: cancellationToken);
        var htmlContent = ClientBenchmarkReportRenderer.Render(data);

        var outDirectory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDirectory))
        {
            Directory.CreateDirectory(outDirectory);
        }
        await File.WriteAllTextAsync(outPath, htmlContent, cancellationToken);

        AnsiConsole.MarkupLine($"[green]Wrote[/] {outPath}");
        return 0;
    });

    var reportCommand = new Command("report", "Report generation commands");
    reportCommand.Subcommands.Add(generateCommand);
    return reportCommand;
}
