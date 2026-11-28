using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Hardware-free layout trust-boundary and whole-frame observation regressions.</summary>
public sealed class SensorPanelTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static SensorPanelTile Tile(string sensor = "/gpu/0/temperature/0", string device = "/gpu/0",
        SensorPanelTileKind kind = SensorPanelTileKind.History, string unit = "°C") => new()
    { TileId = Guid.NewGuid().ToString("D"), SensorId = sensor, DeviceId = device, Unit = unit, Kind = kind };
    private static SensorPanelLayout Layout(params SensorPanelTile[] tiles) => new() { Tiles = tiles };
    private static SensorReading Reading(double? value = 45, string id = "/gpu/0/temperature/0",
        string device = "/gpu/0", FactAvailability availability = FactAvailability.Present,
        FactTrustLevel trust = FactTrustLevel.Reported, string unit = "°C")
        => new(id, device, "GPU", "Core", "Temperature", unit, value, availability, trust,
            "test provider", At, null, "injected");
    private static SensorFrame Frame(long seq, params SensorReading[] readings)
        => new(seq, Stopwatch.Frequency * seq, At.AddSeconds(seq), readings);

    [Fact]
    public void RoundTripPreservesGeometryBindingsKindsAndTopmostButNoObservations()
    {
        var original = Layout(Tile() with { X = 37, Y = 91, Width = 448, Height = 292, ScaleMinimum = -30, ScaleMaximum = 130 })
            with { Topmost = true };
        string json = SensorPanelLayoutService.Serialize(original);
        var loaded = SensorPanelLayoutService.Parse(json);
        Assert.Equal(original.Tiles, loaded.Tiles);
        Assert.True(loaded.Topmost);
        Assert.Equal(original.CanvasWidth, loaded.CanvasWidth);
        Assert.DoesNotContain("value", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"history\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2,\"tiles\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"tiles\":null}")]
    [InlineData("{\"schemaVersion\":1,\"tiles\":[],\"command\":\"calc.exe\"}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"tiles\":[]}")]
    public void InvalidOrUnallowlistedSchemaIsRejected(string json)
    {
        var error = Record.Exception(() => SensorPanelLayoutService.Parse(json));
        Assert.True(error is JsonException or InvalidDataException, error?.ToString() ?? "Invalid layout was accepted.");
    }

    [Fact]
    public void UnknownExecutableFieldWithinTileIsRejected()
    {
        string json = SensorPanelLayoutService.Serialize(Layout(Tile())).Replace("\"kind\":", "\"executablePath\": \"bad.exe\", \"kind\":");
        Assert.Throws<JsonException>(() => SensorPanelLayoutService.Parse(json));
    }

    [Theory]
    [InlineData(-1, 0, 300, 240)]
    [InlineData(0, -1, 300, 240)]
    [InlineData(1599, 0, 300, 240)]
    [InlineData(0, 1199, 300, 240)]
    [InlineData(0, 0, 249, 240)]
    [InlineData(0, 0, 300, 219)]
    [InlineData(0, 0, double.NaN, 240)]
    [InlineData(double.PositiveInfinity, 0, 300, 240)]
    public void NonFiniteOrOutOfBoundsGeometryIsRejected(double x, double y, double width, double height)
        => Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(
            Layout(Tile() with { X = x, Y = y, Width = width, Height = height })));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 100)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(-1e13, 100)]
    public void InvalidScaleIsRejected(double min, double max)
        => Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(
            Layout(Tile() with { ScaleMinimum = min, ScaleMaximum = max })));

    [Fact]
    public void AllGeometryInteractionPathsClampToSameBounds()
    {
        var layout = Layout(); var tile = Tile();
        var moved = SensorPanelLayoutService.Constrain(layout, tile, -100, 9999, 10, 9999);
        Assert.Equal(0, moved.X);
        Assert.Equal(layout.CanvasHeight - moved.Height, moved.Y);
        Assert.Equal(SensorPanelLayoutService.MinimumTileWidth, moved.Width);
        Assert.Equal(1200, moved.Height);
        SensorPanelLayoutService.Validate(layout with { Tiles = [moved] });
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorPanelLayoutService.Constrain(layout, tile, double.NaN, 0, 300, 240));
    }

    [Fact]
    public void DuplicateIdsAndDuplicateSensorKindAreRejectedButDifferentKindsAreAllowed()
    {
        var tile = Tile();
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(Layout(tile, tile with { Kind = SensorPanelTileKind.Numeric })));
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(Layout(tile, tile with { TileId = Guid.NewGuid().ToString("D") })));
        SensorPanelLayoutService.Validate(Layout(tile, tile with { TileId = Guid.NewGuid().ToString("D"), Kind = SensorPanelTileKind.Numeric }));
    }

    [Fact]
    public void TileBudgetStringLimitsEnumAndJsonByteBudgetAreEnforced()
    {
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(Layout(
            Enumerable.Range(0, 33).Select(i => Tile("sensor/" + i)).ToArray())));
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(Layout(Tile(new string('x', 513)))));
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(Layout(Tile("sensor\ncommand"))));
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Validate(Layout(Tile() with { Kind = (SensorPanelTileKind)88 })));
        Assert.Throws<InvalidDataException>(() => SensorPanelLayoutService.Parse(new string('界', 50000)));
    }

    [Fact]
    public void RealZeroIsAValidObservationButMissingDoesNotBecomeZero()
    {
        var runtime = new SensorPanelRuntime(); var layout = Layout(Tile());
        var first = runtime.Update(layout, Frame(1, Reading(0)), Stopwatch.Frequency).Single();
        Assert.Equal(0, first.Value); Assert.Equal(0, first.History.Single().Value);
        var second = runtime.Update(layout, Frame(2, Reading(null)), Stopwatch.Frequency * 2).Single();
        Assert.Null(second.Value); Assert.Null(second.History.Last().Value);
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void AllUnavailableStatesPreserveEvidenceWithoutChartingTheirNumericPayload(FactAvailability availability)
    {
        var state = new SensorPanelRuntime().Update(Layout(Tile()), Frame(1, Reading(70, availability: availability)), Stopwatch.Frequency).Single();
        Assert.Null(state.Value); Assert.Null(state.GaugeFraction); Assert.Null(state.History.Single().Value);
        Assert.Equal(70, state.Reading!.Value); Assert.Contains(availability.ToString(), state.Status);
    }

    [Theory]
    [InlineData(FactTrustLevel.Unknown)]
    [InlineData(FactTrustLevel.Speculative)]
    public void UntrustedReadingsDoNotEnterCharts(FactTrustLevel trust)
    {
        var state = new SensorPanelRuntime().Update(Layout(Tile()), Frame(1, Reading(trust: trust)), Stopwatch.Frequency).Single();
        Assert.Null(state.Value); Assert.Null(state.History.Single().Value); Assert.Contains(trust.ToString(), state.Status);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteObservationsAreRejected(double value)
        => Assert.Null(new SensorPanelRuntime().Update(Layout(Tile()), Frame(1, Reading(value)), Stopwatch.Frequency).Single().Value);

    [Fact]
    public void ExpiryAddsExactlyOneGapAndCannotRepeatOldSampleOnUiTicks()
    {
        var runtime = new SensorPanelRuntime(); var layout = Layout(Tile()); var frame = Frame(1, Reading());
        runtime.Update(layout, frame, Stopwatch.Frequency);
        var same = runtime.Update(layout, frame, Stopwatch.Frequency * 2).Single();
        Assert.Single(same.History);
        var expired = runtime.Update(layout, frame, Stopwatch.Frequency * 12).Single();
        Assert.Null(expired.Value); Assert.Equal(2, expired.History.Count); Assert.Null(expired.History.Last().Value);
        Assert.Equal(2, runtime.Update(layout, frame, Stopwatch.Frequency * 14).Single().History.Count);
        var resumed = runtime.Update(layout, Frame(15, Reading(50)), Stopwatch.Frequency * 15).Single();
        Assert.Equal(new double?[] { 45, null, 50 }, resumed.History.Select(p => p.Value));
    }

    [Fact]
    public void MissingFrameAndLongUiPauseCreateHistoryDiscontinuities()
    {
        var runtime = new SensorPanelRuntime(); var layout = Layout(Tile());
        runtime.Update(layout, Frame(1, Reading()), Stopwatch.Frequency);
        var missing = runtime.Update(layout, Frame(2), Stopwatch.Frequency * 2).Single();
        Assert.Null(missing.Value); Assert.Null(missing.History.Last().Value); Assert.Contains("缺少", missing.Status);
        var resumed = runtime.Update(layout, Frame(20, Reading(80)), Stopwatch.Frequency * 20).Single();
        Assert.Equal(new double?[] { 45, null, 80 }, resumed.History.Select(p => p.Value));
        var runtime2 = new SensorPanelRuntime();
        runtime2.Update(layout, Frame(1, Reading()), Stopwatch.Frequency);
        Assert.Equal(new double?[] { 45, null, 80 }, runtime2.Update(layout, Frame(20, Reading(80)), Stopwatch.Frequency * 20).Single().History.Select(p => p.Value));
    }

    [Fact]
    public void PerGpuAndDiskIdentitiesNeverMatchBySameLabelOrIndexAlone()
    {
        var layout = Layout(Tile(), Tile("/gpu/1/temperature/0", "/gpu/1"), Tile("/disk/0/temperature/0", "/disk/0"));
        var states = new SensorPanelRuntime().Update(layout, Frame(1, Reading(42),
            Reading(66, "/gpu/1/temperature/0", "/gpu/1"), Reading(30, "/disk/0/temperature/0", "/disk/0")), Stopwatch.Frequency);
        Assert.Equal(new double?[] { 42, 66, 30 }, states.Select(s => s.Value));
        Assert.Equal(3, states.Select(s => s.Tile.DeviceId).Distinct().Count());
    }

    [Fact]
    public void DeviceOrUnitMutationAndAmbiguousIdsFailClosed()
    {
        var layout = Layout(Tile());
        foreach (var frame in new[] { Frame(1, Reading(device: "/gpu/9")), Frame(1, Reading(unit: "W")), Frame(1, Reading(), Reading(99)) })
        {
            var state = new SensorPanelRuntime().Update(layout, frame, Stopwatch.Frequency).Single();
            Assert.Null(state.Value); Assert.Null(state.History.Single().Value);
        }
    }

    [Fact]
    public void OutOfOrderOrRepeatedSequenceCannotOverwriteLatestObservation()
    {
        var runtime = new SensorPanelRuntime(); var layout = Layout(Tile());
        runtime.Update(layout, Frame(3, Reading(80)), Stopwatch.Frequency * 3);
        Assert.Equal(80, runtime.Update(layout, Frame(2, Reading(99)), Stopwatch.Frequency * 3).Single().Value);
        var same = runtime.Update(layout, Frame(3, Reading(0)), Stopwatch.Frequency * 3).Single();
        Assert.Equal(80, same.Value); Assert.Single(same.History);
    }

    [Fact]
    public void HistoryIsBoundedAndSharedAcrossDisplayKindsWithoutDuplicateSamples()
    {
        var historyTile = Tile(); var layout = Layout(historyTile, historyTile with
            { TileId = Guid.NewGuid().ToString("D"), Kind = SensorPanelTileKind.Numeric });
        var runtime = new SensorPanelRuntime();
        IReadOnlyList<SensorPanelTileState> states = Array.Empty<SensorPanelTileState>();
        for (int i = 1; i <= 150; i++) states = runtime.Update(layout, Frame(i, Reading(i)), Stopwatch.Frequency * i);
        Assert.All(states, s => Assert.Equal(SensorPanelRuntime.HistoryCapacity, s.History.Count));
        Assert.Equal(31, states[0].History.First().Sequence);
        Assert.Equal(states[0].History, states[1].History);
    }

    [Fact]
    public void LocalStoreAndExplicitExportRoundTripBomAndRejectOversizeWithoutChangingPreviousFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "XinSpect-SensorPanel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "layout.json"); var store = new SensorPanelLayoutStore(path);
            Assert.Empty(store.LoadLocal().Tiles);
            var layout = Layout(Tile()); store.SaveLocal(layout);
            Assert.Equal(layout.Tiles, store.LoadLocal().Tiles);
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => store.SaveLocal(layout with { CanvasWidth = double.NaN }));
            Assert.Equal(before, File.ReadAllBytes(path));
            File.WriteAllText(path, SensorPanelLayoutService.Serialize(layout), new UTF8Encoding(true));
            Assert.Equal(layout.Tiles, SensorPanelLayoutStore.Read(path).Tiles);
            File.WriteAllBytes(path, new byte[SensorPanelLayoutService.MaximumJsonBytes + 1]);
            Assert.Throws<InvalidDataException>(() => SensorPanelLayoutStore.Read(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
