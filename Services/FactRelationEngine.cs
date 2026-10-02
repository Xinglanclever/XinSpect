namespace XinSpect;

/// <summary>事實之間的對帳結論（WP5 矛盾矩陣）：一致／矛盾／無法驗證。讀不到的輸入一律 Unverifiable，絕不冒充矛盾。</summary>
public enum FactRelation
{
    /// <summary>參與事實彼此一致（同版、同值、組合合理）。</summary>
    Consistent,
    /// <summary>參與事實互相矛盾（兩個獨立來源指著不同的事實，或組合在邏輯上不可能）。</summary>
    Contradicts,
    /// <summary>無法驗證：輸入事實缺任一件、非 Present、或缺少可比較的數值——誠實說不知道，不下判決。</summary>
    Unverifiable,
}

/// <summary>單條對帳規則的判定結果（規則只處理 Present 輸入；缺輸入由引擎統一轉 Unverifiable）。</summary>
public sealed record FactRelationOutcome(FactRelation Relation, string Reason)
{
    public static FactRelationOutcome Consistent(string reason) => new(FactRelation.Consistent, reason);
    public static FactRelationOutcome Contradicts(string reason) => new(FactRelation.Contradicts, reason);
    public static FactRelationOutcome Unverifiable(string reason) => new(FactRelation.Unverifiable, reason);
}

/// <summary>
/// 一條對帳規則：Id 唯一、輸入鍵清單明確、判定為純委派（給 Present 事實回結論）、Explanation 自我解釋（V7 §12.9）。
/// 建構子強制非空解釋——「規則要能解釋自己」是契約不是建議。
/// </summary>
public sealed record FactRelationRule
{
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> InputKeys { get; }
    public Func<IReadOnlyDictionary<string, HardwareFact>, FactRelationOutcome> Decide { get; }
    public string Explanation { get; }

    public FactRelationRule(string id, string name, IReadOnlyList<string> inputKeys,
        Func<IReadOnlyDictionary<string, HardwareFact>, FactRelationOutcome> decide, string explanation)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("規則 Id 不可空", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("規則名稱不可空", nameof(name));
        if (inputKeys.Count == 0) throw new ArgumentException("至少要一個輸入鍵", nameof(inputKeys));
        if (string.IsNullOrWhiteSpace(explanation)) throw new ArgumentException("規則必須自我解釋（V7 §12.9）", nameof(explanation));
        Id = id;
        Name = name;
        InputKeys = inputKeys;
        Decide = decide;
        Explanation = explanation;
    }
}

/// <summary>對帳結果一列：規則、結論、原因（進 UI／報告直接渲染）。</summary>
public sealed record FactRelationRow(string RuleId, string RuleName, FactRelation Relation, string Reason, string Explanation);

/// <summary>
/// 對帳引擎：輸入事實集合（允許重複鍵——以鍵分組）與規則清單，逐規則評估。
/// 引擎統一保證三件事：(1) 規則要的鍵缺任一件→Unverifiable 帶缺了什麼；(2) 輸入非 Present→Unverifiable 帶原因；
/// (3) 判定函式只拿到 Present 的事實——個別規則不必重複防禦三態。評估是純函式，可完整單測。
/// </summary>
public static class FactRelationService
{
    public static IReadOnlyList<FactRelationRow> Evaluate(
        IReadOnlyList<FactRelationRule> rules, IReadOnlyList<HardwareFact> facts)
    {
        var byKey = facts.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<FactRelationRow>(rules.Count);
        foreach (var rule in rules)
        {
            var outcome = EvaluateRule(rule, byKey);
            rows.Add(new FactRelationRow(rule.Id, rule.Name, outcome.Relation, outcome.Reason, rule.Explanation));
        }
        return rows;
    }

    private static FactRelationOutcome EvaluateRule(
        FactRelationRule rule, IReadOnlyDictionary<string, HardwareFact> byKey)
    {
        var missing = rule.InputKeys.Where(k => !byKey.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            return FactRelationOutcome.Unverifiable($"缺少輸入事實：{string.Join("、", missing)}——無法對帳，不下判決");

        var notPresent = rule.InputKeys
            .Select(k => byKey[k])
            .Where(f => f.Availability != FactAvailability.Present)
            .ToList();
        if (notPresent.Count > 0)
            return FactRelationOutcome.Unverifiable(
                $"輸入事實讀不到：{string.Join("、", notPresent.Select(f => f.Key))}（{notPresent[0].UnavailableReason ?? "原因不明"}）——無法對帳，不下判決");

        try
        {
            return rule.Decide(byKey);
        }
        catch (Exception ex)
        {
            // 規則本身出錯＝無法驗證，不是硬體矛盾；如實帶原因，絕不讓規則例外冒充 Contradicts
            return FactRelationOutcome.Unverifiable($"規則 {rule.Id} 內部錯誤：{ex.Message}");
        }
    }
}

/// <summary>
/// 內建對帳規則集（V7 §18.1 首批）。規則只看 Present 事實（引擎保證）；每條自我解釋。
/// 加規則＝這裡加一條＋測試一條，RuleId 全庫唯一。
/// </summary>
public static class FactRelationRules
{
    /// <summary>微碼一致性：Windows 說的（登錄檔 Update Revision）與 CPU 說的（MSR 0x8B 高 32 位）應指同一份微碼。</summary>
    public const string MicrocodeRegistryKey = "reg.microcode";
    /// <summary>MSR 0x8B（IA32_BIOS_SIGN_ID）高 32 位＝目前生效微碼修訂版。鍵名全小寫（事實鍵規則）。</summary>
    public const string MicrocodeMsrKey = "msr.0x8b";
    public const string MchbarBaseKey = "mchbar.base";
    public const string MchbarRegistersKey = "mchbar.registers";
    public const string TjMaxKey = "cpu.tjmax";
    public const string BiosCntlKey = "chipset.bios_cntl";
    public const string SmramcKey = "chipset.smramc";
    public const string SecureBootKey = "platform.secure_boot";
    public const string TestSigningKey = "platform.testsigning";
    public const string SecureBootUefiKey = "uefi.secure_boot";
    public const string EcamBaseKey = "pcieaer.ecam";
    public const string AerScanKey = "pcieaer.scan";
    public const string SpiWriteSurfaceKey = "spi.write_surface";
    public const string SpiFrapKey = "spi.frap";
    public const string SpiHsfstsKey = "spi.hsfsts";
    public const string BackendMmioKey = "backend.mmio";
    public const string BackendMsrKey = "backend.msr";
    public const string PlatformFeatureControlKey = "platform.feature_control";
    public const string HvciKey = "platform.hvci";
    public const string DecisionKey = "backend.environment_decision";

    public static readonly IReadOnlyList<FactRelationRule> All =
    [
        new("microcode.consistency", "微碼一致性（登錄檔 vs MSR 0x8B）",
            [MicrocodeRegistryKey, MicrocodeMsrKey],
            f =>
            {
                var reg = f[MicrocodeRegistryKey].NumericValue;
                var msr = f[MicrocodeMsrKey].NumericValue;
                if (reg is null || msr is null)
                    return FactRelationOutcome.Unverifiable("一側無法解析為數值（微碼修訂版應為 32 位元值）——無法比對");
                return reg == msr
                    ? FactRelationOutcome.Consistent($"登錄檔與 MSR 0x8B 同指 0x{(uint)msr:X8}——兩個獨立來源一致")
                    : FactRelationOutcome.Contradicts(
                        $"微碼不一致：登錄檔 0x{(uint)reg:X8} vs CPU 實際 0x{(uint)msr:X8}——Windows 認知與硬體不同版，優先信 MSR（CPU 自己說的）");
            },
            "Windows 登錄檔與 CPU 的 IA32_BIOS_SIGN_ID 是同一份微碼的兩個獨立視角；不一致代表覆蓋機制（mcupdate）狀態混亂或登錄檔過期"),

        new("mchbar.readability", "MCHBAR 映射與可用性",
            [MchbarBaseKey, MchbarRegistersKey],
            f =>
            {
                var registers = f[MchbarRegistersKey];
                return registers.Availability == FactAvailability.Present
                    ? FactRelationOutcome.Consistent("MCHBAR 基底已解析且暫存器視窗可讀")
                    : FactRelationOutcome.Unverifiable(
                        $"MCHBAR 基底已知（{f[MchbarBaseKey].Value}）但暫存器讀不到（{registers.UnavailableReason ?? "原因不明"}）——能力缺口，不是矛盾");
            },
            "基底（PCI BAR）與暫存器（MMIO）來自不同特權層；基底在而暫存器讀不到只說明 MMIO 能力缺，誠實標示而非誤判"),

        new("cpu.tjmax.sanity", "TjMax 合理範圍",
            [TjMaxKey],
            f =>
            {
                var tj = f[TjMaxKey].NumericValue;
                if (tj is null)
                    return FactRelationOutcome.Unverifiable("TjMax 無法解析為數值——無法檢查範圍");
                if (tj is < 50 or > 150)
                    return FactRelationOutcome.Contradicts(
                        $"TjMax={tj:0}°C 超出 x86 處理器合理範圍（50–150°C）——讀值或解碼可疑，不要用它算溫度餘量");
                return FactRelationOutcome.Consistent($"TjMax={tj:0}°C 在合理範圍");
            },
            "MSR_TEMPERATURE_TARGET 解碼若拿到離譜值（如 0 或 255），用它計算的溫度餘量會全部失真——先擋住明顯不可能的值"),

        new("chipset.smm_bwp_vs_smram_lock", "SMM_BWP 與 SMRAM 鎖定交叉",
            [BiosCntlKey, SmramcKey],
            f =>
            {
                bool smmBwp = f[BiosCntlKey].Value.Contains("SMM_BWP=1", StringComparison.Ordinal);
                bool smramLocked = f[SmramcKey].Value.Contains("D_LCK=1", StringComparison.Ordinal);
                if (smmBwp && !smramLocked)
                    return FactRelationOutcome.Contradicts(
                        "BIOS_CNTL 宣稱 SMM_BWP=1（僅 SMM 可寫 BIOS）但 SMRAMC 未鎖（D_LCK=0）——SMM_BWP 的保護語義依賴 SMRAM 鎖定，此組合不可信，視同未保護");
                return FactRelationOutcome.Consistent(
                    smmBwp ? "SMM_BWP=1 且 SMRAM 已鎖——保護語義成立" : "無 SMM_BWP 宣稱，不需 SMRAM 鎖定交叉");
            },
            "SMM_BWP 的「僅 SMM 可寫」只在 SMRAM 鎖定後才有意義（未鎖的 SMRAM 任何 ring0 都能進）；兩個暫存器要一起看"),

        new("platform.secureboot_vs_testsigning", "Secure Boot 與測試簽章互斥",
            [SecureBootKey, TestSigningKey],
            f =>
            {
                bool sbOn = f[SecureBootKey].Value == "開啟";
                bool tsOn = f[TestSigningKey].Value.StartsWith("測試簽章模式開啟", StringComparison.Ordinal);
                if (sbOn && tsOn)
                    return FactRelationOutcome.Contradicts(
                        "Secure Boot 開啟與測試簽章模式生效同時成立——測試簽章在 Secure Boot 開啟的系統上無法生效（核心拒絕例外載入），兩個獨立讀值至少一個有誤或環境遭特殊改動，不要採信任一單方結論");
                return FactRelationOutcome.Consistent(
                    sbOn ? "Secure Boot 開啟且未見測試簽章——語義相容"
                    : tsOn ? "測試簽章模式成立而 Secure Boot 未開啟——語義相容（安全態勢另行評估）"
                    : "兩者皆非「開啟」——無互斥疑慮");
            },
            "testsigning（CodeIntegrity 選項 0x2）與 UEFI Secure Boot 在核心層互斥：SB 開啟時 testsigning 旗標無法生效；同時讀到兩者「開」代表至少一個來源讀錯"),

        new("uefi.secureboot_vs_registry", "Secure Boot 雙來源（UEFI 變數 vs 登錄檔）",
            [SecureBootUefiKey, SecureBootKey],
            f =>
            {
                var uefi = f[SecureBootUefiKey].Value;
                var registry = f[SecureBootKey].Value;
                return uefi == registry
                    ? FactRelationOutcome.Consistent($"兩個獨立來源同指「{uefi}」——韌體變數與作業系統登錄檔一致")
                    : FactRelationOutcome.Contradicts(
                        $"Secure Boot 雙來源不一致：UEFI 變數＝「{uefi}」、登錄檔＝「{registry}」——這兩個來源由不同層寫入，不一致代表開機流程中狀態變更未同步或其一讀取有誤，優先信 UEFI 變數（韌體自己說的）");
            },
            "Secure Boot 的狀態同時存在於韌體變數與 Windows 登錄檔（兩個獨立寫入者）；比對它們是免費的一致性檢查——寫入不同步或快取過期都會現形"),

        new("pcieaer.scan_vs_ecam", "AER 掃描與 ECAM 基底資料流一致性",
            [AerScanKey],
            f =>
            {
                // 這條規則刻意只宣告 scan 為輸入鍵：ecam「缺席」正是矛盾條件之一，
                // 不能走引擎的「非 Present 一律 Unverifiable」守衛，須在規則內自行裁決。
                if (!f.TryGetValue(EcamBaseKey, out var ecam))
                    return FactRelationOutcome.Unverifiable("ECAM 基底事實不存在（收集管線未跑或鍵名錯置）——無從交叉");
                return ecam.Availability == FactAvailability.Present
                    ? FactRelationOutcome.Consistent("ECAM 基底存在且 AER 掃描有結果——上下游一致")
                    : FactRelationOutcome.Contradicts(
                        $"AER 掃描宣稱有結果但 ECAM 基底讀不到（{ecam.UnavailableReason ?? "原因不明"}）——掃描不可能沒有基底，管線狀態矛盾");
            },
            "AER 掃描結果衍生自 ECAM 基底（MCFG）：基底缺席時掃描不可能 Present。這條規則守的是管線自身的資料流一致性——它抓的矛盾來自程式而不是硬體，同樣該被看見"),
        new("backend.hvci_vs_decision", "環境矩陣裁決與 HVCI 事實資料流一致性",
            [HvciKey, DecisionKey],
            f =>
            {
                string hvci = f[HvciKey].Value;
                string decision = f[DecisionKey].Value;
                bool expectedOff = decision.Contains("HVCI 關閉", StringComparison.Ordinal);
                bool expectedOn = decision.Contains("HVCI 開啟", StringComparison.Ordinal);
                if (!expectedOff && !expectedOn)
                    return FactRelationOutcome.Unverifiable("裁決事實文字沒有可辨識的 HVCI 狀態——管線格式變了，先查程式");
                return (expectedOn == (hvci == "開啟"))
                    ? FactRelationOutcome.Consistent("環境矩陣裁決與 HVCI 事實一致")
                    : FactRelationOutcome.Contradicts(
                        $"裁決事實由 HVCI 推導，兩者卻不一致（HVCI＝「{hvci}」、裁決＝「{decision[..Math.Min(20, decision.Length)]}…」）——管線狀態矛盾");
            },
            "backend.environment_decision 由 platform.hvci 推導而來；上下游不一致代表收集管線自身的狀態錯亂——這條規則守的是程式而非硬體"),

        new("chipset.bioscntl_vs_write_surface", "BIOS_CNTL 原始解碼與綜合裁決一致性",
            [BiosCntlKey, SpiWriteSurfaceKey],
            f =>
            {
                static string Level(string text) =>
                    text.StartsWith("最強保護", StringComparison.Ordinal) ? "最強保護"
                    : text.StartsWith("有鎖保護", StringComparison.Ordinal) ? "有鎖保護" : "未保護";
                string cntl = Level(f[BiosCntlKey].Value);
                string surface = Level(f[SpiWriteSurfaceKey].Value);
                return cntl == surface
                    ? FactRelationOutcome.Consistent($"兩者同指「{cntl}」——綜合裁決忠實反映原始解碼")
                    : FactRelationOutcome.Contradicts(
                        $"綜合裁決宣稱「{surface}」但 BIOS_CNTL 原始解碼是「{cntl}」——綜合裁決的輸入之一就是 BIOS_CNTL，兩者不可能是不同等級，管線狀態矛盾");
            },
            "spi.write_surface 的主軸就是 BIOS_CNTL 的裁決階梯；上游與下游給出不同等級代表管線內部錯亂，不是硬體問題"),

        new("spi.frap_vs_write_surface", "FRAP 事實與綜合裁決暴露面一致性",
            [SpiFrapKey, SpiWriteSurfaceKey],
            f =>
            {
                bool frapWritable = f[SpiFrapKey].Value.Contains("BIOS 區域可寫入", StringComparison.Ordinal);
                bool surfaceGrant = f[SpiWriteSurfaceKey].Value.Contains("FRAP bit1=1", StringComparison.Ordinal);
                return frapWritable == surfaceGrant
                    ? FactRelationOutcome.Consistent("FRAP 事實與綜合裁決的暴露面描述一致")
                    : FactRelationOutcome.Contradicts(
                        $"FRAP 事實與綜合裁決不一致（FRAP＝{(frapWritable ? "可寫入" : "不可寫入")}、裁決暴露面{(surfaceGrant ? "有" : "無")}FRAP bit1=1）——管線狀態矛盾");
            },
            "綜合裁決把 FRAP bit1 列為暴露面的依據就是 spi.frap 事實本身；兩邊說不同的話代表資料流錯亂"),

        new("backend.mmio_vs_spi_facts", "SPI 事實與 MMIO 後端資料流一致性",
            [SpiHsfstsKey],
            f =>
            {
                // 後端事實「缺席」正是矛盾條件之一，刻意不列輸入鍵（會被引擎轉 Unverifiable），規則內自查。
                if (!f.TryGetValue(BackendMmioKey, out var mmio))
                    return FactRelationOutcome.Unverifiable("MMIO 後端事實不存在（收集管線未跑或鍵名錯置）——無從交叉");
                return mmio.Availability == FactAvailability.Present
                    ? FactRelationOutcome.Consistent("MMIO 後端在服務且 SPI 事實存在——上下游一致")
                    : FactRelationOutcome.Contradicts(
                        "SPI 事實宣稱 Present 但沒有任何 MMIO 後端在服務——SPI 暫存器只能經 MMIO 讀取，管線狀態矛盾");
            },
            "SPI 快閃暫存器只能經 MMIO 取得；backend.mmio 缺席時 SPI 事實不可能是 Present——守的是管線資料流而非硬體"),

        new("backend.msr_vs_platform_security", "平台安全 MSR 與 MSR 後端資料流一致性",
            [PlatformFeatureControlKey],
            f =>
            {
                if (!f.TryGetValue(BackendMsrKey, out var msr))
                    return FactRelationOutcome.Unverifiable("MSR 後端事實不存在（收集管線未跑或鍵名錯置）——無從交叉");
                return msr.Availability == FactAvailability.Present
                    ? FactRelationOutcome.Consistent("MSR 後端在服務且平台安全事實存在——上下游一致")
                    : FactRelationOutcome.Contradicts(
                        "平台安全 MSR 事實宣稱 Present 但沒有任何 MSR 後端在服務——管線狀態矛盾");
            },
            "IA32_FEATURE_CONTROL 只能經核心 MSR 讀取；backend.msr 缺席時該事實不可能是 Present——守的是管線資料流而非硬體"),

        new("chipset.smramc_open_while_locked", "SMRAM 鎖定下對外開放的非法組合",
            [SmramcKey],
            f =>
            {
                string text = f[SmramcKey].Value;
                bool locked = text.Contains("D_LCK=1", StringComparison.Ordinal);
                bool open = text.Contains("D_OPEN=1", StringComparison.Ordinal);
                return locked && open
                    ? FactRelationOutcome.Contradicts(
                        "SMRAMC 同時出現 D_LCK=1 與 D_OPEN=1——對 D_LCK 寫 1 會強制清 D_OPEN，兩者同時成立的狀態不該存在：暫存器遭異常改動或解碼有誤")
                    : FactRelationOutcome.Consistent("SMRAMC 未出現「鎖定下開放」的非法組合");
            },
            "Intel 對 SMRAMC 的定義：D_LCK 由 0 寫 1 時硬體強制清 D_OPEN。兩位元同時為 1 在合法流程中不可能出現——出現了就是警訊"),
    ];
}
