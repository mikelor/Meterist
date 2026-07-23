using System.CommandLine;
using System.Net.Http;
using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Meterist.Data;
using Meterist.Secrets;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.ClaudeApiPlatform;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.GeminiEnterprise;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddSingleton<IGeminiBillingQueryRepository, BigQueryGeminiBillingRepository>();
builder.Services.AddSingleton<IVendorSpendNormalizer, GeminiEnterpriseSpendNormalizer>();

// AllowAutoRedirect must stay false: the download endpoint's 307 points to a
// pre-signed URL that must never receive our Authorization header — see
// HttpChatGptCostLogRepository's doc comment.
builder.Services.AddHttpClient<IChatGptCostLogRepository, HttpChatGptCostLogRepository>(client =>
    {
        client.BaseAddress = new Uri("https://api.chatgpt.com/");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IVendorSpendNormalizer, ChatGptEnterpriseSpendNormalizer>();

builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeApiPlatformSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ChatGptEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, GeminiEnterpriseSpendExtractor>();

builder.Services.AddScoped<SpendExtractionService>();

using var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MeteristDbContext>().Database.MigrateAsync();
}

var rootCommand = new RootCommand("Meterist — AI vendor spend extraction tool");
rootCommand.Subcommands.Add(BuildCredentialsCommand(host));
rootCommand.Subcommands.Add(BuildRatesCommand(host));
rootCommand.Subcommands.Add(BuildExtractCommand(host));

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

        var effectiveToValue = parseResult.GetValue(effectiveToOption);

        var rate = new VendorRateConfig
        {
            TenantId = parseResult.GetValue(tenantOption),
            VendorId = vendor.Id,
            RateType = parseResult.GetValue(rateTypeOption)!,
            ModelOrSku = parseResult.GetValue(modelOrSkuOption),
            Rate = parseResult.GetValue(rateOption),
            SeatCount = parseResult.GetValue(seatsOption),
            BillingCadence = cadence,
            EffectiveFrom = DateOnly.FromDateTime(parseResult.GetValue(effectiveFromOption)),
            EffectiveTo = effectiveToValue is { } effectiveTo ? DateOnly.FromDateTime(effectiveTo) : null,
        };

        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IVendorRateConfigRepository>();

        // Closes out any previous open-ended rate in the same scope before
        // adding the new one, so a contract renewal never leaves two
        // overlapping windows for the same (tenant-or-public, vendor,
        // model-or-sku) — see IVendorRateConfigRepository's doc comment.
        var closedCount = await repository.CloseOpenEndedRateAsync(
            rate.TenantId, rate.VendorId, rate.ModelOrSku, rate.EffectiveFrom, cancellationToken);

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

    var extractCommand = new Command("extract", "Extract spend for a tenant across a date range");
    extractCommand.Options.Add(tenantOption);
    extractCommand.Options.Add(fromOption);
    extractCommand.Options.Add(toOption);
    extractCommand.Options.Add(vendorOption);

    extractCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var tenantId = parseResult.GetValue(tenantOption)!;
        var from = DateOnly.FromDateTime(parseResult.GetValue(fromOption));
        var to = DateOnly.FromDateTime(parseResult.GetValue(toOption));
        var vendorShortName = parseResult.GetValue(vendorOption);

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
            $"[bold]Meterist[/] — extracting for tenant [yellow]{tenantId}[/], {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");

        using var scope = host.Services.CreateScope();
        var extractionService = scope.ServiceProvider.GetRequiredService<SpendExtractionService>();
        var results = await extractionService.ExtractAsync(
            tenantId, new DateRange(from, to), vendorFilter, cancellationToken);

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
                _ => "[red]Failed[/]",
            };

            table.AddRow(result.DisplayName, statusMarkup, result.RecordCount.ToString(), result.Detail ?? string.Empty);
        }

        AnsiConsole.Write(table);
        return 0;
    });

    return extractCommand;
}
