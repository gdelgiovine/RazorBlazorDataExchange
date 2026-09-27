using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("RazorBlazorDataExchange 0.6 tests");

        await TestOrderedSameRouteCompletion();
        await TestIndependentPropertyLanes();
        await TestSessionWideOrdering();
        await TestReentrantSameRoutePublication();
        await TestOrderedAtomicUpdates();
        await TestTransportBroadcastAndFailureIsolation();
        TestTransportSerializationRoundTrip();
        TestDependencyInjectionRegistration();

        Console.WriteLine("ALL V0.6 TESTS PASSED");
    }

    private static async Task TestOrderedSameRouteCompletion()
    {
        const string sessionId = "ordered-session";
        const string propertyName = "Counter";
        var broker = new RazorBlazorDataExchange();
        var ordered = new RazorBlazorOrderedDataExchange(broker);
        var observed = new List<int>();
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();

        using var subscription = broker.Subscribe<int>(
            sessionId,
            propertyName,
            "Observer",
            async (message, cancellationToken) =>
            {
                if (message.Value == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                lock (observed)
                    observed.Add(message.Value);
            });

        var first = ordered.PublishAsync(sessionId, propertyName, 1, "Publisher-1").AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = ordered.PublishAsync(sessionId, propertyName, 2, "Publisher-2").AsTask();
        await Task.Delay(50);
        Assert(!second.IsCompleted, "Second ordered publication completed before the first lane operation was released.");

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert(observed.SequenceEqual(new[] { 1, 2 }),
            $"Ordered route completion was not preserved. Observed: {string.Join(",", observed)}");
        Assert(ordered.GetMetrics().ActiveLanes == 0, "Ordered lane was not retired after completion.");

        Console.WriteLine("PASS ordered same-route completion");
    }

    private static async Task TestIndependentPropertyLanes()
    {
        const string sessionId = "parallel-property-session";
        var broker = new RazorBlazorDataExchange();
        var ordered = new RazorBlazorOrderedDataExchange(broker);
        var p1Entered = NewSignal();
        var p2Entered = NewSignal();
        var release = NewSignal();

        using var p1 = broker.Subscribe<int>(sessionId, "P1", "Observer-P1", async (_, token) =>
        {
            p1Entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });

        using var p2 = broker.Subscribe<int>(sessionId, "P2", "Observer-P2", async (_, token) =>
        {
            p2Entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });

        var task1 = ordered.PublishAsync(sessionId, "P1", 1, "Publisher-P1").AsTask();
        await p1Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var task2 = ordered.PublishAsync(sessionId, "P2", 2, "Publisher-P2").AsTask();
        await p2Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        release.TrySetResult();
        await Task.WhenAll(task1, task2);

        Console.WriteLine("PASS independent property lanes");
    }

    private static async Task TestSessionWideOrdering()
    {
        const string sessionId = "session-wide-ordering";
        var broker = new RazorBlazorDataExchange();
        var ordered = new RazorBlazorOrderedDataExchange(broker);
        var p1Entered = NewSignal();
        var p2Entered = NewSignal();
        var releaseP1 = NewSignal();

        using var p1 = broker.Subscribe<int>(sessionId, "P1", "Observer-P1", async (_, token) =>
        {
            p1Entered.TrySetResult();
            await releaseP1.Task.WaitAsync(token);
        });

        using var p2 = broker.Subscribe<int>(sessionId, "P2", "Observer-P2", _ => p2Entered.TrySetResult());

        var task1 = ordered.PublishAsync(
            sessionId,
            "P1",
            1,
            "Publisher-P1",
            orderingScope: RazorBlazorExchangeOrderingScope.Session).AsTask();

        await p1Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var task2 = ordered.PublishAsync(
            sessionId,
            "P2",
            2,
            "Publisher-P2",
            orderingScope: RazorBlazorExchangeOrderingScope.Session).AsTask();

        await Task.Delay(50);
        Assert(!p2Entered.Task.IsCompleted,
            "Session ordering allowed a second property to enter before the current session lane completed.");

        releaseP1.TrySetResult();
        await p2Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(task1, task2);

        Console.WriteLine("PASS session-wide ordering");
    }

    private static async Task TestReentrantSameRoutePublication()
    {
        const string sessionId = "reentrant-session";
        const string propertyName = "Counter";
        var broker = new RazorBlazorDataExchange();
        var ordered = new RazorBlazorOrderedDataExchange(broker);
        ExchangeMessage<int>? nestedResult = null;

        using var subscription = broker.Subscribe<int>(
            sessionId,
            propertyName,
            "Blazor:NestedPublisher",
            async (message, token) =>
            {
                if (message.Value != 1)
                    return;

                nestedResult = await ordered.PublishAsync(
                    sessionId,
                    propertyName,
                    2,
                    "Blazor:NestedPublisher",
                    message.CorrelationId,
                    cancellationToken: token);
            });

        await ordered.PublishAsync(sessionId, propertyName, 1, "Razor:Publisher")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert(nestedResult?.Value == 2, "Reentrant ordered publication did not complete.");
        Assert(broker.Get<int>(sessionId, propertyName) == 2, "Reentrant ordered publication did not persist state.");
        Assert(ordered.GetMetrics().ReentrantBypasses >= 1, "Reentrant ordered path was not detected.");

        Console.WriteLine("PASS reentrant same-route publication");
    }

    private static async Task TestOrderedAtomicUpdates()
    {
        const string sessionId = "ordered-update-session";
        const string propertyName = "Counter";
        const int operations = 200;
        var broker = new RazorBlazorDataExchange();
        var ordered = new RazorBlazorOrderedDataExchange(broker);
        broker.StoreValue(sessionId, propertyName, 0, "Seed");

        var tasks = Enumerable.Range(0, operations)
            .Select(i => ordered.UpdateAsync<int>(
                sessionId,
                propertyName,
                current => current + 1,
                $"Worker-{i}").AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        Assert(broker.Get<int>(sessionId, propertyName) == operations,
            "Ordered atomic updates lost one or more increments.");
        Assert(ordered.GetMetrics().CompletedOperations == operations,
            "Ordered metrics did not record all completed updates.");

        Console.WriteLine($"PASS ordered atomic updates ({operations})");
    }

    private static async Task TestTransportBroadcastAndFailureIsolation()
    {
        var transport = new InMemoryRazorBlazorDataExchangeTransport();
        var consumer1 = 0;
        var consumer2 = 0;

        using var failing = transport.Subscribe(
            "FailingConsumer",
            (_, _) => ValueTask.FromException(new InvalidOperationException("Expected transport test failure")));

        using var first = transport.Subscribe("Consumer1", _ => Interlocked.Increment(ref consumer1));
        using var second = transport.Subscribe("Consumer2", _ => Interlocked.Increment(ref consumer2));

        var message = new ExchangeMessage<int>(
            "transport-session",
            "Counter",
            7,
            "Razor:Publisher",
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            DateTimeOffset.UtcNow);

        var envelope = RazorBlazorTransportEnvelope.FromMessage(message, "node-A");
        await transport.PublishAsync(envelope);

        Assert(consumer1 == 1 && consumer2 == 1,
            "In-memory transport did not broadcast to every healthy consumer.");

        var metrics = transport.GetMetrics();
        Assert(metrics.PublishedEnvelopes == 1, "Transport published metric is incorrect.");
        Assert(metrics.DeliveredEnvelopes == 2, "Transport delivered metric is incorrect.");
        Assert(metrics.DeliveryFailures == 1, "Transport failure isolation metric is incorrect.");

        Console.WriteLine("PASS transport broadcast and failure isolation");
    }

    private static void TestTransportSerializationRoundTrip()
    {
        var expected = new TestPayload(17, "Razor-Blazor");
        var message = new ExchangeMessage<TestPayload>(
            "serialization-session",
            "Payload",
            expected,
            "Blazor:Publisher",
            Guid.NewGuid(),
            Guid.NewGuid(),
            11,
            DateTimeOffset.UtcNow);

        var envelope = RazorBlazorTransportEnvelope.FromMessage(message, "node-serialization");
        var actual = envelope.DeserializeValue<TestPayload>();

        Assert(actual == expected, "Transport envelope JSON round-trip changed the payload.");
        Assert(envelope.ValueTypeName.Contains(nameof(TestPayload), StringComparison.Ordinal),
            "Transport envelope did not retain the declared value type name.");

        Console.WriteLine("PASS transport serialization round-trip");
    }

    private static void TestDependencyInjectionRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorBlazorDataExchange();

        using var provider = services.BuildServiceProvider();
        var broker1 = provider.GetRequiredService<RazorBlazorDataExchange>();
        var broker2 = provider.GetRequiredService<RazorBlazorDataExchange>();
        var ordered = provider.GetRequiredService<RazorBlazorOrderedDataExchange>();
        var transport = provider.GetRequiredService<IRazorBlazorDataExchangeTransport>();

        Assert(ReferenceEquals(broker1, broker2), "Broker is no longer singleton.");
        Assert(ordered is not null, "Ordered exchange facade was not registered.");
        Assert(transport is InMemoryRazorBlazorDataExchangeTransport,
            "Default transport is not the in-memory transport.");

        var overrideServices = new ServiceCollection();
        overrideServices.AddLogging();
        overrideServices.AddRazorBlazorDataExchange();
        overrideServices.UseRazorBlazorDataExchangeTransport<TestTransport>();

        using var overrideProvider = overrideServices.BuildServiceProvider();
        Assert(overrideProvider.GetRequiredService<IRazorBlazorDataExchangeTransport>() is TestTransport,
            "Custom transport replacement did not take effect.");

        Console.WriteLine("PASS DI registration and transport replacement");
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record TestPayload(int Count, string Name);

    private sealed class TestTransport : IRazorBlazorDataExchangeTransport
    {
        public string Name => "Test";
        public bool IsDistributed => true;

        public IDisposable Subscribe(
            string consumerId,
            Func<RazorBlazorTransportEnvelope, CancellationToken, ValueTask> handler)
            => new NoopDisposable();

        public ValueTask PublishAsync(
            RazorBlazorTransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public RazorBlazorTransportMetricsSnapshot GetMetrics()
            => new(Name, IsDistributed, 0, 0, 0, 0);

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
