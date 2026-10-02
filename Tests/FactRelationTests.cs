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
        var factsSame = new[] { Fact("reg.microcode", "0x02007006", 0x02007006), Fact("msr.0x8b", "0x02007006", 0x02007006) };
        var factsDiff = new[] { Fact("reg.microcode", "0x02007006", 0x02007006), Fact("msr.0x8b", "0x01007006", 0x01007006) };
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
            Fact("msr.0x8b", "0x02007006", 0x02007006),
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

    [Fact]
    public void SecureBoot開與測試簽章開同時成立_矛盾_單方開_一致()
    {
        var bothOn = new[]
        {
            Fact("platform.secure_boot", "開啟"),
            Fact("platform.testsigning", "測試簽章模式開啟（允許未經微軟簽署的核心驅動載入）"),
        };
        var r = FactRelationService.Evaluate(FactRelationRules.All, bothOn)
            .Single(r => r.RuleId == "platform.secureboot_vs_testsigning");
        Assert.Equal(FactRelation.Contradicts, r.Relation);
        Assert.Contains("至少一個", r.Reason);

        var sbOnly = new[] { Fact("platform.secure_boot", "開啟"), Fact("platform.testsigning", "關閉") };
        Assert.Equal(FactRelation.Consistent,
            FactRelationService.Evaluate(FactRelationRules.All, sbOnly).Single(r => r.RuleId == "platform.secureboot_vs_testsigning").Relation);

        var tsOnly = new[] { Fact("platform.secure_boot", "關閉"), Fact("platform.testsigning", "測試簽章模式開啟（x）") };
        Assert.Equal(FactRelation.Consistent,
            FactRelationService.Evaluate(FactRelationRules.All, tsOnly).Single(r => r.RuleId == "platform.secureboot_vs_testsigning").Relation);
    }

    [Fact]
    public void AER掃描有結果而ECAM基底缺席_矛盾_掃描缺席_Unverifiable()
    {
        var impossible = new[]
        {
            Fact("pcieaer.ecam", "", availability: FactAvailability.NotSupported, reason: "平台未提供 MCFG 表"),
            Fact("pcieaer.scan", "bus 0：掃到 12 台裝置"),
        };
        var r = FactRelationService.Evaluate(FactRelationRules.All, impossible)
            .Single(r => r.RuleId == "pcieaer.scan_vs_ecam");
        Assert.Equal(FactRelation.Contradicts, r.Relation);
        Assert.Contains("不可能", r.Reason);

        var consistent = new[]
        {
            Fact("pcieaer.ecam", "0xE0000000（bus 0-255）"),
            Fact("pcieaer.scan", "bus 0：掃到 12 台裝置"),
        };
        Assert.Equal(FactRelation.Consistent,
            FactRelationService.Evaluate(FactRelationRules.All, consistent).Single(r => r.RuleId == "pcieaer.scan_vs_ecam").Relation);

        var scanMissing = new[]
        {
            Fact("pcieaer.ecam", "0xE0000000（bus 0-255）"),
            Fact("pcieaer.scan", "", availability: FactAvailability.InsufficientPrivilege, reason: "缺 MMIO"),
        };
        Assert.Equal(FactRelation.Unverifiable,
            FactRelationService.Evaluate(FactRelationRules.All, scanMissing).Single(r => r.RuleId == "pcieaer.scan_vs_ecam").Relation);
    }

    [Fact]
    public void 管線一致性族_上下游等級或狀態錯位就是矛盾()
    {
        var at = At;
        static HardwareFact F(string key, string value) =>
            new(key, "測試", key, value, "", "s", FactTrustLevel.Measured, false, At);

        // BIOS_CNTL vs 綜合裁決：等級一致→一致、錯位→矛盾
        var cntlOk = FactRelationService.Evaluate(FactRelationRules.All,
            [F("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
             F("spi.write_surface", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS")])
            .Single(r => r.RuleId == "chipset.bioscntl_vs_write_surface");
        Assert.Equal(FactRelation.Consistent, cntlOk.Relation);

        var cntlBad = FactRelationService.Evaluate(FactRelationRules.All,
            [F("chipset.bios_cntl", "未保護：BLE=0，任何 ring0 皆可寫 BIOS（BIOSWE=1）"),
             F("spi.write_surface", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS")])
            .Single(r => r.RuleId == "chipset.bioscntl_vs_write_surface");
        Assert.Equal(FactRelation.Contradicts, cntlBad.Relation);

        // FRAP vs 綜合裁決暴露面
        var frapBad = FactRelationService.Evaluate(FactRelationRules.All,
            [F("spi.frap", "BIOS 區域不可寫入：主機軟體未獲准（BRWA=0x1 bit1=0）"),
             F("spi.write_surface", "未保護：BLE=0…；暴露面：描述符准主機軟體寫 BIOS 區（FRAP bit1=1）")])
            .Single(r => r.RuleId == "spi.frap_vs_write_surface");
        Assert.Equal(FactRelation.Contradicts, frapBad.Relation);

        // SPI 事實 vs MMIO 後端：SPI 在而後端不在＝矛盾
        var mmioBad = FactRelationService.Evaluate(FactRelationRules.All,
            [new HardwareFact("backend.mmio", "測試", "x", "", "", "s", FactTrustLevel.Measured, false, at,
                null, FactAvailability.NotSupported, "無後端"),
             F("spi.hsfsts", "已鎖定（FLOCKDN=1）")])
            .Single(r => r.RuleId == "backend.mmio_vs_spi_facts");
        Assert.Equal(FactRelation.Contradicts, mmioBad.Relation);

        // 平台安全 MSR vs MSR 後端
        var msrBad = FactRelationService.Evaluate(FactRelationRules.All,
            [new HardwareFact("backend.msr", "測試", "x", "", "", "s", FactTrustLevel.Measured, false, at,
                null, FactAvailability.NotSupported, "無後端"),
             F("platform.feature_control", "已鎖定（Lock=1）")])
            .Single(r => r.RuleId == "backend.msr_vs_platform_security");
        Assert.Equal(FactRelation.Contradicts, msrBad.Relation);

        // HVCI vs 環境矩陣裁決（裁決由 HVCI 推導）
        var decisionBad = FactRelationService.Evaluate(FactRelationRules.All,
            [F("platform.hvci", "開啟"),
             F("backend.environment_decision", "HVCI 關閉：WinRing0 為主力後端；XsRegProbe 為允許清單備援")])
            .Single(r => r.RuleId == "backend.hvci_vs_decision");
        Assert.Equal(FactRelation.Contradicts, decisionBad.Relation);

        var decisionOk = FactRelationService.Evaluate(FactRelationRules.All,
            [F("platform.hvci", "關閉"),
             F("backend.environment_decision", "HVCI 關閉：WinRing0 為主力後端；XsRegProbe 為允許清單備援")])
            .Single(r => r.RuleId == "backend.hvci_vs_decision");
        Assert.Equal(FactRelation.Consistent, decisionOk.Relation);
    }

    [Fact]
    public void SMRAMC鎖定下對外開放是矛盾()
    {
        var illegal = FactRelationService.Evaluate(FactRelationRules.All,
            [Fact("chipset.smramc", "已鎖：D_LCK=1，但 D_OPEN=1——鎖定下對外開放，此組合硬體不應出現")])
            .Single(r => r.RuleId == "chipset.smramc_open_while_locked");
        Assert.Equal(FactRelation.Contradicts, illegal.Relation);
        Assert.Contains("不該存在", illegal.Reason);

        var normal = FactRelationService.Evaluate(FactRelationRules.All,
            [Fact("chipset.smramc", "已鎖：D_LCK=1，SMRAM 設定鎖定")])
            .Single(r => r.RuleId == "chipset.smramc_open_while_locked");
        Assert.Equal(FactRelation.Consistent, normal.Relation);
    }

    [Fact]
    public void BIOS雜湊在而MMIO後端缺席_矛盾_後端在_一致()
    {
        var impossible = new[]
        {
            new HardwareFact("backend.mmio", "測試", "x", "", "", "s", FactTrustLevel.Measured, false, At,
                null, FactAvailability.NotSupported, "無後端"),
            Fact("spi.bios_hash", "SHA-256=abc…（BIOS 區 256 KiB）"),
        };
        var r = FactRelationService.Evaluate(FactRelationRules.All, impossible)
            .Single(r => r.RuleId == "spi.hash_vs_mmio_backend");
        Assert.Equal(FactRelation.Contradicts, r.Relation);
        Assert.Contains("只能經記憶體映射", r.Reason);

        var consistent = new[]
        {
            Fact("backend.mmio", "WinRing0 實體記憶體"),
            Fact("spi.bios_hash", "SHA-256=abc…（BIOS 區 256 KiB）"),
        };
        Assert.Equal(FactRelation.Consistent,
            FactRelationService.Evaluate(FactRelationRules.All, consistent).Single(r => r.RuleId == "spi.hash_vs_mmio_backend").Relation);
    }
}
