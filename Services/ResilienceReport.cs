using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 韌性、降級與失敗申報（ULTIMATE-1000 階段一 P1-085 – P1-095）。
///
/// <para>
/// <b>這一組的主題是「失敗要怎麼被對待」。</b>本專案的原則是：
/// 讀不到不是錯誤（那是硬體的事實），但<b>假裝讀到才是錯誤</b>。
/// 所以降級要宣告、逾時要有上界、例外要留下原因、失敗要有一張總表。
/// </para>
///
/// <para>
/// <b>P1-091「假成功禁止」是這一組的核心：</b>其他幾項都是它的配套——
/// 有降級宣告，使用者才知道現在看到的是降級結果；有失敗總表，
/// 才不會只看見成功的那些而以為一切正常。
/// </para>
/// </summary>
public static class ResilienceReport
{
    public const string EvolutionKey = "key.evolution";
    public const string GapKey = "coverage.gap";
    public const string DiagKey = "diag.count";
    public const string CircuitKey = "src.circuit";
    public const string DegradeKey = "degrade.mode";
    public const string ErrorReasonKey = "fact.error.reason";
    public const string RetryKey = "src.retry";
    public const string FailuresKey = "scan.failures";

    private const string Category = "韌性與降級";
    private const string Source = "掃描骨架／降級政策";

    // ── P1-085 鍵語意變更以新鍵表達 ────────────────────────────────────

    /// <summary>
    /// 產生 <c>key.evolution</c>：版號與鍵的增刪。
    /// <b>語意變更要用新鍵，不更名</b>——更名會讓歷史資料無法比較。
    /// </summary>
    public static HardwareFact ToEvolutionFact(DateTimeOffset at,
        string version, int addedKeys, int removedKeys, IReadOnlyList<string> renamedKeys)
    {
        if (renamedKeys.Count > 0)
            return new HardwareFact(EvolutionKey, Category, "鍵的演進",
                $"{version}：{addedKeys} 增、{removedKeys} 減，另有 {renamedKeys.Count} 個更名："
                + string.Join("、", renamedKeys), "鍵", Source, FactTrustLevel.Derived, false, at,
                addedKeys, FactAvailability.ReadError,
                "更名會讓歷史資料無法比較——語意變了就用新鍵，舊鍵保留並標為棄用");

        return new HardwareFact(EvolutionKey, Category, "鍵的演進",
            $"{version}：新增 {addedKeys} 鍵、移除 {removedKeys} 鍵、無更名", "鍵", Source,
            FactTrustLevel.Derived, false, at, addedKeys, FactAvailability.Present,
            "鍵是公開契約——增減可以，改名不行");
    }

    // ── P1-086 覆蓋缺口報告 ────────────────────────────────────────────

    /// <summary>產生 <c>coverage.gap</c>：逐鍵列出未被任何規則觸及的鍵。</summary>
    public static HardwareFact ToGapFact(DateTimeOffset at,
        IReadOnlyList<string> uncovered, int baseline)
    {
        if (uncovered.Count == 0)
            return new HardwareFact(GapKey, Category, "覆蓋缺口",
                "沒有未覆蓋的鍵", "鍵", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.Present, null);

        var availability = uncovered.Count <= baseline
            ? FactAvailability.NotApplicable    // 在基線內＝已知且可接受，不是缺陷
            : FactAvailability.Unknown;

        return new HardwareFact(GapKey, Category, "覆蓋缺口",
            $"{uncovered.Count} 鍵未被任何規則觸及（基線 {baseline}）："
            + string.Join("、", uncovered.Take(10)) + (uncovered.Count > 10 ? " …" : ""),
            "鍵", Source, FactTrustLevel.Derived, false, at, uncovered.Count, availability,
            uncovered.Count <= baseline
                ? "未覆蓋不是錯誤——強制全覆蓋會逼出湊數的規則，那比誠實申報一個數字更糟"
                : $"缺口增至 {uncovered.Count}（基線 {baseline}）——新增事實時請順手評估要不要納入對帳");
    }

    // ── P1-087 診斷紀錄分層 ────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>diag.count</c>：<b>已接住的診斷與未接住的崩潰分開計數</b>。
    /// 合在一起會讓「工具自己當掉」被算成「這台機器有問題」。
    /// </summary>
    public static HardwareFact ToDiagFact(DateTimeOffset at, int caught, int uncaught)
    {
        if (caught == 0 && uncaught == 0)
            return new HardwareFact(DiagKey, Category, "診斷紀錄",
                "本輪沒有診斷紀錄", "則", Source, FactTrustLevel.Reported, false, at,
                0, FactAvailability.NotApplicable, "沒有診斷可以申報");

        if (uncaught > 0)
            return new HardwareFact(DiagKey, Category, "診斷紀錄",
                $"已接住 {caught} 則、未接住 {uncaught} 則", "則", Source,
                FactTrustLevel.Reported, false, at, caught, FactAvailability.ReadError,
                $"有 {uncaught} 則未接住的例外——那是本程式的缺陷，不是這台機器的問題");

        return new HardwareFact(DiagKey, Category, "診斷紀錄",
            $"已接住 {caught} 則、未接住 0 則", "則", Source, FactTrustLevel.Reported,
            false, at, caught, FactAvailability.Present, null);
    }

    // ── P1-089 逾時與斷路器 ────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>src.circuit</c>：斷路器狀態。
    /// <b>斷路器開啟不是故障，是保護</b>——它讓一個壞掉的來源不再拖垮整輪掃描。
    /// </summary>
    public static HardwareFact ToCircuitFact(DateTimeOffset at,
        IReadOnlyList<(string Source, bool Open, int ConsecutiveFailures)> breakers)
    {
        if (breakers.Count == 0)
            return new HardwareFact(CircuitKey, Category, "來源斷路器",
                "本輪沒有登記任何來源", "個", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有來源可以檢查");

        var open = breakers.Where(b => b.Open).ToList();
        if (open.Count == 0)
            return new HardwareFact(CircuitKey, Category, "來源斷路器",
                $"{breakers.Count} 個來源全部正常", "個", Source, FactTrustLevel.Derived,
                false, at, breakers.Count, FactAvailability.Present, null);

        return new HardwareFact(CircuitKey, Category, "來源斷路器",
            $"{open.Count}/{breakers.Count} 個來源的斷路器已開啟：" +
            string.Join("、", open.Select(o => $"{o.Source}（連續失敗 {o.ConsecutiveFailures} 次）")),
            "個", Source, FactTrustLevel.Derived, false, at, breakers.Count - open.Count,
            FactAvailability.Unknown,
            "斷路器開啟是保護不是故障——它讓一個壞掉的來源不再拖垮整輪掃描，"
            + "但被跳過的來源在報告上必須顯示為『未取得』");
    }

    // ── P1-090 降級路徑宣告 ────────────────────────────────────────────

    /// <summary>產生 <c>degrade.mode</c>：各功能目前處於哪一種模式。</summary>
    public static HardwareFact ToDegradeFact(DateTimeOffset at,
        IReadOnlyList<(string Feature, string Mode)> features)
    {
        if (features.Count == 0)
            return new HardwareFact(DegradeKey, Category, "降級模式",
                "本輪沒有功能登記", "項", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有功能可以檢查");

        var degraded = features.Where(f => !f.Mode.Contains("完整", StringComparison.Ordinal)).ToList();
        if (degraded.Count == 0)
            return new HardwareFact(DegradeKey, Category, "降級模式",
                $"{features.Count} 項功能全部在完整模式", "項", Source,
                FactTrustLevel.Derived, false, at, features.Count, FactAvailability.Present, null);

        return new HardwareFact(DegradeKey, Category, "降級模式",
            $"{degraded.Count}/{features.Count} 項功能處於降級模式：" +
            string.Join("、", degraded.Select(d => $"{d.Feature}={d.Mode}")), "項", Source,
            FactTrustLevel.Derived, false, at, features.Count - degraded.Count,
            FactAvailability.Unknown,
            "降級本身不是錯誤，但使用者必須知道現在看到的是降級結果——"
            + "否則他會以為那張卡片上的空白是機器的實情");
    }

    // ── P1-092 例外訊息正規化 ──────────────────────────────────────────

    /// <summary>
    /// 產生 <c>fact.error.reason</c>：例外轉成事實鍵時，原因有沒有被保留。
    /// <b>只剩型別名而沒有訊息的原因，等於沒有原因。</b>
    /// </summary>
    public static HardwareFact ToErrorReasonFact(DateTimeOffset at,
        IReadOnlyList<(string Key, string? Reason)> errors)
    {
        if (errors.Count == 0)
            return new HardwareFact(ErrorReasonKey, Category, "失敗原因",
                "本輪沒有失敗的事實", "筆", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有失敗可以申報");

        var noReason = errors.Where(e => string.IsNullOrWhiteSpace(e.Reason)).ToList();
        var typeOnly = errors.Where(e => e.Reason is not null
            && !e.Reason.Contains(':') && !e.Reason.Contains('：')).ToList();

        if (noReason.Count > 0)
            return new HardwareFact(ErrorReasonKey, Category, "失敗原因",
                $"{noReason.Count}/{errors.Count} 筆失敗沒有附原因：" +
                string.Join("、", noReason.Select(e => e.Key)), "筆", Source,
                FactTrustLevel.Derived, false, at, errors.Count - noReason.Count,
                FactAvailability.ReadError,
                "有失敗卻沒附原因——使用者會被迫自己猜，而他猜不出來");

        if (typeOnly.Count > 0)
            return new HardwareFact(ErrorReasonKey, Category, "失敗原因",
                $"{typeOnly.Count} 筆只留下例外型別："
                + string.Join("、", typeOnly.Select(e => e.Key)), "筆", Source,
                FactTrustLevel.Derived, false, at, errors.Count - typeOnly.Count,
                FactAvailability.Unknown,
                "只剩型別名而沒有訊息的原因等於沒有原因——IOException 不是一個可處理的說明");

        return new HardwareFact(ErrorReasonKey, Category, "失敗原因",
            $"{errors.Count} 筆失敗全部附有可讀的原因", "筆", Source,
            FactTrustLevel.Derived, false, at, errors.Count, FactAvailability.Present, null);
    }

    // ── P1-094 失敗重試策略 ────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>src.retry</c>：重試與放棄的情形。
    /// <b>「重試後成功」與「重試到底都失敗」要分開</b>——前者是可用的資料，
    /// 後者是缺口，把它們算成同一個數字會讓缺口消失。
    /// </summary>
    public static HardwareFact ToRetryFact(DateTimeOffset at,
        int retriedAndSucceeded, int retriedAndFailed, int givenUp)
    {
        int total = retriedAndSucceeded + retriedAndFailed + givenUp;
        if (total == 0)
            return new HardwareFact(RetryKey, Category, "失敗重試",
                "本輪沒有重試", "次", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有重試可以申報");

        if (retriedAndFailed == 0 && givenUp == 0)
            return new HardwareFact(RetryKey, Category, "失敗重試",
                $"{retriedAndSucceeded} 次重試全部成功", "次", Source,
                FactTrustLevel.Derived, false, at, retriedAndSucceeded,
                FactAvailability.Present, null);

        return new HardwareFact(RetryKey, Category, "失敗重試",
            $"重試成功 {retriedAndSucceeded}、重試後仍失敗 {retriedAndFailed}、放棄 {givenUp}",
            "次", Source, FactTrustLevel.Derived, false, at, retriedAndSucceeded,
            FactAvailability.Unknown,
            $"有 {retriedAndFailed + givenUp} 次最終沒有拿到值——"
            + "那些是報告上的缺口，不能與重試成功混成同一個數字");
    }

    // ── P1-095 全域失敗摘要 ────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>scan.failures</c>：一次掃描的失敗總表。
    /// <b>沒有失敗時要明說「沒有失敗」而不是留空</b>——
    /// 空白的總表與「總表沒跑」在報告上長得一樣。
    /// </summary>
    public static HardwareFact ToFailuresFact(DateTimeOffset at, int steps, int failed,
        IReadOnlyList<string> failures)
    {
        if (steps == 0)
            return new HardwareFact(FailuresKey, Category, "掃描失敗總表",
                "這一輪沒有掃描步驟", "步", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有掃描可以統計");

        if (failed == 0)
            return new HardwareFact(FailuresKey, Category, "掃描失敗總表",
                $"{steps} 步全部完成，沒有任何失敗", "步", Source, FactTrustLevel.Derived,
                false, at, 0, FactAvailability.Present,
                "這是「跑過而且沒有失敗」，不是「沒有這張表」");

        return new HardwareFact(FailuresKey, Category, "掃描失敗總表",
            $"{failed}/{steps} 步失敗：" + string.Join("、", failures.Take(10)), "步",
            Source, FactTrustLevel.Derived, false, at, failed, FactAvailability.Unknown,
            $"{failed} 步沒有取得值——這張總表存在的理由是讓成功的那些不會掩蓋失敗的");
    }
}
