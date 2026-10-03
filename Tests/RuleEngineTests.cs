using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// A45 規則引擎的契約：JSON 載入、驗證器（SpecRef／誤報條件／鍵名／預設分支位置）、
/// 解釋器（為什麼觸發＋規格＋誤報）、以及<b>行為等價</b>——
/// 等價定義（已向使用者宣告）：同一情境下，外部規則與內建 C# 規則的 <b>FactRelation 必須一致</b>；
/// Reason 文字允許語義等價、不逐字相同（內建規則的動態文字由樣板重述）。
/// </summary>
public class RuleEngineTests
{
    private static string BuiltinPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根"), "Rules", "builtin.json");
    }

    private static IReadOnlyList<RuleDefinition> Builtin() => RuleLoader.LoadFromFile(BuiltinPath());

    // ===== 載入與驗證 =====

    [Fact]
    public void builtin載入_26條全數通過驗證()
    {
        var rules = Builtin();
        Assert.Equal(26, rules.Count);
        Assert.True(rules.Select(r => r.Id).Distinct().Count() == rules.Count, "Id 不可重複");
        Assert.Empty(RuleValidator.Validate(rules));
    }

    [Fact]
    public void 內建規則與外部規則的id集合完全一致()
    {
        var csharpIds = FactRelationRules.All.Select(r => r.Id).OrderBy(x => x).ToList();
        var jsonIds = Builtin().Select(r => r.Id).OrderBy(x => x).ToList();
        Assert.Equal(csharpIds, jsonIds); // 一條都不能漏
    }

    [Fact]
    public void 驗證器_缺SpecRef缺誤報條件未登記鍵與預設分支位置都會擋()
    {
        var rules = new List<RuleDefinition>
        {
            new("r1", "無 SpecRef", ["a"], [], [new RuleBranch([], FactRelation.Consistent, "r")], "說明",
                [], ["無"]), // 缺 specRefs
            new("r2", "無誤報條件", ["a"], [], [new RuleBranch([], FactRelation.Consistent, "r")], "說明",
                ["spec"], []), // 缺 falseReports
            new("r3", "打錯鍵", ["a"], [], [new RuleBranch(
                [RulePredicate.ValueEquals("typo-key", "x")], FactRelation.Consistent, "r")], "說明",
                ["spec"], ["無"]), // typo-key 未登記
            new("r4", "預設分支不在最後", ["a"], [],
                [new RuleBranch([], FactRelation.Consistent, "r"),
                 new RuleBranch([RulePredicate.KeyAvailable("a")], FactRelation.Consistent, "r")], "說明",
                ["spec"], ["無"]), // 空 when 在中間
            new("r1", "重複 id", ["a"], [], [new RuleBranch([], FactRelation.Consistent, "r")], "說明",
                ["spec"], ["無"]), // 與 r1 重複
        };
        var errors = RuleValidator.Validate(rules);
        Assert.Contains(errors, e => e.Contains("r1") && e.Contains("SpecRef"));
        Assert.Contains(errors, e => e.Contains("r2") && e.Contains("誤報"));
        Assert.Contains(errors, e => e.Contains("r3") && e.Contains("typo-key"));
        Assert.Contains(errors, e => e.Contains("r4") && e.Contains("最後一個分支"));
        Assert.Contains(errors, e => e.Contains("r1") && e.Contains("重複"));
    }

    [Fact]
    public void 解釋器_輸出為什麼觸發規格與誤報條件()
    {
        var rules = Builtin();
        var rule = Assert.Single(rules, r => r.Id == "chipset.smramc_open_while_locked");
        var facts = new[]
        {
            F("chipset.smramc", "已鎖：D_LCK=1，但 D_OPEN=1——鎖定下對外開放，此組合硬體不應出現"),
        };
        var csharp = FactRelationService.Evaluate(FactRelationRules.All, facts).Single(r => r.RuleId == rule.Id);
        var explanation = RuleExplainer.Explain(rule, csharp);

        Assert.Contains(rule.Name, explanation);
        Assert.Contains("Contradicts", explanation);
        Assert.Contains("D_LCK 寫 1 會強制清 D_OPEN", explanation); // 判決內容
        Assert.Contains("Intel SDM", explanation);                  // 依據規格
        Assert.Contains("誤報條件", explanation);                    // 誤報條件
    }

    // ===== 行為等價：逐條逐情境比對 Relation =====

    private static HardwareFact F(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string? reason = null) =>
        new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Measured, false, At, numeric, availability, reason);

    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> Battery()
    {
        static object[] S(string ruleId, FactRelation expected, params HardwareFact[] facts) =>
            [ruleId, expected, facts];

        // microcode.consistency
        yield return S("microcode.consistency", FactRelation.Consistent,
            F("reg.microcode", "0x02007006", 0x02007006), F("msr.0x8b", "0x02007006", 0x02007006));
        yield return S("microcode.consistency", FactRelation.Contradicts,
            F("reg.microcode", "0x02007006", 0x02007006), F("msr.0x8b", "0x01007006", 0x01007006));
        yield return S("microcode.consistency", FactRelation.Unverifiable,
            F("reg.microcode", "0x02007006"), F("msr.0x8b", "0x02007006")); // 無數值
        // mchbar.readability
        yield return S("mchbar.readability", FactRelation.Consistent,
            F("mchbar.base", "0xFEDC0000"), F("mchbar.registers", "已映射可讀"));
        yield return S("mchbar.readability", FactRelation.Unverifiable,
            F("mchbar.base", "0xFEDC0000"),
            F("mchbar.registers", "", availability: FactAvailability.ReadError, reason: "MMIO 失敗"));
        // cpu.tjmax.sanity
        yield return S("cpu.tjmax.sanity", FactRelation.Consistent, F("cpu.tjmax", "90", 90));
        yield return S("cpu.tjmax.sanity", FactRelation.Contradicts, F("cpu.tjmax", "30", 30));
        yield return S("cpu.tjmax.sanity", FactRelation.Unverifiable, F("cpu.tjmax", "—"));
        // chipset.smm_bwp_vs_smram_lock
        yield return S("chipset.smm_bwp_vs_smram_lock", FactRelation.Consistent,
            F("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"), F("chipset.smramc", "已鎖：D_LCK=1"));
        yield return S("chipset.smm_bwp_vs_smram_lock", FactRelation.Contradicts,
            F("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"), F("chipset.smramc", "未鎖：D_LCK=0"));
        yield return S("chipset.smm_bwp_vs_smram_lock", FactRelation.Consistent,
            F("chipset.bios_cntl", "有鎖保護：BLE=1，開啟寫入會觸發 SMI"), F("chipset.smramc", "已鎖：D_LCK=1"));
        // platform.secureboot_vs_testsigning
        yield return S("platform.secureboot_vs_testsigning", FactRelation.Contradicts,
            F("platform.secure_boot", "開啟"), F("platform.testsigning", "測試簽章模式開啟（x）"));
        yield return S("platform.secureboot_vs_testsigning", FactRelation.Consistent,
            F("platform.secure_boot", "開啟"), F("platform.testsigning", "關閉"));
        yield return S("platform.secureboot_vs_testsigning", FactRelation.Consistent,
            F("platform.secure_boot", "關閉"), F("platform.testsigning", "測試簽章模式開啟（x）"));
        // uefi.secureboot_vs_registry
        yield return S("uefi.secureboot_vs_registry", FactRelation.Consistent,
            F("uefi.secure_boot", "關閉"), F("platform.secure_boot", "關閉"));
        yield return S("uefi.secureboot_vs_registry", FactRelation.Contradicts,
            F("uefi.secure_boot", "開啟"), F("platform.secure_boot", "關閉"));
        // pcieaer.scan_vs_ecam
        yield return S("pcieaer.scan_vs_ecam", FactRelation.Consistent,
            F("pcieaer.ecam", "0xE0000000"), F("pcieaer.scan", "bus 0-31：2 個裝置"));
        yield return S("pcieaer.scan_vs_ecam", FactRelation.Contradicts,
            F("pcieaer.ecam", "", availability: FactAvailability.NotSupported, reason: "平台未提供 MCFG"),
            F("pcieaer.scan", "bus 0-31：2 個裝置"));
        // backend.hvci_vs_decision
        yield return S("backend.hvci_vs_decision", FactRelation.Consistent,
            F("platform.hvci", "關閉"), F("backend.environment_decision", "HVCI 關閉：WinRing0 為主力後端"));
        yield return S("backend.hvci_vs_decision", FactRelation.Contradicts,
            F("platform.hvci", "開啟"), F("backend.environment_decision", "HVCI 關閉：WinRing0 為主力後端"));
        yield return S("backend.hvci_vs_decision", FactRelation.Unverifiable,
            F("platform.hvci", "開啟"), F("backend.environment_decision", "格式不明"));
        // chipset.bioscntl_vs_write_surface
        yield return S("chipset.bioscntl_vs_write_surface", FactRelation.Consistent,
            F("chipset.bios_cntl", "最強保護：SMM_BWP=1"), F("spi.write_surface", "最強保護：SMM_BWP=1"));
        yield return S("chipset.bioscntl_vs_write_surface", FactRelation.Consistent,
            F("chipset.bios_cntl", "有鎖保護：BLE=1"), F("spi.write_surface", "有鎖保護：BLE=1"));
        yield return S("chipset.bioscntl_vs_write_surface", FactRelation.Contradicts,
            F("chipset.bios_cntl", "未保護：BLE=0"), F("spi.write_surface", "最強保護：SMM_BWP=1"));
        // spi.frap_vs_write_surface
        yield return S("spi.frap_vs_write_surface", FactRelation.Consistent,
            F("spi.frap", "BIOS 區域可寫入：BRWA=0x2"), F("spi.write_surface", "…暴露面：FRAP bit1=1…"));
        yield return S("spi.frap_vs_write_surface", FactRelation.Contradicts,
            F("spi.frap", "BIOS 區域不可寫入：BRWA=0x1"), F("spi.write_surface", "…暴露面：FRAP bit1=1…"));
        // backend.mmio_vs_spi_facts
        yield return S("backend.mmio_vs_spi_facts", FactRelation.Consistent,
            F("backend.mmio", "WinRing0 實體記憶體"), F("spi.hsfsts", "已鎖定（FLOCKDN=1）"));
        yield return S("backend.mmio_vs_spi_facts", FactRelation.Contradicts,
            F("backend.mmio", "", availability: FactAvailability.NotSupported, reason: "無後端"),
            F("spi.hsfsts", "已鎖定（FLOCKDN=1）"));
        // backend.msr_vs_platform_security
        yield return S("backend.msr_vs_platform_security", FactRelation.Consistent,
            F("backend.msr", "WinRing0"), F("platform.feature_control", "已鎖定（Lock=1）"));
        yield return S("backend.msr_vs_platform_security", FactRelation.Contradicts,
            F("backend.msr", "", availability: FactAvailability.NotSupported, reason: "無後端"),
            F("platform.feature_control", "已鎖定（Lock=1）"));
        // spi.hash_vs_mmio_backend
        yield return S("spi.hash_vs_mmio_backend", FactRelation.Consistent,
            F("backend.mmio", "WinRing0 實體記憶體"), F("spi.bios_hash", "SHA-256=abc"));
        yield return S("spi.hash_vs_mmio_backend", FactRelation.Contradicts,
            F("backend.mmio", "", availability: FactAvailability.NotSupported, reason: "無後端"),
            F("spi.bios_hash", "SHA-256=abc"));
        // spi.hash_without_map
        yield return S("spi.hash_without_map", FactRelation.Consistent,
            F("spi.flash_map", "快閃 16 MiB"), F("spi.bios_hash", "SHA-256=abc"));
        yield return S("spi.hash_without_map", FactRelation.Contradicts,
            F("spi.flash_map", "", availability: FactAvailability.NotApplicable, reason: "FREG 全空"), F("spi.bios_hash", "SHA-256=abc"));
        // spi.map_vs_regions
        yield return S("spi.map_vs_regions", FactRelation.Consistent,
            F("spi.flash_map", "快閃 16 MiB"), F("spi.regions", "描述符 0x0-0xFFF；BIOS 0x1000-0xBFFFFF"));
        yield return S("spi.map_vs_regions", FactRelation.Contradicts,
            F("spi.flash_map", "快閃 16 MiB"), F("spi.regions", "FREG0-5 全為空（未依描述符配置區域）"));
        // pci.spi_facts_without_controller
        yield return S("pci.spi_facts_without_controller", FactRelation.Consistent,
            F("pci.dev.1f.5", "SPI 控制器"), F("spi.hsfsts", "已鎖定"));
        yield return S("pci.spi_facts_without_controller", FactRelation.Contradicts, F("spi.hsfsts", "已鎖定"));
        yield return S("pci.spi_facts_without_controller", FactRelation.Unverifiable,
            F("pci.dev.1f.5", "", availability: FactAvailability.ReadError, reason: "讀取失敗"),
            F("spi.hsfsts", "已鎖定"));
        // pci.spi_controller_reported_unreachable
        yield return S("pci.spi_controller_reported_unreachable", FactRelation.Consistent,
            F("pci.dev.1f.5", "SPI 控制器"), F("spi.hsfsts", "已鎖定"));
        yield return S("pci.spi_controller_reported_unreachable", FactRelation.Contradicts,
            F("pci.dev.1f.5", "SPI 控制器"),
            F("spi.hsfsts", "", availability: FactAvailability.NotApplicable, reason: "0:1F.5 無回應（找不到 SPI 控制器）"));
        yield return S("pci.spi_controller_reported_unreachable", FactRelation.Consistent,
            F("pci.dev.1f.5", "SPI 控制器"),
            F("spi.hsfsts", "", availability: FactAvailability.NotApplicable, reason: "0:1F.5 非 Intel 裝置（SPI 控制器不在此處）"));
        // uefi.secureboot_vs_setupmode
        yield return S("uefi.secureboot_vs_setupmode", FactRelation.Contradicts,
            F("uefi.secure_boot", "是"), F("uefi.setup_mode", "金鑰未部署（Setup Mode 開啟）"));
        yield return S("uefi.secureboot_vs_setupmode", FactRelation.Consistent,
            F("uefi.secure_boot", "是"), F("uefi.setup_mode", "金鑰已部署（Setup Mode 關閉）"));
        // uefi.audit_vs_deployed
        yield return S("uefi.audit_vs_deployed", FactRelation.Contradicts,
            F("uefi.audit_mode", "是"), F("uefi.deployed_mode", "是"));
        yield return S("uefi.audit_vs_deployed", FactRelation.Consistent,
            F("uefi.audit_mode", "否"), F("uefi.deployed_mode", "是"));
        // uefi.variable_vs_registry_platform
        yield return S("uefi.variable_vs_registry_platform", FactRelation.Consistent,
            F("uefi.secure_boot", "是"), F("platform.secure_boot", "關閉"));
        yield return S("uefi.variable_vs_registry_platform", FactRelation.Contradicts,
            F("uefi.secure_boot", "是"),
            F("platform.secure_boot", "", availability: FactAvailability.NotSupported, reason: "登錄鍵不存在"));
        // spi.service_vs_spi_bar_resource
        yield return S("spi.service_vs_spi_bar_resource", FactRelation.Consistent,
            F("pci.res.1f.5", "記憶體（32-bit） 0xFED10000"), F("spi.hsfsts", "已鎖定"));
        yield return S("spi.service_vs_spi_bar_resource", FactRelation.Contradicts,
            F("pci.res.1f.5", "記憶體（32-bit） 0xFED10000"),
            F("spi.hsfsts", "", availability: FactAvailability.NotApplicable, reason: "SPI 控制器未配置 SPIBAR"));
        yield return S("spi.service_vs_spi_bar_resource", FactRelation.Consistent,
            F("pci.res.1f.5", "無已配置資源（5 個 BAR 為 0）"),
            F("spi.hsfsts", "", availability: FactAvailability.NotApplicable, reason: "SPI 控制器未配置 SPIBAR"));
        // spi.write_surface_vs_hsfsts_flockdn
        yield return S("spi.write_surface_vs_hsfsts_flockdn", FactRelation.Consistent,
            F("spi.hsfsts", "已鎖定（FLOCKDN=1）：SPI 保護設定不可改直至重置"),
            F("spi.write_surface", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"));
        yield return S("spi.write_surface_vs_hsfsts_flockdn", FactRelation.Contradicts,
            F("spi.hsfsts", "已鎖定（FLOCKDN=1）：SPI 保護設定不可改直至重置"),
            F("spi.write_surface", "未保護：BLE=0…；暴露面：SPI 旗號未鎖（FLOCKDN=0，保護設定可被改）"));
        yield return S("spi.write_surface_vs_hsfsts_flockdn", FactRelation.Contradicts,
            F("spi.hsfsts", "未鎖定（FLOCKDN=0）：保護範圍與寫入停用設定仍可被 ring0 改動"),
            F("spi.write_surface", "有鎖保護：BLE=1，開啟寫入會觸發 SMI"));
        yield return S("spi.write_surface_vs_hsfsts_flockdn", FactRelation.Unverifiable,
            F("spi.hsfsts", "已鎖定"),
            F("spi.write_surface", "", availability: FactAvailability.ReadError, reason: "BIOS_CNTL 讀取失敗"));
        // chipset.smramc_open_while_locked
        yield return S("chipset.smramc_open_while_locked", FactRelation.Contradicts,
            F("chipset.smramc", "已鎖：D_LCK=1，但 D_OPEN=1——鎖定下對外開放，此組合硬體不應出現"));
        yield return S("chipset.smramc_open_while_locked", FactRelation.Consistent,
            F("chipset.smramc", "已鎖：D_LCK=1，SMRAM 設定鎖定"));
        // mchbar_registers_without_mmio_backend
        yield return S("mchbar_registers_without_mmio_backend", FactRelation.Consistent,
            F("backend.mmio", "WinRing0 實體記憶體"), F("mchbar.registers", "已映射可讀"));
        yield return S("mchbar_registers_without_mmio_backend", FactRelation.Contradicts,
            F("backend.mmio", "", availability: FactAvailability.NotSupported, reason: "無後端"),
            F("mchbar.registers", "已映射可讀"));
        // tjmax_without_msr_backend
        yield return S("tjmax_without_msr_backend", FactRelation.Consistent,
            F("backend.msr", "WinRing0"), F("cpu.tjmax", "90", 90));
        yield return S("tjmax_without_msr_backend", FactRelation.Contradicts,
            F("backend.msr", "", availability: FactAvailability.NotSupported, reason: "無後端"),
            F("cpu.tjmax", "90", 90));
        // mchbar_base_without_host_bridge
        yield return S("mchbar_base_without_host_bridge", FactRelation.Consistent,
            F("pci.dev.00.0", "Host Bridge ・ Intel"), F("mchbar.base", "0xFEDC0000"));
        yield return S("mchbar_base_without_host_bridge", FactRelation.Contradicts, F("mchbar.base", "0xFEDC0000"));
    }

    [Theory]
    [MemberData(nameof(Battery))]
    public void 行為等價_內建與外部規則的Relation逐情境一致(string ruleId, FactRelation expected, HardwareFact[] facts)
    {
        var csharp = FactRelationService.Evaluate(FactRelationRules.All, facts)
            .Single(r => r.RuleId == ruleId).Relation;
        var external = ExternalRuleEngine.Evaluate(Builtin(), facts)
            .Single(r => r.RuleId == ruleId).Relation;

        Assert.True(csharp == expected,
            $"情境（{ruleId}）內建規則 Relation={csharp}，預期 {expected}——先修情境或內建規則");
        Assert.True(external == expected,
            $"情境（{ruleId}）外部規則 Relation={external}，內建 {csharp}——JSON 與 C# 行為不等價");
    }

    [Fact]
    public void 規則數等價_JSON與CSharp一條不漏()
    {
        Assert.Equal(FactRelationRules.All.Count, Builtin().Count);
    }
}
