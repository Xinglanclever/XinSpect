using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 檔案系統與陰影複製盤點（ST-006）單元測試。
/// </summary>
public class FileSystemVolumeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<FileSystemVolumeFactsService.VolumeRow> Volumes =
    [
        new("C:\\", "NTFS", 500L * 1073741824, 200L * 1073741824, false),
        new("D:\\", "NTFS", 1000L * 1073741824, 100L * 1073741824, false),
    ];

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── 卷冊盤點 ─────────────────────────────────────────────────────────

    [Fact]
    public void 卷冊_分類與容量彙總()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, volumes: () => Volumes), "st.fs.volumes");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("磁碟區 2 個", f.Value);
        Assert.Contains("NTFS 2", f.Value);
        // 500 GiB + 1000 GiB = 1500 GiB（值裡的 GiB 用 N1 格式，含千分位）。
        Assert.Contains("1,500.0 GiB", f.Value);
        Assert.Contains("300.0 GiB", f.Value);
    }

    [Fact]
    public void 卷冊_不列代號與卷標()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, volumes: () => Volumes), "st.fs.volumes");
        Assert.Contains("不列磁碟機代號與卷標", f.Value);
        Assert.DoesNotContain("C:\\", f.Value);
        Assert.DoesNotContain("D:\\", f.Value);
    }

    [Fact]
    public void 卷冊_系統差異要聲明()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, volumes: () => Volumes), "st.fs.volumes");
        Assert.Contains("系統差異聲明", f.Value);
        Assert.Contains("Integrity Streams", f.Value);
    }

    [Fact]
    public void 卷冊_其他檔案系統分組列出()
    {
        var rows = new List<FileSystemVolumeFactsService.VolumeRow>(Volumes)
        {
            new("X:\\", "exFAT", 10, 1, false),
            new("Y:\\", "exFAT", 10, 1, false),
        };
        var f = Fact(FileSystemVolumeFactsService.Collect(At, volumes: () => rows), "st.fs.volumes");
        Assert.Contains("exFAT×2", f.Value);
    }

    [Fact]
    public void 卷冊_零個是值不是缺席()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, volumes: () => []), "st.fs.volumes");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal("磁碟區 0 個。", f.Value);
    }

    [Fact]
    public void 卷冊_讀不到是ReadError不畫成零()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, volumes: () => null), "st.fs.volumes");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0 個卷", f.UnavailableReason);
    }

    // ── ReFS 完整性串流 ───────────────────────────────────────────────────

    [Fact]
    public void ReFS_沒有ReFS卷是不適用不是零()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            refs: () => new FileSystemVolumeFactsService.RefsState(0, 0, 0)), "st.fs.refs");
        // 這是本項最重要的一條：填 0 會被讀成「ReFS 但沒開完整性串流」。
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("NTFS 沒有 Integrity Streams", f.UnavailableReason);
    }

    [Fact]
    public void ReFS_有卷時陳述啟用數與成本()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            refs: () => new FileSystemVolumeFactsService.RefsState(2, 1, 40)), "st.fs.refs");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("ReFS 卷 2 個", f.Value);
        Assert.Contains("啟用完整性串流 1 個", f.Value);
        Assert.Contains("有成本", f.Value);
    }

    [Fact]
    public void ReFS_不做該不該開的判斷()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            refs: () => new FileSystemVolumeFactsService.RefsState(1, 1, 5)), "st.fs.refs");
        Assert.Contains("不對該不該開下判斷", f.Value);
    }

    [Fact]
    public void ReFS_讀取失敗是ReadError()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, refs: () => null), "st.fs.refs");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── VSS 儲存空間 ─────────────────────────────────────────────────────

    [Fact]
    public void VSS_陳述空間與上限比例()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            vss: () => new FileSystemVolumeFactsService.VssState(2, 1073741824, 536870912, 4294967296, true)),
            "st.fs.vss");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("關聯 2 組", f.Value);
        Assert.Contains("12.5%", f.Value);
        Assert.Contains("尚未達上限", f.Value);
    }

    [Fact]
    public void VSS_撞到上限是最重要的訊號()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            vss: () => new FileSystemVolumeFactsService.VssState(1, 1024, 4294967296, 4294967296, true)),
            "st.fs.vss");
        Assert.Contains("已達容量上限", f.Value);
        // 撞上限的後果要說清楚，不能只丟一個數字。
        Assert.Contains("最舊的還原點先消失", f.Value);
        Assert.Equal(4294967296d, f.NumericValue);
    }

    [Fact]
    public void VSS_不列建立時間與來源機器名()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            vss: () => new FileSystemVolumeFactsService.VssState(1, 100, 50, 1000, true)), "st.fs.vss");
        Assert.Contains("不列陰影的建立時間與來源機器名", f.Value);
        Assert.Contains("已用 0", f.Value);
    }

    [Fact]
    public void VSS_已用零是值不是缺席()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            vss: () => new FileSystemVolumeFactsService.VssState(1, 0, 0, 1000, true)), "st.fs.vss");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
    }

    [Fact]
    public void VSS_零關聯是不適用()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            vss: () => new FileSystemVolumeFactsService.VssState(0, 0, 0, 0, true)), "st.fs.vss");
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有在用陰影複製", f.UnavailableReason);
    }

    [Fact]
    public void VSS_落點容量讀不到如實標明()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At,
            vss: () => new FileSystemVolumeFactsService.VssState(1, 100, 50, 1000, false)), "st.fs.vss");
        Assert.Contains("落點容量讀不到", f.Value);
    }

    [Fact]
    public void VSS_讀取失敗是ReadError()
    {
        var f = Fact(FileSystemVolumeFactsService.Collect(At, vss: () => null), "st.fs.vss");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 共同契約 ─────────────────────────────────────────────────────────

    [Fact]
    public void 本批三把鍵齊()
    {
        var facts = FileSystemVolumeFactsService.Collect(At);
        Assert.Equal(
            new[] { "st.fs.refs", "st.fs.volumes", "st.fs.vss" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 三項互不影響()
    {
        var facts = FileSystemVolumeFactsService.Collect(At,
            volumes: () => throw new InvalidOperationException("v"),
            refs: () => null,
            vss: () => new FileSystemVolumeFactsService.VssState(1, 1, 1, 2, true));

        Assert.Equal(FactAvailability.ReadError, Fact(facts, "st.fs.volumes").Availability);
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "st.fs.refs").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "st.fs.vss").Availability);
    }

    [Fact]
    public void 不適用與失敗都帶原因且值為空()
    {
        var facts = FileSystemVolumeFactsService.Collect(At,
            volumes: () => null,
            refs: () => new FileSystemVolumeFactsService.RefsState(0, 0, 0),
            vss: () => new FileSystemVolumeFactsService.VssState(0, 0, 0, 0, true));

        foreach (var f in facts)
        {
            Assert.NotEqual(FactAvailability.Present, f.Availability);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺原因");
            Assert.Equal("", f.Value);
            Assert.Equal(FactTrustLevel.Unknown, f.Trust);
            Assert.Null(f.NumericValue);
        }
    }
}
