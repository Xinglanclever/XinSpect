using System.Windows;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 取用主視窗、主 ViewModel 與 UI 執行緒的唯一入口。
/// </summary>
/// <remarks>
/// 存在的理由是一個會真的讓程式當掉的細節：<c>Application.MainWindow</c> 是相依屬性，
/// getter 會做 <c>VerifyAccess()</c>——<b>在非 UI 執行緒上讀它會丟
/// <see cref="InvalidOperationException"/>「呼叫執行緒無法存取此物件」</b>，不是回 null。
/// 專案裡原本有二十處寫成 <c>Application.Current?.MainWindow?.DataContext as MainViewModel</c>，
/// 問號只擋得住「沒有 Application」，擋不住跨執行緒；ETW 泵執行緒（FrameTimeService 的錯誤路徑）
/// 一旦走到就是背景執行緒未處理例外，整個行程直接結束。
///
/// 因此這裡一律先問 <c>CheckAccess()</c>（<see cref="DispatcherObject"/> 的成員，任何執行緒都能安全呼叫），
/// 不在 UI 執行緒上就<b>誠實回 null</b>，讓呼叫端自己決定要不要改走 <see cref="BeginOnUi"/>。
/// </remarks>
public static class Shell
{
    /// <summary>
    /// UI 執行緒的 Dispatcher；沒有 <see cref="Application"/> 時（單元測試、關機途中）回 null。
    /// </summary>
    /// <remarks><c>Application.Dispatcher</c> 是一般的 CLR 屬性，不做 VerifyAccess，任何執行緒讀都安全。</remarks>
    public static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    /// <summary>目前是否就在 UI 執行緒上。沒有 Application 時視為否。</summary>
    public static bool OnUi => Application.Current?.CheckAccess() == true;

    /// <summary>
    /// 主視窗。<b>不在 UI 執行緒上時回 null</b>，而不是丟例外——背景執行緒本來就不該直接摸視覺樹。
    /// </summary>
    public static Window? TopWindow
    {
        get
        {
            var app = Application.Current;
            if (app is null || !app.CheckAccess()) return null;
            try { return app.MainWindow; }
            catch (Exception ex) { Diag.Swallow("讀取主視窗", ex, "本次以「尚無主視窗」處理"); return null; }
        }
    }

    /// <summary>主視窗（已轉型）。非 UI 執行緒、或主視窗還沒建好時回 null。</summary>
    public static MainWindow? Main => TopWindow as MainWindow;

    /// <summary>
    /// 主 ViewModel。給「檢視在延遲載入時 DataContext 還沒指派」的情境當後備；
    /// 非 UI 執行緒時回 null。
    /// </summary>
    public static MainViewModel? Vm => VmForTests ?? TopWindow?.DataContext as MainViewModel;

    /// <summary>
    /// 測試用的後備 VM。<b>正式執行時永遠是 null</b>；存在的唯一理由是
    /// <c>Application</c> 的 <see cref="Window"/> 綁在建立它的那條執行緒上，
    /// 而測試的每條 STA 執行緒都拿不到那份所有權——沒有這個後備，「使用者開頁時
    /// Shell.Vm 拿得到」這個條件在測試裡就重現不出來（2026-10-26 感測器儀表板的
    /// 建構期 NRE 就是這樣逃過整個測試套件的）。
    /// </summary>
    internal static MainViewModel? VmForTests { get; set; }

    /// <summary>
    /// 把動作排到 UI 執行緒執行。沒有 Application（單元測試、關機途中）時安靜忽略——
    /// 這是「更新畫面」的路徑，拿不到畫面就沒有事情要做。
    /// </summary>
    public static void BeginOnUi(Action a)
    {
        var d = Dispatcher;
        if (d is null) return;
        try { d.BeginInvoke(a); }
        catch (Exception ex) { Diag.Swallow("排入 UI 執行緒", ex, "該次畫面更新被略過"); }
    }
}
