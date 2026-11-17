using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Local synthetic exports only. No network/BMC/native calls.</summary>
public class RedfishPowerScopeTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-17T08:00:00Z");
    private sealed class Bundle : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "xinspect-redfish-power-" + Guid.NewGuid().ToString("N"));
        public Bundle(params string[] chassis)
        {
            Put("/redfish/v1", JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = "2026-11-17T07:00:00Z",
                Chassis = new Dictionary<string, string> { ["@odata.id"] = "/redfish/v1/Chassis" },
            }));
            Put("/redfish/v1/Chassis", JsonSerializer.Serialize(new
            { Members = chassis.Select(id => new Dictionary<string, string> { ["@odata.id"] = "/redfish/v1/Chassis/" + id }) }));
            foreach (string id in chassis)
                Put("/redfish/v1/Chassis/" + id, JsonSerializer.Serialize(new
                { Id = id, Power = new Dictionary<string, string> { ["@odata.id"] = "/redfish/v1/Chassis/" + id + "/Power" } }));
        }
        public void Put(string uri, string json)
        {
            string file = Path.Combine(Root, uri.Trim('/').Replace('/', Path.DirectorySeparatorChar), "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, json);
        }
        public RedfishInventory Read() => RedfishBundleService.Read(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static HardwareFact Scalar(RedfishInventory inventory)
        => RedfishFactsService.Collect(inventory, At).Single(f => f.Key == "rf.power.consumed");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 多機箱不受集合順序影響也不加總(bool reverse)
    {
        using var bundle = reverse ? new Bundle("B", "A") : new Bundle("A", "B");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"MemberId":"0","PowerConsumedWatts":20}]}""");
        bundle.Put("/redfish/v1/Chassis/B/Power", """{"PowerControl":[{"MemberId":"0","PowerConsumedWatts":200}]}""");
        var inventory = bundle.Read();
        Assert.Null(inventory.PowerConsumedWatts);
        Assert.Equal(new[] { 20d, 200d }, inventory.PowerControls.OrderBy(x => x.ChassisResourceUri).Select(x => x.PowerConsumedWatts!.Value));
        Assert.Equal(2, inventory.PowerControls.Select(x => x.ChassisResourceUri).Distinct().Count());
        var fact = Scalar(inventory);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("多筆", fact.UnavailableReason);
        var rows = RedfishFactsService.Collect(inventory, At).Where(f => f.Key.StartsWith("rf.item.power.")).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(2, rows.Select(f => f.Key).Distinct().Count());
        Assert.DoesNotContain(rows, f => f.NumericValue == 220);
        var stdout = new StringWriter();
        Assert.Equal(CliService.ExitOk, CliService.Run(["--redfish", bundle.Root], () => [], stdout, new StringWriter()));
        using var cli = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(JsonValueKind.Null, cli.RootElement.GetProperty("powerConsumedWatts").ValueKind);
        Assert.Equal(2, cli.RootElement.GetProperty("powerControls").GetArrayLength());
        Assert.Equal(new[] { 20d, 200d }, cli.RootElement.GetProperty("powerControls").EnumerateArray()
            .Select(x => x.GetProperty("powerConsumedWatts").GetDouble()).OrderBy(x => x));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"PowerConsumedWatts\":null}")]
    [InlineData("{\"PowerConsumedWatts\":-1}")]
    public void 第二項缺值或畸形不能提升首筆為唯一scalar(string second)
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A/Power", "{\"PowerControl\":[{\"PowerConsumedWatts\":20}," + second + "]}");
        var inventory = bundle.Read();
        Assert.Null(inventory.PowerConsumedWatts);
        Assert.Null(Scalar(inventory).NumericValue);
        Assert.Equal(FactAvailability.Unknown, Scalar(inventory).Availability);
        if (second == "null") Assert.False(inventory.PowerControlsComplete);
        else Assert.Equal(2, inventory.PowerControls.Count);
    }

    [Fact]
    public void 單筆保實際解析來源而不採payload自稱URI()
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A", """{"Id":"A","@odata.id":"https://forged.invalid/SECRET","Power":{"@odata.id":"/redfish/v1/Chassis/A/Power"}}""");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"@odata.id":"/redfish/v1/forged","PowerControl":[{"MemberId":"0","@odata.id":"/redfish/v1/Chassis/A/Power#/PowerControl/0","PhysicalContext":"Chassis","PowerConsumedWatts":320}]}""");
        var inventory = bundle.Read();
        var control = Assert.Single(inventory.PowerControls);
        Assert.Equal("/redfish/v1/Chassis/A", control.ChassisResourceUri);
        Assert.Equal("/redfish/v1/Chassis/A/Power", control.PowerResourceUri);
        Assert.Equal("/redfish/v1/Chassis/A/Power#/PowerControl/0", control.MemberUri);
        Assert.Equal(320, inventory.PowerConsumedWatts);
        Assert.Equal(320, Scalar(inventory).NumericValue);
        Assert.Contains("非整包總功耗", Scalar(inventory).Name);
        Assert.Contains(control.ObservationLocation, Scalar(inventory).Value);
        Assert.DoesNotContain("forged", Scalar(inventory).Value);
    }

    [Fact]
    public void 內嵌資源與缺少MemberId使用明示本機位置()
    {
        using var bundle = new Bundle();
        bundle.Put("/redfish/v1", """{"GeneratedAtUtc":"2026-11-17T07:00:00Z","Chassis":{"Members":[{"Id":"A","Power":{"PowerControl":[{"PowerConsumedWatts":0}]}}]}}""");
        var inventory = bundle.Read();
        var control = Assert.Single(inventory.PowerControls);
        Assert.Equal("/redfish/v1#/Chassis/Members/0", control.ChassisResourceUri);
        Assert.Equal("/redfish/v1#/Chassis/Members/0/Power", control.PowerResourceUri);
        Assert.Null(control.MemberId);
        Assert.Equal(0, inventory.PowerConsumedWatts);
        Assert.Equal(FactAvailability.Present, Scalar(inventory).Availability);
        Assert.Contains("本機陣列索引", Scalar(inventory).Value);
    }

    [Theory]
    [InlineData("\"PowerControl@odata.nextLink\":\"https://never-follow.invalid/page2\",")]
    [InlineData("\"PowerControl@odata.count\":2,")]
    [InlineData("\"PowerControl@odata.count\":\"1\",")]
    public void 部分頁或數量不符不把一筆當唯一完整scope(string annotation)
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A/Power", "{" + annotation + "\"PowerControl\":[{\"PowerConsumedWatts\":20}]}");
        var inventory = bundle.Read();
        Assert.Single(inventory.PowerControls);
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
        Assert.NotEmpty(inventory.Warnings);
    }

    [Fact]
    public void 機箱集合部分頁亦不可唯一投影()
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis", """{"Members@odata.nextLink":"/redfish/v1/Chassis?page=2","Members":[{"@odata.id":"/redfish/v1/Chassis/A"}]}""");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"PowerConsumedWatts":20}]}""");
        var inventory = bundle.Read();
        Assert.Single(inventory.PowerControls);
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
    }

    [Theory]
    [InlineData("https://never-follow.invalid/redfish/v1/Power")]
    [InlineData("/redfish/v1/../../secret")]
    [InlineData("/redfish/v1/Chassis/A/Power?x=1")]
    [InlineData("/redfish/v1/%2e%2e/secret")]
    public void 未解析電力連結不生成來源身份也不查網路(string uri)
    {
        using var bundle = new Bundle("A", "B");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"PowerConsumedWatts":20}]}""");
        bundle.Put("/redfish/v1/Chassis/B", JsonSerializer.Serialize(new
        { Id = "B", Power = new Dictionary<string, string> { ["@odata.id"] = uri } }));
        var inventory = bundle.Read();
        Assert.Single(inventory.PowerControls);
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
        Assert.NotEmpty(inventory.Warnings);
    }

    [Fact]
    public void 觀測保留上限與metadata超限不能回到唯一scalar()
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A/Power", JsonSerializer.Serialize(new
        { PowerControl = Enumerable.Range(0, RedfishBundleService.MaxPowerControls + 1).Select(i => new { MemberId = i.ToString(), PowerConsumedWatts = 20 }) }));
        var inventory = bundle.Read();
        Assert.Equal(RedfishBundleService.MaxPowerControls, inventory.PowerControls.Count);
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
        bundle.Put("/redfish/v1/Chassis/A/Power", JsonSerializer.Serialize(new
        { PowerControl = new[] { new { MemberId = new string('x', RedfishBundleService.MaxPowerMetadataLength + 1), PowerConsumedWatts = 20 } } }));
        inventory = bundle.Read();
        Assert.Null(Assert.Single(inventory.PowerControls).MemberId);
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
    }

    [Fact]
    public void 同MemberId或重複解析資源不丟列也不加總()
    {
        using var bundle = new Bundle("A", "A");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"MemberId":"0","PhysicalContext":"Chassis","RelatedItem":[{"@odata.id":"/redfish/v1/Chassis/A"}],"PowerConsumedWatts":20},{"MemberId":"0","PhysicalContext":"PowerSupply","RelatedItem":[{"@odata.id":"/redfish/v1/Chassis/A/Power#/PowerControl/0"}],"PowerConsumedWatts":10}]}""");
        var inventory = bundle.Read();
        Assert.Equal(4, inventory.PowerControls.Count);
        Assert.Null(inventory.PowerConsumedWatts);
        var rows = RedfishFactsService.Collect(inventory, At).Where(f => f.Key.StartsWith("rf.item.power.")).ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal(4, rows.Select(f => f.Key).Distinct().Count());
        Assert.All(rows, f => Assert.True(FactKeyDynamicCatalog.Families.Keys.Any(prefix => f.Key.StartsWith(prefix, StringComparison.Ordinal))));
    }

    [Fact]
    public void 缺採集時間保持Unknown與匯入時間()
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"PowerConsumedWatts":20}]}""");
        var inventory = bundle.Read() with { CollectedAtUtc = null };
        var facts = RedfishFactsService.Collect(inventory, At).Where(f => f.Key == "rf.power.consumed" || f.Key.StartsWith("rf.item.power.")).ToArray();
        Assert.Equal(2, facts.Length);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.Unknown, f.Availability);
            Assert.Null(f.NumericValue);
            Assert.Equal(FactTrustLevel.Reported, f.Trust);
            Assert.Equal(At, f.MeasuredAtUtc);
            Assert.Contains("來源採集時間未知", f.Source);
        });
    }

    [Fact]
    public void 舊建構子只有scalar無身份不提供可用數值()
    {
        var inventory = new RedfishInventory("synthetic", At, [], [new("A", null, null, null, null)], [], [], [], [], 20, []);
        Assert.Null(RedfishBundleService.SelectPowerConsumed(inventory).Watts);
        Assert.Equal(FactAvailability.Unknown, Scalar(inventory).Availability);
        Assert.Null(Scalar(inventory).NumericValue);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(30)]
    public void 手工舊scalar矛盾或非有限不得越過scope判定(double bad)
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"PowerConsumedWatts":20}]}""");
        var inventory = bundle.Read() with { PowerConsumedWatts = bad };
        Assert.Null(RedfishBundleService.SelectPowerConsumed(inventory).Watts);
        Assert.Null(Scalar(inventory).NumericValue);
    }

    [Fact]
    public void 預設CLI與快照遮蔽原始電力身份但保留逐筆數值()
    {
        using var bundle = new Bundle("SERIAL-SECRET");
        bundle.Put("/redfish/v1/Chassis/SERIAL-SECRET/Power", """{"PowerControl":[{"MemberId":"MEMBER-SECRET","Name":"NAME-SECRET","@odata.id":"https://metadata-only.invalid/URI-SECRET","PowerConsumedWatts":20}]}""");
        // Existing chassis Id is not a protected serial field; replace its display Id for this fixture.
        bundle.Put("/redfish/v1/Chassis/SERIAL-SECRET", """{"Id":"A","Power":{"@odata.id":"/redfish/v1/Chassis/SERIAL-SECRET/Power"}}""");
        var inventory = bundle.Read();
        var stdout = new StringWriter();
        Assert.Equal(CliService.ExitOk, CliService.Run(["--redfish", bundle.Root], () => [], stdout, new StringWriter()));
        string json = stdout.ToString();
        Assert.DoesNotContain("SERIAL-SECRET", json);
        Assert.DoesNotContain("MEMBER-SECRET", json);
        Assert.DoesNotContain("NAME-SECRET", json);
        Assert.DoesNotContain("URI-SECRET", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(20, doc.RootElement.GetProperty("powerConsumedWatts").GetDouble());
        Assert.Equal(20, doc.RootElement.GetProperty("powerControls")[0].GetProperty("powerConsumedWatts").GetDouble());
        var facts = RedfishFactsService.Collect(inventory, At);
        var power = facts.Single(f => f.Key.StartsWith("rf.item.power."));
        Assert.True(power.Sensitive);
        Assert.Contains("SERIAL-SECRET", power.Value);
        string snapshotJson = HardwareSnapshotService.ToJson(HardwareSnapshotService.Create("test", facts, includeSensitive: true));
        Assert.DoesNotContain("SERIAL-SECRET", snapshotJson);
        Assert.DoesNotContain("MEMBER-SECRET", snapshotJson);
        Assert.DoesNotContain("snapshot.rejected", snapshotJson);
    }

    [Fact]
    public void 讀值變化不改觀測鍵且快照能比較()
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"MemberId":"0","PowerConsumedWatts":20}]}""");
        var first = RedfishFactsService.Collect(bundle.Read(), At).Single(f => f.Key.StartsWith("rf.item.power."));
        bundle.Put("/redfish/v1/Chassis/A/Power", """{"PowerControl":[{"MemberId":"0","PowerConsumedWatts":21.125}]}""");
        var second = RedfishFactsService.Collect(bundle.Read(), At).Single(f => f.Key.StartsWith("rf.item.power."));
        Assert.Equal(first.Key, second.Key);
        Assert.NotEqual(first.Value, second.Value);
        Assert.Equal(21.125, second.NumericValue);
        string json = HardwareSnapshotService.ToJson(HardwareSnapshotService.Create("test", [second], includeSensitive: true),
            new HardwareSnapshotSaveOptions { SensitiveValues = SensitiveValuePolicy.Preserve });
        Assert.Contains("21.125", json);
        Assert.Contains(second.Key, json);
    }

    [Theory]
    [InlineData("https://never-follow.invalid/LINK-SECRET", "外部 URL")]
    [InlineData("/redfish/v1/../LINK-SECRET", "路徑穿越")]
    [InlineData("/redfish/v1/Chassis/LINK-SECRET/Power?token=TOKEN-SECRET", "資源 URI")]
    [InlineData("/redfish/v1/Chassis/LINK-SECRET/Power", "找不到")]
    public void 拒絕電力連結保留故障原因且各預設出口不洩漏URI或本機路徑(string uri, string reason)
    {
        using var bundle = new Bundle("A");
        bundle.Put("/redfish/v1/Chassis/A", JsonSerializer.Serialize(new
        { Id = "A", Power = new Dictionary<string, string> { ["@odata.id"] = uri } }));
        var inventory = bundle.Read();
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
        Assert.Contains(inventory.Warnings, warning => warning.Contains(reason, StringComparison.Ordinal));
        Assert.All(inventory.Warnings, warning => Assert.DoesNotContain("SECRET", warning));
        var warningFact = RedfishFactsService.Collect(inventory, At).Single(f => f.Key == "rf.bundle.warnings");
        Assert.True(warningFact.Sensitive);
        Assert.Contains(reason, warningFact.Value);
        string snapshot = HardwareSnapshotService.ToJson(HardwareSnapshotService.Create("test", [warningFact], includeSensitive: true));
        Assert.DoesNotContain("SECRET", snapshot);
        Assert.Contains("rf.bundle.warnings", snapshot);
        var stdout = new StringWriter();
        Assert.Equal(CliService.ExitOk, CliService.Run(["--redfish", bundle.Root], () => [], stdout, new StringWriter()));
        string json = stdout.ToString();
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain(bundle.Root, json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(HardwareSnapshotService.RedactedValue, doc.RootElement.GetProperty("directory").GetString());
        Assert.Contains(doc.RootElement.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void 匯入者傳入原始警告亦以敏感事實遮蔽並可明示保留()
    {
        using var bundle = new Bundle("A");
        var inventory = bundle.Read() with { Warnings = ["故障 URI https://invalid/RAW-SECRET，路徑 C:/USER-SECRET"] };
        var warning = RedfishFactsService.Collect(inventory, At).Single(f => f.Key == "rf.bundle.warnings");
        Assert.True(warning.Sensitive);
        var snapshot = HardwareSnapshotService.Create("test", [warning], includeSensitive: true);
        string redacted = HardwareSnapshotService.ToJson(snapshot);
        Assert.DoesNotContain("RAW-SECRET", redacted);
        Assert.DoesNotContain("USER-SECRET", redacted);
        string preserved = HardwareSnapshotService.ToJson(snapshot,
            new HardwareSnapshotSaveOptions { SensitiveValues = SensitiveValuePolicy.Preserve });
        Assert.Contains("RAW-SECRET", preserved);
        Assert.Contains("USER-SECRET", preserved);
    }
}
