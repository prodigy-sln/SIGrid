using System.Reflection;
using Lamar;
using Lamar.Microsoft.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OKX.Net.Clients;
using OKX.Net.Objects;
using Serilog;
using Serilog.Events;
using SIGrid.App.GridBot.OKX;

namespace SIGrid.App.GridBot;

internal class Program
{
    public static readonly CancellationTokenSource ApplicationStoppingToken = new();

    public static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, _) => ApplicationStoppingToken.Cancel();
        CreateDefaultLogger();

        try
        {
            Log.Information("Starting up {StartupAssembly}...", Assembly.GetEntryAssembly()?.GetName().Name ?? "program");

            var builder = Host.CreateDefaultBuilder(args)
                .UseLamar()
                .ConfigureServices((host, services) =>
                {
                    services.AddHostedService<GridBotStarterService>();

                    services.Configure<SIGridOptions>(options =>
                    {
                        host.Configuration.GetSection("SIGrid").Bind(options);
                        foreach (var symbol in options.TradedSymbols)
                        {
                            symbol.ReinvestProfit /= 100.0M;
                            symbol.TakeProfitPercent /= 100.0M;
                        }
                    });
                    services.Configure<OKXOptions>(host.Configuration.GetSection("OKX"));

                    services.AddSingleton(sp =>
                    {
                        var options = sp.GetRequiredService<IOptions<OKXOptions>>().Value;
                        return new OKXApiCredentials(options.ApiKey, options.ApiSecret, options.ApiPassPhrase);
                    });

                    services.AddSingleton(sp =>
                    {
                        var okxOptions = sp.GetRequiredService<IOptions<OKXOptions>>().Value;
                        return new OKXRestClient(null, sp.GetRequiredService<ILoggerFactory>(),
                            options =>
                            {
                                options.ApiCredentials = sp.GetRequiredService<OKXApiCredentials>();
                                if (okxOptions.RequestTimeout.HasValue)
                                {
                                    options.RequestTimeout = okxOptions.RequestTimeout.Value;
                                }
                            });
                    });

                    services.AddSingleton(sp =>
                    {
                        var okxOptions = sp.GetRequiredService<IOptions<OKXOptions>>().Value;
                        return new OKXSocketClient(options =>
                        {
                            options.ApiCredentials = sp.GetRequiredService<OKXApiCredentials>();
                            options.AutoReconnect = true;
                            if (okxOptions.RequestTimeout.HasValue)
                            {
                                options.RequestTimeout = okxOptions.RequestTimeout.Value;
                            }
                        }, sp.GetRequiredService<ILoggerFactory>());
                    });

                    services.AddTransient<GridBot>();
                })
                .UseSerilog((host, config) =>
                {
                    config.ReadFrom.Configuration(host.Configuration)
#if DEBUG
                        .WriteTo.Debug()
#endif
                        ;
                })
                .ConfigureContainer<ServiceRegistry>((_, services) =>
                {
                    services.Scan(s =>
                    {
                        s.TheCallingAssembly();
                        s.WithDefaultConventions();
                    });
                });

            var host = builder.Build();

            await host.RunAsync(token: ApplicationStoppingToken.Token);

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Program terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }

        static void CreateDefaultLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .CreateBootstrapLogger();
        }
    }
}
