using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RazorBlazorDataExchangeTester;
using RazorBlazorDataExchangeTester.Pages;

internal static class RazorPageModelEventTests
{
    public static void Run()
    {
        const string property = "XCounter";
        const string blazorActor = "Blazor:CounterComponent:regression";

        var exchange = new RazorBlazorDataExchange();
        using var observer = new RazorChangeEventObserver(exchange);

        var context = new DefaultHttpContext();
        context.Features.Set<ISessionFeature>(new TestSessionFeature
        {
            Session = new InMemoryTestSession()
        });
        var accessor = new HttpContextAccessor { HttpContext = context };

        var page = new IndexModel(exchange, accessor, observer);
        var sessionId = page.SessionId;
        Assert(!string.IsNullOrWhiteSpace(sessionId), "Razor PageModel session was not initialized.");

        exchange.Publish(sessionId, property, 7, blazorActor);
        Assert(page.XCounter == 7,
            "An active Razor PageModel did not process a Blazor-originated DataChanged event.");

        exchange.Publish("different-session", property, 99, blazorActor);
        Assert(page.XCounter == 7, "Razor PageModel accepted a change from a different session.");

        exchange.Publish(sessionId, property, 88, "Razor:/Index");
        Assert(page.XCounter == 7, "Razor PageModel accepted its own publisher echo.");

        page.Dispose();
        page.Dispose(); // Unsubscription must be idempotent.

        exchange.Publish(sessionId, property, 8, blazorActor);
        Assert(page.XCounter == 7,
            "A disposed Razor PageModel was still receiving DataChanged events.");

        Assert(observer.TryGetLast(sessionId, out var last) && last?.Value is int value && value == 8,
            "The persistent Razor observer stopped receiving changes after PageModel disposal.");

        Console.WriteLine("PASS request-bound Razor PageModel handler, filtering, cleanup, and persistent observer");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TestSessionFeature : ISessionFeature
    {
        public ISession Session { get; set; } = null!;
    }

    private sealed class InMemoryTestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IEnumerable<string> Keys => _values.Keys;

        public void Clear() => _values.Clear();
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;

        public bool TryGetValue(string key, out byte[]? value)
            => _values.TryGetValue(key, out value);

        public Task LoadAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
