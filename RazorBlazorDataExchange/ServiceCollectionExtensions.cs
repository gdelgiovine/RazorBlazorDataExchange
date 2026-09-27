using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

        services.AddOptions<RazorBlazorDataExchangeOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<RazorBlazorDataExchange>();
        services.TryAddSingleton<RazorBlazorCircuitHandler>();
        services.TryAddScoped<RazorBlazorDataExchangeProvider>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<CircuitHandler>(sp => sp.GetRequiredService<RazorBlazorCircuitHandler>()));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RazorBlazorDataExchangeCleanupService>());

        return services;
    }
}
