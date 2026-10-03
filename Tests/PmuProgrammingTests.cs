using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP27 PMU 編程驗證（**多輪測試・不保證可用**，使用者核准的沙箱前哨）：
/// 最小寫入＝只動 IA32_FIXED_CTR_CTRL（0x38D）的固定計數器 0 使能位（bit0/1），
/// **絕不碰 PMI 位元**；每輪「啟用→讀回→工作量→讀計數→還原→讀回確認」，三輪聚合。
/// 同意閘門（未同意丟例外）、還原在 finally 保證、原值非零時用 OR 不用覆寫。
/// </summary>
public class PmuProgrammingTests
{
    private const uint FixedCtrCtrl = 0x38D;

    [Fact]
    public void 同意閘門_無同意拒跑()
    {
        Assert.Throws<InvalidOperationException>(() =>
            XinSpect.PmuProgrammingService.RunConsentedVerification(userConsent: false, msr: new FakeMsr()));
    }

    [Fact]
    public void 多輪驗證_啟用讀回_計數器活動_還原確認逐輪成立()
    {
        var msr = new FakeMsr();
        var rounds = XinSpect.PmuProgrammingService.RunConsentedVerification(
            userConsent: true, msr: msr, rounds: 3, workload: () => { for (int i = 0; i < 1000; i++) ; });

        Assert.Equal(3, rounds.Count);
        Assert.All(rounds, r =>
        {
            Assert.True(r.EnableReadbackOk);   // 寫入後讀回一致
            Assert.True(r.CounterActive);      // 計數器在動
            Assert.True(r.CleanupOk);          // 還原後讀回原值
        });
        Assert.Equal(0x1234u, msr.Registers[FixedCtrCtrl]);   // 結束後無殘留
        Assert.True(msr.Registers[FixedCtrCtrl] == 0x1234u);  // 原值（假件初始）沒被覆寫掉
    }

    [Fact]
    public void 多輪驗證_原值非零用OR不覆寫_其他計數器使能位保留()
    {
        var msr = new FakeMsr();
        msr.Registers[FixedCtrCtrl] = 0x0C;    // 假設固定計數器 1 已被別人啟用（bit2/3）
        var rounds = XinSpect.PmuProgrammingService.RunConsentedVerification(
            userConsent: true, msr: msr, rounds: 1, workload: () => { });

        Assert.True(rounds[0].EnableReadbackOk);
        Assert.Equal(0x0Cu, msr.Registers[FixedCtrCtrl]);     // 還原後別人的位元還在
    }

    [Fact]
    public void 多輪驗證_MSR不可用與無PMU如實三態()
    {
        var noRing0 = XinSpect.PmuProgrammingService.RunConsentedVerification(
            userConsent: true, msr: new FakeMsr { Available = false });
        Assert.All(noRing0, r => Assert.False(r.EnableReadbackOk));

        var noPmu = XinSpect.PmuProgrammingService.RunConsentedVerification(
            userConsent: true, msr: new FakeMsr(), cpuidProbe: () => (0u, 0u, 0u, 0u));
        Assert.All(noPmu, r => Assert.False(r.EnableReadbackOk));
    }

    [Fact]
    public void 多輪標註_成文於格式化輸出()
    {
        string text = XinSpect.PmuProgrammingService.FormatNotice;
        Assert.Contains("多輪測試", text);
        Assert.Contains("不保證可用", text);
        Assert.Contains("還原", text);
    }

    /// <summary>假 MSR：0x38D 初始 0x1234（OS/USR 對固定計數器 1 的既有設定），其餘暫存器可寫可讀。</summary>
    private sealed class FakeMsr : XinSpect.IPmuMsrAccess
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason => Available ? null : "缺 ring0（假件）";
        public Dictionary<uint, ulong> Registers = new() { [FixedCtrCtrl] = 0x1234, [0x309] = 0 };

        private ulong _counterTicks;

        public ulong? ReadMsr(uint index)
        {
            // 模擬固定計數器 0：僅在 0x38D 使能（bit0/1）時遞增——停用就停數
            if (index == 0x309 && (Registers.GetValueOrDefault<uint, ulong>(0x38D) & 0x3) != 0)
                Registers[0x309] = ++_counterTicks * 100;
            return Registers.TryGetValue(index, out ulong v) ? v : 0;
        }

        public bool WriteMsr(uint index, ulong value)
        {
            Registers[index] = value;
            return true;
        }
    }
}
