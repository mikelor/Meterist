using System.CommandLine;
using Meterist.Core.Vendors;
using Meterist.Data;
using Meterist.Secrets;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.ClaudeApiPlatform;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.GeminiEnterprise;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddMeteristData();
builder.Services.AddMeteristSecretStore();

builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ClaudeApiPlatformSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, ChatGptEnterpriseSpendExtractor>();
builder.Services.AddSingleton<IVendorSpendExtractor, GeminiEnterpriseSpendExtractor>();

using var host = builder.Build();

var tenantOption = new Option<string>("--tenant")
{
    Description = "Tenant identifier to extract spend for",
    Required = true
};

var weekOption = new Option<DateTime>("--week")
{
    Description = "Week-start date (Sunday) to extract spend for",
    Required = true
};

var extractCommand = new Command("extract", "Extract weekly spend for a tenant across all configured vendors");
extractCommand.Options.Add(tenantOption);
extractCommand.Options.Add(weekOption);

extractCommand.SetAction(parseResult =>
{
    var tenantId = parseResult.GetValue(tenantOption);
    var week = parseResult.GetValue(weekOption);

    var extractors = host.Services.GetServices<IVendorSpendExtractor>();

    AnsiConsole.MarkupLine(
        $"[bold]Meterist[/] — extraction scaffold for tenant [yellow]{tenantId}[/], week of [yellow]{week:yyyy-MM-dd}[/]");

    var table = new Table()
        .AddColumn("Vendor")
        .AddColumn("Supports Overage")
        .AddColumn("Supports Per-User");

    foreach (var extractor in extractors)
    {
        table.AddRow(extractor.VendorName, extractor.SupportsOverage.ToString(), extractor.SupportsPerUserBreakdown.ToString());
    }

    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine(
        "[grey]Vendor adapters are scaffolded but not yet implemented — see docs/architecture.md §13 for what's still blocking each one.[/]");
});

var rootCommand = new RootCommand("Meterist — AI vendor spend extraction tool");
rootCommand.Subcommands.Add(extractCommand);

return rootCommand.Parse(args).Invoke();
