using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;

namespace RazorBlazorDataExchangeTester

{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {


            services.AddRazorPages();
            services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddCircuitOptions(option => { option.DetailedErrors = true; });
            services.AddServerSideBlazor()
                .AddCircuitOptions(option => { option.DetailedErrors = true; });


            // servizi Radzen   
            //services.AddRadzenComponents();
            //services.AddScoped<DialogService>();
            //services.AddScoped<NotificationService>();
            //services.AddScoped<TooltipService>();
            //services.AddScoped<ContextMenuService>();
            //services.AddScoped<Radzen.ThemeService>();
            //services.AddScoped<Radzen.Theme>();
            //services.AddScoped<Radzen.ThemeOptions>();
            //services.AddRadzenQueryStringThemeService();

            // fine servizi Radzen  
           

            services.AddDistributedMemoryCache();
            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });
            services.AddHttpContextAccessor();

            services.AddSingleton<RazorBlazorDataExchange>(); // Servizio dati Scoped
          
            
            //services.AddSingleton<RazorBlazorCircuitHandler>(); // CircuitHandler deve essere singleton
            //services.AddScoped<RazorBlazorDataExchange>(); // Servizio dati Scoped
            //services.AddScoped<RazorBlazorDataExchangeProvider>(); // Provider Scoped
            // Aggiungi i servizi Radzen

            //services.AddDbContextFactory<NewdisContext>(options =>
            //     options.UseSqlServer(Configuration.GetConnectionString("Newdis")));


        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
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
            app.UseSession(); // Aggiungi il middleware della sessione
            app.UseHttpsRedirection();
            app.UseStaticFiles();




            app.UseRouting();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                //endpoints.MapRazorComponents<BlazorComponents.CounterComponent>();
                // .AddInteractiveServerRenderMode();
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");

            });


        }
    }
}