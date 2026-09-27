internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("RazorBlazorDataExchange 0.5 stress tests");

        await TestSessionIsolation();
        await TestTypedRoutingAndActorFiltering();
        await TestBidirectionalRazorBlazorFanOut();
        await TestAtomicConcurrency();
        await TestCorrelationLoopSuppression();
        await TestSubscriberFailureIsolation();
        await TestCleanupAndMetrics();

        Console.WriteLine("ALL TESTS PASSED");
    }

    private static Task TestSessionIsolation()
    {
        var broker = new RazorBlazorDataExchange();
        broker.StoreValue("session-A", "Counter", 10, "Razor:A");
        broker.StoreValue("session-B", "Counter", 20, "Razor:B");

        Assert(broker.Get<int>("session-A", "Counter") == 10, "Session A value leaked or was lost.");
        Assert(broker.Get<int>("session-B", "Counter") == 20, "Session B value leaked or was lost.");

        Console.WriteLine("PASS session isolation");
        return Task.CompletedTask;
    }

    private static async Task TestTypedRoutingAndActorFiltering()
    {
        var broker = new RazorBlazorDataExchange();
        var actorACalls = 0;
        var actorBCalls = 0;

        using var actorA = broker.Subscribe<int>(
            "routing-session",
            "Counter",
            "Actor-A",
            _ => Interlocked.Increment(ref actorACalls));

        using var actorB = broker.Subscribe<int>(
            "routing-session",
            "Counter",
            "Actor-B",
            _ => Interlocked.Increment(ref actorBCalls));

        await broker.PublishAsync("routing-session", "Counter", 1, "Actor-A");

        Assert(actorACalls == 0, "Publishing actor received its own notification.");
        Assert(actorBCalls == 1, "Matching peer subscription did not receive exactly one notification.");

        Console.WriteLine("PASS typed routing and actor filtering");
    }

    private static async Task TestBidirectionalRazorBlazorFanOut()
    {
        const string sessionId = "bidirectional-session";
        const string counterProperty = "Counter";

        var broker = new RazorBlazorDataExchange();
        var blazorComponent1Calls = 0;
        var blazorComponent2Calls = 0;
        var otherSessionCalls = 0;
        var razorObservedBlazorMessages = 0;
        var razorObservedStatusProperty = false;

        using var blazor1 = broker.Subscribe<int>(
            sessionId,
            counterProperty,
            "Blazor:Counter:1",
            _ => Interlocked.Increment(ref blazorComponent1Calls));

        using var blazor2 = broker.Subscribe<int>(
            sessionId,
            counterProperty,
            "Blazor:Counter:2",
            _ => Interlocked.Increment(ref blazorComponent2Calls));

        using var isolatedOtherSession = broker.Subscribe<int>(
            "other-session",
            counterProperty,
            "Blazor:OtherSession",
            _ => Interlocked.Increment(ref otherSessionCalls));

        // A Razor/MVC coordinator can observe the whole session, not only one property.
        using var razorCoordinator = broker.SubscribeSession(
            sessionId,
            "Razor:PageCoordinator",
            message =>
            {
                if (message.ActorId.StartsWith("Blazor:", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref razorObservedBlazorMessages);
                    if (message.PropertyName == "Status")
                        razorObservedStatusProperty = true;
                }
            });

        // Razor -> every interested Blazor component in the same session.
        await broker.PublishAsync(sessionId, counterProperty, 5, "Razor:/Index");

        Assert(blazorComponent1Calls == 1, "Razor -> Blazor component 1 was not delivered.");
        Assert(blazorComponent2Calls == 1, "Razor -> Blazor component 2 was not delivered.");
        Assert(otherSessionCalls == 0, "Razor publication crossed the session boundary.");

        // Blazor -> Razor and peer Blazor component. The publishing Blazor actor does not
        // receive a self-echo, but every other interested actor in the session does.
        await broker.PublishAsync(sessionId, counterProperty, 6, "Blazor:Counter:1");

        Assert(blazorComponent1Calls == 1, "Blazor publisher received an unwanted self-echo.");
        Assert(blazorComponent2Calls == 2, "Blazor -> peer Blazor fan-out was not delivered.");
        Assert(razorObservedBlazorMessages == 1, "Blazor -> Razor session subscriber was not delivered.");
        Assert(broker.Get<int>(sessionId, counterProperty) == 6,
            "Blazor publication was not persisted for the next Razor/MVC request.");

        // Session-wide Razor subscription must not be constrained to the Counter property.
        await broker.PublishAsync(sessionId, "Status", "ready", "Blazor:Status:1");

        Assert(razorObservedBlazorMessages == 2,
            "Razor session-wide subscriber did not receive a second Blazor property.");
        Assert(razorObservedStatusProperty,
            "Session-wide routing incorrectly restricted Blazor -> Razor communication by property.");

        Console.WriteLine("PASS Razor <-> Blazor bidirectional fan-out");
    }

    private static async Task TestAtomicConcurrency()
    {
        const int workers = 24;
        const int incrementsPerWorker = 1000;
        const string sessionId = "concurrency-session";
        const string propertyName = "Counter";

        var broker = new RazorBlazorDataExchange();
        broker.StoreValue(sessionId, propertyName, 0, "Seed");

        var tasks = Enumerable.Range(0, workers)
            .Select(worker => Task.Run(() =>
            {
                var actorId = $"Worker-{worker}";
                for (var i = 0; i < incrementsPerWorker; i++)
                {
                    broker.Update<int>(
                        sessionId,
                        propertyName,
                        current => current + 1,
                        actorId);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var expected = workers * incrementsPerWorker;
        var actual = broker.Get<int>(sessionId, propertyName);
        Assert(actual == expected, $"Atomic update failed. Expected {expected}, got {actual}.");

        Console.WriteLine($"PASS atomic concurrency ({expected:N0} updates)");
    }

    private static async Task TestCorrelationLoopSuppression()
    {
        const string sessionId = "correlation-session";
        const string propertyName = "Counter";

        var broker = new RazorBlazorDataExchange();
        var actorBDeliveries = 0;
        var actorADeliveries = 0;
        var actorARepublishWasSuppressed = false;

        using var actorA = broker.Subscribe<int>(
            sessionId,
            propertyName,
            "Actor-A",
            async (message, cancellationToken) =>
            {
                Interlocked.Increment(ref actorADeliveries);

                var result = await broker.PublishAsync(
                    sessionId,
                    propertyName,
                    message.Value + 1,
                    "Actor-A",
                    message.CorrelationId,
                    cancellationToken);

                actorARepublishWasSuppressed = result is null;
            });

        using var actorB = broker.Subscribe<int>(
            sessionId,
            propertyName,
            "Actor-B",
            async (message, cancellationToken) =>
            {
                Interlocked.Increment(ref actorBDeliveries);

                await broker.PublishAsync(
                    sessionId,
                    propertyName,
                    message.Value + 1,
                    "Actor-B",
                    message.CorrelationId,
                    cancellationToken);
            });

        await broker.PublishAsync(sessionId, propertyName, 1, "Actor-A");

        Assert(actorBDeliveries == 1, "Actor B should receive the original publication exactly once.");
        Assert(actorADeliveries == 1, "Actor A should receive Actor B's correlated publication exactly once.");
        Assert(actorARepublishWasSuppressed, "Correlation guard did not stop the A -> B -> A loop.");
        Assert(broker.GetMetrics().SuppressedMessages >= 1, "Suppressed-message metric was not incremented.");

        Console.WriteLine("PASS correlation loop suppression");
    }

    private static async Task TestSubscriberFailureIsolation()
    {
        var broker = new RazorBlazorDataExchange();
        var successfulDeliveries = 0;

        using var failing = broker.Subscribe<int>(
            "failure-session",
            "Counter",
            "Failing-Subscriber",
            (_, _) => ValueTask.FromException(new InvalidOperationException("Expected test failure")));

        using var successful = broker.Subscribe<int>(
            "failure-session",
            "Counter",
            "Successful-Subscriber",
            _ => Interlocked.Increment(ref successfulDeliveries));

        await broker.PublishAsync("failure-session", "Counter", 42, "Publisher");

        Assert(successfulDeliveries == 1, "One failing subscriber prevented another subscriber from running.");
        Assert(broker.GetMetrics().DeliveryFailures == 1, "Delivery failure metric did not record the isolated exception.");

        Console.WriteLine("PASS subscriber failure isolation");
    }

    private static async Task TestCleanupAndMetrics()
    {
        var broker = new RazorBlazorDataExchange();
        broker.StoreValue("cleanup-session", "Value", 123, "Seeder");

        await Task.Delay(5);
        var removed = broker.CleanupInactiveSessions(TimeSpan.Zero);
        var metrics = broker.GetMetrics();

        Assert(removed == 1, "Inactive session cleanup did not remove the expected session.");
        Assert(metrics.RemovedSessions == 1, "Removed-session metric was not incremented.");
        Assert(metrics.ActiveSessions == 0, "Removed session is still reported as active.");

        Console.WriteLine("PASS cleanup and metrics");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
