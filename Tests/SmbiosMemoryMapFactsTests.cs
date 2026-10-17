using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 19 記憶體映射事實的守門（ULTIMATE-1000 階段二 P2-024）。
///
/// <para>
/// <b>這一組的核心是兩個「不會有錯誤訊息」的坑：</b>
/// ①單位——32 位元欄位是 <b>KiB</b>、擴充欄位是 <b>bytes</b>，弄錯就差 1024 倍；
/// ②<code>0xFFFFFFFF</code> 旗標——它代表「改用擴充欄位」，
/// 而韌體設了旗標卻沒填擴充欄時，<b>反推成 4 GiB 會生出一個不存在的範圍</b>。
/// </para>
/// </summary>
public class SmbiosMemoryMapFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Type 19：起始@0x04、結束@0x08（皆 DWORD，KiB）、陣列控制代碼@0x0C、分割寬度@0x0E、擴充起訖@0x0F／0x17（QWORD，bytes）。</summary>
    private static SmbiosStruct Type19(uint startKib, uint endKib, ushort handle = 0x0100,
        byte width = 1, int length = 0x0F)
    {
        var d = new byte[length];
        d[0] = 19;
        d[1] = (byte)length;
        BitConverter.GetBytes(startKib).CopyTo(d, 0x04);
        BitConverter.GetBytes(endKib).CopyTo(d, 0x08);
        BitConverter.GetBytes(handle).CopyTo(d, 0x0C);
        d[0x0E] = width;
        return new SmbiosStruct(19, handle, d, []);
    }

    private static SmbiosStruct Type19Extended(ulong startBytes, ulong endBytes, ushort handle = 0x0100)
    {
        var d = new byte[0x1F];
        d[0] = 19;
        d[1] = 0x1F;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(d, 0x04);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(d, 0x08);
        BitConverter.GetBytes(handle).CopyTo(d, 0x0C);
        d[0x0E] = 2;
        BitConverter.GetBytes((uint)(startBytes & 0xFFFFFFFF)).CopyTo(d, 0x0F);
        BitConverter.GetBytes((uint)(startBytes >> 32)).CopyTo(d, 0x13);
        BitConverter.GetBytes((uint)(endBytes & 0xFFFFFFFF)).CopyTo(d, 0x17);
        BitConverter.GetBytes((uint)(endBytes >> 32)).CopyTo(d, 0x1B);
        return new SmbiosStruct(19, handle, d, []);
    }

    private static HardwareFact Fact(IReadOnlyList<SmbiosStruct> structs, string key)
        => SmbiosMemoryMapFactsService.Collect(At, () => structs).Single(f => f.Key == key);

    [Fact]
    public void 三十二位元欄位是KiB且末端含端點()
    {
        // 0x1000 KiB（4 MiB）起、0x13FF KiB 止 ⇒ 大小 (0x1400−0x1000)×1024 = 1 MiB。
        var range = Fact([Type19(0x1000, 0x13FF)], "sb.memmap.0");

        Assert.Equal(FactAvailability.Present, range.Availability);
        Assert.Contains("32 位元欄位，單位 KiB", range.Value);
        Assert.Contains("0x400000", range.Value);                // 起＝4 MiB
        Assert.Contains("0x4FFFFF", range.Value);                // 訖＝end×1024+1023（含端點）
        Assert.Contains("所屬記憶體陣列 0x0100", range.Value);
        // 1 MiB = 0.0009765625 GB → 四捨五入到三位
        Assert.Equal(0.001, range.NumericValue!.Value, 3);
    }

    [Fact]
    public void 擴充欄位是bytes_且明說走了擴充路徑()
    {
        // 4 GiB 起（超出 32 位元欄位可表達的 KiB 範圍）、+1 GiB 止。
        var range = Fact([Type19Extended(0x1_0000_0000, 0x1_4000_0000 - 1)], "sb.memmap.0");

        Assert.Equal(FactAvailability.Present, range.Availability);
        Assert.Contains("經 64 位元擴充欄位", range.Value);
        Assert.Equal(1.0, range.NumericValue!.Value, 3);
    }

    [Fact]
    public void 旗標設了卻沒有擴充欄_標不支援而不是反推四GiB()
    {
        // 長度 0x13 < 0x1F：擴充欄位不存在。
        var range = Fact([Type19(0xFFFFFFFF, 0xFFFFFFFF, length: 0x13)], "sb.memmap.0");

        Assert.Equal(FactAvailability.NotSupported, range.Availability);
        Assert.Null(range.NumericValue);
        Assert.Contains("不是 4 GiB 邊界", range.UnavailableReason);
        Assert.Contains("規格留白", range.UnavailableReason);
    }

    [Fact]
    public void 沒有Type19_兩把鍵都不適用且說缺席不代表沒有映射()
    {
        var facts = SmbiosMemoryMapFactsService.Collect(At, () => new List<SmbiosStruct>());

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
        Assert.Contains("缺席不代表記憶體沒有映射", facts[0].UnavailableReason);
        Assert.Contains("不拿其他來源的記憶體數字冒充", facts[1].UnavailableReason);
    }

    [Fact]
    public void 讀表失敗_兩把鍵都標讀取錯誤而不是沒有()
    {
        var facts = SmbiosMemoryMapFactsService.Collect(At,
            () => throw new InvalidOperationException("RSMB 讀不到"));

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("「讀不到表」與「表裡沒有 Type 19」是不同的斷言", facts[0].UnavailableReason);
    }

    [Fact]
    public void 部分解不開_合計排除它們且把差額說出來()
    {
        var structs = new List<SmbiosStruct>
        {
            Type19(0x1000, 0x13FF),                                 // 1 MiB 可用
            Type19(0xFFFFFFFF, 0xFFFFFFFF, length: 0x13),           // 解不開
        };

        var count = SmbiosMemoryMapFactsService.Collect(At, () => structs).Single(f => f.Key == "sb.memmap.count");
        var total = SmbiosMemoryMapFactsService.Collect(At, () => structs).Single(f => f.Key == "sb.memmap.total");

        Assert.Equal(2, count.NumericValue);                        // 段數照算
        Assert.Contains("其中 1 段解不開", count.Value);
        Assert.Equal(FactAvailability.Present, total.Availability);
        Assert.Equal(0.001, total.NumericValue!.Value, 3);           // 只含解開的那一段
        Assert.Contains("不以 0 計入", total.Value);
    }

    [Fact]
    public void 全部解不開_不出合計而不是出零()
    {
        var total = Fact([Type19(0xFFFFFFFF, 0xFFFFFFFF, length: 0x13)], "sb.memmap.total");

        Assert.Equal(FactAvailability.NotSupported, total.Availability);
        Assert.Null(total.NumericValue);
        Assert.Contains("沒有可加總的數字，就不出合計", total.UnavailableReason);
    }

    [Fact]
    public void 合計不等於可用記憶體_這句話必須在事實裡()
    {
        var total = Fact([Type19(0x1000, 0x13FF)], "sb.memmap.total");

        Assert.Contains("不等於可用記憶體", total.Value);
        Assert.Contains("保留區", total.Value);
        Assert.Contains("不得互相代言", total.Value);
    }

    [Fact]
    public void 每一段有自己的一把鍵_而且Present的事實不帶不可用理由()
    {
        var structs = new List<SmbiosStruct> { Type19(0x1000, 0x13FF), Type19(0x2000, 0x23FF, handle: 0x0101) };
        var facts = SmbiosMemoryMapFactsService.Collect(At, () => structs);

        Assert.Contains(facts, f => f.Key == "sb.memmap.0");
        Assert.Contains(facts, f => f.Key == "sb.memmap.1");
        Assert.All(facts.Where(f => f.Availability == FactAvailability.Present),
            f => Assert.Null(f.UnavailableReason));
    }

    [Fact]
    public void 分割寬度大於一_要說是交錯映射而不是量測()
    {
        var range = Fact([Type19(0x1000, 0x13FF, width: 4)], "sb.memmap.0");

        Assert.Contains("分割寬度 4", range.Value);
        Assert.Contains("交錯映射", range.Value);
    }
}
