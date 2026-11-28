namespace XinSpect;

/// <summary>Session-locked identity and labels, independent of later UI selection or hot-plug changes.</summary>
internal sealed record SensorLogSensorDefinition(
    string Id, string DeviceId, string DeviceName, string Name, string TypeText, string Unit)
{
    internal static SensorLogSensorDefinition FromReading(SensorReading reading)
        => new(reading.Id, reading.DeviceId, reading.DeviceName, reading.Name, reading.TypeText, reading.Unit);
}
