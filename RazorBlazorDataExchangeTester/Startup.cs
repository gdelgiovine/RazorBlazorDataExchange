using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RazorBlazorDataExchangeTester
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRazorPages();

            // The sample intentionally uses the classic Blazor Server hosting model because
            // the goal is embedding interactive Blazor components inside existing Razor Pages.
            services.AddServerSideBlazor()
                .AddCircuitOptions(options => options.DetailedErrors = true);

            services.AddDistributedMemoryCache();
            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });

            // The broker is intentionally singleton: Razor/MVC HTTP requests and Blazor
            // Server circuits live in different DI scopes and need a common in-process bridge.
            services.AddRazorBlazorDataExchange(options =>
            {
                options.SessionIdleTimeout = TimeSpan.FromMinutes(30);
                options.CleanupInterval = TimeSpan.FromMinutes(5);
                options.CorrelationRetention = TimeSpan.FromMinutes(2);
                options.MaxModificationHistory = 50;
                options.CleanupSessionsWithActiveSubscriptions = false;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseSession();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        }
    }
}
