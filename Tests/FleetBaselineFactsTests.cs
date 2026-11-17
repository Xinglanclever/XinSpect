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
        new("anon-" + model + at.Ticks, at, FleetSnapshotReader.NormalizeModel(model), model,
            facts.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
            facts.ToDictionary(f => f.Key, f => new FleetFactMetadata("°C", FactAvailability.Present, FactTrustLevel.Measured, "fixture"), StringComparer.Ordinal));

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    [Fact]
    public void RepeatedMachineDoesNotCreateIndependentPeers()
    {
        var snapshots = Enumerable.Range(0, 5).Select(i =>
            Snap("A", DateTimeOffset.UnixEpoch.AddDays(i), ("sensor.cpu", 40 + i))
                with { AnonymousMachineId = "one-machine" }).ToArray();
        var summary = Fact(FleetBaselineFactsService.Collect(snapshots, DateTimeOffset.UnixEpoch),
            FleetBaselineFactsService.SummaryKey);
        Assert.NotEqual(FactAvailability.Present, summary.Availability);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void NonFiniteCommonValuesDoNotProduceComparableSummary(double value)
    {
        var snapshots = new[] { Snap("A", DateTimeOffset.UnixEpoch, ("sensor.cpu", value)),
            Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.cpu", 40)) };
        var result = FleetBaselineFactsService.Collect(snapshots, DateTimeOffset.UnixEpoch);
        Assert.NotEqual(FactAvailability.Present, Fact(result, FleetBaselineFactsService.SummaryKey).Availability);
        Assert.Equal(FactAvailability.Unknown, Fact(result, FleetBaselineFactsService.PeerPrefix + "0001").Availability);
    }

    [Fact]
    public void FiniteExtremeMedianDoesNotOverflow()
    {
        var snapshots = new[] { Snap("A", DateTimeOffset.UnixEpoch, ("sensor.cpu", double.MaxValue)),
            Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.cpu", double.MaxValue)) };
        var peer = Fact(FleetBaselineFactsService.Collect(snapshots, DateTimeOffset.UnixEpoch),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Equal(FactAvailability.Present, peer.Availability);
        Assert.DoesNotContain("∞", peer.Value);
        Assert.DoesNotContain("Infinity", peer.Value);
    }

    [Fact]
    public void AssetIdentityHeadlineIsNotAModelAndOversizedFilesAreRejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fleet-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "identity.json"),
                "{\"facts\":[{\"key\":\"asset.identify\",\"value\":\"identifiable: 4 fields\"}]}");
            Assert.Null(Assert.Single(FleetSnapshotReader.Read(dir)).ModelKey);
            File.WriteAllText(Path.Combine(dir, "large.json"), new string(' ', FleetSnapshotReader.MaxFileBytes + 1));
            Assert.Single(FleetSnapshotReader.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DirectoryCandidateBudgetRefusesTruncatedCohort()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fleet-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i <= FleetSnapshotReader.MaxDirectoryEntries; i++)
                File.WriteAllText(Path.Combine(dir, i + ".json"), "{\"facts\":[]}");
            Assert.Empty(FleetSnapshotReader.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConflictingLatestMachineObservationsAreNotSelectedArbitrarily()
    {
        var snapshot = Snap("A", DateTimeOffset.UnixEpoch, ("sensor.cpu", 40));
        var other = Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.cpu", 50));
        var result = FleetBaselineFactsService.Collect([snapshot, snapshot with
            { Facts = new Dictionary<string, double> { ["sensor.cpu"] = 90 } }, other], DateTimeOffset.UnixEpoch);
        Assert.NotEqual(FactAvailability.Present, Fact(result, FleetBaselineFactsService.SummaryKey).Availability);
    }

    [Fact]
    public void LatestObservationPerMachineDefinesThePeerValue()
    {
        var first = Snap("A", DateTimeOffset.UnixEpoch, ("sensor.cpu", 1000)) with { AnonymousMachineId = "a" };
        var latest = Snap("A", DateTimeOffset.UnixEpoch.AddDays(2), ("sensor.cpu", 50)) with { AnonymousMachineId = "a" };
        var second = Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.cpu", 40)) with { AnonymousMachineId = "b" };
        var peer = Fact(FleetBaselineFactsService.Collect([first, latest, second], DateTimeOffset.UnixEpoch),
            FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.Equal(2d, peer.NumericValue);
        Assert.Contains("族群中位 45", peer.Value);
    }

    [Fact]
    public void JsonExponentOverflowIsNotAComparableNumber()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fleet-infinite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "overflow.json"),
                "{\"facts\":[{\"key\":\"sensor.cpu\",\"numericValue\":1e400}]}");
            Assert.Empty(Assert.Single(FleetSnapshotReader.Read(dir)).Facts);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("source")]
    [InlineData("unknown")]
    [InlineData("speculative")]
    [InlineData("missing")]
    public void MissingOrIncompatibleMetadataIsNotComparable(string difference)
    {
        var a = Snap("A", DateTimeOffset.UnixEpoch, ("sensor.cpu", 50));
        var b = Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.cpu", 60));
        var meta = b.Metadata!["sensor.cpu"];
        b = difference == "missing" ? b with { Metadata = null } : b with
        {
            Metadata = new Dictionary<string, FleetFactMetadata>
            {
                ["sensor.cpu"] = difference switch
                {
                    "unit" => meta with { Unit = "°F" },
                    "source" => meta with { Source = "other provider" },
                    "unknown" => meta with { Availability = FactAvailability.Unknown },
                    _ => meta with { Trust = FactTrustLevel.Speculative },
                }
            }
        };
        var result = FleetBaselineFactsService.Collect([a, b], DateTimeOffset.UnixEpoch);
        var summary = Fact(result, FleetBaselineFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.Contains("共同鍵", summary.UnavailableReason);
        Assert.DoesNotContain("第二", summary.UnavailableReason);
    }

    [Fact]
    public void SmallSampleOrderUsesKeysRatherThanIncompatibleUnits()
    {
        var a = Snap("A", DateTimeOffset.UnixEpoch, ("sensor.a", 1), ("sensor.z", 1));
        var b = Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.a", 2), ("sensor.z", 100000));
        var peer = Fact(FleetBaselineFactsService.Collect([a, b], DateTimeOffset.UnixEpoch), FleetBaselineFactsService.PeerPrefix + "0001");
        Assert.True(peer.Value.IndexOf("sensor.a", StringComparison.Ordinal) < peer.Value.IndexOf("sensor.z", StringComparison.Ordinal));
        Assert.Contains("不證明獨立實體機器", peer.Value);
    }

    [Fact]
    public void ReadDiagnosticsSurviveIReadOnlyListAndPreventSuccessfulBaseline()
    {
        IReadOnlyList<FleetSnapshot> snapshots = new FleetReadResult(
            [Snap("A", DateTimeOffset.UnixEpoch, ("sensor.cpu", 40)),
             Snap("A", DateTimeOffset.UnixEpoch.AddDays(1), ("sensor.cpu", 50))], ["FileByteBudgetExceeded"]);
        var summary = Fact(FleetBaselineFactsService.Collect(snapshots, DateTimeOffset.UnixEpoch), FleetBaselineFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.Contains("FileByteBudgetExceeded", summary.UnavailableReason);
    }

    [Fact]
    public void AggregateByteBudgetRejectsFurtherFilesAndReportsUnknown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fleet-total-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string content = "{\"facts\":[]}".PadRight(FleetSnapshotReader.MaxFileBytes);
            for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(dir, i + ".json"), content);
            Assert.Empty(FleetSnapshotReader.Read(dir).Diagnostics);
            File.WriteAllText(Path.Combine(dir, "4.json"), content);
            var snapshots = FleetSnapshotReader.Read(dir);
            Assert.Contains("AggregateByteBudgetExceeded", snapshots.Diagnostics);
            Assert.True(snapshots.Count <= FleetSnapshotReader.MaxTotalBytes / FleetSnapshotReader.MaxFileBytes);
            Assert.Equal(FactAvailability.Unknown, Fact(FleetBaselineFactsService.Collect(snapshots, DateTimeOffset.UnixEpoch), FleetBaselineFactsService.SummaryKey).Availability);
        }
        finally { Directory.Delete(dir, true); }
    }

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
        Assert.Equal(FactAvailability.Unknown, s.Availability);
        Assert.Contains("沒有一個型號出現兩次以上", s.Value);
        Assert.Contains("第二個匿名識別組", s.UnavailableReason);
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
        Assert.Equal(FactAvailability.Unknown, peer.Availability);
        Assert.NotEqual(FactAvailability.Present, Fact(FleetBaselineFactsService.Collect(snaps, at), FleetBaselineFactsService.SummaryKey).Availability);
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
        Assert.Contains("只取最新一份", f.Value);
        Assert.Contains("不判讀差異是好是壞", f.Value);
    }

    // ── 讀取器 ──────────────────────────────────────────────────────────

    private static string WriteSnapshot(string dir, string model, params (string Key, double? Value)[] facts)
    {
        var list = new List<object>
        {
            new { key = "asset.field.系統製造商", value = "ACME", availability = "Present", unit = "", trust = "Reported", source = "fixture" },
                new { key = "asset.field.系統型號", value = model, availability = "Present", unit = "", trust = "Reported", source = "fixture" },
        };
        foreach (var (k, v) in facts)
            list.Add(new { key = k, value = v?.ToString() ?? "", numericValue = v, unit = "°C", availability = "Present", trust = "Measured", source = "fixture" });
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
            Assert.Equal("ACME | ASUS B650", snaps[0].ModelText);
            Assert.Equal("ACME | ASUS B650", snaps[0].ModelKey);
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
            Assert.Contains("SnapshotReadError", FleetSnapshotReader.Read(dir).Diagnostics);
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
