/// <summary>
/// Session-wide subscription APIs for the bidirectional Razor/Blazor broker.
/// These subscriptions deliberately do not constrain messages to one property or one UI technology.
/// Razor/MVC and Blazor actors are symmetric publishers and subscribers.
/// </summary>
public partial class RazorBlazorDataExchange
{
    private const string SessionWidePropertyMarker = "*";

    /// <summary>
    /// Subscribes an actor to every message published in the specified session, regardless
    /// of property name or value type. Messages published by the same actor are suppressed
    /// only to avoid self-echo; messages from every other Razor or Blazor actor are delivered.
    /// </summary>
    public IDisposable SubscribeSession(
        string sessionId,
        string actorId,
        Func<ExchangeMessage, CancellationToken, ValueTask> handler)
    {
        ValidateSession(sessionId);
        ValidateActor(actorId);
        ArgumentNullException.ThrowIfNull(handler);

        var id = Interlocked.Increment(ref _nextSubscriptionId);
        var entry = new SessionExchangeSubscriptionEntry(id, actorId, handler);
        return RegisterSessionSubscription(sessionId, id, entry);
    }

    public IDisposable SubscribeSession(
        string sessionId,
        string actorId,
        Action<ExchangeMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return SubscribeSession(sessionId, actorId, (message, _) =>
        {
            handler(message);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Subscribes an actor to every message of type T published in the specified session,
    /// independently of the property name. This is useful for page-level coordinators that
    /// must observe several exchanged properties without registering one handler per property.
    /// </summary>
    public IDisposable SubscribeSession<T>(
        string sessionId,
        string actorId,
        Func<ExchangeMessage<T>, CancellationToken, ValueTask> handler)
    {
        ValidateSession(sessionId);
        ValidateActor(actorId);
        ArgumentNullException.ThrowIfNull(handler);

        var id = Interlocked.Increment(ref _nextSubscriptionId);
        var entry = new TypedSessionExchangeSubscriptionEntry<T>(id, actorId, handler);
        return RegisterSessionSubscription(sessionId, id, entry);
    }

    public IDisposable SubscribeSession<T>(
        string sessionId,
        string actorId,
        Action<ExchangeMessage<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return SubscribeSession<T>(sessionId, actorId, (message, _) =>
        {
            handler(message);
            return ValueTask.CompletedTask;
        });
    }

    private IDisposable RegisterSessionSubscription(
        string sessionId,
        long subscriptionId,
        IExchangeSubscriptionEntry entry)
    {
        var state = GetOrCreateSession(sessionId);

        lock (state.SyncRoot)
        {
            TouchSessionLocked(state, DateTimeOffset.UtcNow);
            state.Subscriptions[subscriptionId] = entry;
        }

        return new ExchangeSubscriptionHandle(() => Unsubscribe(sessionId, subscriptionId));
    }

    private static void ValidateActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("ActorId cannot be empty.", nameof(actorId));
    }

    private sealed class SessionExchangeSubscriptionEntry : IExchangeSubscriptionEntry
    {
        private readonly Func<ExchangeMessage, CancellationToken, ValueTask> _handler;

        public SessionExchangeSubscriptionEntry(
            long id,
            string actorId,
            Func<ExchangeMessage, CancellationToken, ValueTask> handler)
        {
            Id = id;
            ActorId = actorId;
            _handler = handler;
        }

        public long Id { get; }
        public string PropertyName => SessionWidePropertyMarker;
        public string ActorId { get; }

        public bool CanAccept(ExchangeMessage message)
            => !string.Equals(ActorId, message.ActorId, StringComparison.OrdinalIgnoreCase);

        public ValueTask InvokeAsync(ExchangeMessage message, CancellationToken cancellationToken)
            => _handler(message, cancellationToken);
    }

    private sealed class TypedSessionExchangeSubscriptionEntry<T> : IExchangeSubscriptionEntry
    {
        private readonly Func<ExchangeMessage<T>, CancellationToken, ValueTask> _handler;

        public TypedSessionExchangeSubscriptionEntry(
            long id,
            string actorId,
            Func<ExchangeMessage<T>, CancellationToken, ValueTask> handler)
        {
            Id = id;
            ActorId = actorId;
            _handler = handler;
        }

        public long Id { get; }
        public string PropertyName => SessionWidePropertyMarker;
        public string ActorId { get; }

        public bool CanAccept(ExchangeMessage message)
        {
            if (string.Equals(ActorId, message.ActorId, StringComparison.OrdinalIgnoreCase))
                return false;

            if (message.Value is null)
                return !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null;

            return typeof(T).IsAssignableFrom(message.Value.GetType());
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
}
