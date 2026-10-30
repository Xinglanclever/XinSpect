using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// NVMe Identify 保留區非零掃描（U10 第三圈 NV-0029）：八段保留區的邊界要對，
/// 廠商區不能被算進來，「掃過且乾淨」與「沒掃到」要分開。
/// </summary>
public class NvmeReservedFactsTests
{
    private static byte[] Identify(string model = "TEST NVME 1TB", int length = 4096)
    {
        var b = new byte[length];
        Encoding.ASCII.GetBytes(model).CopyTo(b, 24);
        return b;
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    // ── 掃描器 ──────────────────────────────────────────────────────────

    [Fact]
    public void 保留區清單_八段且位移與長度照nvme_h()
    {
        // 這些數字是從 work/nvme.h 的 struct nvme_id_ctrl 逐欄累加出來的，
        // 不是憑印象寫的：rsvd100@100(11)、rsvd134@134(122)、rsvd352@352(160)、
        // rsvd534@534(2)、rsvd544@544(224)、rsvd1024@1024(768)、rsvd1804@1804(2)、rsvd1807@1807(241)
        Assert.Equal(8, NvmeReservedScanner.Ranges.Length);
        Assert.Equal(("rsvd100", 100, 11), NvmeReservedScanner.Ranges[0]);
        Assert.Equal(("rsvd134", 134, 122), NvmeReservedScanner.Ranges[1]);
        Assert.Equal(("rsvd352", 352, 160), NvmeReservedScanner.Ranges[2]);
        Assert.Equal(("rsvd534", 534, 2), NvmeReservedScanner.Ranges[3]);
        Assert.Equal(("rsvd544", 544, 224), NvmeReservedScanner.Ranges[4]);
        Assert.Equal(("rsvd1024", 1024, 768), NvmeReservedScanner.Ranges[5]);
        Assert.Equal(("rsvd1804", 1804, 2), NvmeReservedScanner.Ranges[6]);
        Assert.Equal(("rsvd1807", 1807, 241), NvmeReservedScanner.Ranges[7]);
    }

    [Fact]
    public void 掃描總長度是1530位元組()
    {
        int expected = 11 + 122 + 160 + 2 + 224 + 768 + 2 + 241;
        Assert.Equal(expected, NvmeReservedScanner.TotalScanned);
        Assert.Equal(1530, NvmeReservedScanner.TotalScanned);
    }

    [Fact]
    public void 全零資料_每段都是零()
    {
        var ranges = NvmeReservedScanner.Scan(Identify())!;
        Assert.Equal(8, ranges.Count);
        Assert.All(ranges, r => Assert.True(r.IsZero));
        Assert.Equal(0, NvmeReservedScanner.TotalNonZero(ranges));
    }

    [Fact]
    public void 不足4096位元組_回null不掃()
    {
        Assert.Null(NvmeReservedScanner.Scan(new byte[4095]));
    }

    [Fact]
    public void 單一非零位元組_要指出位移與值()
    {
        var data = Identify();
        data[200] = 0xAB;       // 落在 rsvd134（134–255）
        var ranges = NvmeReservedScanner.Scan(data)!;
        var hit = ranges.Single(r => r.Name == "rsvd134");
        Assert.False(hit.IsZero);
        Assert.Equal(1, hit.NonZeroCount);
        Assert.Equal(200, hit.FirstNonZeroOffset!.Value);
        Assert.Equal((byte)0xAB, hit.FirstNonZeroValue!.Value);
    }

    [Fact]
    public void 廠商區非零_不算保留區()
    {
        var data = Identify();
        for (int i = 3072; i < 4096; i++) data[i] = 0xFF;   // 整個 vs 區塞滿
        var ranges = NvmeReservedScanner.Scan(data)!;
        Assert.Equal(0, NvmeReservedScanner.TotalNonZero(ranges));
        Assert.All(ranges, r => Assert.True(r.IsZero));
    }

    [Fact]
    public void 有定義的欄位非零_不算保留區()
    {
        var data = Identify();
        data[0] = 0x86;   // vid
        data[4] = (byte)'S';    // sn
        data[266] = 70;   // wctemp（u16 @0x10A）
        data[77] = 5;     // mdts
        var ranges = NvmeReservedScanner.Scan(data)!;
        Assert.Equal(0, NvmeReservedScanner.TotalNonZero(ranges));
    }

    [Fact]
    public void 多段同時非零_逐段計數()
    {
        var data = Identify();
        data[100] = 1;     // rsvd100 首
        data[1807] = 2;    // rsvd1807 首
        data[1808] = 3;    // rsvd1807 內（1806 是 dctype，有定義的欄位，不算保留區）
        var ranges = NvmeReservedScanner.Scan(data)!;
        Assert.Equal(1, ranges.Single(r => r.Name == "rsvd100").NonZeroCount);
        Assert.Equal(2, ranges.Single(r => r.Name == "rsvd1807").NonZeroCount);
        Assert.Equal(3, NvmeReservedScanner.TotalNonZero(ranges));
    }

    // ── 服務 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有取樣_標不適用並分清兩種沒有()
    {
        var facts = NvmeReservedFactsService.Collect([], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, NvmeReservedFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Contains("「沒有 NVMe」與「讀不到 Identify」是兩件事", s.UnavailableReason);
        Assert.Equal(FactAvailability.Present, Fact(facts, NvmeReservedFactsService.BoundaryKey).Availability);
    }

    [Fact]
    public void 全乾淨_值要說只證明保留區為零()
    {
        var facts = NvmeReservedFactsService.Collect(
            [new NvmeReservedSample("磁碟 0", "TEST", NvmeReservedScanner.Scan(Identify()), null)],
            DateTimeOffset.UnixEpoch);
        var disk = Fact(facts, NvmeReservedFactsService.DiskPrefix + "0001");
        Assert.Equal(FactAvailability.Present, disk.Availability);
        Assert.Equal(0d, disk.NumericValue!.Value);
        Assert.Contains("1530 位元組保留區全為零", disk.Value);
        Assert.Contains("不證明韌體沒有別的怪癖", disk.Value);
    }

    [Fact]
    public void 有非零_要指出位移且不猜內容()
    {
        var data = Identify();
        data[560] = 0x5A;    // rsvd544
        var facts = NvmeReservedFactsService.Collect(
            [new NvmeReservedSample("磁碟 0", "TEST", NvmeReservedScanner.Scan(data), null)],
            DateTimeOffset.UnixEpoch);
        var disk = Fact(facts, NvmeReservedFactsService.DiskPrefix + "0001");
        Assert.Equal(1d, disk.NumericValue!.Value);
        Assert.Contains("rsvd544@544", disk.Value);
        Assert.Contains("首個在 0x230＝0x5A", disk.Value);
        Assert.Contains("不猜那些位元組是什麼", disk.Value);
        Assert.Contains("不代表硬碟壞了", disk.Value);
    }

    [Fact]
    public void 讀不到Identify_標讀取錯誤而不是全零()
    {
        var facts = NvmeReservedFactsService.Collect(
            [new NvmeReservedSample("磁碟 0", null, null, "協定查詢逾時")], DateTimeOffset.UnixEpoch);
        var disk = Fact(facts, NvmeReservedFactsService.DiskPrefix + "0001");
        Assert.Equal(FactAvailability.ReadError, disk.Availability);
        Assert.Null(disk.NumericValue);
        Assert.Contains("讀不到就不掃，不假裝掃過", disk.UnavailableReason);
        var s = Fact(facts, NvmeReservedFactsService.SummaryKey);
        Assert.Contains("1 顆的 Identify 讀不到", s.Value);
    }

    [Fact]
    public void 混合情形_總覽分開計數()
    {
        var dirty = Identify();
        dirty[110] = 9;
        var facts = NvmeReservedFactsService.Collect(
        [
            new NvmeReservedSample("磁碟 0", "A", NvmeReservedScanner.Scan(Identify()), null),
            new NvmeReservedSample("磁碟 1", "B", NvmeReservedScanner.Scan(dirty), null),
            new NvmeReservedSample("磁碟 2", null, null, "讀不到"),
        ], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, NvmeReservedFactsService.SummaryKey);
        Assert.Equal(1d, s.NumericValue!.Value);   // 1 個非零位元組
        Assert.Contains("3 顆 NVMe・掃了 2 顆", s.Value);
        Assert.Contains("其中 1 顆有非零保留位元組", s.Value);
        Assert.Contains("1 顆的 Identify 讀不到", s.Value);
    }

    [Fact]
    public void 界線宣告_六條都要在()
    {
        var f = Fact(NvmeReservedFactsService.Collect([], DateTimeOffset.UnixEpoch),
            NvmeReservedFactsService.BoundaryKey);
        Assert.Contains("只掃 **Identify Controller**", f.Value);
        Assert.Contains("rsvd100", f.Value);
        Assert.Contains("**<c>vs</c>（3072–4095，廠商區）不是保留區**", f.Value);
        Assert.Contains("非零**不代表故障**", f.Value);
        Assert.Contains("不猜那些位元組是什麼", f.Value);
        Assert.Contains("是兩種不同的斷言", f.Value);
    }

    [Fact]
    public void 廠商區界線常數與清單最後一段不重疊()
    {
        int lastEnd = NvmeReservedScanner.Ranges.Max(r => r.Offset + r.Length);
        Assert.True(lastEnd <= NvmeReservedScanner.VendorSpecificOffset);
        Assert.Equal(3072, NvmeReservedScanner.VendorSpecificOffset);
        Assert.Equal(1024, NvmeReservedScanner.VendorSpecificLength);
    }
}
