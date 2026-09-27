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
    private readonly ConcurrentDictionary<string, SessionOrderingCoordinator> _sessionCoordinators =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<OrderedExchangeKey, OrderedLane> _lanes = new();
    private static readonly AsyncLocal<HashSet<HeldOrderingToken>?> HeldOrdering = new();

    private long _enteredOperations;
    private long _completedOperations;
    private long _cancelledOperations;
    private long _reentrantBypasses;

    public RazorBlazorOrderedDataExchange(RazorBlazorDataExchange exchange)
        => _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));

    /// <summary>
    /// Publishes while preserving completion order inside the selected ordering scope.
    /// SessionAndProperty allows unrelated properties to progress concurrently.
    /// Session is an exclusive writer for the whole session and therefore waits for all
    /// active ordered property lanes in that session to finish before entering.
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
        ValidateAddress(sessionId, propertyName, orderingScope);

        return await ExecuteOrderedAsync(
            sessionId,
            propertyName,
            orderingScope,
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
    /// selected ordering scope.
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
        ValidateAddress(sessionId, propertyName, orderingScope);

        return await ExecuteOrderedAsync(
            sessionId,
            propertyName,
            orderingScope,
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
            ActiveSessionCoordinators: _sessionCoordinators.Count,
            EnteredOperations: Interlocked.Read(ref _enteredOperations),
            CompletedOperations: Interlocked.Read(ref _completedOperations),
            CancelledOperations: Interlocked.Read(ref _cancelledOperations),
            ReentrantBypasses: Interlocked.Read(ref _reentrantBypasses));

    private async ValueTask<TResult> ExecuteOrderedAsync<TResult>(
        string sessionId,
        string propertyName,
        RazorBlazorExchangeOrderingScope orderingScope,
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var reentrant = GetReentrantDecision(sessionId, propertyName, orderingScope);
        if (reentrant == ReentrantDecision.Bypass)
        {
            Interlocked.Increment(ref _reentrantBypasses);
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        if (reentrant == ReentrantDecision.InvalidEscalation)
        {
            throw new InvalidOperationException(
                "An ordered SessionAndProperty notification cannot re-enter the ordered facade " +
                "with Session scope for the same session. Start the outer workflow with Session " +
                "scope when cross-property/session-wide ordering is required.");
        }

        if (reentrant == ReentrantDecision.InvalidCrossProperty)
        {
            throw new InvalidOperationException(
                "A SessionAndProperty subscriber cannot synchronously enter another ordered property " +
                "in the same session. Use Session scope for workflows that cascade across properties.");
        }

        using var coordinatorReference = AcquireSessionCoordinator(sessionId);

        try
        {
            if (orderingScope == RazorBlazorExchangeOrderingScope.Session)
            {
                await using var writer = await coordinatorReference.Coordinator
                    .EnterWriterAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var held = EnterHeldScope(new HeldOrderingToken(
                    sessionId,
                    null,
                    RazorBlazorExchangeOrderingScope.Session));

                return await ExecuteInsideLaneAsync(operation, cancellationToken).ConfigureAwait(false);
            }

            await using var reader = await coordinatorReference.Coordinator
                .EnterReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            var key = new OrderedExchangeKey(sessionId, propertyName);
            await using var lane = await EnterLaneAsync(key, cancellationToken).ConfigureAwait(false);
            using var propertyHeld = EnterHeldScope(new HeldOrderingToken(
                sessionId,
                propertyName,
                RazorBlazorExchangeOrderingScope.SessionAndProperty));

            return await ExecuteInsideLaneAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancelledOperations);
            throw;
        }
    }

    private async ValueTask<TResult> ExecuteInsideLaneAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
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

    private SessionCoordinatorReference AcquireSessionCoordinator(string sessionId)
    {
        while (true)
        {
            var coordinator = _sessionCoordinators.GetOrAdd(
                sessionId,
                static _ => new SessionOrderingCoordinator());

            lock (coordinator.LifecycleSync)
            {
                if (coordinator.Retired ||
                    !_sessionCoordinators.TryGetValue(sessionId, out var registered) ||
                    !ReferenceEquals(coordinator, registered))
                {
                    continue;
                }

                coordinator.ReferenceCount++;
                return new SessionCoordinatorReference(this, sessionId, coordinator);
            }
        }
    }

    private void ReleaseSessionCoordinator(
        string sessionId,
        SessionOrderingCoordinator coordinator)
    {
        var retire = false;
        lock (coordinator.LifecycleSync)
        {
            coordinator.ReferenceCount--;
            if (coordinator.ReferenceCount == 0)
            {
                coordinator.Retired = true;
                retire = true;
            }
        }

        if (!retire)
            return;

        var collection =
            (ICollection<KeyValuePair<string, SessionOrderingCoordinator>>)_sessionCoordinators;
        collection.Remove(new KeyValuePair<string, SessionOrderingCoordinator>(sessionId, coordinator));
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
                ReleaseLaneReference(key, lane, releaseSemaphore: false);
                throw;
            }
        }
    }

    private void ReleaseLane(OrderedExchangeKey key, OrderedLane lane)
        => ReleaseLaneReference(key, lane, releaseSemaphore: true);

    private void ReleaseLaneReference(
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

    private static ReentrantDecision GetReentrantDecision(
        string sessionId,
        string propertyName,
        RazorBlazorExchangeOrderingScope requestedScope)
    {
        var held = HeldOrdering.Value;
        if (held is null || held.Count == 0)
            return ReentrantDecision.None;

        var sameSession = held
            .Where(token => string.Equals(token.SessionId, sessionId, StringComparison.Ordinal))
            .ToArray();

        if (sameSession.Length == 0)
            return ReentrantDecision.None;

        if (sameSession.Any(token => token.Scope == RazorBlazorExchangeOrderingScope.Session))
            return ReentrantDecision.Bypass;

        if (requestedScope == RazorBlazorExchangeOrderingScope.Session)
            return ReentrantDecision.InvalidEscalation;

        if (sameSession.Any(token =>
            string.Equals(token.PropertyName, propertyName, StringComparison.Ordinal)))
        {
            return ReentrantDecision.Bypass;
        }

        return ReentrantDecision.InvalidCrossProperty;
    }

    private static IDisposable EnterHeldScope(HeldOrderingToken token)
    {
        // AsyncLocal values flow into child async operations. Never mutate an inherited
        // HashSet instance because sibling subscriber flows would otherwise see each other's
        // ordering changes. Clone on entry and restore the previous snapshot on exit.
        var previous = HeldOrdering.Value;
        var current = previous is null
            ? new HashSet<HeldOrderingToken>()
            : new HashSet<HeldOrderingToken>(previous);

        current.Add(token);
        HeldOrdering.Value = current;
        return new HeldScope(previous);
    }

    private static void ValidateAddress(
        string sessionId,
        string propertyName,
        RazorBlazorExchangeOrderingScope orderingScope)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name cannot be empty.", nameof(propertyName));
        if (!Enum.IsDefined(orderingScope))
            throw new ArgumentOutOfRangeException(nameof(orderingScope));
    }

    private readonly record struct OrderedExchangeKey(string SessionId, string PropertyName);

    private readonly record struct HeldOrderingToken(
        string SessionId,
        string? PropertyName,
        RazorBlazorExchangeOrderingScope Scope);

    private enum ReentrantDecision
    {
        None,
        Bypass,
        InvalidEscalation,
        InvalidCrossProperty
    }

    private sealed class OrderedLane
    {
        public object LifecycleSync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    /// <summary>
    /// Writer-preferred async reader/writer coordinator.
    /// Property lanes enter as readers. Session-wide ordering enters as a writer.
    /// </summary>
    private sealed class SessionOrderingCoordinator
    {
        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _roomEmpty = new(1, 1);
        private readonly SemaphoreSlim _readerMutex = new(1, 1);
        private int _readerCount;

        public object LifecycleSync { get; } = new();
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }

        public async ValueTask<ReaderLease> EnterReaderAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            _turnstile.Release();

            await _readerMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            var firstReader = false;
            try
            {
                _readerCount++;
                firstReader = _readerCount == 1;
                if (firstReader)
                    await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (firstReader)
                    _readerCount--;
                throw;
            }
            finally
            {
                _readerMutex.Release();
            }

            return new ReaderLease(this);
        }

        public async ValueTask<WriterLease> EnterWriterAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new WriterLease(this);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }
        }

        private async ValueTask ReleaseReaderAsync()
        {
            await _readerMutex.WaitAsync().ConfigureAwait(false);
            try
            {
                _readerCount--;
                if (_readerCount == 0)
                    _roomEmpty.Release();
            }
            finally
            {
                _readerMutex.Release();
            }
        }

        private void ReleaseWriter()
        {
            _roomEmpty.Release();
            _turnstile.Release();
        }

        public sealed class ReaderLease : IAsyncDisposable
        {
            private SessionOrderingCoordinator? _owner;

            public ReaderLease(SessionOrderingCoordinator owner)
                => _owner = owner;

            public async ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                    await owner.ReleaseReaderAsync().ConfigureAwait(false);
            }
        }

        public sealed class WriterLease : IAsyncDisposable
        {
            private SessionOrderingCoordinator? _owner;

            public WriterLease(SessionOrderingCoordinator owner)
                => _owner = owner;

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _owner, null)?.ReleaseWriter();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SessionCoordinatorReference : IDisposable
    {
        private RazorBlazorOrderedDataExchange? _owner;
        private readonly string _sessionId;

        public SessionCoordinatorReference(
            RazorBlazorOrderedDataExchange owner,
            string sessionId,
            SessionOrderingCoordinator coordinator)
        {
            _owner = owner;
            _sessionId = sessionId;
            Coordinator = coordinator;
        }

        public SessionOrderingCoordinator Coordinator { get; }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?
                .ReleaseSessionCoordinator(_sessionId, Coordinator);
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
        private readonly HashSet<HeldOrderingToken>? _previous;
        private int _disposed;

        public HeldScope(HashSet<HeldOrderingToken>? previous)
            => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            HeldOrdering.Value = _previous;
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
    int ActiveSessionCoordinators,
    long EnteredOperations,
    long CompletedOperations,
    long CancelledOperations,
    long ReentrantBypasses);
