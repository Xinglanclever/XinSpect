using System.Text.Json.Serialization;

namespace XinSpect;

public enum SensorPanelTileKind { Numeric, Gauge, History }

/// <summary>Layout data only. No commands, provider configuration, paths or saved readings.</summary>
public sealed record SensorPanelTile
{
    public required string TileId { get; init; }
    public required string SensorId { get; init; }
    public required string DeviceId { get; init; }
    public required string Unit { get; init; }
    public SensorPanelTileKind Kind { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 300;
    public double Height { get; init; } = 240;
    public double ScaleMinimum { get; init; }
    public double ScaleMaximum { get; init; } = 100;
}

public sealed record SensorPanelLayout
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public double CanvasWidth { get; init; } = 1600;
    public double CanvasHeight { get; init; } = 1200;
    public bool Topmost { get; init; }
    [JsonRequired]
    public IReadOnlyList<SensorPanelTile> Tiles { get; init; } = Array.Empty<SensorPanelTile>();
}

public sealed record SensorPanelPoint(long Sequence, DateTimeOffset ReadAtUtc, double? Value);

/// <summary>Current display value and bounded session-only history. A null point is a gap.</summary>
public sealed record SensorPanelTileState(SensorPanelTile Tile, SensorReading? Reading,
    double? Value, string Status, IReadOnlyList<SensorPanelPoint> History)
{
    public double? GaugeFraction => Value is double value
        ? Math.Clamp((value - Tile.ScaleMinimum) / (Tile.ScaleMaximum - Tile.ScaleMinimum), 0, 1) : null;
}
