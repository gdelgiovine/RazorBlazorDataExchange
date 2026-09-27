using System.Collections.Concurrent;

/// <summary>
/// Optional ordered facade over <see cref="RazorBlazorDataExchange"/>.
///
/// The underlying broker remains the singleton bidirectional Razor/Blazor exchange.
/// This facade adds serialization only for callers that explicitly require completion
/// ordering. Direct broker calls remain fully compatible and unchanged.
/// </summary>
public sealed class RazorBlazorOrderedDataExchange
{
    private readonly RazorBlazorDataExchange _exchange;
    private readonly ConcurrentDictionary<OrderedExchangeKey, OrderedLane> _lanes = new();
    private static readonly AsyncLocal<HashSet<OrderedExchangeKey>?> HeldLanes = new();

    private long _enteredOperations;
    private long _completedOperations;
    private long _cancelledOperations;
    private long _reentrantBypasses;

    public RazorBlazorOrderedDataExchange(RazorBlazorDataExchange exchange)
        => _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));

    /// <summary>
    /// Publishes while preserving completion order inside the selected ordering lane.
    /// SessionAndProperty allows unrelated properties to progress concurrently.
    /// Session serializes all ordered publications in one session and is useful for
    /// workflows that cascade across several properties.
    /// </summary>
    public async ValueTask<ExchangeMessage<T>?> PublishAsync<T>(
        string sessionId,
        string propertyName,
        T? value,
        string actorId,
        Guid? correlationId = null,
        RazorBlazorExchangeOrderingScope orderingScope = RazorBlazorExchangeOrderingScope.SessionAndProperty,
        CancellationToken cancellationToken = default)
    {
        var key = CreateKey(sessionId, propertyName, orderingScope);

        return await ExecuteOrderedAsync(
            key,
            token => _exchange.PublishAsync(
                sessionId,
                propertyName,
                value,
                actorId,
                correlationId,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs an atomic read-modify-write and preserves completion order inside the
    /// selected ordering lane.
    /// </summary>
    public async ValueTask<ExchangeMessage<T>?> UpdateAsync<T>(
        string sessionId,
        string propertyName,
        Func<T?, T?> updater,
        string actorId,
        Guid? correlationId = null,
        RazorBlazorExchangeOrderingScope orderingScope = RazorBlazorExchangeOrderingScope.SessionAndProperty,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updater);
        var key = CreateKey(sessionId, propertyName, orderingScope);

        return await ExecuteOrderedAsync(
            key,
            token => _exchange.UpdateAsync(
                sessionId,
                propertyName,
                updater,
                actorId,
                correlationId,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    public RazorBlazorOrderedDataExchangeMetricsSnapshot GetMetrics()
        => new(
            ActiveLanes: _lanes.Count,
            EnteredOperations: Interlocked.Read(ref _enteredOperations),
            CompletedOperations: Interlocked.Read(ref _completedOperations),
            CancelledOperations: Interlocked.Read(ref _cancelledOperations),
            ReentrantBypasses: Interlocked.Read(ref _reentrantBypasses));

    private async ValueTask<TResult> ExecuteOrderedAsync<TResult>(
        OrderedExchangeKey key,
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        // A subscriber may publish again on the same route while the outer publication is
        // still awaiting its subscribers. Re-acquiring the same lane would deadlock. Keep
        // that causally nested operation inside the current lane instead.
        if (IsHeld(key))
        {
            Interlocked.Increment(ref _reentrantBypasses);
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        OrderedLaneLease lease;
        try
        {
            lease = await EnterLaneAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancelledOperations);
            throw;
        }

        await using (lease.ConfigureAwait(false))
        using (EnterHeldScope(key))
        {
            Interlocked.Increment(ref _enteredOperations);
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref _completedOperations);
            }
        }
    }

    private async ValueTask<OrderedLaneLease> EnterLaneAsync(
        OrderedExchangeKey key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var lane = _lanes.GetOrAdd(key, static _ => new OrderedLane());

            lock (lane.LifecycleSync)
            {
                if (lane.Retired ||
                    !_lanes.TryGetValue(key, out var registered) ||
                    !ReferenceEquals(lane, registered))
                {
                    continue;
                }

                lane.ReferenceCount++;
            }

            try
            {
                await lane.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new OrderedLaneLease(this, key, lane);
            }
            catch
            {
                ReleaseReference(key, lane, releaseSemaphore: false);
                throw;
            }
        }
    }

    private void ReleaseLane(OrderedExchangeKey key, OrderedLane lane)
        => ReleaseReference(key, lane, releaseSemaphore: true);

    private void ReleaseReference(
        OrderedExchangeKey key,
        OrderedLane lane,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
            lane.Gate.Release();

        var retire = false;
        lock (lane.LifecycleSync)
        {
            lane.ReferenceCount--;
            if (lane.ReferenceCount == 0)
            {
                lane.Retired = true;
                retire = true;
            }
        }

        if (!retire)
            return;

        var collection = (ICollection<KeyValuePair<OrderedExchangeKey, OrderedLane>>)_lanes;
        collection.Remove(new KeyValuePair<OrderedExchangeKey, OrderedLane>(key, lane));
    }

    private static OrderedExchangeKey CreateKey(
        string sessionId,
        string propertyName,
        RazorBlazorExchangeOrderingScope orderingScope)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name cannot be empty.", nameof(propertyName));

        return orderingScope switch
        {
            RazorBlazorExchangeOrderingScope.SessionAndProperty => new(sessionId, propertyName),
            RazorBlazorExchangeOrderingScope.Session => new(sessionId, null),
            _ => throw new ArgumentOutOfRangeException(nameof(orderingScope))
        };
    }

    private static bool IsHeld(OrderedExchangeKey key)
        => HeldLanes.Value?.Contains(key) == true;

    private static IDisposable EnterHeldScope(OrderedExchangeKey key)
    {
        // AsyncLocal values flow into child async operations. Never mutate an inherited
        // HashSet instance because sibling subscriber flows would otherwise see each other's
        // lane changes. Clone on entry and restore the previous snapshot on exit.
        var previous = HeldLanes.Value;
        var current = previous is null
            ? new HashSet<OrderedExchangeKey>()
            : new HashSet<OrderedExchangeKey>(previous);

        current.Add(key);
        HeldLanes.Value = current;
        return new HeldScope(previous);
    }

    private readonly record struct OrderedExchangeKey(string SessionId, string? PropertyName);

    private sealed class OrderedLane
    {
        public object LifecycleSync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class OrderedLaneLease : IAsyncDisposable
    {
        private RazorBlazorOrderedDataExchange? _owner;
        private readonly OrderedExchangeKey _key;
        private readonly OrderedLane _lane;

        public OrderedLaneLease(
            RazorBlazorOrderedDataExchange owner,
            OrderedExchangeKey key,
            OrderedLane lane)
        {
            _owner = owner;
            _key = key;
            _lane = lane;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseLane(_key, _lane);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HeldScope : IDisposable
    {
        private readonly HashSet<OrderedExchangeKey>? _previous;
        private int _disposed;

        public HeldScope(HashSet<OrderedExchangeKey>? previous)
            => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            HeldLanes.Value = _previous;
        }
    }
}

/// <summary>
/// Defines the granularity of ordered exchange lanes.
/// </summary>
public enum RazorBlazorExchangeOrderingScope
{
    SessionAndProperty = 0,
    Session = 1
}

public sealed record RazorBlazorOrderedDataExchangeMetricsSnapshot(
    int ActiveLanes,
    long EnteredOperations,
    long CompletedOperations,
    long CancelledOperations,
    long ReentrantBypasses);
