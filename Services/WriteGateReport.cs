using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 寫入閘門與審計鏈的申報（ULTIMATE-1000 階段一 P1-065 – P1-074）。
///
/// <para>
/// <b>本專案的立場是「唯讀為主、寫入必經閘門」。</b>
/// 既有的 <see cref="AuditLogService"/> 與 <see cref="AuditVerifier"/> 已經把
/// 追加寫入、雜湊鏈、竄改偵測做好了——缺的同樣是把那些事實變成輸出，
/// 以及<b>把「哪些寫入被允許」寫成一份可檢查的清單</b>。
/// </para>
///
/// <para>
/// <b>為什麼「寫入白名單」比「寫入計數」重要：</b>計數只能事後發現「寫了很多」，
/// 白名單能在寫入發生<b>之前</b>回答「這一種寫入有沒有被允許」。
/// 沒有白名單，閘門就只是一本帳，不是一道門。
/// </para>
/// </summary>
public static class WriteGateReport
{
    public const string InventoryKey = "guard.inventory";
    public const string CountKey = "write.count";
    public const string LedgerKey = "write.ledger";
    public const string AllowlistKey = "write.allowlist";
    public const string RollbackKey = "write.rollback";
    public const string HeadKey = "audit.head";
    public const string VerifyKey = "audit.verify";
    public const string ManifestKey = "ev.manifest";
    public const string ChainVerifyKey = "ev.chain.verify";

    private const string Category = "寫入與審計";
    private const string Source = "寫入閘門／審計鏈";

    // ── P1-073 寫入白名單 ──────────────────────────────────────────────

    /// <summary>
    /// 一筆被允許的寫入。<b>沒有登記在這裡的寫入動詞一律不得發生。</b>
    /// </summary>
    /// <param name="Verb">寫入種類（例如「審計日誌追加」、「時間膠囊存檔」）。</param>
    /// <param name="Target">寫到哪裡。</param>
    /// <param name="Reversible">能不能撤銷。</param>
    /// <param name="Justification">為什麼需要這個寫入。</param>
    public sealed record AllowedWrite(string Verb, string Target, bool Reversible, string Justification)
    {
        public bool IsWellFormed =>
            !string.IsNullOrWhiteSpace(Verb) && !string.IsNullOrWhiteSpace(Target)
            && !string.IsNullOrWhiteSpace(Justification) && Justification.Trim().Length >= 10;
    }

    /// <summary>本專案允許的寫入清單。<b>新增寫入路徑時必須登記，否則閘門會拒收。</b></summary>
    public static IReadOnlyList<AllowedWrite> Allowlist { get; } =
    [
        new("審計日誌追加", "%ProgramData%\\XinSpect\\Audit\\audit.json", Reversible: false,
            "審計是 append-only；不可撤銷正是它的性質，撤銷能力會讓竄改變得可能"),
        new("時間膠囊存檔", "使用者自選路徑", Reversible: true,
            "使用者主動匯出的事實快照；覆寫前保留舊檔即為回復路徑"),
        new("原始暫存器傾印", "使用者自選路徑", Reversible: true,
            "深測的原始位元組；與時間膠囊同一個回復路徑"),
        new("深測結果儲存", "%TEMP%\\XinSpectDeepBench（可改）", Reversible: true,
            "量測結果的暫存；使用者可清空整個目錄"),
        new("驅動憑證佈署", "%ProgramData%\\XinSpect\\Driver", Reversible: true,
            "深層存取的自簽 CA 與金鑰；解除安裝時移除"),
    ];

    /// <summary>產生 <c>write.allowlist</c>。</summary>
    public static HardwareFact ToAllowlistFact(DateTimeOffset at)
    {
        var malformed = Allowlist.Where(w => !w.IsWellFormed).ToList();
        if (malformed.Count > 0)
            return new HardwareFact(AllowlistKey, Category, "寫入白名單",
                $"{Allowlist.Count} 條登記，其中 {malformed.Count} 條不完整", "條",
                Source, FactTrustLevel.Derived, false, at, Allowlist.Count - malformed.Count,
                FactAvailability.ReadError,
                "白名單條目不完整（缺動詞／目標／理由）——不完整的白名單等於沒有白名單："
                + string.Join("、", malformed.Select(m => m.Verb)));

        int irreversible = Allowlist.Count(w => !w.Reversible);
        return new HardwareFact(AllowlistKey, Category, "寫入白名單",
            $"{Allowlist.Count} 條允許的寫入，其中 {irreversible} 條不可撤銷"
            + (irreversible > 0 ? "（" + string.Join("、", Allowlist.Where(w => !w.Reversible).Select(w => w.Verb)) + "）" : ""),
            "條", Source, FactTrustLevel.Derived, false, at, Allowlist.Count,
            FactAvailability.Present,
            irreversible > 0
                ? "不可撤銷的寫入是刻意設計（審計的性質），但它必須被逐一列出來讓人看見"
                : null);
    }

    /// <summary>把一個想寫入的動詞對白名單。沒登記＝拒收。</summary>
    public static (bool Allowed, string Reason) Check(string verb)
    {
        var hit = Allowlist.FirstOrDefault(w =>
            string.Equals(w.Verb, verb, StringComparison.Ordinal));
        return hit is not null
            ? (true, $"已登記：{hit.Target}")
            : (false, $"「{verb}」不在寫入白名單內——一切寫入必須經閘門，未登記的寫入一律拒收");
    }

    // ── P1-067／P1-068 寫入計數與記帳 ──────────────────────────────────

    /// <summary>一筆寫入紀錄。四個欄位缺一不可。</summary>
    public sealed record WriteRecord(string Verb, string Target, DateTimeOffset At, string Origin)
    {
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(Verb) && !string.IsNullOrWhiteSpace(Target)
            && !string.IsNullOrWhiteSpace(Origin);
    }

    /// <summary>產生 <c>write.count</c>：寫入次數與被拒次數。</summary>
    public static HardwareFact ToCountFact(DateTimeOffset at, int writes, int rejected)
    {
        if (writes == 0 && rejected == 0)
            return new HardwareFact(CountKey, Category, "寫入計數",
                "本輪沒有任何寫入", "次", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有寫入可以統計");

        if (rejected > 0)
            // 被拒不是壞事——它證明閘門有牙。但一定要被看見。
            return new HardwareFact(CountKey, Category, "寫入計數",
                $"{writes} 次寫入，另有 {rejected} 次被閘門拒收", "次",
                Source, FactTrustLevel.Derived, false, at, writes,
                FactAvailability.Unknown,
                $"{rejected} 次寫入不在白名單內被拒——閘門正常運作，但那些路徑需要檢視");

        return new HardwareFact(CountKey, Category, "寫入計數",
            $"{writes} 次寫入，全部在白名單內", "次", Source, FactTrustLevel.Derived,
            false, at, writes, FactAvailability.Present, null);
    }

    /// <summary>產生 <c>write.ledger</c>：逐筆紀錄，缺欄位的要指出來。</summary>
    public static HardwareFact ToLedgerFact(DateTimeOffset at, IReadOnlyList<WriteRecord> records)
    {
        if (records.Count == 0)
            return new HardwareFact(LedgerKey, Category, "寫入記帳",
                "本輪沒有寫入紀錄", "筆", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有寫入可以記帳");

        var incomplete = records.Where(r => !r.IsComplete).ToList();
        if (incomplete.Count > 0)
            return new HardwareFact(LedgerKey, Category, "寫入記帳",
                $"{records.Count} 筆紀錄，其中 {incomplete.Count} 筆欄位不全", "筆",
                Source, FactTrustLevel.Derived, false, at, records.Count - incomplete.Count,
                FactAvailability.ReadError,
                "寫入紀錄缺對象／原因／時間／來源即無法稽核——四欄缺一不可");

        string detail = $"{records.Count} 筆寫入：" + string.Join("、",
            records.GroupBy(r => r.Verb).Select(g => $"{g.Key} {g.Count()}"));
        return new HardwareFact(LedgerKey, Category, "寫入記帳", detail, "筆", Source,
            FactTrustLevel.Derived, false, at, records.Count, FactAvailability.Present, null);
    }

    // ── P1-074 撤銷與回復 ──────────────────────────────────────────────

    /// <summary>產生 <c>write.rollback</c>：不可撤銷的寫入必須逐一列出，且有理由。</summary>
    public static HardwareFact ToRollbackFact(DateTimeOffset at, IReadOnlyList<WriteRecord> records)
    {
        if (records.Count == 0)
            return new HardwareFact(RollbackKey, Category, "撤銷與回復",
                "本輪沒有寫入", "筆", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有寫入需要回復路徑");

        var irreversible = records.Where(r =>
            Allowlist.Any(w => w.Verb == r.Verb && !w.Reversible)).ToList();
        var undeclared = records.Where(r => Allowlist.All(w => w.Verb != r.Verb)).ToList();

        if (undeclared.Count > 0)
            return new HardwareFact(RollbackKey, Category, "撤銷與回復",
                $"{undeclared.Count} 筆寫入的動詞不在白名單內，無從判斷可否撤銷", "筆",
                Source, FactTrustLevel.Derived, false, at, records.Count - undeclared.Count,
                FactAvailability.ReadError,
                "未登記的寫入沒有回復路徑可言——先登記才有得判斷");

        if (irreversible.Count == 0)
            return new HardwareFact(RollbackKey, Category, "撤銷與回復",
                $"{records.Count} 筆寫入全部可撤銷", "筆", Source, FactTrustLevel.Derived,
                false, at, records.Count, FactAvailability.Present, null);

        return new HardwareFact(RollbackKey, Category, "撤銷與回復",
            $"{irreversible.Count} 筆寫入不可撤銷：" + string.Join("、", irreversible.Select(r => r.Verb).Distinct()),
            "筆", Source, FactTrustLevel.Derived, false, at, records.Count - irreversible.Count,
            FactAvailability.Unknown,
            "不可撤銷的寫入已發生——它們的回復路徑是「知道它不可回復」，這件事必須被明說");
    }

    // ── P1-069／P1-070 審計鏈頭與獨立驗證 ──────────────────────────────

    /// <summary>產生 <c>audit.head</c>：鏈頭（最後一筆的雜湊）與長度。</summary>
    public static HardwareFact ToHeadFact(DateTimeOffset at, IReadOnlyList<AuditEntry> entries)
    {
        if (entries.Count == 0)
            return new HardwareFact(HeadKey, Category, "審計鏈頭",
                $"空鏈（創始雜湊 {AuditEntry.Genesis}）", "筆", Source, FactTrustLevel.Measured,
                false, at, 0, FactAvailability.NotApplicable, "日誌尚無任何一筆");

        return new HardwareFact(HeadKey, Category, "審計鏈頭",
            $"第 {entries.Count} 筆、鏈頭 {entries[^1].EntryHash}", "筆", Source,
            FactTrustLevel.Measured, false, at, entries.Count, FactAvailability.Present,
            "鏈頭可對外公告：任何後續改動都會讓它對不上");
    }

    /// <summary>
    /// 產生 <c>audit.verify</c>：獨立驗證的結果。
    /// <b>驗證不依賴被驗證的對象</b>——只吃位元組，不問寫入者是誰。
    /// </summary>
    public static HardwareFact ToVerifyFact(DateTimeOffset at, AuditVerifier.AuditVerdict verdict)
    {
        if (verdict.CheckedCount == 0 && verdict.Valid)
            return new HardwareFact(VerifyKey, Category, "審計鏈驗證",
                "空鏈，無從驗證", "筆", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable, "沒有紀錄可以驗證");

        return verdict.Valid
            ? new HardwareFact(VerifyKey, Category, "審計鏈驗證",
                $"{verdict.CheckedCount} 筆全部通過（雜湊重算＋鏈串接＋序號連續）", "筆",
                Source, FactTrustLevel.Measured, false, at, verdict.CheckedCount,
                FactAvailability.Present, null)
            : new HardwareFact(VerifyKey, Category, "審計鏈驗證",
                $"驗證在第 {verdict.CheckedCount + 1} 筆失敗：{verdict.FailureReason}", "筆",
                Source, FactTrustLevel.Measured, false, at, verdict.CheckedCount,
                FactAvailability.ReadError, verdict.FailureReason);
    }

    // ── P1-071／P1-072 證據包 manifest 與互驗 ──────────────────────────

    /// <summary>產生 <c>ev.manifest</c>：包內檔案的雜湊清單。</summary>
    public static HardwareFact ToManifestFact(DateTimeOffset at,
        IReadOnlyList<(string File, string Sha256)> manifest, IReadOnlyList<string> missing)
    {
        if (manifest.Count == 0)
            return new HardwareFact(ManifestKey, Category, "證據包清單",
                "清單為空——包裡沒有檔案", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.ReadError, "空的 manifest 不能證明任何事——它只證明「沒有東西」");

        if (missing.Count > 0)
            return new HardwareFact(ManifestKey, Category, "證據包清單",
                $"{manifest.Count} 個檔案，其中 {missing.Count} 個不在包裡："
                + string.Join("、", missing.Take(5)), "個", Source, FactTrustLevel.Measured, false, at,
                manifest.Count - missing.Count, FactAvailability.ReadError,
                "清單列出而包裡沒有的檔案——那份證據包不完整");

        return new HardwareFact(ManifestKey, Category, "證據包清單",
            $"{manifest.Count} 個檔案，全部附 SHA-256 且在包裡", "個", Source,
            FactTrustLevel.Measured, false, at, manifest.Count, FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>ev.chain.verify</c>：審計鏈與 manifest <b>互驗</b>。
    /// 兩者都通過才算出報告——單獨一個通過不足以證明什麼。
    /// </summary>
    public static HardwareFact ToChainVerifyFact(DateTimeOffset at,
        bool chainValid, bool manifestValid, string? failure)
    {
        if (chainValid && manifestValid)
            return new HardwareFact(ChainVerifyKey, Category, "證據包互驗",
                "審計鏈與 manifest 互相印證", "項", Source, FactTrustLevel.Measured, false, at,
                2, FactAvailability.Present, null);

        var failed = new List<string>();
        if (!chainValid) failed.Add("審計鏈");
        if (!manifestValid) failed.Add("manifest");

        return new HardwareFact(ChainVerifyKey, Category, "證據包互驗",
            $"{string.Join("、", failed)} 未通過，另一項無從印證", "項", Source,
            FactTrustLevel.Measured, false, at, chainValid || manifestValid ? 1 : 0,
            FactAvailability.ReadError,
            failure ?? "兩者必須互相印證才算數——單獨一個通過不足以證明報告未被改動");
    }

    // ── P1-065 守門清冊輸出 ────────────────────────────────────────────

    /// <summary>產生 <c>guard.inventory</c>：全專案守門一覽。</summary>
    public static HardwareFact ToInventoryFact(DateTimeOffset at)
    {
        var guards = GuardRegistry.Guards;
        if (guards.Count == 0)
            return new HardwareFact(InventoryKey, Category, "守門清冊",
                "清冊為空", "條", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有登記任何守門");

        var byMode = GuardRegistry.CountByMode();
        string detail = $"{guards.Count} 條登記（"
            + string.Join("、", byMode.Select(kv => $"{GuardPolicy.Describe(kv.Key)} {kv.Value}")) + "）";

        int withControl = guards.Count(g => g.HasPositiveControl);
        int excused = guards.Count(g => !g.HasPositiveControl && g.PositiveControlExemption is not null);

        return new HardwareFact(InventoryKey, Category, "守門清冊", detail, "條", Source,
            FactTrustLevel.Derived, false, at, guards.Count, FactAvailability.Present,
            $"其中 {withControl} 條附正對照、{excused} 條載明免除理由——"
            + "其餘是性質類或對帳類，不以正對照為必要");
    }
}
