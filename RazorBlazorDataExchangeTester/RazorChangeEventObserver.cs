using System.Collections.Concurrent;

namespace RazorBlazorDataExchangeTester;

/// <summary>
/// Persistent Razor/MVC-side consumer of the same DataChanged event used by Blazor.
/// A Razor PageModel is request-scoped, so the long-lived event subscription belongs in
/// server-side infrastructure and PageModels consume the observed change state per request.
/// </summary>
public sealed class RazorChangeEventObserver : IDisposable
{
    private readonly RazorBlazorDataExchange _exchange;
    private readonly ConcurrentDictionary<string, RazorObservedChange> _lastBySession =
        new(StringComparer.Ordinal);
    private int _disposed;

    public RazorChangeEventObserver(RazorBlazorDataExchange exchange)
    {
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _exchange.DataChanged += OnDataChanged;
    }

    public bool TryGetLast(string sessionId, out RazorObservedChange? change)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));

        if (_lastBySession.TryGetValue(sessionId, out var found))
        {
            change = found;
            return true;
        }

        change = null;
        return false;
    }

    private void OnDataChanged(object? sender, RazorBlazorDataChangedEventArgs e)
    {
        _lastBySession[e.SessionId] = new RazorObservedChange(
            e.PropertyName,
            e.Value,
            e.ValueType,
            e.ActorId,
            e.MessageId,
            e.CorrelationId,
            e.Version,
            e.Timestamp);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _exchange.DataChanged -= OnDataChanged;
    }
}

public sealed record RazorObservedChange(
    string PropertyName,
    object? Value,
    Type ValueType,
    string ActorId,
    Guid MessageId,
    Guid CorrelationId,
    long Version,
    DateTimeOffset Timestamp);
