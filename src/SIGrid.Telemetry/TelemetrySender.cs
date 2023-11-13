using InfluxDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SIGrid.Telemetry;

public class TelemetrySender
{
    private readonly WriteApiAsync? _writeApi;
    private readonly bool _enabled;

    public TelemetrySender(IOptions<TelemetryOptions> options, IServiceProvider serviceProvider)
    {
        _enabled = options.Value.Enabled;
        if (!_enabled) return;

        var dbClient = serviceProvider.GetRequiredService<InfluxDBClient>();
        _writeApi = dbClient.GetWriteApiAsync();
    }

    public async void WritePriceUpdateAsync(TelemetrySymbolPriceUpdate priceUpdate)
    {
        if (!_enabled) return;

        await _writeApi!.WriteMeasurementAsync(priceUpdate);
    }

    public async void WriteOrderUpdateAsync(TelemetryOrderUpdate orderUpdate)
    {
        if (!_enabled) return;

        await _writeApi!.WriteMeasurementAsync(orderUpdate);
    }
}
