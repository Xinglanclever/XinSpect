using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 同型號族群基線（U10 第三圈 EN-0001）：不上傳、字串相等的型號、樣本少就不給 z 分數。
/// </summary>
public class FleetBaselineFactsTests
{
    private static FleetSnapshot Snap(string model, DateTimeOffset at, params (string Key, double Value)[] facts) =>
        new("anon-" + model, at, FleetSnapshotReader.NormalizeModel(model), model,
            facts.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    // ── 正規化 ──────────────────────────────────────────────────────────

    [Fact]
    public void 型號正規化_只處理排版不動語意()
    {
        Assert.Equal("ASUS TUF GAMING B650", FleetSnapshotReader.NormalizeModel("ASUS  TUF Gaming  B650"));
        Assert.Equal("DELL XPS 15 9520", FleetSnapshotReader.NormalizeModel(" Dell XPS 15 9520 "));
        // 全形字元被去掉，但英數與空白保留
        Assert.Equal("HP ELITEBOOK 840", FleetSnapshotReader.NormalizeModel("HP　EliteBook 840"));
    }

    [Fact]
    public void 型號空白時回null()
    {
        Assert.Null(FleetSnapshotReader.NormalizeModel(null));
        Assert.Null(FleetSnapshotReader.NormalizeModel("   "));
        Assert.Null(FleetSnapshotReader.NormalizeModel("　　"));
    }

    [Fact]
    public void 參與比對的鍵前綴是刻意限縮的()
    {
        // 只有穩定量測前綴；不含裝置清單／事件計數／容量
        Assert.Contains("smbios.", FleetSnapshotReader.ComparableKeyPrefixes);
        Assert.Contains("sensor.", FleetSnapshotReader.ComparableKeyPrefixes);
        Assert.DoesNotContain("os.dev.", FleetSnapshotReader.ComparableKeyPrefixes);
        Assert.DoesNotContain("fs.", FleetSnapshotReader.ComparableKeyPrefixes);
    }

    // ── 服務 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有快照_標不適用且分清兩種沒有()
    {
        var facts = FleetBaselineFactsService.Collect([], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, FleetBaselineFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Contains("「沒有快照」與「沒有同型號的機器」是兩件事", s.UnavailableReason);
        Assert.Equal(FactAvailability.Present, Fact(facts, FleetBaselineFactsService.BoundaryKey).Availability);
    }

    [Fact]
    public void 全部快照都沒有型號_不用全部快照冒充同型號()
    {
        var snaps = new List<FleetSnapshot>
        {
            new("a", DateTimeOffset.UnixEpoch, null, null, new Dictionary<string, double>()),
            new("b", DateTimeOffset.UnixEpoch, null, null, new Dictionary<string, double>()),
        };
        var s = Fact(FleetBaselineFactsService.Collect(snaps, DateTimeOffset.UnixEpoch),
            FleetBaselineFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Contains("分不出族群就沒有族群", s.UnavailableReason);
        Assert.Contains("不會用「全部快照」冒充同型號", s.UnavailableReason);
    }

    [Fact]
    public void 每個型號只有一份_不給基線()
    {
        var snaps = new List<FleetSnapshot>
        {
            Snap("A", DateTimeOffset.UnixEpoch.AddDays(-1), ("sensor.cpu", 50)),
            Snap("B", DateTimeOffset.UnixEpoch, ("sensor.cpu", 60)),
        };
        var s = Fact(FleetBaselineFactsService.Collect(snaps, DateTimeOffset.UnixEpoch),
            FleetBaselineFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Contains("沒有一個型號出現兩次以上", s.Value);
        Assert.Contains("沒有可以比的第二台", s.UnavailableReason);
    }

    [Fact]
    public void 同型號兩份_給中位數但不給z()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snaps = new List<FleetSnapshot>
        {
            Snap("A", at.AddDays(-2), ("sensor.cpu", 50), ("sensor.gpu", 60)),
            Snap("A", at.AddDays(-1), ("sensor.cpu", 70), ("sensor.gpu", 60)),
        };
        var facts = FleetBaselineFactsService.Collect(snaps, at);
        var peer = Fact(facts, FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Equal(2d, peer.NumericValue!.Value);
        Assert.Contains("只有 2 份樣本", peer.Value);
        Assert.Contains("**不給 z 分數**", peer.Value);
        Assert.DoesNotContain("、z ", peer.Value);
        Assert.Contains("族群中位", peer.Value);
    }

    [Fact]
    public void 四份以上_給z分數()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snaps = new List<FleetSnapshot>
        {
            Snap("A", at.AddDays(-4), ("sensor.cpu", 50)),
            Snap("A", at.AddDays(-3), ("sensor.cpu", 51)),
            Snap("A", at.AddDays(-2), ("sensor.cpu", 49)),
            Snap("A", at.AddDays(-1), ("sensor.cpu", 90)),
        };
        var peer = Fact(FleetBaselineFactsService.Collect(snaps, at),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Contains("、z +", peer.Value);
        Assert.DoesNotContain("不給 z 分數", peer.Value);
    }

    [Fact]
    public void 標準差是零時_z算不出而不是印無限大()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snaps = Enumerable.Range(0, 5)
            .Select(i => Snap("A", at.AddDays(-5 + i), ("sensor.cpu", 42))).ToList();
        var peer = Fact(FleetBaselineFactsService.Collect(snaps, at),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Contains("z 算不出（標準差 0）", peer.Value);
        Assert.DoesNotContain("∞", peer.Value);
    }

    [Fact]
    public void 只比每份都有數值的鍵()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snaps = new List<FleetSnapshot>
        {
            Snap("A", at.AddDays(-1), ("sensor.cpu", 50), ("sensor.gpu", 60)),
            Snap("A", at, ("sensor.cpu", 55)),
        };
        var peer = Fact(FleetBaselineFactsService.Collect(snaps, at),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Contains("共 1 個鍵在每一份都有數值", peer.Value);
        Assert.DoesNotContain("sensor.gpu", peer.Value);
    }

    [Fact]
    public void 沒有共同鍵時不編差異()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snaps = new List<FleetSnapshot>
        {
            Snap("A", at.AddDays(-1), ("sensor.cpu", 50)),
            Snap("A", at, ("sensor.gpu", 55)),
        };
        var peer = Fact(FleetBaselineFactsService.Collect(snaps, at),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Contains("沒有共同的可比數值鍵", peer.Value);
        Assert.Contains("沒有共同分母就不比", peer.Value);
    }

    [Fact]
    public void 值裡要明說不判讀好壞()
    {
        var at = DateTimeOffset.UnixEpoch;
        var snaps = new List<FleetSnapshot>
        {
            Snap("A", at.AddDays(-1), ("sensor.cpu", 50)),
            Snap("A", at, ("sensor.cpu", 90)),
        };
        var peer = Fact(FleetBaselineFactsService.Collect(snaps, at),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Contains("本鍵不判讀差異是好是壞", peer.Value);
        Assert.Contains("只說「離族群多遠」", peer.Value);
    }

    [Fact]
    public void 界線宣告_六條都要在()
    {
        var f = Fact(FleetBaselineFactsService.Collect([], DateTimeOffset.UnixEpoch),
            FleetBaselineFactsService.BoundaryKey);
        Assert.Contains("不上傳", f.Value);
        Assert.Contains("字串相等", f.Value);
        Assert.Contains("不給 z 分數", f.Value);
        Assert.Contains("每一份都有數值", f.Value);
        Assert.Contains("不區分", f.Value);
        Assert.Contains("不判讀差異是好是壞", f.Value);
    }

    // ── 讀取器 ──────────────────────────────────────────────────────────

    private static string WriteSnapshot(string dir, string model, params (string Key, double? Value)[] facts)
    {
        var list = new List<object>
        {
            new { key = "asset.identify", value = model },
        };
        foreach (var (k, v) in facts)
            list.Add(new { key = k, value = v?.ToString() ?? "", numericValue = v });
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            appVersion = "test",
            anonymousMachineId = "anon",
            capturedAtUtc = DateTimeOffset.UnixEpoch,
            facts = list,
        });
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void 讀取器_讀得出型號與數值鍵()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-fleet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteSnapshot(dir, "ASUS B650", ("sensor.cpu", 55.5), ("os.dev.summary", 12));
            var snaps = FleetSnapshotReader.Read(dir);
            Assert.Single(snaps);
            Assert.Equal("ASUS B650", snaps[0].ModelText);
            Assert.Equal("ASUS B650", snaps[0].ModelKey);
            Assert.True(snaps[0].Facts.ContainsKey("sensor.cpu"));
            // 不在前綴清單裡的鍵不進比對集合
            Assert.False(snaps[0].Facts.ContainsKey("os.dev.summary"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 讀取器_目錄不存在回空清單()
    {
        Assert.Empty(FleetSnapshotReader.Read(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void 讀取器_壞檔不影響其他份()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-fleet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
            WriteSnapshot(dir, "ASUS B650", ("sensor.cpu", 55.5));
            Assert.Single(FleetSnapshotReader.Read(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 讀取器不引用任何網路型別()
    {
        // 「不上傳」在型別層是可檢查的：這兩個服務的檔案裡不該出現 HttpClient／WebRequest。
        string root = RepoRoot();
        foreach (string f in new[]
                 {
                     Path.Combine(root, "Services", "FleetSnapshotReader.cs"),
                     Path.Combine(root, "Services", "FleetBaselineFactsService.cs"),
                 })
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("HttpClient", text);
            Assert.DoesNotContain("WebRequest", text);
            Assert.DoesNotContain("Socket", text);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Rules")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
