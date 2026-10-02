using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP5 FactRelation 對帳引擎的契約：規則自我解釋、缺輸入＝Unverifiable 帶原因（不以缺值冒充矛盾）、
/// 判定函式純委派可測、事實鍵規則嚴格（打錯鍵的規則直接炸，不靜默通過）。
/// </summary>
public sealed class FactRelationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string? reason = null) =>
        new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Measured, false, At, numeric, availability, reason);

    [Fact]
    public void 每條規則都要能自我解釋_無解釋直接建構失敗()
    {
        Assert.Throws<ArgumentException>(() => new FactRelationRule(
            "test", "測試規則", ["a"], _ => FactRelationOutcome.Consistent("ok"), explanation: ""));
        var rule = new FactRelationRule(
            "test", "測試規則", ["a"], _ => FactRelationOutcome.Consistent("ok"), explanation: "為什麼需要這條規則");
        Assert.Contains("為什麼", rule.Explanation);
    }

    [Fact]
    public void 規則要的輸入鍵缺任一個_Unverifiable帶缺了什麼()
    {
        var rules = new[]
        {
            new FactRelationRule("microcode", "微碼一致性", ["reg.microcode", "msr.0x8B"],
                f => FactRelationOutcome.Consistent("同版"),
                "登錄檔與 MSR 0x8B 應指同一份微碼"),
        };
        var facts = new[] { Fact("reg.microcode", "0x02007006") }; // 缺 msr.0x8B

        var results = FactRelationService.Evaluate(rules, facts).ToList();

        var r = Assert.Single(results);
        Assert.Equal(FactRelation.Unverifiable, r.Relation);
        Assert.Contains("msr.0x8B", r.Reason);
    }

    [Fact]
    public void 微碼一致性_登錄檔與MSR同版為Consistent_不同版為Contradicts()
    {
        var factsSame = new[] { Fact("reg.microcode", "0x02007006", 0x02007006), Fact("msr.0x8B", "0x02007006", 0x02007006) };
        var factsDiff = new[] { Fact("reg.microcode", "0x02007006", 0x02007006), Fact("msr.0x8B", "0x01007006", 0x01007006) };
        var rules = FactRelationRules.All;

        var same = FactRelationService.Evaluate(rules, factsSame).Single(r => r.RuleId == "microcode.consistency");
        var diff = FactRelationService.Evaluate(rules, factsDiff).Single(r => r.RuleId == "microcode.consistency");

        Assert.Equal(FactRelation.Consistent, same.Relation);
        Assert.Equal(FactRelation.Contradicts, diff.Relation);
        Assert.Contains("微碼", diff.Reason);
    }

    [Fact]
    public void 微碼規則_任一側讀不到_Unverifiable不冒充矛盾()
    {
        var facts = new[]
        {
            Fact("reg.microcode", "", availability: FactAvailability.ReadError, reason: "登錄檔無值"),
            Fact("msr.0x8B", "0x02007006", 0x02007006),
        };

        var r = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == "microcode.consistency");

        Assert.Equal(FactRelation.Unverifiable, r.Relation);
        Assert.Contains("讀不到", r.Reason);
    }

    [Fact]
    public void MCHBAR基底在但暫存器讀不到_Unverifiable帶能力缺口說明()
    {
        var facts = new[]
        {
            Fact("mchbar.base", "0xFEDC0000"),
            Fact("mchbar.registers", "", availability: FactAvailability.InsufficientPrivilege, reason: "缺 MMIO 讀取"),
        };

        var r = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == "mchbar.readability");

        Assert.Equal(FactRelation.Unverifiable, r.Relation);
        Assert.Contains("MMIO", r.Reason);
    }

    [Fact]
    public void MCHBAR基底與暫存器都在_Conistent()
    {
        var facts = new[]
        {
            Fact("mchbar.base", "0xFEDC0000"),
            Fact("mchbar.registers", "已映射可讀"),
        };

        var r = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == "mchbar.readability");

        Assert.Equal(FactRelation.Consistent, r.Relation);
    }

    [Fact]
    public void TjMax超出合理範圍_Contradicts_正常範圍Consistent()
    {
        var ok = new[] { Fact("cpu.tjmax", "100", 100) };
        var absurd = new[] { Fact("cpu.tjmax", "200", 200) };

        var rOk = FactRelationService.Evaluate(FactRelationRules.All, ok).Single(r => r.RuleId == "cpu.tjmax.sanity");
        var rBad = FactRelationService.Evaluate(FactRelationRules.All, absurd).Single(r => r.RuleId == "cpu.tjmax.sanity");

        Assert.Equal(FactRelation.Consistent, rOk.Relation);
        Assert.Equal(FactRelation.Contradicts, rBad.Relation);
        Assert.Contains("TjMax", rBad.Reason);
    }

    [Fact]
    public void TjMax無數值_Unverifiable()
    {
        var facts = new[] { Fact("cpu.tjmax", "100") }; // 有文字無 numeric

        var r = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == "cpu.tjmax.sanity");

        Assert.Equal(FactRelation.Unverifiable, r.Relation);
    }

    [Fact]
    public void BIOS寫入保護未鎖且SMRAM未鎖_兩者交叉為Contradicts級警示()
    {
        // BIOS_CNTL BLE=0（未保護）而 SMRAMC D_LCK=1（已鎖）：組合不常見但不矛盾——真正的矛盾是
        // BIOSWE=1（可寫）配 BLE=0：任何人可寫 BIOS。用兩條事實的文字裁決交叉。
        var facts = new[]
        {
            Fact("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
            Fact("chipset.smramc", "未鎖：D_LCK=0"),
        };

        var r = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == "chipset.smm_bwp_vs_smram_lock");

        // SMM_BWP=1 只在 SMRAM 鎖定（D_LCK=1）時才有意義：D_LCK=0 卻宣稱 SMM_BWP 生效＝組合不可信
        Assert.Equal(FactRelation.Contradicts, r.Relation);
        Assert.Contains("SMRAM", r.Reason);
    }

    [Fact]
    public void BIOS寫入保護與SMRAM一致鎖定_Contradicts不誤報()
    {
        var facts = new[]
        {
            Fact("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
            Fact("chipset.smramc", "已鎖：D_LCK=1，SMRAM 設定鎖定"),
        };

        var r = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == "chipset.smm_bwp_vs_smram_lock");

        Assert.Equal(FactRelation.Consistent, r.Relation);
    }

    [Fact]
    public void 全部規則的輸入鍵都是三態感知的_讀不到的輸入一律Unverifiable()
    {
        // 規則集契約：任何規則遇到 NotPresent 的事實必須回 Unverifiable——由引擎統一保證，
        // 個別規則只處理 Present 的情況（判定函式拿到的保證都是 Present）。
        foreach (var rule in FactRelationRules.All)
        {
            var unavailable = rule.InputKeys.Select(k => Fact(k, "", availability: FactAvailability.ReadError, reason: "測試：讀不到")).ToArray();
            var results = FactRelationService.Evaluate([rule], unavailable);
            Assert.Equal(FactRelation.Unverifiable, Assert.Single(results).Relation);
        }
    }

    [Fact]
    public void 規則清單規則id不重複()
    {
        var ids = FactRelationRules.All.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
