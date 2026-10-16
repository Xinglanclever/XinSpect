using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 信任根來源陳述（ULTIMATE-1000 階段二 P2-058）。
///
/// <para>
/// <b>「硬體信任根」是一個分類問題，而三種根不互斥。</b>
/// 它們是三條獨立的信任來源，可以並存：
/// </para>
///
/// <list type="bullet">
///   <item><b>Boot Guard</b>——錨在 <b>CPU 熔絲裡的 OEM 金鑰雜湊</b>。
///     本機看得到的證據是 FIT 目錄裡有 ACM／啟動模組條目。</item>
///   <item><b>TPM 度量</b>——錨在 <b>TPM 的背書金鑰（EK）</b>。
///     本機看得到的證據是關鍵 PCR 已被延伸。</item>
///   <item><b>Secure Boot</b>——錨在 <b>韌體裡的平台金鑰（PK）</b>。
///     本機看得到的證據是 Secure Boot 啟用。</item>
/// </list>
///
/// <para>
/// <b>界線，而這是本項最重要的一句：</b>
/// <b>「看得到證據」與「根已生效」之間有距離。</b>
/// 尤其 Boot Guard：<b>FIT 目錄裡有 ACM 條目不代表 Boot Guard 已啟用</b>
/// （既有的 <c>fw.bg.profile</c> 就是這樣說的，而它讀不到設定檔）。
/// 本服務只陳述<b>哪幾種根的證據存在</b>，不宣告「這台機器的根是 X」——
/// 後者需要查看熔絲與設定檔，而兩者都不在使用者模式的範圍內。
/// </para>
///
/// <para>
/// <b>本服務不自己去讀任何東西</b>——它接收已經產出的三組事實。
/// 這與 <see cref="MeasuredBootFactsService"/> 的 <c>FromFacts</c>、
/// <see cref="SmbiosCrossFactsService"/> 的 <c>OsView</c> 是同一個模式。
/// </para>
/// </summary>
public static class TrustRootFactsService
{
    private const string Category = "信賴根";
    private const string Source = "既有事實的交叉陳述（FIT 目錄／關鍵 PCR／Secure Boot）";

    public const string RootKey = "bt.root";
    public const string TypesKey = "bt.root.types";

    /// <summary>三種根的證據狀態。<c>null</c> 代表那一組事實讀不到。</summary>
    public sealed record Evidence(
        bool? BootGuardAcmInFit,   // FIT 目錄裡有 ACM 條目
        bool? MeasuredBoot,        // 關鍵 PCR 已延伸
        bool? SecureBootEnabled);  // Secure Boot 啟用

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Evidence? evidence)
    {
        if (evidence is null)
            return Na(at, "沒有任何信任根證據——本服務不自己去讀"
                + "（FIT 目錄、關鍵 PCR、Secure Boot 各有自己的可及性事實）",
                FactAvailability.NotSupported);

        var present = new List<(string Name, string Anchor, string EvidenceText)>();
        var absent = new List<string>();
        var unknown = new List<string>();

        Classify(evidence.BootGuardAcmInFit, "Boot Guard",
            "CPU 熔絲裡的 OEM 金鑰雜湊", "FIT 目錄裡有 ACM／啟動模組條目",
            present, absent, unknown);

        Classify(evidence.MeasuredBoot, "TPM 度量開機",
            "TPM 的背書金鑰（EK）", "關鍵 PCR 已被延伸",
            present, absent, unknown);

        Classify(evidence.SecureBootEnabled, "Secure Boot",
            "韌體裡的平台金鑰（PK）", "Secure Boot 已啟用",
            present, absent, unknown);

        if (present.Count == 0 && unknown.Count == 3)
            return Na(at, "三種根的證據都讀不到——**「讀不到」與「沒有」是不同的斷言**："
                + "前者代表這幾條路現在都不通，後者代表這台機器沒有硬體信任根",
                FactAvailability.InsufficientPrivilege);

        return
        [
            new HardwareFact(RootKey, Category, "信任根",
                present.Count == 0
                    ? "沒有觀察到任何一種根的證據"
                    : $"觀察到 {present.Count} 種：{string.Join("、", present.Select(p => p.Name))}",
                "種", Source, FactTrustLevel.Derived, false, at, present.Count,
                present.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
                present.Count == 0
                    ? "三組證據都讀得到，但都沒看到根——**那是一個有意義的結果**："
                      + "它排除了「讀不到所以看不到」這個解釋"
                    : "**三種根不互斥**：它們是三條獨立的信任來源，可以並存。"
                      + "本服務不宣告「這台機器的根是 X」——"
                      + "**「看得到證據」與「根已生效」之間有距離**，"
                      + "尤其 Boot Guard：FIT 目錄裡有 ACM 條目**不代表 Boot Guard 已啟用**"),

            new HardwareFact(TypesKey, Category, "信任根類型與錨點",
                present.Count == 0
                    ? "（無）"
                    : string.Join("；", present.Select(p => $"{p.Name}→{p.Anchor}（{p.EvidenceText}）")),
                "", Source, FactTrustLevel.Derived, false, at, present.Count,
                FactAvailability.Present,
                "每一種的名字後面是它的**錨點**，括號裡是**本機看得到的證據**。"
                + "**錨點本身看不到**：CPU 熔絲與 TPM 的 EK 都不在使用者模式的範圍內。"
                + (absent.Count > 0 ? $"沒有證據的：{string.Join("、", absent)}。" : "")
                + (unknown.Count > 0 ? $"讀不到的：{string.Join("、", unknown)}。" : "")
                + "**「沒有證據」與「讀不到」在這裡分開列**——把兩者混在一起"
                + "會讓一個讀不到的正常機器看起來像沒有信任根"),
        ];
    }

    private static void Classify(bool? value, string name, string anchor, string evidenceText,
        List<(string, string, string)> present, List<string> absent, List<string> unknown)
    {
        if (value is null) { unknown.Add(name); return; }
        if (value.Value) present.Add((name, anchor, evidenceText));
        else absent.Add(name);
    }

    /// <summary>
    /// 從既有的事實裡挑出三組證據。
    ///
    /// <para>
    /// <b>它不解碼任何東西，只是從已經產出的事實裡取值。</b>
    /// 找不到就回 <c>null</c>（<b>「讀不到」不是「沒有」</b>）。
    /// </para>
    /// </summary>
    public static Evidence? FromFacts(IReadOnlyList<HardwareFact> facts)
    {
        if (facts.Count == 0) return null;

        // 註：這裡原本有一個 `bool? Value(string key)` 的區域函式，而它**沒有被用**——
        // 每一組證據後來都改成直白的分支（見下面 secure boot 那段的三個判斷）。
        // 留著它會產生 CS8321，而建置警告的基線是 0（WarningBaselineTests）。

        // Boot Guard：FIT 條目裡有沒有 ACM／啟動模組。
        // fw.fit.summary 的字串裡有「ACM·Startup N」——**去讀它的字串是脆弱的**，
        // 所以本服務改用「條目數 > 0 且 fit 表存在」當證據，
        // 而把「ACM 有幾條」留給 fw.fit.summary 自己說。
        var fitCount = facts.FirstOrDefault(f => f.Key == "fw.fit.count");
        bool? acmInFit = fitCount is null ? null
            : fitCount.Availability == FactAvailability.Present && fitCount.NumericValue > 0;

        var measured = facts.FirstOrDefault(f => f.Key == MeasuredBootFactsService.MeasuredKey);
        bool? measuredBoot = measured is null ? null
            : measured.Availability switch
            {
                FactAvailability.Present => true,
                FactAvailability.NotApplicable => false,
                _ => null,   // **讀不到不是「沒有」**——InsufficientPrivilege／ReadError／Unknown
            };

        // Secure Boot：只看 platform.secure_boot 這把鍵。
        //
        // 第一版我把這個判斷塞進一個三元運算式，而它同時問了
        // 「鍵在不在」「Availability 是什麼」「NumericValue 是不是 0」三件事
        // ——測試用一個「鍵不存在」的輸入就把它逼出原型（回 False 而不是 null）。
        //
        // **一個運算式裡塞三個判斷，就會有一個判斷沒被驗到。**
        // 所以改成直白的 if，而每一條分支只回答一個問題：
        //   鍵不見了      → null（讀不到）
        //   Present       → true
        //   NotApplicable → false（讀得到，但沒有）
        //   其他（ReadError／InsufficientPrivilege／Unknown）
        //                 → null（**讀不到不是「沒有」**）
        bool? secureBoot = null;
        var sb = facts.FirstOrDefault(f => f.Key == "platform.secure_boot");
        if (sb is not null)
        {
            secureBoot = sb.Availability switch
            {
                FactAvailability.Present => true,
                FactAvailability.NotApplicable => false,
                _ => null,
            };
        }

        if (acmInFit is null && measuredBoot is null && secureBoot is null) return null;
        return new Evidence(acmInFit, measuredBoot, secureBoot);
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [RootKey, TypesKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
