using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 發佈、建置與對外文件（ULTIMATE-1000 階段一 P1-096 – P1-105）。
///
/// <para>
/// <b>這一組處理的是「別人怎麼拿到與相信這份成果」：</b>
/// 建置可不可重現、版號與變更記錄有沒有同步、測試基線是不是真的、
/// 授權與限制有沒有講清楚、文件裡的數字能不能重跑驗證。
/// </para>
///
/// <para>
/// <b>P1-105「文件與程式對帳」是這一組的核心：</b>其餘幾項都是它的具體對象——
/// 徽章、基線、版號、限制清單都是文件裡的數字，而<b>文件裡的數字如果只能人工維護，
/// 它就一定會漂移</b>。所以每一項都要有一個「重跑得出來」的來源。
/// </para>
/// </summary>
public static class ReleaseReport
{
    public const string SingleFileKey = "build.singlefile";
    public const string ReproducibleKey = "build.reproducible";
    public const string VerifyKey = "release.verify";
    public const string BaselineKey = "tests.baseline";
    public const string NoticesKey = "license.notices";
    public const string CertPlanKey = "cert.plan";
    public const string LimitsKey = "limits.public";
    public const string GuideKey = "contrib.guide";
    public const string DocReconcileKey = "doc.reconcile";

    private const string Category = "發佈與建置";
    private const string Source = "發佈政策";

    // ── P1-096 單檔可攜發佈 ────────────────────────────────────────────

    /// <summary>產生 <c>build.singlefile</c>：單檔發佈的形態。</summary>
    public static HardwareFact ToSingleFileFact(DateTimeOffset at,
        bool singleFile, long sizeBytes, int bundledFiles)
    {
        if (!singleFile)
            return new HardwareFact(SingleFileKey, Category, "單檔發佈",
                "目前不是單檔發佈", "檔", Source, FactTrustLevel.Measured, false, at,
                bundledFiles, FactAvailability.NotSupported,
                "免安裝是產品方向；多檔發佈需要使用者解壓到某個目錄才能跑");

        return new HardwareFact(SingleFileKey, Category, "單檔發佈",
            $"單一執行檔 {sizeBytes / (1024.0 * 1024.0):0.0} MB（內含 {bundledFiles} 個組件）",
            "檔", Source, FactTrustLevel.Measured, false, at, 1, FactAvailability.Present,
            "單檔不等於免依賴——它仍需要對應的 .NET 執行階段，這一點必須寫在限制文件裡");
    }

    // ── P1-097 建置可重現 ──────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>build.reproducible</c>：兩次建置的輸出是否相同。
    /// <b>「大部分相同」不算可重現</b>——只要有一個位元組不同，驗證就沒有意義。
    /// </summary>
    public static HardwareFact ToReproducibleFact(DateTimeOffset at,
        string? firstHash, string? secondHash)
    {
        if (firstHash is null || secondHash is null)
            return new HardwareFact(ReproducibleKey, Category, "建置可重現",
                "沒有可比對的兩次建置", "", Source, FactTrustLevel.Measured, false, at,
                null, FactAvailability.NotApplicable, "要判定可重現至少需要兩次建置");

        if (string.Equals(firstHash, secondHash, StringComparison.Ordinal))
            return new HardwareFact(ReproducibleKey, Category, "建置可重現",
                $"兩次建置雜湊相同：{firstHash}", "", Source, FactTrustLevel.Measured,
                false, at, null, FactAvailability.Present,
                "相同來源得相同輸出——這讓「這份執行檔是不是從那個 commit 建的」變成可回答的問題");

        return new HardwareFact(ReproducibleKey, Category, "建置可重現",
            $"兩次建置雜湊不同：{firstHash} / {secondHash}", "", Source,
            FactTrustLevel.Measured, false, at, null, FactAvailability.ReadError,
            "只要有一個位元組不同就不可重現——「大部分相同」不是可重現");
    }

    // ── P1-099 資產雜湊與發佈驗證 ──────────────────────────────────────

    /// <summary>產生 <c>release.verify</c>：發佈資產與其雜湊。</summary>
    public static HardwareFact ToVerifyFact(DateTimeOffset at,
        IReadOnlyList<(string Asset, string Sha256, bool Verifiable)> assets, bool scriptPresent)
    {
        if (assets.Count == 0)
            return new HardwareFact(VerifyKey, Category, "發佈驗證",
                "沒有發佈資產", "項", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable, "沒有資產可以驗證");

        var bad = assets.Where(a => !a.Verifiable).ToList();
        if (!scriptPresent)
            return new HardwareFact(VerifyKey, Category, "發佈驗證",
                $"{assets.Count} 項資產，但沒有驗證腳本", "項", Source,
                FactTrustLevel.Measured, false, at, assets.Count, FactAvailability.Unknown,
                "沒有腳本，使用者只能自己算雜湊——那等於要求他信任我們的說明");

        if (bad.Count > 0)
            return new HardwareFact(VerifyKey, Category, "發佈驗證",
                $"{bad.Count} 項資產沒有可驗的雜湊：" + string.Join("、", bad.Select(b => b.Asset)),
                "項", Source, FactTrustLevel.Measured, false, at, assets.Count - bad.Count,
                FactAvailability.ReadError, "沒有雜湊的資產無從驗證——下載之後無法確認它有沒有被換過");

        return new HardwareFact(VerifyKey, Category, "發佈驗證",
            $"{assets.Count} 項資產全部附 SHA-256，並有驗證腳本", "項", Source,
            FactTrustLevel.Measured, false, at, assets.Count, FactAvailability.Present, null);
    }

    // ── P1-100 測試基線 ────────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>tests.baseline</c>：基線與實際的關係。
    /// <b>基線高於實際＝有測試消失了</b>；低於實際＝有測試加了但基線沒更新。
    /// 兩者都要被看見，但意義相反。
    /// </summary>
    public static HardwareFact ToBaselineFact(DateTimeOffset at, int baseline, int actual,
        IReadOnlyList<int> badgeValues)
    {
        if (actual == 0)
            return new HardwareFact(BaselineKey, Category, "測試基線",
                "沒有測到任何測試", "條", Source, FactTrustLevel.Measured, false, at,
                baseline, FactAvailability.ReadError, "跑不到測試——基線不能反映任何事");

        var wrongBadges = badgeValues.Where(b => b != baseline).ToList();
        string detail = $"基線 {baseline}、實際 {actual}、徽章 {string.Join("/", badgeValues)}";

        if (actual < baseline)
            return new HardwareFact(BaselineKey, Category, "測試基線", detail, "條", Source,
                FactTrustLevel.Measured, false, at, actual, FactAvailability.ReadError,
                $"實際比基線少 {baseline - actual} 條——有測試消失了，這比測試失敗更嚴重");

        if (wrongBadges.Count > 0)
            return new HardwareFact(BaselineKey, Category, "測試基線", detail, "條", Source,
                FactTrustLevel.Measured, false, at, actual, FactAvailability.ReadError,
                $"徽章與基線不一致（{wrongBadges.Count} 份）——徽章是給外人看的數字，對不上就是說謊");

        if (actual > baseline)
            return new HardwareFact(BaselineKey, Category, "測試基線", detail, "條", Source,
                FactTrustLevel.Measured, false, at, actual, FactAvailability.Unknown,
                $"實際比基線多 {actual - baseline} 條——加了測試但基線沒更新，"
                + "在下一次發版前這是預期狀態，但不能忘記");

        return new HardwareFact(BaselineKey, Category, "測試基線", detail, "條", Source,
            FactTrustLevel.Measured, false, at, actual, FactAvailability.Present, null);
    }

    // ── P1-101 授權與第三方聲明 ────────────────────────────────────────

    /// <summary>產生 <c>license.notices</c>：授權與第三方元件聲明。</summary>
    public static HardwareFact ToNoticesFact(DateTimeOffset at,
        bool licensePresent, int thirdPartyCount, int declaredThirdParty)
    {
        if (!licensePresent)
            return new HardwareFact(NoticesKey, Category, "授權與第三方聲明",
                "沒有授權檔", "項", Source, FactTrustLevel.Reported, false, at,
                0, FactAvailability.ReadError, "沒有授權檔的發佈等於沒有說明使用條件");

        if (thirdPartyCount > declaredThirdParty)
            return new HardwareFact(NoticesKey, Category, "授權與第三方聲明",
                $"用了 {thirdPartyCount} 個第三方元件，只聲明 {declaredThirdParty} 個", "項",
                Source, FactTrustLevel.Reported, false, at, declaredThirdParty,
                FactAvailability.ReadError,
                $"有 {thirdPartyCount - declaredThirdParty} 個第三方元件沒被聲明——"
                + "沒聲明的授權義務還是義務");

        return new HardwareFact(NoticesKey, Category, "授權與第三方聲明",
            $"授權檔齊備，{declaredThirdParty} 個第三方元件全部聲明", "項", Source,
            FactTrustLevel.Reported, false, at, declaredThirdParty,
            FactAvailability.Present, null);
    }

    // ── P1-102 認證計畫 ────────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>cert.plan</c>：認證計畫。
    /// <b>「已送出」與「已通過」是兩件事</b>——把它們混為一談是最容易發生的誇大。
    /// </summary>
    public static HardwareFact ToCertPlanFact(DateTimeOffset at,
        IReadOnlyList<(string Item, string State)> items)
    {
        if (items.Count == 0)
            return new HardwareFact(CertPlanKey, Category, "認證計畫",
                "沒有認證項目", "項", Source, FactTrustLevel.Reported, false, at,
                0, FactAvailability.NotApplicable, "沒有認證項目可以申報");

        int passed = items.Count(i => i.State.Contains("通過", StringComparison.Ordinal));
        var pending = items.Where(i => !i.State.Contains("通過", StringComparison.Ordinal)).ToList();

        if (pending.Count == 0)
            return new HardwareFact(CertPlanKey, Category, "認證計畫",
                $"{items.Count} 項全部通過", "項", Source, FactTrustLevel.Reported,
                false, at, passed, FactAvailability.Present, null);

        // 未通過的項目要逐一列出狀態——「已送出」不能被讀成「已通過」。
        return new HardwareFact(CertPlanKey, Category, "認證計畫",
            $"{passed}/{items.Count} 項通過；其餘：" +
            string.Join("、", pending.Select(p => $"{p.Item}={p.State}")), "項", Source,
            FactTrustLevel.Reported, false, at, passed, FactAvailability.Unknown,
            "未通過的項目如實列出狀態——『已送出』不等於『已通過』");
    }

    // ── P1-103 公開限制文件 ────────────────────────────────────────────

    /// <summary>產生 <c>limits.public</c>：對外限制清單。</summary>
    public static HardwareFact ToLimitsFact(DateTimeOffset at,
        IReadOnlyList<string> limits, bool documentPresent)
    {
        if (!documentPresent)
            return new HardwareFact(LimitsKey, Category, "公開限制",
                "沒有對外的限制文件", "條", Source, FactTrustLevel.Reported, false, at,
                0, FactAvailability.ReadError,
                "限制沒有寫出來，使用者就會假設它沒有——而每一個限制都會在某個場合變成他的問題");

        if (limits.Count == 0)
            return new HardwareFact(LimitsKey, Category, "公開限制",
                "限制文件存在但沒有任何條目", "條", Source, FactTrustLevel.Reported,
                false, at, 0, FactAvailability.Unknown,
                "空的限制清單不可信——任何工具都有它做不到的事");

        string detail = $"{limits.Count} 條限制，例如：" + string.Join("、", limits.Take(4));
        return new HardwareFact(LimitsKey, Category, "公開限制", detail, "條", Source,
            FactTrustLevel.Reported, false, at, limits.Count, FactAvailability.Present,
            "限制清單的可信度取決於它有多具體——『可能不準』不是一條限制");
    }

    // ── P1-104 貢獻者實作指南 ──────────────────────────────────────────

    /// <summary>產生 <c>contrib.guide</c>：新來源的加入程序。</summary>
    public static HardwareFact ToGuideFact(DateTimeOffset at,
        IReadOnlyList<string> steps, bool guidePresent)
    {
        if (!guidePresent || steps.Count == 0)
            return new HardwareFact(GuideKey, Category, "貢獻者指南",
                "沒有貢獻者指南", "步", Source, FactTrustLevel.Reported, false, at,
                0, FactAvailability.ReadError,
                "沒有加入程序，新來源就會各自發明流程——而流程的每一條都是踩過的坑");

        // 一個完整的加入程序至少要涵蓋：宣告、實作、鍵、測試、出處。
        string[] required = ["宣告", "實作", "鍵", "測試", "出處"];
        var covered = required.Where(r => steps.Any(s => s.Contains(r, StringComparison.Ordinal))).ToList();
        var missing = required.Except(covered).ToList();

        if (missing.Count > 0)
            return new HardwareFact(GuideKey, Category, "貢獻者指南",
                $"{steps.Count} 步，缺：{string.Join("、", missing)}", "步", Source,
                FactTrustLevel.Reported, false, at, covered.Count, FactAvailability.Unknown,
                $"加入程序缺少 {missing.Count} 個必要環節——缺的那一環就是下一個人會漏掉的東西");

        return new HardwareFact(GuideKey, Category, "貢獻者指南",
            $"{steps.Count} 步，涵蓋宣告／實作／鍵／測試／出處", "步", Source,
            FactTrustLevel.Reported, false, at, steps.Count, FactAvailability.Present, null);
    }

    // ── P1-105 文件與程式對帳 ──────────────────────────────────────────

    /// <summary>
    /// 產生 <c>doc.reconcile</c>：文件裡的數字能不能重跑驗證。
    /// <b>這一項問的不是「數字對不對」，而是「有沒有一個重跑得出來的來源」。</b>
    /// 只能人工維護的數字一定會漂移——不是因為人不小心，是因為它需要有人一直記得。
    /// </summary>
    public static HardwareFact ToDocReconcileFact(DateTimeOffset at,
        IReadOnlyList<(string Claim, string Source, bool Rerunnable)> claims)
    {
        if (claims.Count == 0)
            return new HardwareFact(DocReconcileKey, Category, "文件數字對帳",
                "文件沒有可對帳的數字", "項", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有數字可以對帳");

        var manual = claims.Where(c => !c.Rerunnable).ToList();
        if (manual.Count == 0)
            return new HardwareFact(DocReconcileKey, Category, "文件數字對帳",
                $"{claims.Count} 項數字全部可重跑驗證", "項", Source,
                FactTrustLevel.Derived, false, at, claims.Count, FactAvailability.Present, null);

        return new HardwareFact(DocReconcileKey, Category, "文件數字對帳",
            $"{manual.Count}/{claims.Count} 項數字只能人工維護：" +
            string.Join("、", manual.Select(m => m.Claim)), "項", Source,
            FactTrustLevel.Derived, false, at, claims.Count - manual.Count,
            FactAvailability.Unknown,
            "只能人工維護的數字一定會漂移——不是因為人不小心，是因為它需要有人一直記得");
    }
}
