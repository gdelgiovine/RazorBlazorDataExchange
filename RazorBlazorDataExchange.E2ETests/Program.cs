using Microsoft.Playwright;
using System.Diagnostics;

internal static class Program
{
    private const int TimeoutMs = 20_000;

    private static async Task Main()
    {
        var baseUrl = Environment.GetEnvironmentVariable("RBDX_BASE_URL")
            ?? "http://127.0.0.1:5087";

        Console.WriteLine($"RazorBlazorDataExchange 0.5 browser E2E tests against {baseUrl}");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var sharedSessionContext = await browser.NewContextAsync();
        var isolatedSessionContext = await browser.NewContextAsync();

        try
        {
            await TestBidirectionalSamePageAsync(sharedSessionContext, baseUrl);
            await TestCrossCircuitSameSessionAsync(sharedSessionContext, baseUrl);
            await TestSessionIsolationAsync(sharedSessionContext, isolatedSessionContext, baseUrl);
        }
        finally
        {
            await isolatedSessionContext.CloseAsync();
            await sharedSessionContext.CloseAsync();
        }

        Console.WriteLine("ALL BROWSER E2E TESTS PASSED");
    }

    private static async Task TestBidirectionalSamePageAsync(IBrowserContext context, string baseUrl)
    {
        var page = await context.NewPageAsync();
        await NavigateWithRetryAsync(page, baseUrl);
        await WaitForInteractiveComponentsAsync(page);
        await WaitForAllVisibleCountersAsync(page, 0);

        var component1Actor = await ReadActorIdAsync(page, "CounterComponent1");
        var component2Actor = await ReadActorIdAsync(page, "CounterComponent2");
        Assert(component1Actor != component2Actor, "Two component instances in the same circuit share the same ActorId.");

        // Razor -> Blazor #1 + Blazor #2.
        await page.Locator("[data-testid='increment-razor']").ClickAsync();
        await WaitForAllVisibleCountersAsync(page, 1);
        await WaitForTextAsync(page, "[data-testid='last-update-source']", "Razor:/Index");

        // Blazor #1 -> Razor DOM + Blazor #2.
        await page.Locator("[data-component-id='CounterComponent1'] [data-testid='increment-blazor']").ClickAsync();
        await WaitForAllVisibleCountersAsync(page, 2);
        await WaitForTextAsync(page, "[data-testid='last-update-source']", component1Actor);

        // Blazor #2 -> Razor DOM + Blazor #1.
        await page.Locator("[data-component-id='CounterComponent2'] [data-testid='increment-blazor']").ClickAsync();
        await WaitForAllVisibleCountersAsync(page, 3);
        await WaitForTextAsync(page, "[data-testid='last-update-source']", component2Actor);

        // The Blazor publication is persisted in the singleton broker and is visible to
        // a subsequent Razor HTTP request, not only to the already-rendered DOM via JS.
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForInteractiveComponentsAsync(page);
        await WaitForAllVisibleCountersAsync(page, 3);

        Console.WriteLine("PASS same-page Razor <-> Blazor bidirectional synchronization");
        await page.CloseAsync();
    }

    private static async Task TestCrossCircuitSameSessionAsync(IBrowserContext context, string baseUrl)
    {
        var pageA = await context.NewPageAsync();
        var pageB = await context.NewPageAsync();

        await NavigateWithRetryAsync(pageA, baseUrl);
        await NavigateWithRetryAsync(pageB, baseUrl);
        await WaitForInteractiveComponentsAsync(pageA);
        await WaitForInteractiveComponentsAsync(pageB);

        var initial = await ReadIntAsync(pageA, "[data-testid='razor-counter-value']");
        await WaitForBlazorCountersAsync(pageA, initial);
        await WaitForAllVisibleCountersAsync(pageB, initial);

        // pageA and pageB share the ASP.NET session cookie but have independent Blazor circuits.
        var sessionA = await ReadTextAsync(pageA, "[data-testid='razor-session-id']");
        var sessionB = await ReadTextAsync(pageB, "[data-testid='razor-session-id']");
        Assert(sessionA == sessionB, "Two tabs in the same browser context did not share the exchange session.");

        var pageAActor1 = await ReadActorIdAsync(pageA, "CounterComponent1");
        var pageBActor1 = await ReadActorIdAsync(pageB, "CounterComponent1");
        Assert(
            pageAActor1 != pageBActor1,
            "Equal logical ComponentIds in separate circuits resolved to the same ActorId.");

        // Razor request in circuit/page B fans out to Blazor components in BOTH circuits.
        await pageB.Locator("[data-testid='increment-razor']").ClickAsync();
        var afterRazor = initial + 1;
        await WaitForAllVisibleCountersAsync(pageB, afterRazor);
        await WaitForBlazorCountersAsync(pageA, afterRazor);

        // pageA Razor DOM is intentionally not a live broker subscriber after its HTTP response.
        Assert(
            await ReadIntAsync(pageA, "[data-testid='razor-counter-value']") == initial,
            "A stale Razor DOM was unexpectedly treated as a live server-side subscriber.");

        // Blazor in circuit A publishes to peer Blazor components in both circuits.
        await pageA.Locator("[data-component-id='CounterComponent1'] [data-testid='increment-blazor']").ClickAsync();
        var afterBlazor = afterRazor + 1;
        await WaitForAllVisibleCountersAsync(pageA, afterBlazor);
        await WaitForBlazorCountersAsync(pageB, afterBlazor);

        // pageB gets the server-side value on its next Razor request.
        await pageB.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForInteractiveComponentsAsync(pageB);
        await WaitForAllVisibleCountersAsync(pageB, afterBlazor);

        Console.WriteLine("PASS same-session cross-circuit fan-out");
        await pageB.CloseAsync();
        await pageA.CloseAsync();
    }

    private static async Task TestSessionIsolationAsync(
        IBrowserContext sharedContext,
        IBrowserContext isolatedContext,
        string baseUrl)
    {
        var sharedPage = await sharedContext.NewPageAsync();
        var isolatedPage = await isolatedContext.NewPageAsync();

        await NavigateWithRetryAsync(sharedPage, baseUrl);
        await NavigateWithRetryAsync(isolatedPage, baseUrl);
        await WaitForInteractiveComponentsAsync(sharedPage);
        await WaitForInteractiveComponentsAsync(isolatedPage);

        var sharedSession = await ReadTextAsync(sharedPage, "[data-testid='razor-session-id']");
        var isolatedSession = await ReadTextAsync(isolatedPage, "[data-testid='razor-session-id']");
        Assert(sharedSession != isolatedSession, "Independent browser contexts unexpectedly share the same session.");

        var sharedValueBefore = await ReadIntAsync(sharedPage, "[data-testid='razor-counter-value']");
        await WaitForAllVisibleCountersAsync(isolatedPage, 0);

        await isolatedPage.Locator("[data-testid='increment-razor']").ClickAsync();
        await WaitForAllVisibleCountersAsync(isolatedPage, 1);

        // The other session must remain unchanged, including its live Blazor subscribers.
        await WaitForBlazorCountersAsync(sharedPage, sharedValueBefore);
        Assert(
            await ReadIntAsync(sharedPage, "[data-testid='razor-counter-value']") == sharedValueBefore,
            "An update leaked across exchange sessions.");

        Console.WriteLine("PASS browser-session isolation");
        await isolatedPage.CloseAsync();
        await sharedPage.CloseAsync();
    }

    private static async Task NavigateWithRetryAsync(IPage page, string baseUrl)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                var response = await page.GotoAsync(baseUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 5_000
                });

                if (response?.Ok == true)
                    return;

                lastError = new InvalidOperationException($"HTTP status {response?.Status} while opening {baseUrl}.");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException($"Tester application did not become reachable at {baseUrl}.", lastError);
    }

    private static async Task WaitForInteractiveComponentsAsync(IPage page)
    {
        await page.Locator("[data-component-id='CounterComponent1'] [data-testid='increment-blazor']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = TimeoutMs });

        await page.Locator("[data-component-id='CounterComponent2'] [data-testid='increment-blazor']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = TimeoutMs });

        await WaitForTextPrefixAsync(
            page,
            "[data-component-id='CounterComponent1'] [data-testid='actor-id']",
            "Blazor:CounterComponent:CounterComponent1:");
        await WaitForTextPrefixAsync(
            page,
            "[data-component-id='CounterComponent2'] [data-testid='actor-id']",
            "Blazor:CounterComponent:CounterComponent2:");
    }

    private static Task WaitForAllVisibleCountersAsync(IPage page, int expected)
        => Task.WhenAll(
            WaitForIntAsync(page, "[data-testid='razor-counter-value']", expected),
            WaitForIntAsync(page, "[data-component-id='CounterComponent1'] [data-testid='counter-value']", expected),
            WaitForIntAsync(page, "[data-component-id='CounterComponent2'] [data-testid='counter-value']", expected));

    private static Task WaitForBlazorCountersAsync(IPage page, int expected)
        => Task.WhenAll(
            WaitForIntAsync(page, "[data-component-id='CounterComponent1'] [data-testid='counter-value']", expected),
            WaitForIntAsync(page, "[data-component-id='CounterComponent2'] [data-testid='counter-value']", expected));

    private static Task<string> ReadActorIdAsync(IPage page, string componentId)
        => ReadTextAsync(page, $"[data-component-id='{componentId}'] [data-testid='actor-id']");

    private static async Task WaitForIntAsync(IPage page, string selector, int expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < TimeoutMs)
        {
            try
            {
                if (await ReadIntAsync(page, selector) == expected)
                    return;
            }
            catch
            {
                // The component may still be attaching to its SignalR circuit.
            }

            await Task.Delay(100);
        }

        var actual = await ReadTextAsync(page, selector);
        throw new InvalidOperationException($"Timed out waiting for {selector} to become {expected}. Actual: '{actual}'.");
    }

    private static async Task WaitForTextAsync(IPage page, string selector, string expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < TimeoutMs)
        {
            try
            {
                if (string.Equals(await ReadTextAsync(page, selector), expected, StringComparison.Ordinal))
                    return;
            }
            catch
            {
                // Element may not exist yet.
            }

            await Task.Delay(100);
        }

        var actual = await ReadTextAsync(page, selector);
        throw new InvalidOperationException($"Timed out waiting for {selector} to become '{expected}'. Actual: '{actual}'.");
    }

    private static async Task WaitForTextPrefixAsync(IPage page, string selector, string expectedPrefix)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < TimeoutMs)
        {
            try
            {
                var actual = await ReadTextAsync(page, selector);
                if (actual.StartsWith(expectedPrefix, StringComparison.Ordinal))
                    return;
            }
            catch
            {
                // Element may not exist yet.
            }

            await Task.Delay(100);
        }

        var finalValue = await ReadTextAsync(page, selector);
        throw new InvalidOperationException(
            $"Timed out waiting for {selector} to start with '{expectedPrefix}'. Actual: '{finalValue}'.");
    }

    private static async Task<int> ReadIntAsync(IPage page, string selector)
    {
        var text = await ReadTextAsync(page, selector);
        return int.Parse(text);
    }

    private static async Task<string> ReadTextAsync(IPage page, string selector)
    {
        var locator = page.Locator(selector);
        var text = await locator.TextContentAsync(new LocatorTextContentOptions { Timeout = 2_000 });
        return text?.Trim() ?? string.Empty;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
