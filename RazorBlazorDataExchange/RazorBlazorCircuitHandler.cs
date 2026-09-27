using Microsoft.AspNetCore.Components.Server.Circuits;
using System.Collections.Concurrent;

/// <summary>
/// Tracks Blazor Server circuits without creating per-circuit exchange instances.
/// The exchange broker remains intentionally singleton and is shared across HTTP requests and circuits.
/// </summary>
public class RazorBlazorCircuitHandler : CircuitHandler
{
    private readonly RazorBlazorDataExchange _exchange;
    private readonly ConcurrentDictionary<string, CircuitRegistration> _circuits = new(StringComparer.Ordinal);

    public RazorBlazorCircuitHandler(RazorBlazorDataExchange exchange)
        => _exchange = exchange;

    public RazorBlazorDataExchange Exchange => _exchange;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuits[circuit.Id] = new CircuitRegistration(circuit.Id, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuits.TryRemove(circuit.Id, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Compatibility method. A known circuit resolves to the shared singleton broker.
    /// </summary>
    public RazorBlazorDataExchange? GetInstance(string circuitId)
    {
        if (string.IsNullOrWhiteSpace(circuitId))
            return null;

        return _circuits.ContainsKey(circuitId) ? _exchange : null;
    }

    public bool IsCircuitKnown(string circuitId)
        => !string.IsNullOrWhiteSpace(circuitId) && _circuits.ContainsKey(circuitId);

    public int ActiveCircuitCount => _circuits.Count;

    private sealed record CircuitRegistration(string CircuitId, DateTimeOffset OpenedAt);
}
