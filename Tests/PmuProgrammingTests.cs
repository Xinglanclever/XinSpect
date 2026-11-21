using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP27 PMU 編程驗證（**多輪測試・不保證可用**，使用者核准的沙箱前哨）：
/// 最小寫入＝只動 IA32_FIXED_CTR_CTRL（0x38D）的固定計數器 0 使能位（bit0/1），
/// **絕不碰 PMI 位元**；每輪「啟用→讀回→工作量→讀計數→還原→讀回確認」，三輪聚合。
/// 同意閘門（未同意丟例外）、finally 嘗試還原並核對、原值非零時用 OR 不用覆寫。
/// 假件僅驗交易流程，不證明實機所有權或恢復能力。
/// </summary>
[Collection(HardwareOperationPolicyCollection.Name)]
public class PmuProgrammingTests
{
    private const uint FixedCtrCtrl = 0x38D;
    private static (uint, uint, uint, uint) SupportedPmu() => (2u, 0u, 0u, 1u | (48u << 5));

    [Fact]
    public void 同意閘門_無同意拒跑()
    {
        Assert.Throws<InvalidOperationException>(() =>
            XinSpect.PmuProgrammingService.RunConsentedVerification(userConsent: false, msr: new FakeMsr()));
    }

    [Fact]
    public void 多輪驗證_啟用讀回_計數器活動_還原確認逐輪成立()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr();
        var rounds = XinSpect.PmuProgrammingService.RunConsentedVerification(
            userConsent: true, msr: msr, rounds: 3, workload: () => { for (int i = 0; i < 1000; i++) ; }, cpuidProbe: SupportedPmu);

        Assert.Equal(3, rounds.Count);
        Assert.Equal(6, msr.Writes.Count); // One setup and one verified cleanup per round.
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
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr();
        msr.Registers[FixedCtrCtrl] = 0x0C;    // 假設固定計數器 1 已被別人啟用（bit2/3）
        var rounds = XinSpect.PmuProgrammingService.RunConsentedVerification(
            userConsent: true, msr: msr, rounds: 1, workload: () => { }, cpuidProbe: SupportedPmu);

        Assert.True(rounds[0].EnableReadbackOk);
        Assert.Equal(0x0Cu, msr.Registers[FixedCtrCtrl]);     // 還原後別人的位元還在
    }

    [Fact]
    public void 多輪驗證_MSR不可用與無PMU如實三態()
    {
        using var policy = new EnabledHighRiskPolicy();
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

    [Fact]
    public void 第一輪負載撤銷授權只恢復不再啟動新一輪()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr();
        int loads = 0;
        var results = PmuProgrammingService.RunConsentedVerification(true, msr, 3, () =>
        {
            loads++;
            policy.Settings.HighRiskHardwareOperationsEnabled = false;
        }, SupportedPmu);
        Assert.Single(results);
        Assert.Equal(1, loads);
        Assert.True(results[0].CleanupOk);
        Assert.Equal(new ulong[] { 0x1237, 0x1234 }, msr.Writes);
        Assert.Equal(0x1234ul, msr.Registers[FixedCtrCtrl]);
    }

    [Fact]
    public void 原值讀取時撤銷授權不得開始設定()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr { OnRead = _ => policy.Settings.HighRiskHardwareOperationsEnabled = false };
        int loads = 0;
        var results = PmuProgrammingService.RunConsentedVerification(true, msr, 3, () => loads++,
            SupportedPmu);
        Assert.Single(results);
        Assert.Empty(msr.Writes);
        Assert.Equal(0, loads);
        Assert.False(results[0].EnableReadbackOk);
    }

    [Fact]
    public void 設定後撤銷授權仍恢復但不得跑負載()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr { OnWrite = _ => policy.Settings.AdvancedDiagnosticsEnabled = false };
        int loads = 0;
        var results = PmuProgrammingService.RunConsentedVerification(true, msr, 3, () => loads++,
            SupportedPmu);
        Assert.Single(results);
        Assert.Equal(0, loads);
        Assert.True(results[0].EnableReadbackOk);
        Assert.False(results[0].CounterActive);
        Assert.True(results[0].CleanupOk);
        Assert.Equal(new ulong[] { 0x1237, 0x1234 }, msr.Writes);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void 部分設定與清理失敗只還原一次並核對讀回(bool setupOk, bool cleanupWriteOk, bool cleanupReadOk)
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr
        {
            SetupOk = setupOk, CleanupWriteOk = cleanupWriteOk, CleanupReadOk = cleanupReadOk,
        };
        var results = PmuProgrammingService.RunConsentedVerification(true, msr, 1, () => { },
            SupportedPmu);
        Assert.Single(results);
        Assert.Equal(new ulong[] { 0x1237, 0x1234 }, msr.Writes);
        Assert.Equal(setupOk, results[0].EnableReadbackOk);
        Assert.Equal(cleanupWriteOk && cleanupReadOk, results[0].CleanupOk);
        Assert.Equal(1, msr.CleanupReads);
    }

    [Fact]
    public void 負載丟例外仍單次還原且讀回()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr();
        Assert.Throws<InvalidOperationException>(() => PmuProgrammingService.RunConsentedVerification(
            true, msr, 1, () => throw new InvalidOperationException("injected workload failure"),
            SupportedPmu));
        Assert.Equal(new ulong[] { 0x1237, 0x1234 }, msr.Writes);
        Assert.Equal(1, msr.CleanupReads);
        Assert.Equal(0x1234ul, msr.Registers[FixedCtrCtrl]);
    }

    [Fact]
    public void 清理丟例外同時保留負載原始例外與恢復未確認仍嘗試讀回()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr { ThrowCleanup = true };
        var expected = new InvalidOperationException("original workload error");
        var actual = Assert.Throws<AggregateException>(() => PmuProgrammingService.RunConsentedVerification(
            true, msr, 1, () => throw expected, SupportedPmu));
        Assert.Same(expected, actual.InnerExceptions[0]);
        Assert.Contains("restoration could not be verified", actual.Message);
        Assert.Contains(actual.Flatten().InnerExceptions, e => e.Message == "cleanup failure");
        Assert.Equal(2, msr.Writes.Count);
        Assert.Equal(1, msr.CleanupReads);
    }

    [Fact]
    public void 負載異常且清理讀回缺失同時報告兩項失敗()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr { CleanupReadOk = false };
        var expected = new InvalidOperationException("original workload error");
        var actual = Assert.Throws<AggregateException>(() => PmuProgrammingService.RunConsentedVerification(
            true, msr, 3, () => throw expected, SupportedPmu));
        Assert.Same(expected, actual.InnerExceptions[0]);
        Assert.Contains("restoration could not be verified", actual.InnerExceptions[1].Message);
        Assert.Equal(2, msr.Writes.Count);
        Assert.Equal(1, msr.CleanupReads);
    }

    [Fact]
    public void 原生編程未驗證不得建立可用讀寫通路()
    {
        var msr = new WinRing0PmuMsrAccess();
        Assert.False(msr.Available);
        Assert.Contains("CPU／SMT", msr.UnavailableReason);
        Assert.Contains("釘選", msr.UnavailableReason);
        Assert.Contains("寫回", msr.UnavailableReason);
        Assert.Null(msr.ReadMsr(FixedCtrCtrl));
        Assert.False(msr.WriteMsr(FixedCtrCtrl, 3));
    }

    [Fact]
    public void 清理未確認不得再啟動下一輪()
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr { CleanupReadOk = false };
        int loads = 0;
        var results = PmuProgrammingService.RunConsentedVerification(true, msr, 3, () => loads++,
            SupportedPmu);
        Assert.Single(results);
        Assert.False(results[0].CleanupOk);
        Assert.Equal(1, loads);
        Assert.Equal(2, msr.Writes.Count);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void 計數器任一讀取缺失不得宣稱活動且仍清理(int missingRead, int expectedLoads)
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr { MissingCounterRead = missingRead };
        int loads = 0;
        var result = Assert.Single(PmuProgrammingService.RunConsentedVerification(
            true, msr, 1, () => loads++, SupportedPmu));
        Assert.True(result.EnableReadbackOk);
        Assert.False(result.CounterActive);
        Assert.True(result.CleanupOk);
        Assert.Equal(expectedLoads, loads);
        Assert.Equal(new ulong[] { 0x1237, 0x1234 }, msr.Writes);
        Assert.Equal(1, msr.CleanupReads);
    }

    [Theory]
    [InlineData(0u, 1u, 48u)]
    [InlineData(1u, 1u, 48u)]
    [InlineData(2u, 0u, 48u)]
    [InlineData(2u, 1u, 0u)]
    [InlineData(2u, 1u, 65u)]
    public void 未宣告固定計數器零或合法位寬不得設定(uint version, uint count, uint width)
    {
        using var policy = new EnabledHighRiskPolicy();
        var msr = new FakeMsr();
        int loads = 0;
        var result = Assert.Single(PmuProgrammingService.RunConsentedVerification(
            true, msr, 1, () => loads++, () => (version, 0u, 0u, count | (width << 5))));
        Assert.False(result.EnableReadbackOk);
        Assert.False(result.CounterActive);
        Assert.Empty(msr.Writes);
        Assert.Equal(0, loads);
    }

    /// <summary>假 MSR：0x38D 初始 0x1234（OS/USR 對固定計數器 1 的既有設定），其餘暫存器可寫可讀。</summary>
    private sealed class FakeMsr : XinSpect.IPmuMsrAccess
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason => Available ? null : "缺 ring0（假件）";
        public Dictionary<uint, ulong> Registers = new() { [FixedCtrCtrl] = 0x1234, [0x309] = 0 };

        private ulong _counterTicks;
        public List<ulong> Writes { get; } = [];
        public Action<uint>? OnRead;
        public Action<ulong>? OnWrite;
        public bool SetupOk = true, CleanupWriteOk = true, CleanupReadOk = true;
        public bool ThrowCleanup;
        public int CleanupReads;
        public int MissingCounterRead;
        private int _counterReads;

        public ulong? ReadMsr(uint index)
        {
            OnRead?.Invoke(index);
            if (index == 0x309 && ++_counterReads == MissingCounterRead) return null;
            if (index == FixedCtrCtrl && Writes.Count == 2)
            {
                CleanupReads++;
                if (!CleanupReadOk) return null;
            }
            // 模擬固定計數器 0：僅在 0x38D 使能（bit0/1）時遞增——停用就停數
            if (index == 0x309 && (Registers.GetValueOrDefault<uint, ulong>(0x38D) & 0x3) != 0)
                Registers[0x309] = ++_counterTicks * 100;
            return Registers.TryGetValue(index, out ulong v) ? v : 0;
        }

        public bool WriteMsr(uint index, ulong value)
        {
            Assert.True(HardwareOperationGate.CanWriteHardware, "Existing operation must retain restoration permission.");
            Writes.Add(value);
            if (Writes.Count == 2 && ThrowCleanup) throw new InvalidOperationException("cleanup failure");
            Registers[index] = value;
            OnWrite?.Invoke(value);
            return Writes.Count == 1 ? SetupOk : CleanupWriteOk;
        }
    }

    private sealed class EnabledHighRiskPolicy : IDisposable
    {
        public EnabledHighRiskPolicy()
        {
            Settings = new SettingsService(loadFromDisk: false)
            {
                AdvancedDiagnosticsEnabled = true,
                HighRiskHardwareOperationsEnabled = true,
            };
            HardwareOperationGate.Bind(Settings);
        }

        public SettingsService Settings { get; }

        public void Dispose() => HardwareOperationGate.Bind(null);
    }
}
