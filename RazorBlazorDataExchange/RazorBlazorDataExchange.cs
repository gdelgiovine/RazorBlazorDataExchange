using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.ComponentModel;

/// <summary>
/// In-process singleton broker used to exchange state and notifications across the
/// Razor/MVC HTTP request boundary and Blazor Server circuit boundary.
///
/// The broker itself is intentionally singleton. User state and subscriptions are
/// isolated inside per-session partitions.
/// </summary>
public partial class RazorBlazorDataExchange : INotifyPropertyChanged
{
    private readonly ConcurrentDictionary<string, ExchangeSessionState> _sessions =
        new(StringComparer.Ordinal);

    private readonly RazorBlazorDataExchangeOptions _options;
    private readonly ILogger<RazorBlazorDataExchange> _logger;

    private long _lastAccessTicks = DateTime.UtcNow.Ticks;
    private long _nextSubscriptionId;
    private long _publishedMessages;
    private long _deliveredMessages;
    private long _suppressedMessages;
    private long _deliveryFailures;
    private long _removedSessions;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataChangeWithActorEventArgs>? DataChangeWithActor;
    public event EventHandler<DataChangesWithActorEventArgs>? DataChangesWithActor;

    /// <summary>
    /// Compatibility constructor for code that creates the broker directly.
    /// DI should use the options/logger constructor.
    /// </summary>
    public RazorBlazorDataExchange()
        : this(Options.Create(new RazorBlazorDataExchangeOptions()), NullLogger<RazorBlazorDataExchange>.Instance)
    {
    }

    public RazorBlazorDataExchange(
        IOptions<RazorBlazorDataExchangeOptions> options,
        ILogger<RazorBlazorDataExchange> logger)
    {
        _options = options?.Value ?? new RazorBlazorDataExchangeOptions();
        _logger = logger ?? NullLogger<RazorBlazorDataExchange>.Instance;
    }

    /// <summary>
    /// Last activity observed anywhere in the broker.
    /// </summary>
    public DateTime LastAccess => new(Volatile.Read(ref _lastAccessTicks), DateTimeKind.Utc);

    /// <summary>
    /// Stores a value without publishing a notification.
    /// </summary>
    public void StoreValue(string sessionId, string propertyName, object? value, string setter)
    {
        ValidateAddress(sessionId, propertyName, setter);
        var state = GetOrCreateSession(sessionId);
        var now = DateTimeOffset.UtcNow;

        lock (state.SyncRoot)
        {
            TouchSessionLocked(state, now);
            UpsertValueLocked(state, propertyName, value, value?.GetType() ?? typeof(object), setter, now);
        }
    }

    /// <summary>
    /// Returns a defensive copy of the stored metadata.
    /// </summary>
    public ObjectValueMetadata? GetValueMetadata(string sessionId, string propertyName)
    {
        ValidateSessionAndProperty(sessionId, propertyName);

        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            TouchBroker();
            return null;
        }

        lock (state.SyncRoot)
        {
            TouchSessionLocked(state, DateTimeOffset.UtcNow);
            return state.Values.TryGetValue(propertyName, out var metadata)
                ? CloneMetadata(metadata)
                : null;
        }
    }

    public object? GetValue(string sessionId, string propertyName)
        => GetValueMetadata(sessionId, propertyName)?.Value;

    public bool TryGet<T>(string sessionId, string propertyName, out T? value)
    {
        var raw = GetValue(sessionId, propertyName);

        if (raw is null)
        {
            value = default;
            return false;
        }

        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public T? Get<T>(string sessionId, string propertyName)
        => TryGet<T>(sessionId, propertyName, out var value) ? value : default;

    /// <summary>
    /// Publishes a typed value. Synchronous handlers are executed inline. If a subscriber
    /// performs asynchronous work, that work is observed without blocking the publishing thread.
    /// Use PublishAsync when the caller must await completion of every asynchronous subscriber.
    /// </summary>
    public ExchangeMessage<T>? Publish<T>(
        string sessionId,
        string propertyName,
        T? value,
        string actorId,
        Guid? correlationId = null)
    {
        var prepared = PreparePublication(
            sessionId,
            propertyName,
            value,
            typeof(T),
            actorId,
            correlationId,
            legacyGetters: null,
            legacyIsProcessing: false);

        if (prepared is null)
            return null;

        DispatchSynchronously(prepared);
        RaiseLegacySingle(prepared);
        return ToTypedMessage<T>(prepared.Message);
    }

    /// <summary>
    /// Publishes a typed value and asynchronously dispatches matching subscriptions.
    /// Individual subscriber failures are isolated and logged.
    /// </summary>
    public async ValueTask<ExchangeMessage<T>?> PublishAsync<T>(
        string sessionId,
        string propertyName,
        T? value,
        string actorId,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = PreparePublication(
            sessionId,
            propertyName,
            value,
            typeof(T),
            actorId,
            correlationId,
            legacyGetters: null,
            legacyIsProcessing: false);

        if (prepared is null)
            return null;

        await DispatchAsync(prepared, cancellationToken).ConfigureAwait(false);
        RaiseLegacySingle(prepared);
        return ToTypedMessage<T>(prepared.Message);
    }

    /// <summary>
    /// Atomically performs read-modify-write on one property and publishes the result.
    /// Synchronous handlers run inline; incomplete asynchronous handlers continue without
    /// blocking the publishing thread. Use UpdateAsync to await every asynchronous subscriber.
    /// </summary>
    public ExchangeMessage<T>? Update<T>(
        string sessionId,
        string propertyName,
        Func<T?, T?> updater,
        string actorId,
        Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(updater);

        var prepared = PrepareAtomicUpdate(sessionId, propertyName, updater, actorId, correlationId);
        if (prepared is null)
            return null;

        DispatchSynchronously(prepared);
        RaiseLegacySingle(prepared);
        return ToTypedMessage<T>(prepared.Message);
    }

    /// <summary>
    /// Atomically performs read-modify-write on one property and asynchronously publishes the result.
    /// </summary>
    public async ValueTask<ExchangeMessage<T>?> UpdateAsync<T>(
        string sessionId,
        string propertyName,
        Func<T?, T?> updater,
        string actorId,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updater);

        var prepared = PrepareAtomicUpdate(sessionId, propertyName, updater, actorId, correlationId);
        if (prepared is null)
            return null;

        await DispatchAsync(prepared, cancellationToken).ConfigureAwait(false);
        RaiseLegacySingle(prepared);
        return ToTypedMessage<T>(prepared.Message);
    }

    /// <summary>
    /// Registers a session/property subscription. The returned handle must be disposed
    /// when the consumer (typically a Blazor component) is disposed.
    /// </summary>
    public IDisposable Subscribe<T>(
        string sessionId,
        string propertyName,
        string actorId,
        Func<ExchangeMessage<T>, CancellationToken, ValueTask> handler)
    {
        ValidateAddress(sessionId, propertyName, actorId);
        ArgumentNullException.ThrowIfNull(handler);

        var state = GetOrCreateSession(sessionId);
        var id = Interlocked.Increment(ref _nextSubscriptionId);
        var entry = new ExchangeSubscriptionEntry<T>(id, propertyName, actorId, handler);

        lock (state.SyncRoot)
        {
            TouchSessionLocked(state, DateTimeOffset.UtcNow);
            state.Subscriptions[id] = entry;
        }

        return new ExchangeSubscriptionHandle(() => Unsubscribe(sessionId, id));
    }

    public IDisposable Subscribe<T>(
        string sessionId,
        string propertyName,
        string actorId,
        Action<ExchangeMessage<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return Subscribe<T>(sessionId, propertyName, actorId, (message, _) =>
        {
            handler(message);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Removes all subscriptions owned by one actor in a session.
    /// Useful for circuit/component cleanup.
    /// </summary>
    public int UnsubscribeActor(string sessionId, string actorId)
    {
        ValidateSession(sessionId);
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("ActorId cannot be empty.", nameof(actorId));

        if (!_sessions.TryGetValue(sessionId, out var state))
            return 0;

        var removed = 0;
        lock (state.SyncRoot)
        {
            var ids = state.Subscriptions
                .Where(pair => string.Equals(pair.Value.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var id in ids)
            {
                if (state.Subscriptions.Remove(id))
                    removed++;
            }

            TouchSessionLocked(state, DateTimeOffset.UtcNow);
        }

        return removed;
    }

    /// <summary>
    /// Removes inactive session partitions. By default sessions that still have live
    /// subscriptions are retained to avoid breaking an idle but connected Blazor circuit.
    /// </summary>
    public int CleanupInactiveSessions(TimeSpan timeout)
        => CleanupInactiveSessions(timeout, _options.CleanupSessionsWithActiveSubscriptions);

    public int CleanupInactiveSessions(TimeSpan timeout, bool includeSessionsWithActiveSubscriptions)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var now = DateTimeOffset.UtcNow;
        var candidates = new List<(string SessionId, ExchangeSessionState State)>();

        foreach (var pair in _sessions)
        {
            lock (pair.Value.SyncRoot)
            {
                var inactive = now - pair.Value.LastAccess > timeout;
                var removable = includeSessionsWithActiveSubscriptions || pair.Value.Subscriptions.Count == 0;

                if (inactive && removable)
                    candidates.Add((pair.Key, pair.Value));
            }
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            lock (candidate.State.SyncRoot)
            {
                var inactive = now - candidate.State.LastAccess > timeout;
                var removable = includeSessionsWithActiveSubscriptions || candidate.State.Subscriptions.Count == 0;
                if (!inactive || !removable)
                    continue;

                var collection = (ICollection<KeyValuePair<string, ExchangeSessionState>>)_sessions;
                if (collection.Remove(new KeyValuePair<string, ExchangeSessionState>(candidate.SessionId, candidate.State)))
                {
                    removed++;
                    Interlocked.Increment(ref _removedSessions);
                }
            }
        }

        if (removed > 0)
            _logger.LogDebug("Removed {RemovedSessionCount} inactive Razor/Blazor exchange sessions.", removed);

        return removed;
    }

    public bool RemoveSession(string sessionId)
    {
        ValidateSession(sessionId);
        var removed = _sessions.TryRemove(sessionId, out _);
        if (removed)
            Interlocked.Increment(ref _removedSessions);
        return removed;
    }

    /// <summary>
    /// Returns a point-in-time diagnostic snapshot without exposing session identifiers or values.
    /// </summary>
    public RazorBlazorDataExchangeMetricsSnapshot GetMetrics()
    {
        var storedValues = 0;
        var subscriptions = 0;

        foreach (var state in _sessions.Values)
        {
            lock (state.SyncRoot)
            {
                storedValues += state.Values.Count;
                subscriptions += state.Subscriptions.Count;
            }
        }

        return new RazorBlazorDataExchangeMetricsSnapshot(
            _sessions.Count,
            storedValues,
            subscriptions,
            Interlocked.Read(ref _publishedMessages),
            Interlocked.Read(ref _deliveredMessages),
            Interlocked.Read(ref _suppressedMessages),
            Interlocked.Read(ref _deliveryFailures),
            Interlocked.Read(ref _removedSessions));
    }

    // ---------------------------------------------------------------------
    // Legacy API compatibility
    // ---------------------------------------------------------------------

    public void NotifyDataChange(
        string sessionId,
        string property,
        object? value,
        string setter,
        List<string>? getters = null,
        bool isProcessingNotification = false)
    {
        var prepared = PreparePublication(
            sessionId,
            property,
            value,
            value?.GetType() ?? typeof(object),
            setter,
            correlationId: null,
            legacyGetters: getters,
            legacyIsProcessing: isProcessingNotification);

        if (prepared is null)
            return;

        DispatchSynchronously(prepared);
        RaiseLegacySingle(prepared);
    }

    public void NotifyDataChange(
        string sessionId,
        string property,
        string setter,
        List<string>? getters = null,
        bool isProcessingNotification = false)
    {
        NotifyDataChange(sessionId, property, GetValue(sessionId, property), setter, getters, isProcessingNotification);
    }

    public void NotifyDataChanges(
        string sessionId,
        List<string> properties,
        string setter,
        List<string>? getters = null,
        bool isProcessingNotification = false)
    {
        ValidateSession(sessionId);
        ArgumentNullException.ThrowIfNull(properties);
        if (string.IsNullOrWhiteSpace(setter))
            throw new ArgumentException("Setter cannot be empty.", nameof(setter));

        TouchBroker();
        Interlocked.Increment(ref _publishedMessages);

        if (!_options.EnableLegacyEvents)
            return;

        var args = new DataChangesWithActorEventArgs(
            sessionId,
            properties,
            setter,
            getters,
            isProcessingNotification);

        InvokeLegacyHandlers(DataChangesWithActor, args, "DataChangesWithActor");
    }

    public bool SameSetter(string setter1, string setter2)
        => string.Equals(setter1, setter2, StringComparison.OrdinalIgnoreCase);

    public bool SameSession(string sessionId1, string sessionId2)
        => string.Equals(sessionId1, sessionId2, StringComparison.OrdinalIgnoreCase);

    public bool ShouldProcessEvent(string sessionId, string setter, DataChangeWithActorEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!SameSession(sessionId, e.SessionId))
            return false;
        if (SameSetter(setter, e.Setter))
            return false;
        if (e.IsProcessingNotification)
            return false;

        return true;
    }

    // ---------------------------------------------------------------------
    // Broker internals
    // ---------------------------------------------------------------------

    private PreparedPublication? PreparePublication(
        string sessionId,
        string propertyName,
        object? value,
        Type declaredType,
        string actorId,
        Guid? correlationId,
        List<string>? legacyGetters,
        bool legacyIsProcessing)
    {
        ValidateAddress(sessionId, propertyName, actorId);
        var state = GetOrCreateSession(sessionId);
        var now = DateTimeOffset.UtcNow;

        lock (state.SyncRoot)
        {
            TouchSessionLocked(state, now);
            return CreatePreparedPublicationLocked(
                state,
                propertyName,
                value,
                declaredType,
                actorId,
                correlationId,
                legacyGetters,
                legacyIsProcessing,
                now);
        }
    }

    private PreparedPublication? PrepareAtomicUpdate<T>(
        string sessionId,
        string propertyName,
        Func<T?, T?> updater,
        string actorId,
        Guid? correlationId)
    {
        ValidateAddress(sessionId, propertyName, actorId);
        var state = GetOrCreateSession(sessionId);
        var now = DateTimeOffset.UtcNow;

        lock (state.SyncRoot)
        {
            TouchSessionLocked(state, now);

            T? current = default;
            if (state.Values.TryGetValue(propertyName, out var metadata) && metadata.Value is not null)
            {
                if (metadata.Value is not T typed)
                {
                    throw new InvalidCastException(
                        $"Stored value '{propertyName}' in session '{sessionId}' is {metadata.Type.FullName}, not {typeof(T).FullName}.");
                }

                current = typed;
            }

            var next = updater(current);
            return CreatePreparedPublicationLocked(
                state,
                propertyName,
                next,
                typeof(T),
                actorId,
                correlationId,
                legacyGetters: null,
                legacyIsProcessing: false,
                now);
        }
    }

    private PreparedPublication? CreatePreparedPublicationLocked(
        ExchangeSessionState state,
        string propertyName,
        object? value,
        Type declaredType,
        string actorId,
        Guid? correlationId,
        List<string>? legacyGetters,
        bool legacyIsProcessing,
        DateTimeOffset now)
    {
        PruneCorrelationGuardsLocked(state, now);

        var correlation = correlationId ?? Guid.NewGuid();
        var guardKey = new CorrelationGuardKey(
            correlation,
            actorId.ToUpperInvariant(),
            propertyName);

        if (state.CorrelationGuards.ContainsKey(guardKey))
        {
            Interlocked.Increment(ref _suppressedMessages);
            _logger.LogDebug(
                "Suppressed recursive exchange publication. Session={SessionId}, Property={PropertyName}, Actor={ActorId}, Correlation={CorrelationId}",
                state.SessionId,
                propertyName,
                actorId,
                correlation);
            return null;
        }

        state.CorrelationGuards[guardKey] = now;

        var valueType = value?.GetType() ?? declaredType;
        var version = UpsertValueLocked(state, propertyName, value, valueType, actorId, now);

        var message = new ExchangeMessage(
            state.SessionId,
            propertyName,
            value,
            valueType,
            actorId,
            Guid.NewGuid(),
            correlation,
            version,
            now);

        var subscriptions = state.Subscriptions.Values.ToArray();
        Interlocked.Increment(ref _publishedMessages);

        return new PreparedPublication(
            message,
            subscriptions,
            legacyGetters?.ToList() ?? new List<string>(),
            legacyIsProcessing);
    }

    private long UpsertValueLocked(
        ExchangeSessionState state,
        string propertyName,
        object? value,
        Type valueType,
        string actorId,
        DateTimeOffset now)
    {
        state.Values.TryGetValue(propertyName, out var previous);
        var version = (previous?.Version ?? 0) + 1;
        var history = previous?.ModificationHistory
            .Select(CloneHistory)
            .ToList() ?? new List<ModificationHistory>();

        if (previous is not null && _options.MaxModificationHistory > 0)
        {
            history.Add(new ModificationHistory
            {
                ModifiedBy = previous.LastModifiedBy,
                ModifiedAt = previous.LastModifiedAt,
                PreviousValue = previous.Value
            });

            if (history.Count > _options.MaxModificationHistory)
                history.RemoveRange(0, history.Count - _options.MaxModificationHistory);
        }
        else if (_options.MaxModificationHistory == 0)
        {
            history.Clear();
        }

        state.Values[propertyName] = new ObjectValueMetadata
        {
            Value = value,
            Type = valueType,
            LastModifiedBy = actorId,
            LastModifiedAt = now.UtcDateTime,
            Version = version,
            ModificationHistory = history
        };

        return version;
    }

    private void DispatchSynchronously(PreparedPublication prepared)
    {
        foreach (var subscription in prepared.Subscriptions)
        {
            if (!subscription.CanAccept(prepared.Message))
                continue;

            try
            {
                var pending = subscription.InvokeAsync(prepared.Message, CancellationToken.None);
                if (pending.IsCompletedSuccessfully)
                {
                    pending.GetAwaiter().GetResult();
                    Interlocked.Increment(ref _deliveredMessages);
                }
                else
                {
                    _ = CompleteDeferredDispatchAsync(pending, subscription, prepared.Message);
                }
            }
            catch (Exception ex)
            {
                RecordDeliveryFailure(ex, subscription, prepared.Message);
            }
        }
    }

    private async Task CompleteDeferredDispatchAsync(
        ValueTask pending,
        IExchangeSubscriptionEntry subscription,
        ExchangeMessage message)
    {
        try
        {
            await pending.ConfigureAwait(false);
            Interlocked.Increment(ref _deliveredMessages);
        }
        catch (Exception ex)
        {
            RecordDeliveryFailure(ex, subscription, message);
        }
    }

    private async Task DispatchAsync(PreparedPublication prepared, CancellationToken cancellationToken)
    {
        var tasks = prepared.Subscriptions
            .Where(subscription => subscription.CanAccept(prepared.Message))
            .Select(subscription => DispatchOneAsync(subscription, prepared.Message, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DispatchOneAsync(
        IExchangeSubscriptionEntry subscription,
        ExchangeMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await subscription.InvokeAsync(message, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _deliveredMessages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative cancellation is not considered a subscriber failure.
        }
        catch (Exception ex)
        {
            RecordDeliveryFailure(ex, subscription, message);
        }
    }

    private void RecordDeliveryFailure(
        Exception exception,
        IExchangeSubscriptionEntry subscription,
        ExchangeMessage message)
    {
        Interlocked.Increment(ref _deliveryFailures);
        _logger.LogError(
            exception,
            "Exchange subscriber failed. Session={SessionId}, Property={PropertyName}, SubscriberActor={ActorId}",
            message.SessionId,
            message.PropertyName,
            subscription.ActorId);
    }

    private void RaiseLegacySingle(PreparedPublication prepared)
    {
        if (!_options.EnableLegacyEvents)
            return;

        var message = prepared.Message;
        var args = new DataChangeWithActorEventArgs(
            message.SessionId,
            message.PropertyName,
            message.Value,
            message.ValueType,
            message.ActorId,
            prepared.LegacyGetters,
            prepared.LegacyIsProcessing);

        InvokeLegacyHandlers(DataChangeWithActor, args, "DataChangeWithActor");
        InvokePropertyChangedHandlers(new PropertyChangedEventArgs(message.PropertyName));
    }

    private void InvokeLegacyHandlers<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string eventName)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
            return;

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _deliveryFailures);
                _logger.LogError(ex, "Legacy exchange event handler failed. Event={EventName}", eventName);
            }
        }
    }

    private void InvokePropertyChangedHandlers(PropertyChangedEventArgs args)
    {
        var handlers = PropertyChanged;
        if (handlers is null)
            return;

        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _deliveryFailures);
                _logger.LogError(ex, "PropertyChanged exchange event handler failed.");
            }
        }
    }

    private void Unsubscribe(string sessionId, long subscriptionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return;

        lock (state.SyncRoot)
        {
            state.Subscriptions.Remove(subscriptionId);
            TouchSessionLocked(state, DateTimeOffset.UtcNow);
        }
    }

    private ExchangeSessionState GetOrCreateSession(string sessionId)
    {
        ValidateSession(sessionId);
        TouchBroker();
        return _sessions.GetOrAdd(sessionId, id => new ExchangeSessionState(id));
    }

    private void TouchSessionLocked(ExchangeSessionState state, DateTimeOffset now)
    {
        state.LastAccess = now;
        TouchBroker(now.UtcDateTime);
    }

    private void TouchBroker()
        => TouchBroker(DateTime.UtcNow);

    private void TouchBroker(DateTime nowUtc)
        => Interlocked.Exchange(ref _lastAccessTicks, nowUtc.Ticks);

    private void PruneCorrelationGuardsLocked(ExchangeSessionState state, DateTimeOffset now)
    {
        if (state.CorrelationGuards.Count == 0)
            return;

        // Avoid a full dictionary scan on every publication while still bounding memory.
        if (state.CorrelationGuards.Count < 1024 && now - state.LastCorrelationPrune < _options.CorrelationRetention)
            return;

        var expiredBefore = now - _options.CorrelationRetention;
        var expired = state.CorrelationGuards
            .Where(pair => pair.Value < expiredBefore)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in expired)
            state.CorrelationGuards.Remove(key);

        state.LastCorrelationPrune = now;
    }

    private static bool CanDeliverTo<T>(ExchangeMessage message)
    {
        var targetType = typeof(T);

        if (message.Value is not null)
            return targetType.IsAssignableFrom(message.Value.GetType());

        // For null payloads there is no runtime value type to inspect. The envelope keeps
        // the declared publication type specifically so typed routing remains correct.
        return targetType.IsAssignableFrom(message.ValueType);
    }

    private static ExchangeMessage<T> ToTypedMessage<T>(ExchangeMessage message)
    {
        T? value = default;
        if (message.Value is not null)
            value = (T)message.Value;

        return new ExchangeMessage<T>(
            message.SessionId,
            message.PropertyName,
            value,
            message.ActorId,
            message.MessageId,
            message.CorrelationId,
            message.Version,
            message.Timestamp);
    }

    private static ObjectValueMetadata CloneMetadata(ObjectValueMetadata source)
        => new()
        {
            Value = source.Value,
            Type = source.Type,
            LastModifiedBy = source.LastModifiedBy,
            LastModifiedAt = source.LastModifiedAt,
            Version = source.Version,
            ModificationHistory = source.ModificationHistory.Select(CloneHistory).ToList()
        };

    private static ModificationHistory CloneHistory(ModificationHistory source)
        => new()
        {
            ModifiedBy = source.ModifiedBy,
            ModifiedAt = source.ModifiedAt,
            PreviousValue = source.PreviousValue
        };

    private static void ValidateSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));
    }

    private static void ValidateSessionAndProperty(string sessionId, string propertyName)
    {
        ValidateSession(sessionId);
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name cannot be empty.", nameof(propertyName));
    }

    private static void ValidateAddress(string sessionId, string propertyName, string actorId)
    {
        ValidateSessionAndProperty(sessionId, propertyName);
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("ActorId cannot be empty.", nameof(actorId));
    }

    private sealed class ExchangeSessionState
    {
        public ExchangeSessionState(string sessionId)
        {
            SessionId = sessionId;
            LastAccess = DateTimeOffset.UtcNow;
            LastCorrelationPrune = DateTimeOffset.UtcNow;
        }

        public string SessionId { get; }
        public object SyncRoot { get; } = new();
        public Dictionary<string, ObjectValueMetadata> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<long, IExchangeSubscriptionEntry> Subscriptions { get; } = new();
        public Dictionary<CorrelationGuardKey, DateTimeOffset> CorrelationGuards { get; } = new();
        public DateTimeOffset LastAccess { get; set; }
        public DateTimeOffset LastCorrelationPrune { get; set; }
    }

    private readonly record struct CorrelationGuardKey(Guid CorrelationId, string ActorId, string PropertyName);

    private sealed record PreparedPublication(
        ExchangeMessage Message,
        IExchangeSubscriptionEntry[] Subscriptions,
        List<string> LegacyGetters,
        bool LegacyIsProcessing);

    private interface IExchangeSubscriptionEntry
    {
        long Id { get; }
        string PropertyName { get; }
        string ActorId { get; }
        bool CanAccept(ExchangeMessage message);
        ValueTask InvokeAsync(ExchangeMessage message, CancellationToken cancellationToken);
    }

    private sealed class ExchangeSubscriptionEntry<T> : IExchangeSubscriptionEntry
    {
        private readonly Func<ExchangeMessage<T>, CancellationToken, ValueTask> _handler;

        public ExchangeSubscriptionEntry(
            long id,
            string propertyName,
            string actorId,
            Func<ExchangeMessage<T>, CancellationToken, ValueTask> handler)
        {
            Id = id;
            PropertyName = propertyName;
            ActorId = actorId;
            _handler = handler;
        }

        public long Id { get; }
        public string PropertyName { get; }
        public string ActorId { get; }

        public bool CanAccept(ExchangeMessage message)
        {
            if (!string.Equals(PropertyName, message.PropertyName, StringComparison.Ordinal))
                return false;

            if (string.Equals(ActorId, message.ActorId, StringComparison.OrdinalIgnoreCase))
                return false;

            return CanDeliverTo<T>(message);
        }

        public ValueTask InvokeAsync(ExchangeMessage message, CancellationToken cancellationToken)
        {
            T? value = default;
            if (message.Value is not null)
                value = (T)message.Value;

            var typed = new ExchangeMessage<T>(
                message.SessionId,
                message.PropertyName,
                value,
                message.ActorId,
                message.MessageId,
                message.CorrelationId,
                message.Version,
                message.Timestamp);

            return _handler(typed, cancellationToken);
        }
    }

    private sealed class ExchangeSubscriptionHandle : IDisposable
    {
        private Action? _unsubscribe;

        public ExchangeSubscriptionHandle(Action unsubscribe)
            => _unsubscribe = unsubscribe;

        public void Dispose()
            => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
