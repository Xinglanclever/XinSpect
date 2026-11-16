namespace XinSpect;

public sealed record CoreTemperatureSample(
    string Source, string DeviceIdentity, int CoreIndex, double Celsius, DateTimeOffset CapturedAtUtc);

public sealed record CoreTemperatureComparison(
    string DeviceIdentity, int CoreIndex, double PrimaryCelsius, double ExternalCelsius,
    double DifferenceCelsius, double SampleGapMilliseconds, DateTimeOffset CapturedAtUtc);

public static class SensorCrossCheckService
{
    public static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(3);

    // LHM 0.9.6 GenericCpu.CoreString labels non-hybrid cores from 1; Core Temp channels start at 0.
    internal static int LhmCoreIndex(string? name)
    {
        if (name == "CPU Core") return 0;
        const string prefix = "CPU Core #";
        return name is not null && name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int index) && index is > 0 and <= 256
            ? index - 1 : -1;
    }

    public static IReadOnlyList<CoreTemperatureComparison> CompareCoreTemperatures(
        IEnumerable<CoreTemperatureSample> primary, IEnumerable<ExternalReading> external)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(external);

        var validPrimary = primary.Where(IsValidPrimary).ToArray();
        var validExternal = external.Where(IsComparableExternal)
            .Select(ToCelsius).Where(sample => sample is not null).Select(sample => sample!).ToArray();

        var primaryGroups = validPrimary.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToArray());
        var externalGroups = validExternal.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToArray());
        var results = new List<CoreTemperatureComparison>();

        foreach (var (key, p) in primaryGroups)
        {
            if (p.Length != 1 || !externalGroups.TryGetValue(key, out var e) || e.Length != 1) continue;
            double gap = Math.Abs((p[0].CapturedAtUtc - e[0].CapturedAtUtc).TotalMilliseconds);
            if (gap > MaximumSampleGap.TotalMilliseconds) continue;
            results.Add(new CoreTemperatureComparison(key.DeviceIdentity, key.CoreIndex,
                p[0].Celsius, e[0].Celsius, Math.Abs(p[0].Celsius - e[0].Celsius), gap,
                p[0].CapturedAtUtc > e[0].CapturedAtUtc ? p[0].CapturedAtUtc : e[0].CapturedAtUtc));
        }

        return results.OrderBy(x => x.DeviceIdentity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CoreIndex).ToArray();
    }

    private static bool IsValidPrimary(CoreTemperatureSample sample)
        => string.Equals(sample.Source, "LHM", StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(sample.DeviceIdentity)
           && sample.CoreIndex >= 0
           && sample.CapturedAtUtc != default
           && double.IsFinite(sample.Celsius) && sample.Celsius is > 0 and <= 130;

    private static bool IsComparableExternal(ExternalReading reading)
        => string.Equals(reading.Source, "Core Temp", StringComparison.Ordinal)
           && string.Equals(reading.SensorType, "Temperature", StringComparison.Ordinal)
           && reading.ChannelIndex is >= 0
           && !string.IsNullOrWhiteSpace(reading.DeviceIdentity)
           && reading.CapturedAtUtc.HasValue
           && reading.CapturedAtUtc.Value != default
           && double.IsFinite(reading.Value);

    private static CoreTemperatureSample? ToCelsius(ExternalReading reading)
    {
        double celsius = reading.Unit switch
        {
            "°C" => reading.Value,
            "°F" => (reading.Value - 32) * 5 / 9,
            _ => double.NaN,
        };
        if (!double.IsFinite(celsius) || celsius is <= 0 or > 130) return null;
        return new CoreTemperatureSample("Core Temp", reading.DeviceIdentity!,
            reading.ChannelIndex!.Value, celsius, reading.CapturedAtUtc!.Value);
    }

    private static (string DeviceIdentity, int CoreIndex) Key(CoreTemperatureSample sample)
        => (sample.DeviceIdentity.Trim(), sample.CoreIndex);
}
