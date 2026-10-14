using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 幀呈現觀測（Vol 1 GP-003）：把「畫面到底呈現在螢幕上了沒有」與「多久畫一格」分開講。
/// </summary>
/// <remarks>
/// <para>
/// <b>與 <see cref="FrameTimeService"/> 的關係是分工不是重複。</b>既有那一頁量的是
/// <b>Present 事件之間的時間間隔</b>（平均 FPS、1%／0.1% Low、中位數與最大值），
/// 資料源是 DXGI#42 與 Dwm-Core#40。本項量的是<b>呈現鏈路本身是否成立</b>：
/// 應用程式送出的畫面有沒有真的被組合、被哪個節奏組合、有沒有被丟掉。
/// 兩者的差別在於——<b>幀時間漂亮不等於畫面呈現得出來</b>（獨佔全螢幕切換、DWM 接管、
/// 多顯示器各跑不同節奏，都會讓前者正常而後者不成立）。
/// </para>
/// <para>
/// <b>誠實界線一：不猜呈現模式。</b>獨佔／翻轉／合成這三種模式要靠 DXGI 的
/// <c>PresentMon</c> 式欄位或驅動回報才能判定；<b>本版不解析就絕不顯示</b>——
/// 從事件計數回推模式是猜的，而猜出來的模式會被拿去解釋延遲，錯得更遠。
/// 所以 <see cref="PresentProfile.Mode"/> 只有「未解析」這一種值，並在值裡寫明為什麼。
/// </para>
/// <para>
/// <b>誠實界線二：liveness 是三態，不是布林。</b>「有沒有畫面在動」有三種答案：
/// 有、沒有、<b>不知道</b>。觀測窗內 0 個事件可以是「真的沒在動」（閒置桌面），
/// 也可以是「這個堆疊上 DXGI 不發事件」（實測 Server 2025 有這種情況，見
/// <see cref="FrameTimeService"/> 的註解）。把它壓成 false 就是把「不知道」說成「沒有」。
/// </para>
/// <para>
/// <b>誠實界線三：不列行程名稱。</b>逐行程的呈現計數以 <c>pid</c> 與匿名序號呈現；
/// 行程名常含使用者帳號、專案或客戶代號（同 <c>po.proctree</c>／<c>dl.dll</c> 的紀律）。
/// </para>
/// <para>
/// <b>誠實界線四：不建立新工作階段就去不了 .etl 以外的地方。</b>真實通路必須由呼叫端
/// 提供觀測窗與事件取樣（PresentMon 式即時 session）；本項只負責判讀，
/// <b>未注入取樣＝不適用</b>，不是「0 幀」。
/// </para>
/// </remarks>
public static class PresentObservationFactsService
{
    public const string Category = "顯示";

    /// <summary>彙總：呈現鏈路的 liveness 與計數。</summary>
    public const string SummaryKey = "gp.present.summary";
    /// <summary>逐行程的呈現計數（anonymous）。</summary>
    public const string CountKey = "gp.present.count";

    /// <summary>判定「有畫面在動」所需的最少事件數；低於此數只說「疑似」。</summary>
    public const int MinEventsForLiveness = 2;

    /// <summary>呈現鏈路上「不知道有沒有在動」時用的字。</summary>
    public const string UnknownWord = "未知";

    /// <summary>呈現模式。<b>本版只有未解析一種值</b>——解析不出來就不給出聽起來像答案的字。</summary>
    public enum PresentMode
    {
        /// <summary>本版不解析呈現模式（獨佔／翻轉／合成）。</summary>
        Unresolved,
    }

    /// <summary>幀呈現的 liveness 三態。</summary>
    public enum Liveness
    {
        /// <summary>觀測窗內確實有畫面在動。</summary>
        Flowing,
        /// <summary>觀測窗內沒有事件——可能是真的沒在動，也可能是這個堆疊不發事件。</summary>
        Silent,
        /// <summary>沒有任何取樣可判（未注入探針）。</summary>
        Unknown,
    }

    /// <summary>
    /// 一次觀測的輸入：觀測窗秒數、逐行程（pid, 事件數）的呈現計數、DXGI 與 DWM 兩個來源各自的事件數、
    /// 以及被丟棄的未解事件數。
    /// </summary>
    public sealed record PresentInputs(
        double WindowSeconds,
        IReadOnlyList<(int Pid, long Events)> PerProcess,
        long DxgiEvents,
        long DwmEvents,
        long UnparsedEvents);

    /// <summary>呈現鏈路的輪廓。純函式產物，可單獨驗算。</summary>
    public sealed record PresentProfile(
        Liveness State,
        int ActiveProcesses,
        long TotalEvents,
        PresentMode Mode,
        bool DxgiSilent,
        double? EventsPerSecond);

    /// <summary>
    /// 由輸入判讀輪廓。<b>核心規則：DWM 有事件而 DXGI 全無，代表「應用程式層送不出來但桌面仍在組合」</b>——
    /// 這是實測過的堆疊差異，不是異常；兩者都無才可能是真的沒在動，而即使如此也只能說「沒看到」。
    /// </summary>
    public static PresentProfile Summarize(PresentInputs x)
    {
        long total = x.DxgiEvents + x.DwmEvents;
        int active = x.PerProcess.Count(p => p.Events > 0);
        double? perSec = x.WindowSeconds > 0 ? total / x.WindowSeconds : null;

        Liveness state = total >= MinEventsForLiveness ? Liveness.Flowing
            : total > 0 ? Liveness.Silent          // 只看到零星事件：不足以宣稱在動，也不足以宣稱沒動
            : Liveness.Silent;                     // 完全沒有事件

        return new PresentProfile(state, active, total, PresentMode.Unresolved,
            DxgiSilent: x.DxgiEvents == 0, EventsPerSecond: perSec);
    }

    /// <summary>由取樣算出事實。<paramref name="inputs"/> 為 <c>null</c>＝未注入探針（沒觀測）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, PresentInputs? inputs = null)
    {
        if (inputs is null)
            return
            [
                Unavailable(at, SummaryKey, "幀呈現觀測 摘要",
                    "未注入呈現事件取樣——未觀測不等於 0 幀；本項<b>不自行建立新的 ETW 工作階段</b>，"
                    + "取樣必須由呼叫端（PresentMon 式即時 session）提供並標註量測期間"),
                Unavailable(at, CountKey, "幀呈現 逐行程計數",
                    "未注入呈現事件取樣——未觀測不等於 0 幀"),
            ];
        if (inputs.WindowSeconds <= 0)
            return
            [
                Unavailable(at, SummaryKey, "幀呈現觀測 摘要",
                    "觀測窗長度是 0——無法換算任何速率，不以 0 冒充。量測期間必須標註才讀得懂數字"),
                Unavailable(at, CountKey, "幀呈現 逐行程計數", "觀測窗長度是 0——無法產出逐行程計數"),
            ];

        var p = Summarize(inputs);
        string liveness = p.State switch
        {
            Liveness.Flowing => $"觀測窗內有畫面在動（{p.TotalEvents} 個呈現事件，"
                + $"{p.EventsPerSecond:0.#}/秒）",
            Liveness.Silent => p.TotalEvents == 0
                ? "觀測窗內<b>沒有看到</b>任何呈現事件"
                : $"觀測窗內只有 {p.TotalEvents} 個呈現事件——不足以宣稱在動，也不足以宣稱沒動",
            _ => "無法判定有沒有畫面在動",
        };

        // 「沒看到」與「沒有」是兩件事：這是本項最重要的一句，所以兩個分支都帶著它
        string caveat = p.TotalEvents == 0
            ? "。<b>「沒看到」不等於「沒有畫面」</b>：觀測窗內 0 個事件可以是真的閒置，"
              + "也可以是這個堆疊上 DXGI 根本不發事件（實測 Server 2025 有這種情況）——"
              + "所以本項把 liveness 記成三態，不壓成一個布林值。"
            : "。";

        string dxgi = p.DxgiSilent && p.TotalEvents > 0
            ? "<b>DXGI 全靜而 DWM 有事件</b>：代表應用程式層送不出來但桌面仍在組合——"
              + "這是實測過的堆疊差異，不是異常，也不是「壞了」。"
            : p.DxgiSilent
                ? "DXGI 與 DWM 兩個來源在觀測窗內都沒有事件。"
                : "";

        string summary = liveness + caveat + dxgi
            + $"逐行程計數：{p.ActiveProcesses} 個行程有呈現事件（<b>不列行程名稱</b>——"
            + "名稱常含使用者帳號或專案代號）。"
            + $"<b>呈現模式未解析</b>：獨佔／翻轉／合成要靠 DXGI 的 PresentMon 式欄位或驅動回報才能判定，"
            + "本版解析不了就不顯示——從事件計數回推模式是猜的，而猜出來的模式會被拿去解釋延遲，錯得更遠。"
            + $"<b>量測期間</b>：觀測窗 {inputs.WindowSeconds:0.#} 秒"
            + (inputs.UnparsedEvents > 0
                ? $"；另有 {inputs.UnparsedEvents} 個未解事件未計入（沒有 schema，如實分開列）"
                : "")
            + "。<b>幀時間漂亮不等於畫面呈現得出來</b>——獨佔全螢幕切換、DWM 接管、"
            + "多顯示器各跑不同節奏，都會讓既有的幀時間頁正常而呈現鏈路不成立。";

        long maxPerProcess = inputs.PerProcess.Count == 0 ? 0 : inputs.PerProcess.Max(p => p.Events);
        string count = inputs.PerProcess.Count == 0
            ? "沒有逐行程的呈現事件（觀測窗內沒有任何行程送過畫面，或取樣未涵蓋）"
            : $"觀測窗內共 {inputs.PerProcess.Count} 個行程、最多的一個 {maxPerProcess} 個事件"
              + $"（合計 {p.TotalEvents}）。逐項以 pid 與匿名序號表示，<b>不列行程名稱</b>；"
              + "計數是事件數不是幀數保證——同一次呈現可能產生多個事件，反過來也可能被合併，"
              + "所以這個數字只作規模參考，不當 FPS 用。";

        return
        [
            new HardwareFact(SummaryKey, Category, "幀呈現觀測 摘要", summary, "個",
                "呈現事件取樣（呼叫端提供的即時 session；本項唯讀判讀，不建立新工作階段）",
                FactTrustLevel.Measured, false, at, p.TotalEvents),
            new HardwareFact(CountKey, Category, "幀呈現 逐行程計數", count, "個",
                "呈現事件取樣（逐行程計數，不揭露行程名稱）",
                FactTrustLevel.Measured, false, at, p.ActiveProcesses),
        ];
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name, string reason)
        => new(key, Category, name, "", "", "呈現事件取樣（未觀測）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, reason);
}
