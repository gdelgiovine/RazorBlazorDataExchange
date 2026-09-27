using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class RazorBlazorDataExchangeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singleton cross-scope broker, circuit registry, compatibility provider
    /// and automatic cleanup service.
    /// </summary>
    public static IServiceCollection AddRazorBlazorDataExchange(
        this IServiceCollection services,
        Action<RazorBlazorDataExchangeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services
            .AddOptions<RazorBlazorDataExchangeOptions>()
            .Validate(options => options.SessionIdleTimeout >= TimeSpan.Zero,
                "SessionIdleTimeout cannot be negative.")
            .Validate(options => options.CleanupInterval >= TimeSpan.Zero,
                "CleanupInterval cannot be negative. Use TimeSpan.Zero to disable automatic cleanup.")
            .Validate(options => options.CorrelationRetention >= TimeSpan.Zero,
                "CorrelationRetention cannot be negative.")
            .Validate(options => options.MaxModificationHistory >= 0,
                "MaxModificationHistory cannot be negative.")
            .ValidateOnStart();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<RazorBlazorDataExchange>();
        services.TryAddSingleton<RazorBlazorCircuitHandler>();
        services.TryAddScoped<RazorBlazorDataExchangeProvider>();

        // Blazor resolves CircuitHandler, while application code may resolve the concrete
        // registry. Both references point at the same singleton instance.
        services.AddSingleton<CircuitHandler>(sp => sp.GetRequiredService<RazorBlazorCircuitHandler>());

        services.AddHostedService<RazorBlazorDataExchangeCleanupService>();

        return services;
    }
}
