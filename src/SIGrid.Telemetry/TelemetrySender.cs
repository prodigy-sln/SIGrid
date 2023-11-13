using InfluxDB.Client;

namespace SIGrid.Telemetry;

public class TelemetrySender
{
    private readonly WriteApiAsync _writeApi;

    public TelemetrySender(InfluxDBClient dbClient)
    {
        _writeApi = dbClient.GetWriteApiAsync();
    }

    public async void WritePriceUpdateAsync(TelemetrySymbolPriceUpdate priceUpdate)
    {
        await _writeApi.WriteMeasurementAsync(priceUpdate);
    }

    public async void WriteOrderUpdateAsync(TelemetryOrderUpdate orderUpdate)
    {
        await _writeApi.WriteMeasurementAsync(orderUpdate);
    }
}
