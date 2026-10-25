using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// MMIO 位址空間總量的守門（U10-MM-0052）。
///
/// <para>
/// <b>核心是三個「不出錯誤訊息」的坑：</b>
/// ①解析步長——CM_PARTIAL_RESOURCE_DESCRIPTOR 是 4 位元組對齊打包的 <b>20 bytes</b>，
/// 拿 x64 自然對齊的 24 去解會得到看起來像資料的垃圾；
/// ②洞的定義——只有「最高 RAM 端點以下」的間隙才計，端點以上無上界；
/// ③端點口徑——區段用「起點＋長度」（不含端點），洞同口徑，不能混用含端點的那套（Type 19 是含端點的）。
/// </para>
/// </summary>
public class MemMmioFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<MemMmioFactsService.PhysRange> R(params (ulong Start, ulong Length)[] ranges)
        => ranges.Select(r => new MemMmioFactsService.PhysRange(r.Start, r.Length)).ToList();

    private static MemMmioFactsService.MemoryMapProbe Probe(IReadOnlyList<MemMmioFactsService.PhysRange>? ranges)
        => () => ranges;

    [Fact]
    public void 讀取失敗要標讀取錯誤且不填零()
    {
        var fact = MemMmioFactsService.Collect(At, Probe(null)).Single();
        Assert.Equal(MemMmioFactsService.TotalKey, fact.Key);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("「讀不到」與「沒有洞」是不同的斷言", fact.UnavailableReason);
    }

    [Fact]
    public void 空描述元清單是不適用不是錯誤()
    {
        var fact = MemMmioFactsService.Collect(At, Probe(R())).Single();
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("解析後的答案", fact.UnavailableReason);
    }

    [Fact]
    public void 洞的計算_合併重疊_端點以下取間隙()
    {
        // RAM：0x1000–0xA0000、0x100000–0x110000、重疊段 0x108000–0x120000（合併到 0x120000）。
        var holes = MemMmioFactsService.ComputeHoles(R(
            (0x1000, 0x9F000), (0x100000, 0x10000), (0x108000, 0x18000)));
        Assert.Equal(2, holes.Count);
        Assert.Equal(0UL, holes[0].Start);
        Assert.Equal(0x1000UL, holes[0].End);                 // 頁 0 的洞
        Assert.Equal(0xA0000UL, holes[1].Start);
        Assert.Equal(0x100000UL, holes[1].End);               // 傳統 BIOS ROM 區（0x60000 bytes）
    }

    [Fact]
    public void 最高RAM端點以上不計()
    {
        // 只有一段低階 RAM：0x1000 以上沒有別的 RAM 可夾出間隙——上面什麼都不計。
        Assert.Empty(MemMmioFactsService.ComputeHoles(R((0, 0x1000))));
    }

    [Fact]
    public void 沒有低段RAM時0到最低RAM之間也是洞()
    {
        // RAM 只在 4 GiB 以上：0–4 GiB 整段不是 RAM——照定義它是一個洞（定義寫進總計事實的文字裡）。
        var holes = MemMmioFactsService.ComputeHoles(R((0x1_0000_0000, 0x1000)));
        var hole = Assert.Single(holes);
        Assert.Equal(0UL, hole.Start);
        Assert.Equal(0x1_0000_0000UL, hole.End);
    }

    [Fact]
    public void 總計事實帶定義與界線且數值是洞的合計()
    {
        // 洞：[0,0x1000)＋[0xA0000,0x100000)＝0x1000＋0x60000＝0x61000 bytes。
        var facts = MemMmioFactsService.Collect(At, Probe(R((0x1000, 0x9F000), (0x100000, 0x1000))));
        var total = facts.Single(f => f.Key == MemMmioFactsService.TotalKey);
        Assert.Equal(FactAvailability.Present, total.Availability);
        Assert.Equal(Math.Round(0x61000 / 1024.0 / 1024 / 1024, 3), total.NumericValue);
        Assert.Contains("最高 RAM 端點以上", total.Value);
        Assert.Contains("並列不互判", total.Value);           // 與 sb.memmap.* 的交叉界線
        Assert.Null(total.UnavailableReason);

        // 逐洞家族鍵
        var hole0 = facts.Single(f => f.Key == "mem.mmio.hole.0");
        Assert.Contains("0x0–0x1000", hole0.Value);
        var hole1 = facts.Single(f => f.Key == "mem.mmio.hole.1");
        Assert.Contains("0xA0000–0x100000", hole1.Value);
        Assert.Equal(2, facts.Count(f => f.Key.StartsWith("mem.mmio.hole.")));
    }

    [Fact]
    public void 真實登錄格式的解析_二十位元組步長_只收記憶體型描述元()
    {
        // 依真機核對的佈局合成：fullCount=1、InterfaceType+BusNumber+Ver+Rev+Count、4 個描述元（2 型別3＋1 型別7＋1 型別1）。
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(1u));             // fullCount
        data.AddRange(BitConverter.GetBytes(5u));             // InterfaceType (PCIBus 之類，不解讀)
        data.AddRange(BitConverter.GetBytes(0u));             // BusNumber
        data.AddRange(BitConverter.GetBytes((ushort)0));      // Version
        data.AddRange(BitConverter.GetBytes((ushort)0));      // Revision
        data.AddRange(BitConverter.GetBytes(4u));             // descriptor count
        void Add(byte type, ulong start, uint len)
        {
            data.Add(type); data.Add(0);                      // Type、ShareDisposition
            data.AddRange(BitConverter.GetBytes((ushort)0));  // Flags
            data.AddRange(BitConverter.GetBytes(start));      // Start（u64 @+4）
            data.AddRange(BitConverter.GetBytes(len));        // Length（u32 @+12）
            data.AddRange(new byte[4]);                       // 對齊填充 → 20 bytes
        }
        Add(3, 0x1000, 0x9F000);
        Add(7, 0x1_0000_0000, 0x7A00000);                     // CmResourceTypeMemoryLarge
        Add(1, 0xDEAD, 0x1000);                               // Port 型別：跳過
        Add(3, 0x4E678000, 0x1988000);

        var ranges = MemMmioFactsService.ParseResourceList(data.ToArray());
        Assert.NotNull(ranges);
        Assert.Equal(3, ranges!.Count);
        Assert.Equal(0x1000UL, ranges[0].Start);
        Assert.Equal(0x1_0000_0000UL, ranges[1].Start);       // 型別 7 收進來
        Assert.Equal(0x4E678000UL, ranges[2].Start);
    }

    [Fact]
    public void 解析太短的資料要回null而不是硬解()
    {
        Assert.Null(MemMmioFactsService.ParseResourceList(new byte[3]));
        Assert.Null(MemMmioFactsService.ParseResourceList(Array.Empty<byte>()));
    }
}
