using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// WHEA 節區知識包（U10 第四圈批次一）的守門：唯一分母、GUID 逐字對照、
/// 「未收錄不猜」政策、摘要數字與明細對帳。
/// </summary>
public class WheaSectionKnowledgeTests
{
    private static HardwareFact Fact(List<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static List<HardwareFact> Collect() =>
        WheaSectionFactsService.Collect(DateTimeOffset.UnixEpoch).ToList();

    [Fact]
    public void 知識包唯一分母_十五節區八通知九埠型別()
    {
        // 這三個數字是本知識包的「唯一分母」——出處表的筆數，多一條少一條都要回到出處解釋。
        Assert.Equal(15, WheaSectionKnowledge.Sections.Count);
        Assert.Equal(8, WheaSectionKnowledge.NotifyTypes.Count);
        Assert.Equal(9, WheaSectionKnowledge.PciePortTypes.Count);
    }

    [Fact]
    public void 抽樣四條GUID逐字對照()
    {
        // 逐字對照是防「抄表抄錯一個字元」的守門——GUID 一字元之差就是另一個節區。
        Assert.Contains(WheaSectionKnowledge.Sections, s =>
            s.GuidText == "A5BC1114-6F64-4EDE-B863-3E83ED7C83B1" && s.Name.Contains("Platform Memory"));
        Assert.Contains(WheaSectionKnowledge.Sections, s =>
            s.GuidText == "D995E954-BBC1-430F-AD91-B44DCB3C6F35" && s.Name.Contains("PCIExpress"));
        Assert.Contains(WheaSectionKnowledge.Sections, s =>
            s.GuidText == "EB5E4685-CA66-4769-B6A2-26068B001326" && s.Name.Contains("PCI Component/Device"));
        Assert.Contains(WheaSectionKnowledge.NotifyTypes, n =>
            n.GuidText == "2DCE8BB1-BDD7-450E-B9AD-9CF4EBD4F890" && n.Name.Contains("CMC"));
    }

    [Fact]
    public void 查表不分大小寫且容忍大括號()
    {
        Assert.Equal("Platform Memory（平台記憶體）", WheaSectionKnowledge.SectionName("a5bc1114-6f64-4ede-b863-3e83ed7c83b1"));
        Assert.Equal("Platform Memory（平台記憶體）", WheaSectionKnowledge.SectionName("{A5BC1114-6F64-4EDE-B863-3E83ED7C83B1}"));
        Assert.Equal("PCIe—PCI Express Error（PCI Express 錯誤）", WheaSectionKnowledge.NotifyName("{cf93c01f-1a16-4dfc-b8bc-9c4daf67c104}"));
    }

    [Fact]
    public void 未知GUID回null不猜()
    {
        // U10-WH-0013 政策：知識包未收錄的 GUID，語意就是「未收錄」——回 null，絕不猜一個像樣的名字。
        Assert.Null(WheaSectionKnowledge.SectionName("00000000-0000-0000-0000-000000000000"));
        Assert.Null(WheaSectionKnowledge.SectionName(null));
        Assert.Null(WheaSectionKnowledge.SectionName(""));
        Assert.Null(WheaSectionKnowledge.NotifyName("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));
    }

    [Fact]
    public void IA64故意不收進表()
    {
        // Linux cper.h 的 CPER_SEC_PROC_IPF 巨集呼叫位元組參數 9 個（GUID_INIT 只收 8 個）——
        // 位元組序列不可信，本版不命名其 GUID；這條釘住「表裡沒有它」是決定，不是遺漏。
        Assert.DoesNotContain(WheaSectionKnowledge.Sections, s => s.Name.Contains("IA64"));
        Assert.DoesNotContain(WheaSectionKnowledge.Sections, s => s.GuidText.StartsWith("E429FAF1"));
    }

    [Fact]
    public void PCIe埠型別_未定義碼回null()
    {
        Assert.Equal("Endpoint（端點）", WheaSectionKnowledge.PciePortName(0));
        Assert.Equal("Root Port（根埠）", WheaSectionKnowledge.PciePortName(4));
        Assert.Equal("PCIe-to-PCI-X Bridge（PCIe 轉 PCI-X 橋）", WheaSectionKnowledge.PciePortName(7));
        // 2／3 在 SDK 列舉中未定義——不猜。
        Assert.Null(WheaSectionKnowledge.PciePortName(2));
        Assert.Null(WheaSectionKnowledge.PciePortName(3));
        Assert.Null(WheaSectionKnowledge.PciePortName(13));
    }

    [Fact]
    public void 摘要數字與明細對帳()
    {
        var facts = Collect();
        var summary = Fact(facts, WheaSectionFactsService.SummaryKey);
        Assert.True(summary.NumericValue == WheaSectionKnowledge.Sections.Count,
            "summary 的數字必須是節區表筆數（由 Count 算出，不得手寫常數）");
        Assert.Contains($"收錄 {WheaSectionKnowledge.Sections.Count} 條節區型別 GUID", summary.Value);
        Assert.Contains($"{WheaSectionKnowledge.NotifyTypes.Count} 條通知類型", summary.Value);
        Assert.Contains($"{WheaSectionKnowledge.PciePortTypes.Count} 格 PCIe 埠型別碼", summary.Value);
    }

    [Fact]
    public void 節區表值包含全部十五條GUID()
    {
        var facts = Collect();
        var sections = Fact(facts, WheaSectionFactsService.SectionsKey);
        foreach (var s in WheaSectionKnowledge.Sections)
            Assert.Contains(s.GuidText, sections.Value);
        var notify = Fact(facts, WheaSectionFactsService.NotifyKey);
        foreach (var n in WheaSectionKnowledge.NotifyTypes)
            Assert.Contains(n.GuidText, notify.Value);
    }

    [Fact]
    public void IPF遺產列是NotApplicable且說明含待核()
    {
        var facts = Collect();
        var ipf = Fact(facts, WheaSectionFactsService.IpfLegacyKey);
        Assert.Equal(FactAvailability.NotApplicable, ipf.Availability);
        Assert.True(ipf.NumericValue is null, "非 Present 的事實不得帶數字");
        Assert.Contains("待核", ipf.UnavailableReason);
        Assert.Contains("GUID_INIT", ipf.UnavailableReason);
    }

    [Fact]
    public void 界線鍵Present且值含四條界線()
    {
        var facts = Collect();
        var boundary = Fact(facts, WheaSectionFactsService.BoundaryKey);
        Assert.Equal(FactAvailability.Present, boundary.Availability);
        Assert.Contains("不拆 blob", boundary.Value);
        Assert.Contains("未收錄的 GUID 政策", boundary.Value);
        Assert.Contains("沒有解碼實作", boundary.Value);
    }

    [Fact]
    public void 全部鍵都是繁中值且無簡體字元()
    {
        var facts = Collect();
        var simplifiedSentinels = new[] { "处", "络", "检", "认", "应", "发", "译", "这", "关", "数" };
        foreach (var f in facts)
        {
            foreach (var text in new[] { f.Value, f.Name, f.UnavailableReason ?? "" })
                foreach (var s in simplifiedSentinels)
                    Assert.False(text.Contains(s), $"{f.Key} 出現簡體字「{s}」：{text[..Math.Min(60, text.Length)]}");
        }
    }

    [Fact]
    public void 六把鍵一次到齊()
    {
        var facts = Collect();
        var keys = facts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, facts.Count);
        Assert.Contains(WheaSectionFactsService.SummaryKey, keys);
        Assert.Contains(WheaSectionFactsService.SectionsKey, keys);
        Assert.Contains(WheaSectionFactsService.NotifyKey, keys);
        Assert.Contains(WheaSectionFactsService.PcieDevTypeKey, keys);
        Assert.Contains(WheaSectionFactsService.IpfLegacyKey, keys);
        Assert.Contains(WheaSectionFactsService.BoundaryKey, keys);
    }
}
