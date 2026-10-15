using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 敏感度分級與匯出政策（ULTIMATE-1000 階段一 P1-045、P1-046、P1-047）。
///
/// <para>
/// <b>既有的機制是三條彼此不相干的判斷：</b><c>HardwareFact.Sensitive</c> 一個布林、
/// <c>InterchangeAndReportFactsService.IsSensitiveKey</c> 一份硬編碼鍵清單、
/// <c>SensitiveValuePolicy</c> 一個兩值列舉。三者合起來能做事，但<b>說不出「這一欄為什麼被遮蔽」</b>——
/// 而 L1／L2／L3 的分野正是為了解釋這件事：L1 是本機元數據（遮蔽是禮貌）、
/// L2 是安全相關（遮蔽是預設且可解除）、L3 是禁項（<b>任何路徑都不得輸出</b>）。
/// </para>
///
/// <para>
/// <b>這一支不取代既有機制，而是補上它們缺的那一層：分級。</b>
/// 既有判斷仍然是那三條；新的分級是它們的<b>依據</b>——
/// 沒有分級，「為什麼這一欄被遮蔽」永遠只能回答「因為程式這樣寫」。
/// </para>
/// </summary>
public static class SensitivityPolicy
{
    public const string LevelKey = "fact.sensitivity";
    public const string MaskKey = "export.mask";
    public const string ReleaseKey = "export.release";

    private const string Category = "敏感度";

    /// <summary>
    /// 敏感度等級。
    /// <b>L3 與其他三級的差別不是程度而是種類</b>：L0–L2 是「要不要遮蔽」，
    /// L3 是「不存在可以輸出它的設定」。
    /// </summary>
    public enum Level
    {
        /// <summary>L0：硬體與版本事實，預設輸出。</summary>
        L0,
        /// <summary>L1：本機元數據（機器名、使用者名、路徑）——匯出時遮蔽。</summary>
        L1,
        /// <summary>L2：安全相關——匯出預設遮蔽，但使用者可逐類解除。</summary>
        L2,
        /// <summary>L3：禁項——任何功能不得輸出，顯示與匯出兩條路徑皆釘死。</summary>
        L3,
    }

    /// <summary>
    /// 由既有資料推導分級。<b>推導順序即優先序</b>：L3 最優先，
    /// 因為一個同時像 L1 又像 L3 的鍵，必須按 L3 處理。
    /// </summary>
    public static Level Classify(HardwareFact fact)
    {
        if (IsForbiddenKey(fact.Key)) return Level.L3;
        if (fact.Sensitive) return Level.L2;
        if (IsLocalMetadataKey(fact.Key)) return Level.L1;
        return Level.L0;
    }

    /// <summary>
    /// L3 禁項：這些鍵<b>不得出現在任何輸出中</b>，包括顯示與匯出。
    /// 清單必須與 <c>InterchangeAndReportFactsService.IsSensitiveKey</c> 的語意一致——
    /// 但那一份是「要遮蔽」，這一份是「不得存在」，兩者門檻不同。
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "secret.", "credential.", "key.private", "key.secret", "token.",
        "password.", "dpapi.", "lsa.secret", "sam.hash", "ntlm.",
    ];

    /// <summary>L1 本機元數據：會識別出「這台機器是誰的」但不涉及安全的欄位。</summary>
    private static readonly string[] LocalMetadataPrefixes =
    [
        "system.name", "user.", "profile.", "path.", "share.", "smb.",
        "net.hostname", "domain.", "machine.", "os.install.path",
    ];

    private static bool IsForbiddenKey(string key) =>
        ForbiddenPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsLocalMetadataKey(string key) =>
        LocalMetadataPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>產生 <c>fact.sensitivity</c>：逐級統計，並列出 L3 有幾筆（那個數字必須永遠是 0）。</summary>
    public static HardwareFact ToLevelFact(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts)
    {
        if (facts.Count == 0)
            return new HardwareFact(LevelKey, Category, "敏感度分級",
                "這一輪沒有任何事實", "筆", "敏感度政策", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有事實可以分級");

        var byLevel = facts.GroupBy(Classify).ToDictionary(g => g.Key, g => g.Count());
        int l3 = byLevel.TryGetValue(Level.L3, out int n3) ? n3 : 0;
        int Count(Level l) => byLevel.TryGetValue(l, out int n) ? n : 0;
        string detail = string.Join("、", new[] { Level.L0, Level.L1, Level.L2, Level.L3 }
            .Select(l => $"{l} {Count(l)}"));

        // 出現 L3 本身不是錯——錯的是「L3 被輸出」。這把鍵是統計，能不能輸出由 P1-048 的守門釘住。
        return new HardwareFact(LevelKey, Category, "敏感度分級", detail, "筆",
            "敏感度政策", FactTrustLevel.Derived, false, at, facts.Count, FactAvailability.Present,
            l3 > 0 ? $"本輪有 {l3} 筆 L3 禁項——它們不得出現在任何輸出中，這一筆統計本身只含鍵名不含值" : null);
    }

    /// <summary>
    /// 產生 <c>export.mask</c>：匯出時實際遮蔽了幾筆、依哪一級。
    /// </summary>
    public static HardwareFact ToMaskFact(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts,
        SensitiveValuePolicy policy)
    {
        if (facts.Count == 0)
            return new HardwareFact(MaskKey, Category, "匯出遮蔽",
                "這一輪沒有匯出任何事實", "筆", "敏感度政策", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有匯出可以統計");

        int l1 = facts.Count(f => Classify(f) == Level.L1);
        int l2 = facts.Count(f => Classify(f) == Level.L2);
        int masked = policy == SensitiveValuePolicy.Redact ? l1 + l2 : l1;

        string detail = policy == SensitiveValuePolicy.Redact
            ? $"遮蔽 {masked} 筆（L1 {l1}、L2 {l2}）"
            : $"只遮蔽 L1 {l1} 筆；L2 {l2} 筆依使用者選擇保留";

        return new HardwareFact(MaskKey, Category, "匯出遮蔽", detail, "筆",
            "敏感度政策", FactTrustLevel.Derived, false, at, masked,
            // Preserve 是使用者主動選的，不是缺陷——但那是一個需要被看見的狀態。
            policy == SensitiveValuePolicy.Redact ? FactAvailability.Present : FactAvailability.Unknown,
            policy == SensitiveValuePolicy.Redact ? null
                : "匯出保留了 L2 安全相關欄位——這是使用者主動選擇的結果，不是預設");
    }

    /// <summary>
    /// 產生 <c>export.release</c>：L2 逐類解除的紀錄。
    ///
    /// <para>
    /// <b>解除必須逐類、必須留紀錄。</b>「全部解除」不是一個選項——
    /// 那會讓 L2 的預設遮蔽形同虛設：使用者在對話框上按一次「確定」就等於
    /// 把安全相關欄位全部交出去，而他未必知道有哪些類。
    /// </para>
    /// </summary>
    public static HardwareFact ToReleaseFact(DateTimeOffset at,
        IReadOnlyCollection<string> availableClasses, IReadOnlyCollection<string> releasedClasses)
    {
        if (availableClasses.Count == 0)
            return new HardwareFact(ReleaseKey, Category, "L2 解除紀錄",
                "本輪沒有可解除的類別", "類", "敏感度政策", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有 L2 類別可以解除");

        if (releasedClasses.Count == 0)
            return new HardwareFact(ReleaseKey, Category, "L2 解除紀錄",
                $"可解除 {availableClasses.Count} 類，本輪未解除任何一類", "類",
                "敏感度政策", FactTrustLevel.Derived, false, at, 0, FactAvailability.Present, null);

        return new HardwareFact(ReleaseKey, Category, "L2 解除紀錄",
            $"已解除 {releasedClasses.Count}/{availableClasses.Count} 類：" + string.Join("、", releasedClasses),
            "類", "敏感度政策", FactTrustLevel.Derived, false, at, releasedClasses.Count,
            FactAvailability.Unknown,
            "L2 欄位已依使用者選擇解除遮蔽——該選擇應留下可稽核的紀錄");
    }

    /// <summary>
    /// P1-048 的掃描核心：找出<b>出現在任何輸出中</b>的 L3 事實。
    /// 空清單＝禁項沒有外洩。
    /// </summary>
    public static IReadOnlyList<HardwareFact> ForbiddenLeaks(IEnumerable<HardwareFact> facts)
        => facts.Where(f => Classify(f) == Level.L3).ToList();
}
