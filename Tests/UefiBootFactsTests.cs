using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// UEFI 開機設定事實的契約：變數不存在＝NotSupported（不推測成關閉）、SetupMode 缺席不列、
/// 特權拿不到整組三態、Secure Boot 雙來源（UEFI 變數 vs 登錄檔）交叉對帳。
/// 全部以注入探測驗證，不碰真實韌體變數與特權 API。
/// </summary>
public class UefiBootFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 全部可讀時五項事實齊_金鑰未部署走警示色()
    {
        var facts = UefiBootFactsService.Collect(At,
            readByte: name => name switch
            {
                "SecureBoot" => 1,
                "SetupMode" => 1,
                "AuditMode" => 0,
                "DeployedMode" => 0,
                _ => null,
            },
            readBootOrderCount: () => 4, privilegeOk: true);

        Assert.Equal(5, facts.Count);
        Assert.Equal("是", Assert.Single(facts, f => f.Key == "uefi.secure_boot").Value);
        var setup = Assert.Single(facts, f => f.Key == "uefi.setup_mode");
        Assert.StartsWith("金鑰未部署", setup.Value);
        Assert.True(EvidenceFactRow.From(setup).IsWarning);
        Assert.Equal(4, Assert.Single(facts, f => f.Key == "uefi.boot_order_count").NumericValue);
    }

    [Fact]
    public void SecureBoot變數不存在標不支援_可選變數缺席不列()
    {
        var facts = UefiBootFactsService.Collect(At,
            readByte: _ => null, readBootOrderCount: () => null, privilegeOk: true);

        var sb = Assert.Single(facts, f => f.Key == "uefi.secure_boot");
        Assert.Equal(FactAvailability.NotSupported, sb.Availability);
        Assert.Contains("Legacy", sb.UnavailableReason);
        Assert.Null(sb.NumericValue);

        Assert.Null(facts.FirstOrDefault(f => f.Key == "uefi.setup_mode")); // 可選變數缺席→整項不列
        Assert.Equal(FactAvailability.NotSupported, Assert.Single(facts, f => f.Key == "uefi.boot_order_count").Availability);
    }

    [Fact]
    public void 特權拿不到整組如實三態()
    {
        var facts = UefiBootFactsService.Collect(At,
            readByte: _ => 1, readBootOrderCount: () => 3, privilegeOk: false);

        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
        Assert.Contains("SeSystemEnvironmentPrivilege", Assert.Single(facts, f => f.Key == "uefi.secure_boot").UnavailableReason);
    }

    [Fact]
    public void 對帳_雙來源一致_不一致_單邊缺席()
    {
        // 口徑：UEFI 側「是／否」、登錄檔側「開啟／關閉」（兩個產生器的真實值域），比對走 NumericValue 0/1。
        FactRelationRow Row(string uefi, string registry, double? uefiNum, double? regNum) =>
            FactRelationService.Evaluate(FactRelationRules.All,
            [
                new HardwareFact("uefi.secure_boot", "測試", "x", uefi, "", "s", FactTrustLevel.Reported, false, At, uefiNum),
                new HardwareFact("platform.secure_boot", "測試", "x", registry, "", "s", FactTrustLevel.Reported, false, At, regNum),
            ]).Single(r => r.RuleId == "uefi.secureboot_vs_registry");

        Assert.Equal(FactRelation.Consistent, Row("是", "開啟", 1, 1).Relation);
        Assert.Equal(FactRelation.Consistent, Row("否", "關閉", 0, 0).Relation);
        Assert.Equal(FactRelation.Contradicts, Row("是", "關閉", 1, 0).Relation);
        Assert.Contains("優先信 UEFI", Row("是", "關閉", 1, 0).Reason);

        // 一側是未知值（無 0/1 數值）→ 誠實 Unverifiable，不冒充矛盾。
        Assert.Equal(FactRelation.Unverifiable, Row("未知值 2", "開啟", null, 1).Relation);

        // UEFI 側讀不到 → 引擎統一轉 Unverifiable，不冒充矛盾。
        var missing = FactRelationService.Evaluate(FactRelationRules.All,
        [
            new HardwareFact("uefi.secure_boot", "測試", "x", "", "", "s", FactTrustLevel.Reported, false, At,
                null, FactAvailability.NotSupported, "變數不存在"),
            new HardwareFact("platform.secure_boot", "測試", "x", "關閉", "", "s", FactTrustLevel.Reported, false, At, 0),
        ]).Single(r => r.RuleId == "uefi.secureboot_vs_registry");
        Assert.Equal(FactRelation.Unverifiable, missing.Relation);
    }
}
