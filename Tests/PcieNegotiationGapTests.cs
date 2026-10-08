using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// PCIe 鏈路落差的判讀（純函式）。這一組測試的重點有兩層：
/// 一、每一種分類都要有「已知答案」的正確輸入；
/// 二、<b>不能誤導</b>——查不到上游時必須說「未知」，不能當成「沒有限制」，
/// 也不能把規格上正常的降速講成故障。
/// </summary>
public class PcieNegotiationGapTests
{
    // ── Link Control 2 的存在判定 ─────────────────────────────────────────

    [Theory]
    [InlineData(0x02, true)]   // 能力版本 2（PCIe 2.0 起）→ 有 Link Control 2
    [InlineData(0x03, true)]   // 版本 3
    [InlineData(0x01, false)]  // 版本 1：+0x30 不在這個結構裡
    [InlineData(0x00, false)]  // 讀不到／韌體沒填
    public void LinkControl2_只在能力版本2以上存在(int capVersion, bool expected)
    {
        // 能力暫存器：bit0..3 是版本，bit4..7 是埠類型（根埠 4）
        uint capReg = (uint)((4 << 4) | capVersion);
        Assert.Equal(expected, PcieNegotiationGap.HasLinkControl2(capReg));
    }

    [Fact]
    public void LinkControl2_版本位在高半字的低4位不與埠類型混淆()
    {
        // 實際讀回的是 DWORD，低半字＝能力暫存器，高半字＝能力清單的下一指標等
        uint readback = 0xDEAD_0000 | (uint)((4 << 4) | 2);
        Assert.True(PcieNegotiationGap.HasLinkControl2(readback));
    }

    // ── Target Link Speed（協商上限） ─────────────────────────────────────

    [Theory]
    [InlineData(0x0, 0)]  // 未設定
    [InlineData(0x1, 1)]  // Gen1
    [InlineData(0x4, 4)]  // Gen4
    [InlineData(0x6, 6)]  // Gen6
    [InlineData(0x8, 8)]  // 規格保留；如實回傳代碼，不臆測世代
    [InlineData(0xF, 15)]
    public void TargetLinkSpeed_取低4位保留原代碼(int raw, int expected)
    {
        // Link Control 2：bit0..3 是 Target Link Speed，其餘（Enter Compliance、
        // 各種選擇性均衡設定）在同一個 DWORD 的高位，不能滲進來
        Assert.Equal(expected, PcieNegotiationGap.DecodeTargetLinkSpeed(0xFFFF_FFF0u | (uint)raw));
    }

    // ── 省電宣告 ──────────────────────────────────────────────────────────

    [Fact]
    public void 省電宣告_取的是ASPM與L0sL1位而非時鐘電源管理()
    {
        // Link Capabilities：bits 11:10＝ASPM Support、bit 12＝L1 Substates、
        // bit 9／bit 18＝時鐘電源管理（Clock Power Management 與其宣告）。
        // 近年平台上時鐘電源管理幾乎每條鏈路都是 1，單獨看它會把每張卡都判成「有省電能力」，
        // 這個旗標就失去區分力，所以它單獨出現不算數。
        Assert.False(PcieNegotiationGap.DeclaresPowerSaving(0));
        Assert.False(PcieNegotiationGap.DeclaresPowerSaving(1u << 18));
        Assert.True(PcieNegotiationGap.DeclaresPowerSaving(1u << 10));   // L0s
        Assert.True(PcieNegotiationGap.DeclaresPowerSaving(1u << 11));   // L1
        Assert.True(PcieNegotiationGap.DeclaresPowerSaving(1u << 12));   // L1 Substates
        Assert.True(PcieNegotiationGap.DeclaresPowerSaving(0x2u << 10)); // ASPM＝L1
        // 速度與寬度欄位本身不能滲進來（實務上幾乎都非 0）
        Assert.False(PcieNegotiationGap.DeclaresPowerSaving(0x100 | 4));
    }

    // ── 落差分類：已知答案 ────────────────────────────────────────────────

    [Fact]
    public void 完全符合_回無落差且不給警示()
    {
        // Gen4 x16 能力，實際也是 Gen4 x16：沒有任何事要查
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 4, CurWidth: 16,
            MaxSpeedUpstream: 4, MaxWidthUpstream: 16,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.None, v.Kind);
        Assert.Equal(PcieNegotiationGap.GapSeverity.None, v.Severity);
        Assert.NotEqual("", v.Headline);
    }

    [Fact]
    public void 寬度不足_判為寬度受限且值得查()
    {
        // 能力 x16、插槽電氣只有 x4：這是使用者真正想知道的那一種
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 4, CurWidth: 4,
            MaxSpeedUpstream: 4, MaxWidthUpstream: 16,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.WidthLimited, v.Kind);
        Assert.Equal(PcieNegotiationGap.GapSeverity.WorthChecking, v.Severity);
        Assert.Contains("x4", v.Evidence);
        Assert.Contains("x16", v.Evidence);
    }

    [Fact]
    public void 上游限制_速率與寬度都受上游埠壓低時分類仍為寬度受限並指出上游()
    {
        // 顯卡自己是 Gen4 x16，但插在只到 Gen3 x8 的上游埠後面。
        // 對使用者而言要講清楚的第一件事是「寬度掉了」，所以分類是 WidthLimited；
        // 但依據必須指出瓶頸在上游，不能讓他去怪這張卡。
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 3, CurWidth: 8,
            MaxSpeedUpstream: 3, MaxWidthUpstream: 8,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.WidthLimited, v.Kind);
        Assert.Contains("上游", v.Evidence);
        Assert.Contains("這張卡不是瓶頸", v.Evidence);
    }

    [Fact]
    public void 上游只限速度_判為上游限制且不給警示()
    {
        // 寬度沒掉（x16 全開），但上游埠只到 Gen3，裝置是 Gen4
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 3, CurWidth: 16,
            MaxSpeedUpstream: 3, MaxWidthUpstream: 16,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.UpstreamLimit, v.Kind);
        Assert.Equal(PcieNegotiationGap.GapSeverity.None, v.Severity);   // 不是故障，是拓撲
        Assert.Contains("上游", v.Headline);
    }

    [Fact]
    public void 上游寬度略高於目前值_仍視為上游造成的落差()
    {
        // 容差：上游宣告 x16 而目前只有 x8，但上游同時也把速度壓在 Gen2——
        // 至少速度那一項確定是上游造成的，依據不能講成「鏈路的問題都在這張卡」
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 2, CurWidth: 8,
            MaxSpeedUpstream: 2, MaxWidthUpstream: 16,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.WidthLimited, v.Kind);
        Assert.Contains("上游連接埠最大 x16", v.Evidence);
    }

    [Fact]
    public void 上游資訊未知_不得講成沒有限制()
    {
        // 上游讀不到（(0,0)）＋速率較低：只能說「可能閒置降速」，
        // 不能說「上游沒有限制」——這是這個類別最容易犯的誤導
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 2, CurWidth: 16,
            MaxSpeedUpstream: 0, MaxWidthUpstream: 0,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.SpeedMaybeIdle, v.Kind);
        Assert.DoesNotContain("上游無限制", v.Evidence);
        Assert.DoesNotContain("上限為", v.Evidence);
    }

    [Fact]
    public void 上游限制但寬度也降_寬度優先判為寬度受限()
    {
        // 寬度掉到 x4 而上游也只到 x4：對使用者而言第一件事是「插槽電氣是 x4」，
        // 上游限制是原因、不是結論，所以寬度受限優先
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 4, CurWidth: 4,
            MaxSpeedUpstream: 4, MaxWidthUpstream: 4,
            TargetSpeed: 4, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.WidthLimited, v.Kind);
    }

    [Fact]
    public void 協商上限被壓低_講明是設定造成的而非能力不足()
    {
        // Link Control 2 的 Target Link Speed 被設成 Gen2，但能力是 Gen4：
        // 這種情形再怎麼重協商都不會上 Gen4，除非改設定。這正是唯讀判讀能給的洞見。
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 4, CapWidth: 16, CurSpeed: 2, CurWidth: 16,
            MaxSpeedUpstream: 0, MaxWidthUpstream: 0,
            TargetSpeed: 2, TargetSpeedProgrammable: true));

        Assert.Equal(PcieNegotiationGap.GapKind.SpeedMaybeIdle, v.Kind);
        Assert.Contains("上限", v.Evidence);
        Assert.Contains("Gen2", v.Evidence);
    }

    [Fact]
    public void 能力全為零_判為未知而不是符合()
    {
        // 讀不到能力（空插槽、驅動未載入）：唯一誠實的答案是不確定
        var v = PcieNegotiationGap.Judge(new PcieLinkGap(
            CapSpeed: 0, CapWidth: 0, CurSpeed: 0, CurWidth: 0,
            MaxSpeedUpstream: 0, MaxWidthUpstream: 0,
            TargetSpeed: 0, TargetSpeedProgrammable: false));

        Assert.Equal(PcieNegotiationGap.GapKind.Unknown, v.Kind);
        Assert.Equal(PcieNegotiationGap.GapSeverity.None, v.Severity);
    }

    [Fact]
    public void 沒有訊息時_Headline與Evidence都不得是空字串()
    {
        // 任何一種分類都要有話可說，UI 才不會出現空白卡片
        var kinds = new[]
        {
            new PcieLinkGap(4, 16, 4, 16, 4, 16, 4, true),
            new PcieLinkGap(4, 16, 4, 4, 4, 16, 4, true),
            new PcieLinkGap(4, 16, 3, 8, 3, 8, 4, true),
            new PcieLinkGap(4, 16, 2, 16, 0, 0, 4, true),
            new PcieLinkGap(0, 0, 0, 0, 0, 0, 0, false),
        };
        foreach (var g in gaps(kinds))
        {
            var v = PcieNegotiationGap.Judge(g);
            Assert.False(string.IsNullOrWhiteSpace(v.Headline));
            Assert.False(string.IsNullOrWhiteSpace(v.Evidence));
        }

        static IEnumerable<PcieLinkGap> gaps(PcieLinkGap[] a) => a;
    }

    [Fact]
    public void 判讀不得寫入_只比較唯讀欄位()
    {
        // 這個類別的存在前提是「不碰硬體」。可由公開介面驗證：只有純函式，
        // 沒有任何 WinRing0／P/Invoke 的型別進到簽章裡。
        var asm = typeof(PcieNegotiationGap).Assembly;
        var referenced = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(referenced, n => n.Contains("WinRing", StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(PcieNegotiationGap).GetMethods(),
                   m => Assert.DoesNotContain("Write", m.Name, StringComparison.OrdinalIgnoreCase));
    }
}
