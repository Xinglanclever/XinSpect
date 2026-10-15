using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// 暫態還原契約（ULTIMATE-1000 階段一 P1-003）。
///
/// <para>
/// <b>這是整個計畫裡最不能出錯的一條。</b>掃描硬體時常需要進入暫態：
/// 寫 MSR 設定事件計數器、進出 SuperIO 設定視窗、切換 PCI 設定位址。
/// 這些暫態若沒還原，機器會停在「被改過」的狀態——而且使用者不會知道。
/// 事實讀錯了可以重讀，硬體停在改過的狀態可能要重開機才會好。
/// </para>
///
/// <para>
/// 契約：<b>任何進入暫態的動作，都必須在同一輪掃描結束前還原</b>，
/// 還原動作放在 <c>finally</c>、不因例外或逾時而略過，且還原本身失敗要
/// <b>被記錄下來而不是吞掉</b>。
/// </para>
/// </summary>
public sealed class TransientScope : IDisposable
{
    private readonly string _name;
    private readonly Action? _restore;
    private bool _entered;
    private bool _restored;
    private string? _restoreFailure;

    private TransientScope(string name, Action? restore)
    {
        _name = name;
        _restore = restore;
        _entered = true;
    }

    /// <summary>這個暫態的名稱（會出現在掃描紀錄裡）。</summary>
    public string Name => _name;

    /// <summary>是否確實還原。</summary>
    public bool Restored => _restored;

    /// <summary>還原失敗的原因；成功時為 null。</summary>
    public string? RestoreFailure => _restoreFailure;

    /// <summary>
    /// 進入一個暫態。<paramref name="restore"/> 是還原動作，
    /// <b>不可為 null</b>——沒有還原動作就不該進入暫態。
    /// </summary>
    public static TransientScope Enter(string name, Action restore) =>
        new(name, restore ?? throw new ArgumentNullException(nameof(restore),
            "進入暫態必須提供還原動作；沒有還原動作的暫態不允許存在"));

    /// <summary>
    /// 執行還原。還原失敗時<b>不往外丟例外</b>（那會蓋掉原本的失敗原因），
    /// 而是記在 <see cref="RestoreFailure"/> 並讓掃描紀錄取用。
    /// </summary>
    public void Dispose()
    {
        if (_restored) return;
        try
        {
            _restore?.Invoke();
            _restored = true;
        }
        catch (Exception ex)
        {
            // 記錄型別，不記訊息全文：訊息可能含暫存器位址或路徑。
            _restoreFailure = ex.GetType().Name;
            _restored = false;
        }
    }

    /// <summary>把這個暫態的結局轉成一筆掃描紀錄。</summary>
    public ScanStepResult ToStepResult(TimeSpan elapsed, ScanStepOutcome outcome, string? detail = null)
    {
        string? d = detail;
        if (!_restored)
            d = _restoreFailure is null ? "未進入" : $"還原失敗：{_restoreFailure}";
        return new ScanStepResult(_name, outcome, _entered, _restored, elapsed, d);
    }
}

/// <summary>
/// 暫態還原的統計（P1-003 的輸出鍵 <c>scan.restore.ok</c> 由此產生）。
/// 只做計數，不列暫態名稱——暫態名稱會洩漏我們動過哪些硬體寄存器。
/// </summary>
public static class TransientRestoreReport
{
    /// <summary>把一輪掃描的步驟紀錄彙總成還原統計。</summary>
    public static (int Entered, int Restored, int Failed) Summarize(ScanOutcome outcome)
    {
        int entered = 0, restored = 0, failed = 0;
        foreach (var s in outcome.Steps)
        {
            if (!s.EnteredTransient) continue;
            entered++;
            if (s.Restored) restored++;
            else failed++;
        }
        return (entered, restored, failed);
    }

    /// <summary>產生 <c>scan.restore.ok</c> 的事實。</summary>
    public static HardwareFact ToFact(DateTimeOffset at, ScanOutcome outcome)
    {
        var (entered, restored, failed) = Summarize(outcome);
        if (entered == 0)
            return new HardwareFact("scan.restore.ok", "掃描骨架", "暫態還原",
                "本輪未進入任何暫態", "個", "掃描骨架自我檢查", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有暫態要還原");

        string value = failed == 0
            ? $"進入 {entered} 個暫態、全部還原"
            : $"進入 {entered} 個暫態、{failed} 個未還原（硬體可能停在改過的狀態）";
        return new HardwareFact("scan.restore.ok", "掃描骨架", "暫態還原", value, "個",
            "掃描骨架自我檢查", FactTrustLevel.Derived, false, at,
            failed, failed == 0 ? FactAvailability.Present : FactAvailability.ReadError,
            failed == 0 ? null : $"{failed} 個暫態未還原");
    }
}
