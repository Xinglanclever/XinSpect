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
    /// <summary>MSR 0x8B（IA32_BIOS_SIGN_ID）高 32 位＝目前生效微碼修訂版。</summary>
    public const string MicrocodeMsrKey = "msr.0x8B";
    public const string MchbarBaseKey = "mchbar.base";
    public const string MchbarRegistersKey = "mchbar.registers";
    public const string TjMaxKey = "cpu.tjmax";
    public const string BiosCntlKey = "chipset.bios_cntl";
    public const string SmramcKey = "chipset.smramc";

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
    ];
}
