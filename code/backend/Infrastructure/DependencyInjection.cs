using Application.Interfaces;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("CyberKitDb"));

            services.AddScoped<IEnvironmentService, EnvironmentService>();

            services.Configure<Infrastructure.Pulumi.ProxmoxOptions>(configuration.GetSection(Infrastructure.Pulumi.ProxmoxOptions.SectionName));
            services.Configure<OpnsenseOptions>(configuration.GetSection(OpnsenseOptions.SectionName));
            services.AddHttpClient<IOpnsenseService, OpnsenseService>((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<OpnsenseOptions>>().Value;
                client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{o.ApiKey}:{o.ApiSecret}")));
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var o = sp.GetRequiredService<IOptions<OpnsenseOptions>>().Value;
                var handler = new HttpClientHandler();
                if (o.AllowInsecure) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                return handler;
            });

            return services;
        }
    }
}

