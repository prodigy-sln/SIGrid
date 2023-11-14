using InfluxDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SIGrid.Telemetry;

public class TelemetrySender
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelemetrySender> _log;
    private readonly bool _enabled;

    private InfluxDBClient GetClient() => _serviceProvider.GetRequiredService<InfluxDBClient>();

    public TelemetrySender(IOptions<TelemetryOptions> options, IServiceProvider serviceProvider, ILogger<TelemetrySender> log)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _enabled = options.Value.Enabled;
    }

    public async void WritePriceUpdateAsync(TelemetrySymbolPriceUpdate priceUpdate)
    {
        if (!_enabled) return;

        try
        {
            await GetClient().GetWriteApiAsync().WriteMeasurementAsync(priceUpdate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error sending telemetry.");
        }
    }

    public async void WriteOrderUpdateAsync(TelemetryOrderUpdate orderUpdate)
    {
        if (!_enabled) return;
        
        try
        {
            await GetClient().GetWriteApiAsync().WriteMeasurementAsync(orderUpdate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error sending telemetry.");
        }
    }
}
