using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>Versioned, strictly allowlisted layout codec; rejects the entire invalid import.</summary>
public static class SensorPanelLayoutService
{
    public const int MaximumTiles = 32;
    public const int MaximumJsonBytes = 128 * 1024;
    public const double MinimumTileWidth = 250, MinimumTileHeight = 220;
    public const double MaximumCanvasWidth = 4096, MaximumCanvasHeight = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<SensorPanelTileKind>(allowIntegerValues: false) },
    };

    public static string Serialize(SensorPanelLayout layout)
    {
        Validate(layout);
        string json = JsonSerializer.Serialize(layout, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("版面檔超過 128 KiB。");
        return json;
    }

    public static SensorPanelLayout Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("版面檔超過 128 KiB。");
        // System.Text.Json otherwise accepts duplicate properties with last-one-wins semantics.
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicateProperties(doc.RootElement);
        var layout = JsonSerializer.Deserialize<SensorPanelLayout>(json, JsonOptions)
            ?? throw new InvalidDataException("版面檔是空值。");
        Validate(layout);
        return layout;
    }

    private static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("版面檔包含重複欄位。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RejectDuplicateProperties(item);
    }

    public static void Validate(SensorPanelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.SchemaVersion != 1) throw new InvalidDataException("不支援此版面格式版本。");
        if (!FiniteBetween(layout.CanvasWidth, 600, MaximumCanvasWidth)
            || !FiniteBetween(layout.CanvasHeight, 400, MaximumCanvasHeight))
            throw new InvalidDataException("畫布大小須介於 600×400 與 4096×4096。");
        if (layout.Tiles is null || layout.Tiles.Count > MaximumTiles)
            throw new InvalidDataException("每份版面最多 32 個磁貼。");
        var ids = new HashSet<Guid>();
        var bindings = new HashSet<(string, string, SensorPanelTileKind)>();
        foreach (var tile in layout.Tiles)
        {
            if (tile is null || !Guid.TryParseExact(tile.TileId, "D", out var id) || id == Guid.Empty || !ids.Add(id))
                throw new InvalidDataException("磁貼識別碼無效或重複。");
            if (!ValidIdentity(tile.SensorId, false) || !ValidIdentity(tile.DeviceId, false) || !ValidIdentity(tile.Unit, true)
                || !Enum.IsDefined(tile.Kind)) throw new InvalidDataException("感測器綁定或顯示類型無效。");
            if (!bindings.Add((tile.SensorId, tile.DeviceId, tile.Kind)))
                throw new InvalidDataException("同一感測器的相同顯示類型不可重複。");
            if (!FiniteBetween(tile.Width, MinimumTileWidth, 1600) || !FiniteBetween(tile.Height, MinimumTileHeight, 1200)
                || !FiniteBetween(tile.X, 0, layout.CanvasWidth - tile.Width)
                || !FiniteBetween(tile.Y, 0, layout.CanvasHeight - tile.Height))
                throw new InvalidDataException("磁貼的位置或大小超出畫布邊界。");
            if (!FiniteBetween(tile.ScaleMinimum, -1e12, 1e12) || !FiniteBetween(tile.ScaleMaximum, -1e12, 1e12)
                || tile.ScaleMaximum <= tile.ScaleMinimum || tile.ScaleMaximum - tile.ScaleMinimum < 1e-9)
                throw new InvalidDataException("刻度上限必須大於下限，且介於 ±10¹²。");
        }
    }

    private static bool ValidIdentity(string? value, bool allowEmpty)
        => value is not null && value.Length <= 512 && (allowEmpty || !string.IsNullOrWhiteSpace(value))
            && !value.Any(char.IsControl);
    private static bool FiniteBetween(double value, double min, double max)
        => double.IsFinite(value) && value >= min && value <= max;

    /// <summary>Drag, resize and keyboard operations all use the same geometry boundary.</summary>
    public static SensorPanelTile Constrain(SensorPanelLayout layout, SensorPanelTile tile,
        double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(x));
        width = Math.Clamp(width, MinimumTileWidth, Math.Min(1600, layout.CanvasWidth));
        height = Math.Clamp(height, MinimumTileHeight, Math.Min(1200, layout.CanvasHeight));
        return tile with { Width = width, Height = height, X = Math.Clamp(x, 0, layout.CanvasWidth - width),
            Y = Math.Clamp(y, 0, layout.CanvasHeight - height) };
    }
}

/// <summary>Local-only layout store. Only an explicit file dialog supplies import/export paths.</summary>
public sealed class SensorPanelLayoutStore
{
    public string LocalPath { get; }
    public SensorPanelLayoutStore(string? localPath = null) => LocalPath = localPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect", "sensor-panel.json");

    public SensorPanelLayout LoadLocal() => File.Exists(LocalPath) ? Read(LocalPath) : new SensorPanelLayout();
    public void SaveLocal(SensorPanelLayout layout) => Write(LocalPath, layout);

    public static SensorPanelLayout Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > SensorPanelLayoutService.MaximumJsonBytes)
            throw new InvalidDataException("版面檔超過 128 KiB。");
        // Bound the actual read too, including files that change after the length check.
        var bytes = new byte[SensorPanelLayoutService.MaximumJsonBytes + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
        if (count > SensorPanelLayoutService.MaximumJsonBytes) throw new InvalidDataException("版面檔超過 128 KiB。");
        int offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return SensorPanelLayoutService.Parse(new UTF8Encoding(false, true).GetString(bytes, offset, count - offset));
    }

    public static void Write(string path, SensorPanelLayout layout)
    {
        string json = SensorPanelLayoutService.Serialize(layout);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".sensor-panel-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
