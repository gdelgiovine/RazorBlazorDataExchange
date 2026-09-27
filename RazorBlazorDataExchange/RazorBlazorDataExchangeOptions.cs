/// <summary>
/// Runtime options for the singleton Razor/Blazor exchange broker.
/// </summary>
public sealed class RazorBlazorDataExchangeOptions
{
    /// <summary>
    /// A session with no activity for this interval becomes eligible for cleanup.
    /// Sessions with active subscriptions are preserved by default.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Frequency used by the background cleanup service.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Correlation guard lifetime used to prevent an actor from re-publishing
    /// the same property within the same notification chain indefinitely.
    /// </summary>
    public TimeSpan CorrelationRetention { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum number of historical values retained per property.
    /// Set to 0 to disable history retention.
    /// </summary>
    public int MaxModificationHistory { get; set; } = 50;

    /// <summary>
    /// If true, automatic cleanup may remove inactive sessions even while they still
    /// contain subscriptions. The safe default is false.
    /// </summary>
    public bool CleanupSessionsWithActiveSubscriptions { get; set; }

    /// <summary>
    /// Continue raising the legacy DataChangeWithActor/DataChangesWithActor events.
    /// </summary>
    public bool EnableLegacyEvents { get; set; } = true;
}
