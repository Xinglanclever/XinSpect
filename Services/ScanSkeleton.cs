using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// 唯讀掃描骨架（ULTIMATE-1000 階段一 P1-001）。
///
/// <para>
/// <b>這個介面存在的理由不是「統一風格」，是讓兩件事變成機械可檢查：</b>
/// ① 所有來源以同一種順序被讀取（發現→讀取→解碼→三態→鍵）；
/// ② 任何暫態必定還原。
/// 本專案已經有 60 多個事實服務，每一支都自己決定順序、自己決定要不要還原暫態。
/// 那件事在單一服務裡看起來無害，在 60 支裡面就是「同一件事 60 種寫法」——
/// 而本專案已經吃過一次這種虧（同一件事四套命名空間）。
/// </para>
///
/// <para>
/// <b>骨架不判斷事實的意義。</b>它只負責順序與還原。判斷留給各域服務，
/// 事實的意義留給使用者。這是本專案第一條誠實界線的介面化。
/// </para>
///
/// <para>
/// <b>骨架不允許寫入。</b>寫入必經閘門（P1-067）。因此本介面與其所有實作
/// 都不得出現寫入 API——這一條由 <c>ScanSkeletonGuardTests</c> 以原始碼掃描釘死，
/// 並附一個「該紅會紅」的正對照。
/// </para>
/// </summary>
public interface IReadOnlyScanner
{
    /// <summary>
    /// 來源名稱。用於事實的 <c>Source</c> 欄與掃描失敗時的指名——
    /// <b>必須是穩定的字串常數</b>，不得含機器名、使用者名或時間。
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// 以固定順序讀取，回傳事實與各步驟的狀態。
    /// <b>不得拋出例外</b>：失敗要變成 <see cref="ScanStepResult"/> 裡的六態，不是往上丟。
    /// 呼叫端（而非骨架）決定要不要把例外當成程式缺陷記錄下來。
    /// </summary>
    ScanOutcome Scan(ScanContext context);
}

/// <summary>掃描的輸入條件。刻意只有時間戳與取消權杖——骨架不該依賴 UI 狀態。</summary>
public sealed record ScanContext(DateTimeOffset At, CancellationToken Cancellation = default);

/// <summary>
/// 掃描的結果。<see cref="Steps"/> 為<b>有序</b>清單，順序就是實際執行順序，
/// 讓「為什麼這一步沒跑」可以從紀錄本身看出來，不必推測。
/// </summary>
public sealed record ScanOutcome(
    IReadOnlyList<HardwareFact> Facts,
    IReadOnlyList<ScanStepResult> Steps)
{
    /// <summary>這一輪是否曾有暫態被進入（用來對帳「有沒有東西需要還原」）。</summary>
    public bool EnteredTransient => Steps.Any(s => s.EnteredTransient);

    /// <summary>掃描是否被取消。取消時 <see cref="Facts"/> 保留已得事實，不整批丟棄（P1-004）。</summary>
    public bool WasCancelled => Steps.Any(s => s.Outcome == ScanStepOutcome.Cancelled);
}

/// <summary>一個步驟的結局。刻意與 <see cref="FactAvailability"/> 對齊但不等於它——</summary>
public enum ScanStepOutcome
{
    /// <summary>這一步執行完成。</summary>
    Completed,
    /// <summary>這一步的前提不成立（例如沒有對應硬體），而這是環境事實、不是錯誤。</summary>
    NotApplicable,
    /// <summary>這一步嘗試了但失敗。</summary>
    Failed,
    /// <summary>呼叫端要求中止。</summary>
    Cancelled,
}

/// <summary>
/// 一個步驟的紀錄。這是骨架唯一「自己產出」的東西——
/// 骨架不產事實（事實來自各域服務），只產「我做了什麼、結果如何」的紀錄。
/// </summary>
/// <param name="Name">步驟名。逾時或失敗時要能指名到這一步（P1-002）。</param>
/// <param name="Outcome">結局。</param>
/// <param name="EnteredTransient">這一步是否進入過暫態。進入過就必須還原（P1-003）。</param>
/// <param name="Restored">暫態是否確實還原。<c>EnteredTransient=true</c> 而這裡是 false，就是缺陷。</param>
/// <param name="Elapsed">耗時。供 P1-002 的超時上界對帳。</param>
/// <param name="Detail">失敗原因或補充說明；成功時可為 null。</param>
public sealed record ScanStepResult(
    string Name,
    ScanStepOutcome Outcome,
    bool EnteredTransient,
    bool Restored,
    TimeSpan Elapsed,
    string? Detail)
{
    /// <summary>
    /// 這一步是否「進入暫態卻沒有還原」。骨架把這個判斷做成方法而不是留給呼叫端，
    /// 因為它是整個介面最重要的一條不變式。
    /// </summary>
    public bool LeftTransientUnrestored => EnteredTransient && !Restored;

    public static ScanStepResult Completed(string name, TimeSpan elapsed,
        bool enteredTransient = false, bool restored = true, string? detail = null) =>
        new(name, ScanStepOutcome.Completed, enteredTransient, restored, elapsed, detail);

    public static ScanStepResult NotApplicable(string name, TimeSpan elapsed, string reason) =>
        new(name, ScanStepOutcome.NotApplicable, false, true, elapsed, reason);

    public static ScanStepResult Failed(string name, TimeSpan elapsed, string reason,
        bool enteredTransient = false, bool restored = true) =>
        new(name, ScanStepOutcome.Failed, enteredTransient, restored, elapsed, reason);

    public static ScanStepResult Cancelled(string name, TimeSpan elapsed) =>
        new(name, ScanStepOutcome.Cancelled, false, true, elapsed, "呼叫端要求中止");
}

/// <summary>
/// 掃描骨架的執行器：把「固定順序 ＋ 逾時上界 ＋ 還原檢查 ＋ 中斷保留」四件事
/// 收在一個地方，讓每個來源不必各自重寫一遍。
///
/// <para>
/// <b>它不做的事</b>：不讀硬體、不解碼、不判斷事實意義、不寫入。
/// 它是流程，不是功能。
/// </para>
/// </summary>
public static class ScanSkeleton
{
    /// <summary>
    /// 依序執行各步驟。任一步驟的例外都轉成 <see cref="ScanStepOutcome.Failed"/>，
    /// <b>不中斷後續步驟</b>——一次掃描要盡可能多地取得事實，這是 P1-004 的另一面。
    /// </summary>
    /// <param name="steps">步驟清單。名稱需唯一，重複時以第一次為準（避免同一步驟被記兩次）。</param>
    public static ScanOutcome Run(
        ScanContext context,
        IEnumerable<(string Name, Func<ScanStepResult> Body)> steps)
    {
        var facts = new List<HardwareFact>();
        var results = new List<ScanStepResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, body) in steps)
        {
            if (!seen.Add(name))
                continue;   // 同名步驟只跑一次：重複註冊是設定錯誤，不是兩次讀取

            if (context.Cancellation.IsCancellationRequested)
            {
                results.Add(ScanStepResult.Cancelled(name, TimeSpan.Zero));
                continue;   // 後續步驟全部記為取消，已得事實保留（P1-004）
            }

            var sw = Stopwatch.StartNew();
            ScanStepResult result;
            try
            {
                result = body();
            }
            catch (OperationCanceledException)
            {
                result = ScanStepResult.Cancelled(name, sw.Elapsed);
            }
            catch (Exception ex)
            {
                // 例外轉狀態。這裡刻意記錄型別而非訊息全文——訊息可能含路徑與使用者名。
                result = ScanStepResult.Failed(name, sw.Elapsed, ex.GetType().Name);
            }
            results.Add(result);
        }

        return new ScanOutcome(facts, results);
    }

    /// <summary>
    /// 骨架的自我檢查：一輪掃描結束後，逐條驗證不變式。
    /// 回傳的問題清單為空＝這一輪沒有違反骨架契約。
    ///
    /// <para>
    /// 這是「守門放在骨架裡」而不是「放在測試裡」的刻意選擇：測試只能驗測試當下那個情境，
    /// 而這個檢查在<b>每一次真實掃描</b>後都會跑。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Validate(ScanOutcome outcome)
    {
        var problems = new List<string>();
        foreach (var s in outcome.Steps)
        {
            if (s.LeftTransientUnrestored)
                problems.Add($"步驟「{s.Name}」進入暫態但未還原——這是最嚴重的一類缺陷（硬體可能停在改過的状态）");
            if (s.Outcome == ScanStepOutcome.Failed && string.IsNullOrWhiteSpace(s.Detail))
                problems.Add($"步驟「{s.Name}」失敗但沒有留下原因");
        }
        if (outcome.Steps.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != outcome.Steps.Count)
            problems.Add("步驟名稱有重複——同一輪掃描裡同一個名字代表兩個步驟");
        return problems;
    }
}
