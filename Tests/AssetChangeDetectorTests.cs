using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP42 資產生命週期：快照差分 → 資產事件的分類契約。
/// 只有「資產級」key 前綴（記憶體／處理器／顯示卡／儲存／主機板）產生資產事件，
/// 其餘變更是狀態變更不是資產事件——界線機器釘住；同 key 前後值入描述。
/// </summary>
public class AssetChangeDetectorTests
{
    private static HardwareFact F(string key, string name, string value, string category = "測試") =>
        new(key, category, name, value, "", "s", FactTrustLevel.Reported, false,
            DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"));

    private static HardwareSnapshot Snap(params HardwareSnapshotFact[] facts)
    {
        var at = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00");
        return new HardwareSnapshot
        {
            SchemaVersion = 1, AppVersion = "t", AnonymousMachineId = "sha256:" + new string('a', 64),
            CapturedAtUtc = at, SensitiveValuesPreserved = false,
            Facts = facts,
            Integrity = new HardwareSnapshotIntegrity { Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm, Hash = new string('a', 64) },
        };
    }

    private static HardwareSnapshotFact S(string key, string name, string value, string category = "測試") => new()
    {
        Key = key, Category = category, Name = name, Value = value,
        Source = "s", Trust = FactTrustLevel.Reported, Sensitive = false,
        MeasuredAtUtc = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"),
    };

    [Fact]
    public void 資產事件_新增移除變更逐類分類()
    {
        var before = Snap(
            S("mem.spd.dimm0", "DIMM0", "16 GiB", "記憶體"),
            S("gpu.old", "顯示卡", "RTX A", "顯示卡"),
            S("msr.0x8b", "微碼", "0x02007006", "韌體安全"));
        var after = Snap(
            S("mem.spd.dimm0", "DIMM0", "16 GiB", "記憶體"),
            S("mem.spd.dimm1", "DIMM1", "16 GiB", "記憶體"),
            S("gpu.model", "顯示卡", "RTX B", "顯示卡"),
            S("msr.0x8b", "微碼", "0x02007007", "韌體安全"));

        var events = XinSpect.AssetChangeDetector.Detect(before, after);

        var added = Assert.Single(events, e => e.Kind == XinSpect.AssetEventKind.Added && e.Key == "mem.spd.dimm1");
        Assert.Equal("記憶體", added.AssetClass);

        var removed = Assert.Single(events, e => e.Kind == XinSpect.AssetEventKind.Removed);
        Assert.Equal("gpu.old", removed.Key);
        Assert.Equal("顯示卡", removed.AssetClass);

        var changed = Assert.Single(events, e => e.Kind == XinSpect.AssetEventKind.Changed);
        Assert.Equal("msr.0x8b", changed.Key);
        Assert.Equal("狀態", changed.AssetClass);      // 韌體安全不是資產——是狀態變更
        Assert.Contains("0x02007006", changed.Description);
        Assert.Contains("0x02007007", changed.Description);
    }

    [Fact]
    public void 資產事件_無變更回空_跨機器如實拒()
    {
        var snap = Snap(S("mem.spd.dimm0", "DIMM0", "16 GiB", "記憶體"));
        Assert.Empty(XinSpect.AssetChangeDetector.Detect(snap, snap));

        var otherMachine = snap with { AnonymousMachineId = "sha256:" + new string('b', 64) };
        Assert.Throws<InvalidOperationException>(() =>
            XinSpect.AssetChangeDetector.Detect(snap, otherMachine));
    }
}
