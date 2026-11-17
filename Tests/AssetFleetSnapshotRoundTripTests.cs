using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>R378: real asset producer and snapshot serializer; all device data is injected.</summary>
public sealed class AssetFleetSnapshotRoundTripTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-asset-roundtrip-" + Guid.NewGuid().ToString("N"));

    public AssetFleetSnapshotRoundTripTests() => Directory.CreateDirectory(_directory);

    private static IReadOnlyList<HardwareFact> ProducerFacts(int machine, string model = "X1", bool placeholder = false)
    {
        var record = new AssetRecord(
            [new AssetField("系統製造商", "ACME", "SMBIOS Type 1 offset 0x04", false),
             new AssetField("系統型號", model, "SMBIOS Type 1 offset 0x05", placeholder)],
            0, "", "", false);
        return AssetFactsService.Collect(At.AddDays(machine), () => record).Concat(
            [new HardwareFact("board.serial", "fixture", "fixture serial", "PRIVATE-SERIAL-" + machine,
                "", "fixture-only", FactTrustLevel.Reported, true, At.AddDays(machine)),
             new HardwareFact("sensor.cpu", "fixture", "temperature", (40 + machine).ToString(),
                "°C", "fixture-only", FactTrustLevel.Measured, false, At.AddDays(machine), 40 + machine)]).ToArray();
    }

    private string SaveProducer(int machine, string model = "X1", bool placeholder = false,
        FactAvailability availability = FactAvailability.Present)
    {
        var facts = ProducerFacts(machine, model, placeholder);
        if (availability != FactAvailability.Present)
            facts = facts.Select(f => f.Name == "系統型號"
                ? new HardwareFact(f.Key, f.Category, f.Name, "unconfirmed model", f.Unit, f.Source,
                    f.Trust, f.Sensitive, f.MeasuredAtUtc, null, availability, "fixture unavailable")
                : f).ToArray();
        var snapshot = HardwareSnapshotService.Create("fixture", facts, false)
            with { CapturedAtUtc = At.AddDays(machine) };
        string path = Path.Combine(_directory, machine + ".json");
        HardwareSnapshotService.Save(path, snapshot);
        return path;
    }

    [Fact]
    public void RealAssetProducerSurvivesSaveLoadAndBuildsFleetCohort()
    {
        for (int i = 0; i < 2; i++)
        {
            string path = SaveProducer(i);
            var loaded = HardwareSnapshotService.Load(path);
            Assert.Equal(0, HardwareSnapshotService.CountRejectedFacts(loaded));
            var manufacturer = Assert.Single(loaded.Facts, f => f.Name == "系統製造商");
            var model = Assert.Single(loaded.Facts, f => f.Name == "系統型號");
            Assert.Equal("asset.field.system_manufacturer", manufacturer.Key);
            Assert.Equal("asset.field.system_model", model.Key);
            Assert.Equal("ACME", manufacturer.Value);
            Assert.Equal("X1", model.Value);
            Assert.Equal(FactAvailability.Present, model.Availability);
            Assert.Equal(FactTrustLevel.Reported, model.Trust);
            Assert.Equal("SMBIOS Type 1 offset 0x05", model.Source);
            Assert.Equal("", model.Unit);
            Assert.Equal(At.AddDays(i), model.MeasuredAtUtc);
            Assert.False(model.Sensitive);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var persisted = json.RootElement.GetProperty("facts").EnumerateArray().ToArray();
            Assert.False(Assert.Single(persisted, f => f.GetProperty("key").GetString() == model.Key)
                .TryGetProperty("availability", out _));
            Assert.False(Assert.Single(persisted, f => f.GetProperty("key").GetString() == "sensor.cpu")
                .TryGetProperty("availability", out _));
            Assert.DoesNotContain("PRIVATE-SERIAL-", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.True(HardwareSnapshotService.IsRedacted(Assert.Single(loaded.Facts, f => f.Key == "board.serial").Value));
        }

        var projected = FleetSnapshotReader.Read(_directory);
        Assert.Empty(projected.Diagnostics);
        Assert.Equal(2, projected.Count);
        Assert.All(projected, s => Assert.Equal("ACME | X1", s.ModelKey));
        var facts = FleetBaselineFactsService.Collect(projected, At);
        Assert.Equal(FactAvailability.Present, Assert.Single(facts, f => f.Key == "fleet.summary").Availability);
        Assert.Contains("族群中位 40.5", Assert.Single(facts, f => f.Key == "fleet.peer.0001").Value);
    }

    [Fact]
    public void FleetCliComparesSnapshotsWrittenByTheProductionSerializer()
    {
        SaveProducer(0);
        SaveProducer(1);
        using var output = new StringWriter();
        Assert.Equal(CliService.ExitOk, CliService.Run(["--fleet", "--snapshots", _directory],
            () => [], output, new StringWriter()));
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("comparable").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("snapshotCount").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.DoesNotContain("PRIVATE-SERIAL-", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"FutureState\"")]
    [InlineData("999")]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"\"")]
    public void ExplicitInvalidAvailabilityNeverDefaultsToPresent(string token)
    {
        var path = SaveProducer(0);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var fact in json["facts"]!.AsArray())
            if (fact!["key"]!.GetValue<string>() is "asset.field.system_model" or "sensor.cpu")
                fact["availability"] = System.Text.Json.Nodes.JsonNode.Parse(token);
        File.WriteAllText(path, json.ToJsonString());

        var projected = Assert.Single(FleetSnapshotReader.Read(_directory));
        Assert.Null(projected.ModelKey);
        Assert.False(projected.Metadata!.ContainsKey("sensor.cpu"));
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void ExplicitUnavailableSensorDoesNotBecomeComparable(FactAvailability availability)
    {
        for (int machine = 0; machine < 2; machine++)
        {
            var path = SaveProducer(machine);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var sensor = json["facts"]!.AsArray().Single(f => f!["key"]!.GetValue<string>() == "sensor.cpu")!;
            sensor["availability"] = availability.ToString();
            File.WriteAllText(path, json.ToJsonString());
        }
        var projected = FleetSnapshotReader.Read(_directory);
        Assert.All(projected, s =>
        {
            Assert.Equal("ACME | X1", s.ModelKey);
            Assert.Equal(availability, s.Metadata!["sensor.cpu"].Availability);
        });
        Assert.Equal(FactAvailability.Unknown, Assert.Single(FleetBaselineFactsService.Collect(projected, At),
            f => f.Key == "fleet.summary").Availability);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Default string", true)]
    public void MissingOrPlaceholderModelNeverCreatesAPeer(string model, bool placeholder)
    {
        SaveProducer(0, model, placeholder);
        SaveProducer(1, model, placeholder);
        var projected = FleetSnapshotReader.Read(_directory);
        Assert.All(projected, s => Assert.Null(s.ModelKey));
        Assert.DoesNotContain(FleetBaselineFactsService.Collect(projected, At), f => f.Key.StartsWith("fleet.peer.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void PersistedUnavailableModelPreservesStateAndNeverCreatesAPeer(FactAvailability availability)
    {
        var path = SaveProducer(0, availability: availability);
        var loaded = HardwareSnapshotService.Load(path);
        Assert.Equal(availability, Assert.Single(loaded.Facts, f => f.Name == "系統型號").Availability);
        Assert.Null(Assert.Single(FleetSnapshotReader.Read(_directory)).ModelKey);
    }

    [Fact]
    public void ArbitraryUnicodeKeysRemainRejectedBySnapshotValidation()
    {
        var snapshot = HardwareSnapshotService.Create("fixture",
            [new HardwareFact("asset.field.任意欄位", "fixture", "field", "value", "", "fixture",
                FactTrustLevel.Reported, false, At)], false);
        var rejected = Assert.Single(snapshot.Facts);
        Assert.Equal(FactAvailability.ReadError, rejected.Availability);
        Assert.StartsWith("snapshot.rejected.", rejected.Key, StringComparison.Ordinal);
        Assert.StartsWith(HardwareSnapshotService.RejectedMarker, rejected.UnavailableReason);
    }

    [Theory]
    [InlineData("asset.field.system_manufacturer", "asset.field.系統製造商")]
    [InlineData("asset.field.system_model", "asset.field.系統型號")]
    public void CanonicalAndLegacyModelAliasesAreRejectedAsAmbiguous(string canonical, string legacy)
    {
        static object Fact(string key) => new { key, value = "same", unit = "", source = "fixture", availability = "Present", trust = "Reported" };
        File.WriteAllText(Path.Combine(_directory, "ambiguous.json"), JsonSerializer.Serialize(new
        {
            facts = new[] { Fact(canonical), Fact(legacy) },
        }));
        var projected = FleetSnapshotReader.Read(_directory);
        Assert.Empty(projected);
        Assert.Contains("AmbiguousModelField", projected.Diagnostics);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(FleetBaselineFactsService.Collect(projected, At),
            f => f.Key == "fleet.summary").Availability);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
