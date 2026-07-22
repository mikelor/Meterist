using System.CommandLine;
using Meterist.Core.Extraction;
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
