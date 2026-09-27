using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Infrastructure transport contract for future multi-process exchange adapters.
///
/// The application-facing broker API does not depend on a specific transport. A Redis,
/// Service Bus or other adapter can implement this contract without changing Razor/Blazor
/// publishers and subscribers.
/// </summary>
public interface IRazorBlazorDataExchangeTransport
{
    string Name { get; }
    bool IsDistributed { get; }

    IDisposable Subscribe(
        string consumerId,
        Func<RazorBlazorTransportEnvelope, CancellationToken, ValueTask> handler);

    ValueTask PublishAsync(
        RazorBlazorTransportEnvelope envelope,
        CancellationToken cancellationToken = default);

    RazorBlazorTransportMetricsSnapshot GetMetrics();
}

/// <summary>
/// Serialization-neutral wire envelope. PayloadJson and ValueTypeName deliberately avoid
/// exposing System.Type or live object references to a future distributed transport.
/// </summary>
public sealed record RazorBlazorTransportEnvelope(
    string OriginNodeId,
    string SessionId,
    string PropertyName,
    string ActorId,
    Guid MessageId,
    Guid CorrelationId,
    long SourceVersion,
    DateTimeOffset Timestamp,
    string ValueTypeName,
    string PayloadJson)
{
    public static RazorBlazorTransportEnvelope FromMessage<T>(
        ExchangeMessage<T> message,
        string originNodeId,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(originNodeId))
            throw new ArgumentException("Origin node id cannot be empty.", nameof(originNodeId));

        var type = typeof(T);
        return new RazorBlazorTransportEnvelope(
            originNodeId,
            message.SessionId,
            message.PropertyName,
            message.ActorId,
            message.MessageId,
            message.CorrelationId,
            message.Version,
            message.Timestamp,
            type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
            JsonSerializer.Serialize(message.Value, type, serializerOptions));
    }

    public T? DeserializeValue<T>(JsonSerializerOptions? serializerOptions = null)
        => JsonSerializer.Deserialize<T>(PayloadJson, serializerOptions);
}

public sealed record RazorBlazorTransportMetricsSnapshot(
    string TransportName,
    bool IsDistributed,
    int ActiveSubscribers,
    long PublishedEnvelopes,
    long DeliveredEnvelopes,
    long DeliveryFailures);

/// <summary>
/// Default process-local transport implementation. It is primarily the reference
/// implementation of the transport SPI and a deterministic test surface for adapters.
/// It uses broadcast fan-out: every registered consumer receives every envelope.
/// </summary>
public sealed class InMemoryRazorBlazorDataExchangeTransport : IRazorBlazorDataExchangeTransport
{
    private readonly ConcurrentDictionary<long, Subscription> _subscriptions = new();
    private readonly ILogger<InMemoryRazorBlazorDataExchangeTransport> _logger;
    private long _nextSubscriptionId;
    private long _publishedEnvelopes;
    private long _deliveredEnvelopes;
    private long _deliveryFailures;

    public InMemoryRazorBlazorDataExchangeTransport()
        : this(NullLogger<InMemoryRazorBlazorDataExchangeTransport>.Instance)
    {
    }

    public InMemoryRazorBlazorDataExchangeTransport(
        ILogger<InMemoryRazorBlazorDataExchangeTransport> logger)
        => _logger = logger ?? NullLogger<InMemoryRazorBlazorDataExchangeTransport>.Instance;

    public string Name => "InMemory";
    public bool IsDistributed => false;

    public IDisposable Subscribe(
        string consumerId,
        Func<RazorBlazorTransportEnvelope, CancellationToken, ValueTask> handler)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer id cannot be empty.", nameof(consumerId));
        ArgumentNullException.ThrowIfNull(handler);

        var id = Interlocked.Increment(ref _nextSubscriptionId);
        _subscriptions[id] = new Subscription(id, consumerId, handler);
        return new SubscriptionHandle(() => _subscriptions.TryRemove(id, out _));
    }

    public IDisposable Subscribe(
        string consumerId,
        Action<RazorBlazorTransportEnvelope> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Subscribe(consumerId, (envelope, _) =>
        {
            handler(envelope);
            return ValueTask.CompletedTask;
        });
    }

    public async ValueTask PublishAsync(
        RazorBlazorTransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Interlocked.Increment(ref _publishedEnvelopes);

        var snapshot = _subscriptions.Values.ToArray();
        if (snapshot.Length == 0)
            return;

        var tasks = snapshot.Select(subscription =>
            DeliverAsync(subscription, envelope, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public RazorBlazorTransportMetricsSnapshot GetMetrics()
        => new(
            Name,
            IsDistributed,
            _subscriptions.Count,
            Interlocked.Read(ref _publishedEnvelopes),
            Interlocked.Read(ref _deliveredEnvelopes),
            Interlocked.Read(ref _deliveryFailures));

    private async Task DeliverAsync(
        Subscription subscription,
        RazorBlazorTransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await subscription.Handler(envelope, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _deliveredEnvelopes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _deliveryFailures);
            _logger.LogError(
                ex,
                "Exchange transport subscriber failed. Transport={Transport}, Consumer={ConsumerId}, Session={SessionId}, Property={PropertyName}",
                Name,
                subscription.ConsumerId,
                envelope.SessionId,
                envelope.PropertyName);
        }
    }

    private sealed record Subscription(
        long Id,
        string ConsumerId,
        Func<RazorBlazorTransportEnvelope, CancellationToken, ValueTask> Handler);

    private sealed class SubscriptionHandle : IDisposable
    {
        private Action? _unsubscribe;

        public SubscriptionHandle(Action unsubscribe)
            => _unsubscribe = unsubscribe;

        public void Dispose()
            => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
