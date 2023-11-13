using InfluxDB.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SIGrid.Telemetry;

public static class InfluxDbServiceRegistry
{
    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<InfluxDbOptions>(config.GetSection("Influx"));
        services.AddTransient(sp =>
        {
            var dbOptions = sp.GetRequiredService<IOptions<InfluxDbOptions>>().Value;
            return new InfluxDBClient(new InfluxDBClientOptions(dbOptions.Host)
            {
                AllowHttpRedirects = true,
                Bucket = dbOptions.Bucket,
                Org = dbOptions.Organization,
                Token = dbOptions.ApiKey
            });
        });
        services.AddTransient<TelemetrySender>();

        return services;
    }
}
