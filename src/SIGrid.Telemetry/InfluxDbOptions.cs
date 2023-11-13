namespace SIGrid.Telemetry;

public class InfluxDbOptions
{
    public string Host { get; init; }

    public string Organization { get; init; }

    public string Bucket { get; init; }

    public string ApiKey { get; init; }
}
