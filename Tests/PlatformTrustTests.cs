using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>平台可信度解碼的純函式測試。合成值與本機實測值並用，不接觸硬體。</summary>
public class PlatformTrustTests
{
    [Fact]
    public void 簽章由三個暫存器併成十二位元組()
    {
        // "Microsoft Hv" = 4D 69 63 72 | 6F 73 6F 66 | 74 20 48 76（小端逐暫存器）
        Assert.Equal("Microsoft Hv", PlatformTrustDecoder.HypervisorSignature(0x7263694D, 0x666F736F, 0x76482074));
        Assert.Equal("Microsoft Hyper-V", PlatformTrustDecoder.HypervisorVendor("Microsoft Hv"));
    }

    [Fact]
    public void 未知簽章原樣呈現而不硬猜廠商()
        => Assert.Equal("ZZZZ", PlatformTrustDecoder.HypervisorVendor("ZZZZ"));

    [Fact]
    public void 空簽章不假裝有廠商()
        => Assert.Equal("—（簽章為空）", PlatformTrustDecoder.HypervisorVendor(""));

    [Fact]
    public void VBS三態與讀不到分開表達()
    {
        Assert.Equal("未啟用", PlatformTrustDecoder.DescribeVbsStatus(0));
        Assert.Equal("已啟用但未執行", PlatformTrustDecoder.DescribeVbsStatus(1));
        Assert.Equal("已啟用並正在執行", PlatformTrustDecoder.DescribeVbsStatus(2));
        Assert.Equal("—（讀不到）", PlatformTrustDecoder.DescribeVbsStatus(null));   // null 不能被寫成「未啟用」
    }

    [Fact]
    public void 本機實測的安全服務清單為僅含零()
    {
        // 本機實測 SecurityServicesRunning = [0]：代表沒有任何服務在跑，不是「有一項代碼 0 的服務」。
        Assert.Equal("無（空清單或僅含 0）", PlatformTrustDecoder.DescribeServices([0]));
        Assert.Equal("無（空清單或僅含 0）", PlatformTrustDecoder.DescribeServices([]));
        Assert.Equal("無（空清單或僅含 0）", PlatformTrustDecoder.DescribeServices(null));
    }

    [Fact]
    public void 安全服務代碼逐項命名()
        => Assert.Equal("Credential Guard、記憶體完整性（HVCI）", PlatformTrustDecoder.DescribeServices([1, 2]));

    [Fact]
    public void 未知服務代碼如實標示而非略過()
        => Assert.Contains("未知代碼 99", PlatformTrustDecoder.DescribeServices([99]));

    [Fact]
    public void 可用安全屬性與讀不到分開()
    {
        Assert.Equal("Hypervisor 支援、Secure Boot", PlatformTrustDecoder.DescribeProperties([1, 2]));
        Assert.Equal("—（讀不到或為空）", PlatformTrustDecoder.DescribeProperties([]));
    }

    [Fact]
    public void 本機實測的程式碼完整性選項為啟用加UMCI()
    {
        // 本機實測 NtQSI(103) raw=0800000005000000 → Length=8、Options=0x05。
        string s = PlatformTrustDecoder.DescribeCodeIntegrity(0x05);
        Assert.Contains("已啟用", s);
        Assert.Contains("使用者模式程式碼完整性（UMCI）", s);
        Assert.DoesNotContain("測試簽章", s);
        Assert.DoesNotContain("HVCI", s);
    }

    [Fact]
    public void 測試簽章與核心除錯會被列出()
    {
        string s = PlatformTrustDecoder.DescribeCodeIntegrity(0x0001 | 0x0002 | 0x0080);
        Assert.Contains("測試簽章模式（testsigning）", s);
        Assert.Contains("核心除錯模式", s);
    }

    [Fact]
    public void HVCI核心模式旗標可被辨識()
        => Assert.Contains("HVCI 核心模式已啟用", PlatformTrustDecoder.DescribeCodeIntegrity(0x0401));

    [Fact]
    public void 選項為零時說未啟用而不是空字串()
        => Assert.Equal("未啟用（Options 為 0）", PlatformTrustDecoder.DescribeCodeIntegrity(0));

    [Fact]
    public void 旗標表不重複()
    {
        var flags = PlatformTrustDecoder.CodeIntegrityFlags.Select(f => f.Flag).ToHashSet();
        Assert.Equal(PlatformTrustDecoder.CodeIntegrityFlags.Length, flags.Count);
    }

    [Fact]
    public void 本機結論為裸機原生讀值()
    {
        // 本機實測：hypervisor 位 = 0、VBS 狀態 = 0、CPUID 0x80000007 EDX 位 8 = 1。
        string v = PlatformTrustDecoder.Verdict(false, 0, true);
        Assert.Contains("裸機執行", v);
        Assert.Contains("原生讀值", v);
        Assert.DoesNotContain("⚠", v);
    }

    [Fact]
    public void 有hypervisor時明說MSR只能當參考()
    {
        string v = PlatformTrustDecoder.Verdict(true, 2, true);
        Assert.Contains("⚠", v);
        Assert.Contains("Hyper-V 上的一個分割區", v);
        Assert.Contains("只能當參考", v);
    }

    [Fact]
    public void 有hypervisor但VBS未啟用時歸因於別處()
        => Assert.Contains("hypervisor 來自別處", PlatformTrustDecoder.Verdict(true, 0, true));

    [Fact]
    public void 沒有恆定TSC時明說時間換算不可信()
    {
        string v = PlatformTrustDecoder.Verdict(false, 0, false);
        Assert.Contains("沒有 Invariant TSC", v);
        Assert.Contains("不可信", v);
    }
}
