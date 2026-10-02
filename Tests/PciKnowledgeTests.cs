using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// PCI 知識層（WP30）的契約：類別碼 dword 解析、規格有據的角色／廠商名、未收錄誠實標、
/// 描述行全部段落可稽核。覆蓋檢查同時升級（PciKnowledge 進 SpecRefRegistry）。
/// </summary>
public class PciKnowledgeTests
{
    [Fact]
    public void 類別碼dword解析_欄位位置照PCI規格()
    {
        // SMBus 控制器：base 0x0C、sub 0x05、progIF 0x00、rev 0x01
        var (b, s, p, r) = PciKnowledge.DecodeClassCode(0x0C050001u);
        Assert.Equal((byte)0x0C, b);
        Assert.Equal((byte)0x05, s);
        Assert.Equal((byte)0x00, p);
        Assert.Equal((byte)0x01, r);
    }

    [Fact]
    public void 常見類別碼有名字_未收錄的subclass只報base()
    {
        Assert.Equal("序列匯流排控制器", PciKnowledge.BaseClassName(0x0C));
        Assert.Equal("SMBus", PciKnowledge.SubClassName(0x0C, 0x05));
        Assert.Equal("NVMe（非揮發性記憶體）", PciKnowledge.SubClassName(0x01, 0x08));
        Assert.Equal("Host Bridge（主機橋）", PciKnowledge.SubClassName(0x06, 0x00));
        Assert.Null(PciKnowledge.BaseClassName(0x12));      // 未收錄的 base class
        Assert.Null(PciKnowledge.SubClassName(0x02, 0x99)); // base 收錄、sub 未收錄
    }

    [Fact]
    public void 廠商ID知名子集有名字_未收錄誠實標()
    {
        Assert.Equal("Intel", PciKnowledge.VendorName(0x8086));
        Assert.Equal("AMD", PciKnowledge.VendorName(0x1022));
        Assert.Null(PciKnowledge.VendorName(0xABCD));
    }

    [Fact]
    public void 描述行_角色廠商與原始碼並列可稽核()
    {
        var ids = PciKnowledge.DecodeVendorDevice(0x15B8_8086);
        var cls = PciKnowledge.DecodeClassCode(0x02000000u);
        string text = PciKnowledge.Describe(ids, cls);

        Assert.Contains("網路控制器", text);
        Assert.Contains("Ethernet", text);
        Assert.Contains("Intel", text);
        Assert.Contains("0x8086:0x15B8", text);   // 原始 ID 保留（稽核面）
        Assert.Contains("類別碼 0x0200", text);
    }

    [Fact]
    public void 描述行_未收錄項目如實標示不猜()
    {
        var ids = PciKnowledge.DecodeVendorDevice(0x1234_ABCD);
        var cls = PciKnowledge.DecodeClassCode(0xFF000000u);
        string text = PciKnowledge.Describe(ids, cls);

        Assert.Contains("未分類", text);
        Assert.Contains("Vendor 0xABCD（未收錄）", text);
        Assert.DoesNotContain("Intel", text);
    }
}
