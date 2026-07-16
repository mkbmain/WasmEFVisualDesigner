using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;
using Xunit;

namespace EfSchemaVisualizer.SmokeTests;

/// <summary>
/// Boots a real headless Chromium against the actual <c>dotnet publish</c>
/// output and confirms the Blazor WebAssembly app renders, rather than just
/// compiling and unit-testing its logic in isolation. Requires the published
/// <c>wwwroot</c> directory to already exist (set via the
/// <c>SMOKE_TEST_PUBLISH_DIR</c> environment variable) and the Playwright
/// Chromium browser to be installed; skips itself when either precondition
/// isn't met, so a plain local `dotnet test` doesn't fail.
/// </summary>
public class AppBootSmokeTests
{
    [Fact]
    public async Task PublishedApp_BootsAndRendersWithoutConsoleErrors()
    {
        var publishDir = Environment.GetEnvironmentVariable("SMOKE_TEST_PUBLISH_DIR");
        if (string.IsNullOrWhiteSpace(publishDir) || !Directory.Exists(publishDir))
        {
            return; // No published output to smoke-test against (e.g. a local `dotnet test` run).
        }

        var indexHtmlPath = Path.Combine(publishDir, "index.html");
        Assert.True(File.Exists(indexHtmlPath), $"Expected {indexHtmlPath} to exist in the published output.");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var fileProvider = new PhysicalFileProvider(publishDir);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            // Blazor WASM ships extensions (.dat, .blat, .wasm data segments,
            // etc.) a bare static-file server doesn't have MIME mappings for;
            // without this, ASP.NET Core 404s them instead of serving with a
            // generic content type, and the runtime never finishes booting.
            ServeUnknownFileTypes = true,
        });
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        var consoleErrors = new List<string>();
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
            {
                consoleErrors.Add(msg.Text);
            }
        };
        page.PageError += (_, error) => consoleErrors.Add(error);
        page.Response += (_, response) =>
        {
            if (!response.Ok)
            {
                consoleErrors.Add($"{(int)response.Status} {response.Url}");
            }
        };

        await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 120_000 });

        // The app renders its two source textareas once Blazor WASM has
        // finished booting and Home.razor has run its first render. The WASM
        // runtime payload is tens of megabytes (see docs/backlog.md's
        // WASM-payload-size item), so first boot is slow — give it a
        // generous timeout rather than the Playwright default.
        try
        {
            await page.WaitForSelectorAsync("textarea", new PageWaitForSelectorOptions { Timeout = 120_000 });
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"App did not render in time; observed errors:\n{string.Join('\n', consoleErrors)}");
        }

        Assert.Empty(consoleErrors);

        await app.StopAsync();
    }
}
