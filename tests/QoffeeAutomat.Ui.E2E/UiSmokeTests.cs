using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace QoffeeAutomat.Ui.E2E;

/// <summary>
/// Browser smoke test of the UI (PLAN.md T408): one walk through the main user journey against the published agent with the
/// simulated platform. It is a single test on purpose: the steps build on each other (the password, the command, the
/// sensor) and a single browser session keeps the run short. A Playwright trace of a failed run is written to
/// <c>playwright-traces/</c>.
/// </summary>
[Trait("Category", "E2E")]
public sealed class UiSmokeTests : IClassFixture<AgentFixture>
{
    private const string Password = "E2E-smoke-password-1!";
    private const string CommandId = "smoke_greeting";
    private const string SensorId = "smoke_sensor";

    private static readonly float StepTimeout = 30_000;

    private readonly AgentFixture _agent;

    /// <summary>Creates the test with the running agent.</summary>
    /// <param name="agent">The agent fixture.</param>
    public UiSmokeTests(AgentFixture agent)
    {
        _agent = agent;
    }

    [Fact]
    public async Task User_journey_from_setup_to_export()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-US",
            AcceptDownloads = true,
            BaseURL = _agent.BaseUri.ToString(),
        });
        context.SetDefaultTimeout(StepTimeout);
        context.SetDefaultNavigationTimeout(StepTimeout);
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });

        IPage page = await context.NewPageAsync();
        bool failed = true;
        try
        {
            await FirstRunSetupAsync(page);
            await SignOutAndLogInAsync(page);
            await CreateCommandAsync(page);
            await ToggleEnabledAsync(page);
            await CreateSensorAsync(page);
            await StartPairingAsync(page);
            await ExportConfigurationAsync(page);
            failed = false;
        }
        catch (Exception) when (WriteFailureNotes())
        {
            throw;
        }
        finally
        {
            if (failed)
            {
                Directory.CreateDirectory(AgentFixture.TraceDirectory);
                await context.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(AgentFixture.TraceDirectory, "ui-smoke-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip"),
                });
            }
            else
            {
                await context.Tracing.StopAsync();
            }
        }
    }

    private static async Task GoToAsync(IPage page, string href)
    {
        // In-app navigation through the menu keeps the WebAssembly app and its session running.
        await page.Locator($"a[href='{href}']").First.ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(href)));
    }

    private async Task FirstRunSetupAsync(IPage page)
    {
        await page.GotoAsync("/setup");
        ILocator inputs = page.Locator("form input");
        await Expect(inputs).ToHaveCountAsync(3);
        await inputs.Nth(0).FillAsync(_agent.SetupToken);
        await inputs.Nth(1).FillAsync(Password);
        await inputs.Nth(2).FillAsync(Password);
        await page.Locator("form button[type=submit]").ClickAsync();

        // Setup signs in and lands on the dashboard.
        await Expect(page.GetByTestId("agent-status")).ToBeVisibleAsync();
    }

    private static async Task SignOutAndLogInAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign out" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/login"));

        await page.Locator("form input[type=password]").FillAsync(Password);
        await page.Locator("form button[type=submit]").ClickAsync();
        await Expect(page.GetByTestId("agent-status")).ToBeVisibleAsync();
    }

    private static async Task CreateCommandAsync(IPage page)
    {
        await GoToAsync(page, "/commands");
        await page.GetByTestId("command-new").ClickAsync();
        await Expect(page.GetByTestId("section-general")).ToBeVisibleAsync();

        await page.GetByTestId("field-name").Locator("input").FillAsync("Smoke greeting");
        await page.GetByTestId("field-id").Locator("input").FillAsync(CommandId);
        await page.GetByTestId("field-action-type").SelectOptionAsync("powershell");

        // The variable the script takes: a text parameter named "who".
        await page.GetByTestId("add-parameter").ClickAsync();
        await page.GetByTestId("param-0-name").Locator("input").FillAsync("who");

        // PowerShell code in the Monaco editor. Typing would trigger auto-closing of brackets and quotes, so the model is set.
        await Expect(page.GetByTestId("code-editor").Locator(".monaco-editor").First).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("() => window.monaco && window.monaco.editor.getModels().length > 0");
        await page.EvaluateAsync(
            "code => window.monaco.editor.getModels()[0].setValue(code)",
            "param([string]$who)\nWrite-Output \"Hello $who\"");

        // Test run with a value for the variable; the draft is not saved yet.
        await page.GetByTestId("test-var-who").Locator("input").FillAsync("E2E");
        await page.GetByTestId("test-submit").ClickAsync();
        await Expect(page.GetByTestId("test-result")).ToHaveAttributeAsync("data-state", "succeeded");
        // Under --platform simulated, SimulatedProcessRunner echoes the pwsh wrapper invocation as stdout.
        await Expect(page.GetByTestId("test-output")).ToContainTextAsync("wrapper.ps1");

        await page.GetByTestId("editor-save").ClickAsync();
        await Expect(page.GetByTestId("editor-saved")).ToBeVisibleAsync();
    }

    private static async Task ToggleEnabledAsync(IPage page)
    {
        await GoToAsync(page, "/commands");
        ILocator row = page.GetByTestId("command-" + CommandId);
        await Expect(row).ToBeVisibleAsync();

        string before = await row.GetAttributeAsync("data-enabled") ?? string.Empty;
        string after = before == "true" ? "false" : "true";
        await page.GetByTestId("enabled-" + CommandId).Locator("input").ClickAsync(new LocatorClickOptions { Force = true });
        await Expect(row).ToHaveAttributeAsync("data-enabled", after);

        // The change is stored by the agent: it is still there after a reload.
        await page.ReloadAsync();
        await Expect(page.GetByTestId("command-" + CommandId)).ToHaveAttributeAsync("data-enabled", after);
    }

    private static async Task CreateSensorAsync(IPage page)
    {
        await GoToAsync(page, "/sensors");
        await page.GetByTestId("sensor-new").ClickAsync();
        await Expect(page.GetByTestId("sensor-section-general")).ToBeVisibleAsync();

        await page.GetByTestId("sensor-field-name").Locator("input").FillAsync("Smoke sensor");
        await page.GetByTestId("sensor-field-id").Locator("input").FillAsync(SensorId);
        await page.GetByTestId("sensor-field-source-type").SelectOptionAsync("powershell");
        await page.GetByTestId("sensor-field-code").Locator("textarea").First.FillAsync("42");

        await page.GetByTestId("sensor-test").ClickAsync();
        await Expect(page.GetByTestId("sensor-test-success")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("sensor-test-value")).ToContainTextAsync("42");

        await page.GetByTestId("sensor-save").ClickAsync();
        await Expect(page.GetByTestId("sensor-saved")).ToBeVisibleAsync();

        await GoToAsync(page, "/sensors");
        await Expect(page.GetByTestId("sensor-" + SensorId)).ToBeVisibleAsync();
    }

    private static async Task StartPairingAsync(IPage page)
    {
        await GoToAsync(page, "/link");
        await page.GetByTestId("link-pair").ClickAsync();

        ILocator code = page.GetByTestId("pairing-code");
        await Expect(code).ToBeVisibleAsync();
        await Expect(code).ToHaveTextAsync(new Regex(@"\S{4,}"));
        string text = (await code.InnerTextAsync()).Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "The pairing code is empty.");
    }

    private static async Task ExportConfigurationAsync(IPage page)
    {
        await GoToAsync(page, "/backup");
        IDownload download = await page.RunAndWaitForDownloadAsync(
            () => page.GetByTestId("export-start").ClickAsync());

        string path = Path.Combine(Path.GetTempPath(), "qoffee-e2e-export-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await download.SaveAsAsync(path);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Assert.NotEmpty(bytes);
            Assert.True(bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', "The export is not a zip archive.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private bool WriteFailureNotes()
    {
        // Evaluated as an exception filter: the agent output goes to the test output of the failed run.
        Console.Error.WriteLine("Agent output:" + Environment.NewLine + _agent.Log);
        return false;
    }
}
