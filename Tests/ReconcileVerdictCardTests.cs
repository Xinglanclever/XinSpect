using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 交叉對帳判決卡（A45 之後的顯示層）：ReloadDriverBackedFacts 後判決卡有 26 條、
/// 摘要計數正確、矛盾的徽章 Severity＝Critical（走警示色）。以假讀取器驗證，不碰硬體。
/// </summary>
public class ReconcileVerdictCardTests
{
    [Fact]
    public void 重載後判決卡_26條_摘要計數正確_三態輸入走Unverifiable()
    {
        var svc = new EvidenceLabService();
        svc.ReloadDriverBackedFacts(new ReconcileFakePci(), new DeniedMsr(), new DeniedMmio(),
            new EmptyAcpi(), new DeniedIoPort());

        // 26 條全載。platform.* 兩條的輸入（CodeIntegrity／登錄檔）usermode 可讀 → 一致；
        // 其餘 24 條的輸入（MSR/MMIO/PCI）三態 → 引擎守衛 Unverifiable。
        Assert.Equal(26, svc.ReconcileVerdicts.Count);
        Assert.Equal(2, svc.ReconcileVerdicts.Count(v => v.Relation == FactRelation.Consistent));
        Assert.Equal(24, svc.ReconcileVerdicts.Count(v => v.Relation == FactRelation.Unverifiable));
        Assert.Equal(0, svc.ReconcileVerdicts.Count(v => v.Relation == FactRelation.Contradicts)); // 沒有假資料就沒有假矛盾
        Assert.All(svc.ReconcileVerdicts.Where(v => v.Relation == FactRelation.Consistent),
            v => Assert.Equal(Severity.Good, v.Severity));
        Assert.All(svc.ReconcileVerdicts.Where(v => v.Relation == FactRelation.Unverifiable),
            v => Assert.Equal(Severity.Neutral, v.Severity));
        Assert.Contains("一致 2", svc.ReconcileSummary);
        Assert.Contains("無法驗證 24", svc.ReconcileSummary);
    }

    [Fact]
    public void 判決卡_矛盾案例的Severity是Critical_一致是Good()
    {
        var at = DateTimeOffset.UtcNow;
        var facts = new[]
        {
            new HardwareFact("chipset.smramc", "測試", "SMRAM", "已鎖：D_LCK=1，但 D_OPEN=1——鎖定下對外開放", "", "s",
                FactTrustLevel.Measured, false, at),
        };
        var contradicts = FactRelationService.Evaluate(FactRelationRules.All, facts)
            .Where(r => r.Relation == FactRelation.Contradicts).ToList();
        Assert.NotEmpty(contradicts); // SMRAM 非法組合情境有矛盾可驗證

        var verdicts = contradicts.Select(r => new ReconcileVerdictRow(
            r.RuleName, r.Relation, "矛盾", r.Reason, Severity.Critical)).ToList();
        Assert.All(verdicts, v => Assert.Equal(Severity.Critical, v.Severity));
    }

    // ===== 三態假件（全部讀不到 → 26 條全部 Unverifiable） =====

    private sealed class ReconcileFakePci : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }

    private sealed class DeniedMsr : IKernelMsrReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public ulong? ReadMsr(uint index) => null;
    }

    private sealed class DeniedMmio : IMmioReader
    {
        public bool Available => false;
        public string? UnavailableReason => "未載入（測試假件）";
        public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
    }

    private sealed class EmptyAcpi : IAcpiTableSource
    {
        public bool Available => false;
        public string? UnavailableReason => "列舉失敗（測試假件）";
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    private sealed class DeniedIoPort : IIoPortAccess
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 I/O 埠存取（測試假件）";
        public byte? InByte(uint port) => null;
        public bool OutByte(uint port, byte value) => false;
    }
}
