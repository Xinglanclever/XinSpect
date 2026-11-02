using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 合規對照的守門：缺資料不得變成通過；工具沒做的驗證（驗簽、Quote、復原）必須維持 Unverifiable。
/// </summary>
public class ComplianceMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 1, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string reason = "")
        => new(key, "測試域", key, availability == FactAvailability.Present ? value : "", "",
            "測試來源", FactTrustLevel.Measured, false, At, numeric, availability,
            availability == FactAvailability.Present ? null : reason);

    private static ComplianceControl Control(ComplianceReport r, string id)
        => r.Controls.Single(c => c.Id == id);

    private static AttackTechnique Technique(ComplianceReport r, string id)
        => r.Techniques.Single(t => t.Id == id);

    [Fact]
    public void 未保護的機器_三條147控制項都要NotSatisfied()
    {
        var report = ComplianceMappingService.Evaluate(
        [
            Fact("chipset.bios_cntl", "未保護：BLE=0，任何 ring0 皆可寫 BIOS"),
            Fact("chipset.smramc", "未鎖：D_LCK=0"),
            Fact("spi.prr", "PR0-4 全部停用：SPI 硬體層無範圍保護"),
            Fact("spi.write_surface", "未保護：BLE=0，任何 ring0 皆可寫 BIOS"),
            Fact("bt.measured_boot", "", null, FactAvailability.NotApplicable, "關鍵 PCR 全為零"),
            Fact("tpm.replay.summary", "", null, FactAvailability.ReadError, "TBS 不可用"),
            Fact("uefi.secure_boot", "否", 0),
            Fact("byovd.hits", "0 個", 0),
            Fact("sec.vbs.state", "VirtualizationBasedSecurityStatus＝0（未啟用）", 0),
        ]);

        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-147-WP").Status);
        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-147-SMM").Status);
        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-147-PRR").Status);
        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-155-SB").Status);
        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-155-MEASURE").Status);
        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-193-PROTECT").Status);

        Assert.Equal(AttackExposure.Exposed, Technique(report, "T1542.001").Exposure);
        Assert.Equal(AttackExposure.Exposed, Technique(report, "T1495").Exposure);
        Assert.Equal(AttackExposure.Exposed, Technique(report, "T1562.001").Exposure);
        Assert.Equal(AttackExposure.Exposed, Technique(report, "T1553.006").Exposure);
        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1014").Exposure);
    }

    [Fact]
    public void 受保護的機器_可判定的控制項要Satisfied_但驗簽與Quote與復原仍Unverifiable()
    {
        var report = ComplianceMappingService.Evaluate(
        [
            Fact("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
            Fact("chipset.smramc", "已鎖：D_LCK=1，SMRAM 設定鎖定"),
            Fact("spi.prr", "PR0 0x400000–0x40FFFF 寫+讀保護"),
            Fact("spi.write_surface", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
            Fact("bt.measured_boot", "有量測證據（PCR 0、PCR 4 已延伸）", 1),
            Fact("tpm.replay.summary",
                "事件 12 個・SHA-256 bank 比對：一致 10／不一致 0／log 未覆蓋 2／實讀失敗 0", 10),
            Fact("uefi.secure_boot", "是", 1),
            Fact("uefi.db.count", "11 個", 11),
            Fact("uefi.dbx.count", "77 個", 77),
            Fact("byovd.hits", "0 個", 0),
            Fact("sec.vbs.state", "VirtualizationBasedSecurityStatus＝2（已啟用且執行中）", 2),
        ]);

        foreach (string id in new[]
                 {
                     "NIST-800-147-WP", "NIST-800-147-SMM", "NIST-800-147-PRR",
                     "NIST-800-155-MEASURE", "NIST-800-155-REPLAY", "NIST-800-155-SB",
                     "NIST-800-193-PROTECT", "NIST-800-193-DETECT",
                 })
            Assert.Equal(ComplianceStatus.Satisfied, Control(report, id).Status);

        Assert.Equal(ComplianceStatus.Unverifiable, Control(report, "NIST-800-147-UPD").Status);
        Assert.Equal(ComplianceStatus.Unverifiable, Control(report, "NIST-800-155-QUOTE").Status);
        Assert.Equal(ComplianceStatus.Unverifiable, Control(report, "NIST-800-193-RECOVER").Status);

        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1542.001").Exposure);
        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1495").Exposure);
        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1014").Exposure);
        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1562.001").Exposure);
        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1553.006").Exposure);
    }

    [Fact]
    public void 什麼都讀不到_不得有任何Satisfied()
    {
        var report = ComplianceMappingService.Evaluate([]);

        Assert.All(report.Controls, c => Assert.Equal(ComplianceStatus.Unverifiable, c.Status));
        Assert.All(report.Techniques, t => Assert.Equal(AttackExposure.Unknown, t.Exposure));
        Assert.DoesNotContain(report.Controls, c => c.Status == ComplianceStatus.Satisfied);
    }

    [Fact]
    public void 有鎖但仍有暴露面_寫入保護與193保護都要NotSatisfied()
    {
        var report = ComplianceMappingService.Evaluate(
        [
            Fact("chipset.bios_cntl", "有鎖保護：BLE=1，開啟寫入會觸發 SMI"),
            Fact("spi.write_surface",
                "有鎖保護：BLE=1，開啟寫入會觸發 SMI；暴露面：SPI 旗號未鎖（FLOCKDN=0，保護設定可被改）"),
        ]);

        var wp = Control(report, "NIST-800-147-WP");
        Assert.Equal(ComplianceStatus.NotSatisfied, wp.Status);
        Assert.Contains("FLOCKDN=0", wp.Reason, StringComparison.Ordinal);

        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-193-PROTECT").Status);
        // BIOS_CNTL 本身仍有 BLE，故 T1542.001 的「BIOS 寫入面」判為已緩解——兩個判斷的範圍不同，不是矛盾。
        Assert.Equal(AttackExposure.Mitigated, Technique(report, "T1542.001").Exposure);
    }

    [Fact]
    public void 重放有一顆不一致_155與193偵測都要NotSatisfied()
    {
        var report = ComplianceMappingService.Evaluate(
        [
            Fact("bt.measured_boot", "有量測證據（PCR 0 已延伸）", 1),
            Fact("tpm.replay.summary",
                "事件 9 個・SHA-256 bank 比對：一致 8／不一致 1／log 未覆蓋 0／實讀失敗 0", 8),
        ]);

        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-155-REPLAY").Status);
        Assert.Equal(ComplianceStatus.NotSatisfied, Control(report, "NIST-800-193-DETECT").Status);
    }

    [Fact]
    public void VBS狀態1_政策已開但未執行_算Unknown而不是Mitigated()
    {
        var report = ComplianceMappingService.Evaluate(
        [
            Fact("sec.vbs.state", "VirtualizationBasedSecurityStatus＝1（已啟用但未執行）", 1),
        ]);

        Assert.Equal(AttackExposure.Unknown, Technique(report, "T1562.001").Exposure);
    }
}