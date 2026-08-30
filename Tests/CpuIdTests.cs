using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>CPUID 解碼純函式：餘 synthetic 暫存器值驗證解讀，不執行真實 CPUID。</summary>
public class CpuIdTests
{
    [Fact]
    public void 品牌字串去除尾端空白與NUL()
    {
        var raw = new byte[48];
        System.Text.Encoding.ASCII.GetBytes("Genuine Intel(R) CPU").CopyTo(raw, 0);
        Assert.Equal("Genuine Intel(R) CPU", CpuIdService.Decoder.DecodeBrand(raw));
    }

    [Fact]
    public void 品牌字串_全空白回傳破折號()
    {
        Assert.Equal("—", CpuIdService.Decoder.DecodeBrand(new byte[48]));
    }

    [Fact]
    public void 快取子葉解碼_典型L1資料快取()
    {
        // type=1（資料）level=1，EAX 25:14＝1 → 最多 2 個邏輯處理器可定址；8 路、1 分割、64B 行、64 集合 → 32 KB
        uint eax = 1 | (1u << 5) | (1u << 14);
        uint ebx = (7u << 22) | 63u;
        var row = CpuIdService.Decoder.DecodeCacheSubleaf(eax, ebx, 63, 0);
        Assert.NotNull(row);
        Assert.Equal("L1 資料", row.Level);
        Assert.Equal("32 KB", row.Capacity);
        Assert.Equal("8 路", row.Ways);
        Assert.Equal("64 B", row.Line);
        Assert.Equal("64", row.Sets);
        // 欄位數的是邏輯處理器（且向上取 2 的冪次），不是核心；措辭必須是執行緒上界
        Assert.Equal("最多 2 執行緒", row.Shared);
        // L1 之下沒有更低階快取，含括性對它無意義
        Assert.Equal("—", row.Inclusive);
    }

    [Fact]
    public void 快取子葉_含括性只看EDX位1_不看位0()
    {
        // level=3（L3）、EAX 25:14＝63 → 最多 64 個邏輯處理器（18C/36T 機器實測值）
        uint eax = 3 | (3u << 5) | (63u << 14);
        uint ebx = (10u << 22) | 63u;

        // EDX＝0：位 1 為 0 → 非含括。1.6.2 之前誤用位 0 當判據，於此會顯示「—」
        var noninc = CpuIdService.Decoder.DecodeCacheSubleaf(eax, ebx, 1023, 0);
        Assert.NotNull(noninc);
        Assert.Equal("最多 64 執行緒", noninc.Shared);
        Assert.Equal("非含括", noninc.Inclusive);

        // 只設位 0（WBINVD／INVD 行為）不該讓它變成含括
        var wbinvdOnly = CpuIdService.Decoder.DecodeCacheSubleaf(eax, ebx, 1023, 0x1);
        Assert.Equal("非含括", wbinvdOnly!.Inclusive);

        // 設位 1 才是含括下層
        var inclusive = CpuIdService.Decoder.DecodeCacheSubleaf(eax, ebx, 1023, 0x2);
        Assert.Equal("含括", inclusive!.Inclusive);
    }

    [Fact]
    public void 快取子葉_type為0表示列舉終止()
    {
        Assert.Null(CpuIdService.Decoder.DecodeCacheSubleaf(0, 0, 0, 0));
    }

    [Fact]
    public void 位址位元_實體36虛擬48()
    {
        Assert.Equal("36 / 48 bits", CpuIdService.Decoder.DecodeAddressBits(36u | (48u << 8)));
    }

    [Fact]
    public void 拓樸子葉_核心層數的是每封裝邏輯處理器數()
    {
        // 18 核 36 執行緒：核心層的 EBX 15:0 ＝ 36（每封裝的邏輯處理器數），不是 36 個核心
        var row = CpuIdService.Decoder.DecodeTopologySubleaf(6, 36, 2u << 8, 1);
        Assert.NotNull(row);
        Assert.Equal("核心層", row.Level);
        Assert.Equal("36 個邏輯處理器", row.Count);
        Assert.Equal("APIC ID 右移 6 bits", row.Shift);
    }

    [Fact]
    public void 拓樸子葉_SMT層數的是每核心執行緒數()
    {
        var row = CpuIdService.Decoder.DecodeTopologySubleaf(1, 2, 1u << 8, 0);
        Assert.NotNull(row);
        Assert.Equal("執行緒（SMT）層", row.Level);
        Assert.Equal("2 個邏輯處理器", row.Count);
    }

    [Fact]
    public void 拓樸子葉_計數為0表示終止()
    {
        Assert.Null(CpuIdService.Decoder.DecodeTopologySubleaf(0, 0, 0, 2));
    }

    [Fact]
    public void 混合架構類型解碼()
    {
        Assert.Equal("—", CpuIdService.Decoder.DecodeHybrid(0));
        Assert.Contains("E-core", CpuIdService.Decoder.DecodeHybrid(0x20 | (0x5Cu << 8)));
        Assert.Contains("P-core", CpuIdService.Decoder.DecodeHybrid(0x40 | (0x97u << 8)));
    }

    [Fact]
    public void 標稱頻率解碼_leaf16()
    {
        Assert.Equal("基準 2200 MHz ・ 最大 4400 MHz ・ 匯流排 100 MHz",
            CpuIdService.Decoder.DecodeFreq16(2200, 4400, 100));
        Assert.Equal("—", CpuIdService.Decoder.DecodeFreq16(0, 0, 0));
    }

    [Fact]
    public void 標稱頻率解碼_leaf15由晶振與比值推得()
    {
        // 24 MHz 晶振 × 175/2 = 2.1 GHz
        var s = CpuIdService.Decoder.DecodeFreq15(2, 175, 24_000_000);
        Assert.Contains("GHz", s);
    }

    [Fact]
    public void 功能位元表_四元組與名稱皆不重複()
    {
        var keys = CpuIdService.Decoder.FeatureTable.Select(f => (f.Leaf, f.SubLeaf, f.Reg, f.Bit)).ToHashSet();
        Assert.Equal(CpuIdService.Decoder.FeatureTable.Length, keys.Count);

        var names = CpuIdService.Decoder.FeatureTable.Select(f => f.Name).ToHashSet();
        Assert.Equal(CpuIdService.Decoder.FeatureTable.Length, names.Count);
    }

    // ── 以下皆為本機（i9-7980XE）實測的原始暫存器值，不是編造的合成值 ──────────

    [Fact]
    public void 熱能電源葉_本機實測沒有HWP()
    {
        // 本機實測 0x06：EAX=0x00000077 EBX=0x00000002 ECX=0x00000009
        var rows = CpuIdService.Decoder.DecodePower(0x77, 0x02, 0x09);
        string caps = rows.First(r => r.Key.Contains("已回報的能力")).Value;
        Assert.Contains("數位溫度感測器（DTS）", caps);
        Assert.Contains("Turbo Boost", caps);
        Assert.Contains("封裝熱管理（PTM）", caps);
        Assert.DoesNotContain("硬體 P-state（HWP）", caps);   // 位 7 為 0，不能列進「已回報的能力」

        Assert.Contains("不支援", rows.First(r => r.Key == "硬體 P-state（HWP）").Value);
        Assert.Contains("2 個", rows.First(r => r.Key.Contains("溫度中斷閾值數")).Value);
        Assert.Contains("支援：MPERF", rows.First(r => r.Key.Contains("硬體協調回饋")).Value);
        Assert.Contains("支援 IA32_ENERGY_PERF_BIAS", rows.First(r => r.Key.Contains("能耗偏好")).Value);
    }

    [Fact]
    public void 熱能電源葉_全零時不假裝有能力()
    {
        var rows = CpuIdService.Decoder.DecodePower(0, 0, 0);
        Assert.Contains("—（EAX 為 0）", rows.First(r => r.Key.Contains("已回報的能力")).Value);
        Assert.Contains("不支援", rows.First(r => r.Key.Contains("硬體協調回饋")).Value);
    }

    [Fact]
    public void PMU葉_本機實測為版本四與四加三個計數器()
    {
        // 本機實測 0x0A：EAX=0x07300404 EBX=0 EDX=0x00000603
        var rows = CpuIdService.Decoder.DecodePmu(0x07300404, 0, 0x0603);
        Assert.Equal("版本 4", rows.First(r => r.Key == "架構 PMU 版本").Value);
        Assert.Equal("每邏輯處理器 4 個 × 48 位元", rows.First(r => r.Key == "通用計數器").Value);
        Assert.Equal("3 個 × 48 位元", rows.First(r => r.Key == "固定功能計數器").Value);
    }

    [Fact]
    public void PMU葉_版本零時明說不能用而不是列出零個計數器()
    {
        var rows = CpuIdService.Decoder.DecodePmu(0, 0, 0);
        Assert.Single(rows);
        Assert.Contains("版本 0", rows[0].Value);
        Assert.Contains("無法運作", rows[0].Value);
    }

    [Fact]
    public void 架構事件_EBX的位為一代表不可用()
    {
        // 本機實測 EBX=0：七個事件全部可用。位設起來才是「不可用」——反過來讀會把 0 說成 1。
        Assert.Contains("全部可用", CpuIdService.Decoder.DescribeArchEvents(0, 7));
        Assert.Contains("不可用：核心週期", CpuIdService.Decoder.DescribeArchEvents(0b1, 7));
        Assert.Contains("LLC 失誤", CpuIdService.Decoder.DescribeArchEvents(1u << 4, 7));
    }

    [Fact]
    public void 架構事件_位向量長度為零時不列舉()
        => Assert.Equal("—（EBX 位向量長度為 0）", CpuIdService.Decoder.DescribeArchEvents(0xFF, 0));

    [Fact]
    public void 架構事件_不超出位向量宣告的長度()
    {
        // 宣告長度 7，第八個事件（Topdown 插槽）即使位被設起來也不該被列出
        Assert.DoesNotContain("Topdown", CpuIdService.Decoder.DescribeArchEvents(1u << 7, 7));
    }

    [Fact]
    public void XSAVE主葉_本機實測為八個元件與二六八八位元組()
    {
        // 本機實測 0x0D/0：EAX=0x000000FF EBX=0x00000A80 ECX=0x00000A80 EDX=0
        var row = CpuIdService.Decoder.DecodeXSaveMain(0xFF, 0, 0x0A80, 0x0A80);
        Assert.Contains("x87 FPU", row.Value);
        Assert.Contains("AVX-512 zmm16–31", row.Value);   // 位 7
        Assert.Contains("2,688", row.Value);
        Assert.DoesNotContain("PKRU", row.Value);         // 位 9 為 0
    }

    [Fact]
    public void XSAVE主葉_位圖為零時不列元件()
        => Assert.Contains("位圖為 0", CpuIdService.Decoder.DecodeXSaveMain(0, 0, 0, 0).Value);

    [Fact]
    public void XSAVE子葉一_本機實測四種變體與監督者PT狀態()
    {
        // 本機實測 0x0D/1：EAX=0x0000000F EBX=0x00000A80 ECX=0x00000100
        var row = CpuIdService.Decoder.DecodeXSaveExtras(0x0F, 0x0A80, 0x0100);
        Assert.Contains("XSAVEOPT", row.Value);
        Assert.Contains("XSAVES／XRSTORS", row.Value);
        Assert.DoesNotContain("XFD", row.Value);                      // 位 4 為 0
        Assert.Contains("Processor Trace", row.Value);                // ECX 位 8
    }

    [Fact]
    public void XSAVE元件_本機實測AVX與MPX的大小位移()
    {
        // 本機實測 0x0D/2：EAX=0x100 EBX=0x240；0x0D/3：EAX=0x40 EBX=0x3C0
        var avx = CpuIdService.Decoder.DecodeXSaveComponent(2, 0x100, 0x240, 0);
        Assert.NotNull(avx);
        Assert.Contains("AVX（YMM 高 128 位）", avx!.Key);
        Assert.Contains("256 位元組", avx.Value);
        Assert.Contains("位移 576", avx.Value);

        var bnd = CpuIdService.Decoder.DecodeXSaveComponent(3, 0x40, 0x3C0, 0);
        Assert.Contains("位移 960", bnd!.Value);
    }

    [Fact]
    public void XSAVE元件_大小為零視為未支援而不是零位元組元件()
        => Assert.Null(CpuIdService.Decoder.DecodeXSaveComponent(9, 0, 0, 0));

    [Fact]
    public void XSAVE元件_監督者狀態不報位移()
    {
        var row = CpuIdService.Decoder.DecodeXSaveComponent(8, 0x80, 0x1234, 1);
        Assert.Contains("監督者狀態", row!.Value);
        Assert.DoesNotContain("位移", row.Value);
    }

    [Fact]
    public void 恆定TSC_本機實測為支援()
    {
        // 本機實測 0x80000007：EDX=0x00000100（位 8）
        Assert.Contains("支援", CpuIdService.Decoder.DecodeInvariantTsc(0x100));
        string no = CpuIdService.Decoder.DecodeInvariantTsc(0);
        Assert.Contains("不支援", no);
        Assert.Contains("不可信", no);
    }

    [Fact]
    public void 拓樸交叉驗證_本機實測十八核三十六執行緒為一致()
    {
        string s = CpuIdService.Decoder.CrossCheckTopology(2, 36, 18, 36);
        Assert.StartsWith("一致（推得 1 個封裝）", s);
        Assert.DoesNotContain("⚠", s);
    }

    [Fact]
    public void 拓樸交叉驗證_雙插槽也算一致()
        => Assert.Contains("2 個封裝", CpuIdService.Decoder.CrossCheckTopology(2, 36, 36, 72));

    [Fact]
    public void 拓樸交叉驗證_核心數對不上時明說不一致()
    {
        string s = CpuIdService.Decoder.CrossCheckTopology(2, 36, 16, 36);
        Assert.Contains("⚠ 不一致", s);
        Assert.Contains("應有 18 顆實體核心", s);
    }

    [Fact]
    public void 拓樸交叉驗證_缺資料時不做推論而不是說一致()
    {
        Assert.StartsWith("—", CpuIdService.Decoder.CrossCheckTopology(0, 0, 0, 0));
        Assert.StartsWith("—", CpuIdService.Decoder.CrossCheckTopology(2, 36, 18, 0));
        Assert.StartsWith("—", CpuIdService.Decoder.CrossCheckTopology(4, 2, 18, 36));   // 每封裝數小於每核心數
    }

    [Fact]
    public void 拓樸交叉驗證_邏輯處理器數非整數倍時明說可能被停用()
        => Assert.Contains("整數倍", CpuIdService.Decoder.CrossCheckTopology(2, 36, 17, 34 + 1));
}
