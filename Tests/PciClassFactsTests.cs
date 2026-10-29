using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 類別碼三層解碼（U10-PC-0004，第 317 輪）：這一支的價值全在「分層」——
/// 同一個「序列匯流排／USB」，progIF 0x20 是 EHCI、0x30 是 XHCI，混成一個名字就分不出來了。
/// 測試因此盯三件事：三層各自查表、查不到就說查不到、以及**不拿別層的名字頂替**。
/// </summary>
public class PciClassFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 11, 0, 0, TimeSpan.Zero);

    private const uint UsbXhci = 0x0C03_3000;   // 序列匯流排／USB／XHCI
    private const uint Vga = 0x0300_0000;       // 顯示／VGA 相容顯示／progIF 0
    private const uint UnknownBase = 0x1200_0000;// 基礎類別 0x12（對照表未收錄）

    private static PciClassFactsService.Sample S(byte dev, byte fn, uint raw)
        => new(dev, fn, raw);

    private static HardwareFact One(IReadOnlyList<HardwareFact> facts, string key)
        => Assert.Single(facts, f => f.Key == key);

    [Fact]
    public void 三層都有名稱時要三層都寫出來()
    {
        var facts = PciClassFactsService.Collect([S(0x14, 0, UsbXhci)], At);
        var fact = One(facts, PciClassFactsService.DevicePrefix + "14.0");

        Assert.Contains("基礎類別 0x0C「序列匯流排控制器」", fact.Value);
        Assert.Contains("子類別 0x03「USB」", fact.Value);
        Assert.Contains("progIF 0x30「XHCI（USB 3.x 主機控制器）」", fact.Value);
        Assert.Equal(3, fact.NumericValue);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    [Fact]
    public void progIF沒收錄時只說沒收錄而且不得拿子類別頂替()
    {
        var fact = One(PciClassFactsService.Collect([S(2, 0, Vga)], At),
            PciClassFactsService.DevicePrefix + "02.0");

        Assert.Contains("progIF 0x00「本版沒有這一層的名稱，只報原值」", fact.Value);
        Assert.Equal(2, fact.NumericValue);
        // progIF 那一段不得出現子類別的名字
        int progAt = fact.Value.IndexOf("progIF", StringComparison.Ordinal);
        Assert.True(progAt > 0);
        Assert.DoesNotContain("VGA 相容顯示", fact.Value[progAt..]);
    }

    [Fact]
    public void 基礎類別沒收錄時三層都要標沒收錄且層數為一()
    {
        var fact = One(PciClassFactsService.Collect([S(0x1F, 5, UnknownBase)], At),
            PciClassFactsService.DevicePrefix + "1f.5");
        Assert.Equal(1, fact.NumericValue);
        Assert.Contains("基礎類別 0x12「本版沒有這一層的名稱，只報原值」", fact.Value);
    }

    [Fact]
    public void revision要寫出來()
    {
        var fact = One(PciClassFactsService.Collect([S(0, 0, 0x0C033001u)], At),
            PciClassFactsService.DevicePrefix + "00.0");
        Assert.Contains("revision 0x01", fact.Value);
    }

    [Fact]
    public void 總覽的三個數字要與逐功能事實相符()
    {
        var samples = new[] { S(0x14, 0, UsbXhci), S(2, 0, Vga), S(0x1F, 5, UnknownBase) };
        var facts = PciClassFactsService.Collect(samples, At);
        var summary = One(facts, PciClassFactsService.SummaryKey);

        int full = facts.Count(f => f.Key.StartsWith(PciClassFactsService.DevicePrefix, StringComparison.Ordinal)
                                    && f.NumericValue == 3);
        int toSub = facts.Count(f => f.Key.StartsWith(PciClassFactsService.DevicePrefix, StringComparison.Ordinal)
                                     && f.NumericValue == 2);

        Assert.Equal(3, summary.NumericValue);
        Assert.Contains($"三層都有名稱 {full} 個", summary.Value);
        Assert.Contains($"只到子類別 {toSub} 個", summary.Value);
        Assert.Contains("只到基礎類別 1 個", summary.Value);
        Assert.Equal(1, full);
        Assert.Equal(1, toSub);
    }

    [Fact]
    public void 讀取失敗要寫進總覽且不併進三個分類數字()
    {
        var summary = One(PciClassFactsService.Collect([S(0x14, 0, UsbXhci)], At, readErrors: 2),
            PciClassFactsService.SummaryKey);

        Assert.Contains("另有 2 個功能的類別碼讀取失敗", summary.Value);
        Assert.Contains("1 個功能", summary.Value);
        Assert.Equal(1, summary.NumericValue);
    }

    [Fact]
    public void 沒有樣本時是NotApplicable而不是解碼失敗()
    {
        var summary = One(PciClassFactsService.Collect([], At), PciClassFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotApplicable, summary.Availability);
        Assert.Contains("不是解碼失敗", summary.UnavailableReason);
        Assert.Equal("", summary.Value);
        Assert.Null(summary.NumericValue);
    }

    [Fact]
    public void 六態契約_Present不帶理由_其餘必帶理由()
    {
        foreach (var facts in new[]
                 {
                     PciClassFactsService.Collect([S(0x14, 0, UsbXhci)], At),
                     PciClassFactsService.Collect([], At),
                 })
        {
            foreach (var fact in facts)
            {
                if (fact.Availability == FactAvailability.Present) Assert.Null(fact.UnavailableReason);
                else Assert.False(string.IsNullOrWhiteSpace(fact.UnavailableReason));
            }
        }
    }

    [Fact]
    public void 鍵形狀與家族登記()
    {
        var facts = PciClassFactsService.Collect([S(0x0E, 0, UsbXhci)], At);
        var key = facts.First(f => f.Key.StartsWith(PciClassFactsService.DevicePrefix, StringComparison.Ordinal)).Key;
        Assert.Equal("pci.class.dev.0e.0", key);
        Assert.True(FactKeyDynamicCatalog.Matches(key));
    }

    [Fact]
    public void progIF表只收錄有據的幾格其餘一律null()
    {
        Assert.Equal("UHCI（USB 1.x 主機控制器）", PciKnowledge.ProgIfName(0x0C, 0x03, 0x00));
        Assert.Equal("OHCI（USB 1.x 主機控制器）", PciKnowledge.ProgIfName(0x0C, 0x03, 0x10));
        Assert.Equal("EHCI（USB 2.0 主機控制器）", PciKnowledge.ProgIfName(0x0C, 0x03, 0x20));
        Assert.Equal("XHCI（USB 3.x 主機控制器）", PciKnowledge.ProgIfName(0x0C, 0x03, 0x30));
        Assert.Equal("NVMe（NVM Express）", PciKnowledge.ProgIfName(0x01, 0x08, 0x02));
        Assert.Equal("AHCI 1.0", PciKnowledge.ProgIfName(0x01, 0x06, 0x01));

        // 未收錄一律 null：這是「本版沒有名稱」的來源，不是「裝置沒有宣告」。
        Assert.Null(PciKnowledge.ProgIfName(0x0C, 0x03, 0x40));
        Assert.Null(PciKnowledge.ProgIfName(0x03, 0x00, 0x00));
        Assert.Null(PciKnowledge.ProgIfName(0x01, 0x08, 0x09));
        Assert.Null(PciKnowledge.ProgIfName(0x12, 0x00, 0x00));
    }

    [Fact]
    public void progIF表的格數由掃描算出來()
    {
        // 6 格（USB 4＋NVMe 2＋AHCI 1）；表一改這個數字跟著動，不用手抄。
        Assert.Equal(7, PciKnowledge.ProgIfCount());
    }

    [Fact]
    public void ReadClasses要走同一趟走訪而且缺權限要回理由()
    {
        var (samples, readErrors, reason) = Bus0InventoryService.ReadClasses(new FakePci());

        Assert.Null(reason);
        Assert.Equal(0, readErrors);
        // 0:02.0（顯示）、0:14.0（USB 3）、0:1F.0 與 0:1F.5（多功能）——fn3 不該出現
        Assert.Equal(new[] { "02.0", "14.0", "1f.0", "1f.5" },
            samples.Select(s => $"{s.Device:x2}.{s.Function}").OrderBy(x => x, StringComparer.Ordinal).ToArray());

        var (_, _, denied) = Bus0InventoryService.ReadClasses(new DeniedPci());
        Assert.Contains("ring0", denied);
    }

    private sealed class FakePci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register)
        {
            if (bus != 0) return 0xFFFF_FFFF;
            return (device, function, register) switch
            {
                (0x02, 0, 0x00) => 0x3EA0_8086,
                (0x02, 0, 0x08) => 0x0300_0000u,
                (0x02, 0, 0x0C) => 0x0000_0000,
                (0x14, 0, 0x00) => 0xA36D_8086,
                (0x14, 0, 0x08) => 0x0C03_3000u,
                (0x14, 0, 0x0C) => 0x0000_0000,
                (0x1F, 0, 0x00) => 0x06D1_8086,
                (0x1F, 0, 0x08) => 0x0601_0000u,
                (0x1F, 0, 0x0C) => 0x0080_0000,
                (0x1F, 5, 0x00) => 0x06C0_8086,
                (0x1F, 5, 0x08) => 0xFF00_0000u,
                (_, _, var r) when r is >= 0x10 and <= 0x30 => 0x0000_0000,
                _ => 0xFFFF_FFFF,
            };
        }
    }

    private sealed class DeniedPci : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }
}
