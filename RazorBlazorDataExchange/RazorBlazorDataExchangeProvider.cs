using Microsoft.AspNetCore.Http;

/// <summary>
/// Compatibility provider that resolves the single application broker from either
/// the current HTTP context or a known Blazor circuit.
/// </summary>
public class RazorBlazorDataExchangeProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly RazorBlazorCircuitHandler _circuitHandler;
    private readonly RazorBlazorDataExchange _exchange;

    public RazorBlazorDataExchangeProvider(
        IHttpContextAccessor httpContextAccessor,
        RazorBlazorCircuitHandler circuitHandler,
        RazorBlazorDataExchange exchange)
    {
        _httpContextAccessor = httpContextAccessor;
        _circuitHandler = circuitHandler;
        _exchange = exchange;
    }

    /// <summary>
    /// Backward-compatible constructor for code that previously supplied only the accessor and circuit handler.
    /// </summary>
    public RazorBlazorDataExchangeProvider(
        IHttpContextAccessor httpContextAccessor,
        RazorBlazorCircuitHandler circuitHandler)
        : this(httpContextAccessor, circuitHandler, circuitHandler.Exchange)
    {
    }

    public RazorBlazorDataExchange GetOrCreate(string? circuitId = null)
    {
        if (!string.IsNullOrWhiteSpace(circuitId))
        {
            var circuitExchange = _circuitHandler.GetInstance(circuitId);
            if (circuitExchange is not null)
                return circuitExchange;
        }

        // Force session initialization when a normal Razor/MVC request is available.
        // The broker itself is not stored in session; only the session identity is used by callers.
        var context = _httpContextAccessor.HttpContext;
        if (context?.Session is not null)
            context.Session.SetString("RazorBlazorDataExchange.SessionInitialized", "true");

        return _exchange;
    }

    public int CleanupInactiveSessions(TimeSpan timeout)
        => _exchange.CleanupInactiveSessions(timeout);
}
