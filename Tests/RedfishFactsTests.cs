using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// <see cref="RedfishFactsService"/> 的守門：離線 Redfish 目錄轉成 <c>rf.*</c> 事實時，
/// 信任等級一律 Reported、讀不到如實標 Unavailable、逐項鍵穩定（同一份包重讀得同一組鍵）。
/// </summary>
public class RedfishFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static RedfishInventory Inventory() => new(
        "C:/fake/redfish",
        new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero),
        [new RedfishManager("BMC", "AST2600", "2.1.0", "BMC", "Enabled/OK")],
        [new RedfishChassis("1U", " RackMount", "SN123", "On", "Normal")],
        [new RedfishSystem("SYS", "Acme", "S-100", "SN456", "1.2.3", "On")],
        [new RedfishPsu("PSU1", "ACME-750", "1.0", 412.5, 230.0, "Enabled/OK")],
        [new RedfishFan("FAN1", 5400, "Enabled/OK")],
        [new RedfishTemperature("CPU", 43.5, 85, "Enabled/OK")],
        187.5,
        ["被拒絕的 @odata.id：http://example.com（外部 URL 不追）"])
        {
            PowerControlsComplete = true,
            PowerControls = [new("/redfish/v1/Chassis/1U", "/redfish/v1/Chassis/1U/Power", 0,
                "0", "/redfish/v1/Chassis/1U/Power#/PowerControl/0", "System power", "Chassis", 187.5)
                { ScopeResolved = true }],
        };

    [Fact]
    public void 全部事實都是Reported_沒有任何Measured()
    {
        var facts = RedfishFactsService.Collect(Inventory(), At);
        Assert.NotEmpty(facts);
        Assert.All(facts, f => Assert.Equal(FactTrustLevel.Reported, f.Trust));
    }

    [Fact]
    public void 彙總與邊界鍵都在靜態目錄裡()
    {
        var facts = RedfishFactsService.Collect(Inventory(), At);
        foreach (string key in new[] { RedfishFactsService.SummaryKey, RedfishFactsService.BoundaryKey,
                 "rf.power.consumed", "rf.bundle.warnings" })
            Assert.Contains(facts, f => f.Key == key);

        // 彙總要講清楚「回報不是量測」；邊界要講清楚不連網。
        Assert.Contains("Reported", facts.First(f => f.Key == RedfishFactsService.SummaryKey).Value, StringComparison.Ordinal);
        Assert.Contains("不連網", facts.First(f => f.Key == RedfishFactsService.BoundaryKey).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 逐項鍵屬於rf_item家族且穩定()
    {
        var once = RedfishFactsService.Collect(Inventory(), At);
        var again = RedfishFactsService.Collect(Inventory(), At);
        var items = once.Where(f => f.Key.StartsWith("rf.item.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(items);
        Assert.Contains(items, f => f.Key.StartsWith("rf.item.temp.cpu-", StringComparison.Ordinal));
        Assert.Contains(items, f => f.Key.StartsWith("rf.item.psu.psu1-", StringComparison.Ordinal));
        Assert.All(items, f => Assert.Matches(@"^[a-z0-9](?:[a-z0-9._\-\[\]]{0,159})$", f.Key));
        // 同一份包重讀：鍵集合完全一致（順序也一致）
        Assert.Equal(items.Select(f => f.Key), again.Where(f => f.Key.StartsWith("rf.item.", StringComparison.Ordinal)).Select(f => f.Key));
        // 全部鍵要嘛在目錄、要嘛命中動態家族
        foreach (var f in once)
            Assert.True(FactKeyCatalog.Keys.Contains(f.Key)
                        || FactKeyDynamicCatalog.Families.Keys.Any(p => f.Key.StartsWith(p, StringComparison.Ordinal)),
                $"鍵 {f.Key} 既不在目錄也不命中動態家族");
    }

    [Fact]
    public void 空目錄如實標Unavailable_不冒充沒有BMC()
    {
        var empty = new RedfishInventory("C:/fake/empty", null, [], [], [], [], [], [], null, ["找不到 redfish/v1/index.json"]);
        var facts = RedfishFactsService.Collect(empty, At);
        var summary = facts.First(f => f.Key == RedfishFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.NotNull(summary.UnavailableReason);
    }

    [Fact]
    public void 警告鍵把被拒絕的資源帶出來()
    {
        var facts = RedfishFactsService.Collect(Inventory(), At);
        var warnings = facts.First(f => f.Key == "rf.bundle.warnings");
        Assert.Contains("外部 URL", warnings.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 序號事實敏感且逐項沿用來源採集時間()
    {
        var inventory = Inventory() with { CollectedAtUtc = new DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.FromHours(8)) };
        var facts = RedfishFactsService.Collect(inventory, At);
        Assert.True(facts.Single(f => f.Value.Contains("SN123", StringComparison.Ordinal)).Sensitive);
        Assert.All(facts.Where(f => f.Key.StartsWith("rf.item.", StringComparison.Ordinal)
            || f.Key == "rf.power.consumed"), f =>
        {
            Assert.Equal(At, f.MeasuredAtUtc);
            Assert.Contains("2026-11-03T08:00:00.0000000+00:00", f.Source);
            Assert.Contains("2026-11-03T12:00:00.0000000+00:00", f.Source);
        });
    }

    [Fact]
    public void 未知採集時間不冒充即時可用數值()
    {
        var facts = RedfishFactsService.Collect(Inventory() with { CollectedAtUtc = null }, At);
        Assert.All(facts.Where(f => f.Key.StartsWith("rf.item.", StringComparison.Ordinal)
            || f.Key == "rf.power.consumed" || f.Key == "rf.summary"), f =>
        {
            Assert.Equal(FactAvailability.Unknown, f.Availability);
            Assert.Null(f.NumericValue);
            Assert.Contains("採集時間", f.UnavailableReason);
            Assert.Equal(At, f.MeasuredAtUtc);
            Assert.Contains("來源採集時間未知", f.Source);
        });
    }

    [Fact]
    public void 標點截長與重複Id不產生重複鍵()
    {
        string longPrefix = new('A', 50);
        var inventory = Inventory() with { Fans = new[] { "A/B", "A-B", "???", "!!!", longPrefix + "1", longPrefix + "2", "A/B", "cpu", "CPU", "Cpu" }
            .Select(id => new RedfishFan(id, 1000, null)).ToArray() };
        var first = RedfishFactsService.Collect(inventory, At).Where(f => f.Key.StartsWith("rf.item.fan.")).Select(f => f.Key).ToArray();
        Assert.Equal(10, first.Distinct().Count());
        Assert.All(first, key => Assert.Matches(@"^[a-z0-9](?:[a-z0-9._\-\[\]]{0,159})$", key));
        Assert.Equal(first, RedfishFactsService.Collect(inventory, At).Where(f => f.Key.StartsWith("rf.item.fan.")).Select(f => f.Key));
    }

    [Fact]
    public void 預設時間膠囊序列化遮蔽機箱序號()
    {
        var facts = RedfishFactsService.Collect(Inventory(), At);
        string chassisKey = facts.Single(f => f.Key.StartsWith("rf.item.chassis.", StringComparison.Ordinal)
            && f.Sensitive).Key;
        var snapshot = HardwareSnapshotService.Create("test", facts, includeSensitive: true);
        string json = HardwareSnapshotService.ToJson(snapshot);
        Assert.DoesNotContain("SN123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.rejected", json, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var chassis = doc.RootElement.GetProperty("facts").EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == chassisKey);
        Assert.Contains("已遮蔽", chassis.GetProperty("value").GetString());
    }
}
