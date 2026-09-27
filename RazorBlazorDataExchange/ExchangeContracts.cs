using System.ComponentModel;

/// <summary>
/// Legacy single-property change notification. Kept for source compatibility with the original API.
/// New code should prefer <see cref="ExchangeMessage{T}"/> subscriptions.
/// </summary>
public class DataChangeWithActorEventArgs : PropertyChangedEventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public string Setter { get; set; } = string.Empty;
    public object? Value { get; set; }
    public Type Type { get; set; } = typeof(object);
    public List<string> Getters { get; set; } = new();
    public bool IsProcessingNotification { get; set; }

    public DataChangeWithActorEventArgs(
        string sessionId,
        string propertyName,
        object? value,
        Type? type,
        string setter,
        List<string>? getters,
        bool isProcessingNotification = false)
        : base(propertyName)
    {
        SessionId = sessionId;
        Setter = setter;
        Getters = getters ?? new List<string>();
        Value = value;
        Type = type ?? value?.GetType() ?? typeof(object);
        IsProcessingNotification = isProcessingNotification;
    }
}

/// <summary>
/// Legacy multi-property change notification. Kept for source compatibility.
/// </summary>
public class DataChangesWithActorEventArgs : PropertyChangedEventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public string Setter { get; set; } = string.Empty;
    public List<string> Properties { get; set; } = new();
    public List<string> Getters { get; set; } = new();
    public bool IsProcessingNotification { get; set; }

    public DataChangesWithActorEventArgs(
        string sessionId,
        List<string>? properties,
        string setter,
        List<string>? getters,
        bool isProcessingNotification = false)
        : base(string.Join(",", properties ?? new List<string>()))
    {
        SessionId = sessionId;
        Setter = setter;
        Getters = getters ?? new List<string>();
        Properties = properties ?? new List<string>();
        IsProcessingNotification = isProcessingNotification;
    }
}

/// <summary>
/// Immutable, non-generic envelope used internally by the broker and exposed for diagnostics.
/// </summary>
public sealed record ExchangeMessage(
    string SessionId,
    string PropertyName,
    object? Value,
    Type ValueType,
    string ActorId,
    Guid MessageId,
    Guid CorrelationId,
    long Version,
    DateTimeOffset Timestamp);

/// <summary>
/// Strongly typed envelope delivered to typed subscriptions.
/// </summary>
public sealed record ExchangeMessage<T>(
    string SessionId,
    string PropertyName,
    T? Value,
    string ActorId,
    Guid MessageId,
    Guid CorrelationId,
    long Version,
    DateTimeOffset Timestamp);

/// <summary>
/// Point-in-time broker metrics.
/// </summary>
public sealed record RazorBlazorDataExchangeMetricsSnapshot(
    int ActiveSessions,
    int StoredValues,
    int ActiveSubscriptions,
    long PublishedMessages,
    long DeliveredMessages,
    long SuppressedMessages,
    long DeliveryFailures,
    long RemovedSessions);

/// <summary>
/// Metadata associated with a stored exchange value.
/// </summary>
public class ObjectValueMetadata
{
    public object? Value { get; set; }
    public Type Type { get; set; } = typeof(object);
    public string LastModifiedBy { get; set; } = string.Empty;
    public DateTime LastModifiedAt { get; set; }
    public long Version { get; set; }
    public List<ModificationHistory> ModificationHistory { get; set; } = new();
}

/// <summary>
/// One item of value modification history.
/// </summary>
public class ModificationHistory
{
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedAt { get; set; }
    public object? PreviousValue { get; set; }
}
