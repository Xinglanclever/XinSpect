using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// RAPL 能量計的誠實閘門——把 <see cref="CeilingDecoder.ValidateEnergyCounter"/> 的
/// 「基線窗 vs 負載窗」檢查從深測頁下沉到資料源（2026-11-07）。
/// 2026-11-07 補丁：探測改為背景單飛（AsyncLazy），不再在呼叫端執行緒上跑——
/// GUI 首讀原會把 6 秒探測搬上 UI 執行緒，總覽頁／瓶頸頁／歷史取樣首拍整頁凍結。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要它</b>：本機（Skylake-X）的 0x611 能量計讀得到、也在前進，但速率不隨
/// 真實功耗變化（閒置與滿載只差幾個百分比）——硬換算得出「18 核滿載 1.2 W」的荒謬瓦數。
/// CeilingService 在深測頁建了閘門，但即時路徑（迷你浮窗／感測器／CLI sys）直接取
/// LHM 的封裝功率或 Ring0 差分，沒有驗證——同一份事實從不同的門出去，誠實等級不一樣。
/// </para>
/// <para>
/// <b>方法</b>：程序內一次性自證——基線窗（閒置速率）→ 自旋負載窗（一般優先權、
/// 邏輯數−2，與 CeilingService 同一紀律）→ 交給 <see cref="CeilingDecoder.ValidateEnergyCounter"/>
/// 判決。判決結果緩存整個行程；呼叫端據此決定顯示瓦數或三態。
/// </para>
/// <para>
/// <b>界線</b>：探測需要 MSR 讀取（管理員＋驅動服務）；讀不到時判決是
/// <see cref="RaplGateVerdict.NoCounter"/>，呼叫端同樣不該顯示瓦數（來源不明 ≠ 可信）。
/// 負載窗會讓 CPU 忙碌約 1+3 秒——一次性成本、全程在背景執行緒，探測結果全程緩存。
/// </para>
/// </remarks>
public enum RaplGateVerdict
{
    /// <summary>尚未探測。</summary>
    Unknown = 0,
    /// <summary>通過：計數速率隨負載上升，瓦數可信。</summary>
    Trusted = 1,
    /// <summary>不通過：計數器與負載脫鉤——瓦數不可信，只顯示原始計數。</summary>
    NotResponding = 2,
    /// <summary>沒有可用的能量計（讀不到 0x611）。</summary>
    NoCounter = 3,
}

public static class RaplEnergyGate
{
    /// <summary>探測判決（程序內緩存）。</summary>
    public static RaplGateVerdict Verdict { get; private set; } = RaplGateVerdict.Unknown;

    /// <summary>判決的人話說明（進 CLI 的 sys 行與 GUI 的狀態提示）。</summary>
    public static string? Note { get; private set; }

    /// <summary>最後一次讀到的原始能量計數（三態顯示用）。</summary>
    public static ulong LastRawCount { get; private set; }

    /// <summary>探測是否仍在背景進行（GUI 三態顯示「能量計探測中」的判準）。</summary>
    public static bool IsProbing => _probeTask is { IsCompleted: false };

    private static readonly object Sync = new();
    private static Task? _probeTask;   // 單飛：同一行程最多一跑；判決寫入由這唯一一跑負責，免鎖

    /// <summary>
    /// 惰性啟動一次性探測（AsyncLazy 語意）：第一次呼叫以 <see cref="Task.Run"/> 在背景執行緒
    /// 跑完整探測，之後所有呼叫（含並行）拿回同一個 Task——<b>絕不在呼叫端執行緒上跑探測本體</b>。
    /// GUI（總覽頁繫結、瓶頸頁 Timer、HistoryStore 取樣）一律「射後不理」：探測未完成時
    /// <see cref="Verdict"/> 維持 <see cref="RaplGateVerdict.Unknown"/>，呼叫端以
    /// <see cref="IsProbing"/> 顯示「能量計探測中」，探測完成的下一拍 Publish 自然補上結果。
    /// </summary>
    public static Task EnsureStarted(Func<ulong?> readEnergy, Func<double?> readTemp)
    {
        lock (Sync)
        {
            if (_probeTask is null)
                _probeTask = Task.Run(() => RunOnceCoreAsync(readEnergy, readTemp));
            return _probeTask;
        }
    }

    /// <summary>await 探測完成（明確要結果的呼叫端用，例如 CLI 的 sys 行）。重複呼叫共用同一跑。</summary>
    public static async Task RunOnceAsync(Func<ulong?> readEnergy, Func<double?> readTemp) =>
        await EnsureStarted(readEnergy, readTemp).ConfigureAwait(false);

    /// <summary>
    /// 同步版：<b>僅限無同步內容的 CLI 主控台呼叫端</b>（Program.cs），GUI 一律禁用——
    /// GUI 呼叫會把 6 秒探測搬回 UI 執行緒（正是本補丁修掉的凍結）。
    /// </summary>
    public static void RunOnce(Func<ulong?> readEnergy, Func<double?> readTemp) =>
        EnsureStarted(readEnergy, readTemp).GetAwaiter().GetResult();

    private static async Task RunOnceCoreAsync(Func<ulong?> readEnergy, Func<double?> readTemp)
    {
        try
        {
            // ── 基線窗：3 秒，閒置速率（窗長與修復前一致；等待改 Task.Delay，不再佔住呼叫執行緒）──
            var e1 = readEnergy();
            if (e1 is null)
            {
                Verdict = RaplGateVerdict.NoCounter;
                Note = "0x611 讀不到——本平台沒有可用的封裝能量計。";
                return;
            }
            var t1 = readTemp();
            var sw = Stopwatch.StartNew();
            await Task.Delay(3000).ConfigureAwait(false);
            var e2 = readEnergy();
            if (e2 is null) { Verdict = RaplGateVerdict.NoCounter; Note = "0x611 第二次讀取失敗。"; return; }
            double baseRate = Rate(e1.Value, e2.Value, sw.Elapsed.TotalSeconds);
            LastRawCount = e2.Value;

            // ── 負載窗：自旋執行緒（一般優先權、邏輯數−2、最少 4 條——CeilingService 同一紀律） ──
            // 維持專用 Thread 而非執行緒集區：自旋會吃滿核，不該餓死集區上的其他工作。
            int threads = Math.Max(4, Math.Clamp(Environment.ProcessorCount - 2, 4, 32));
            var stop = new CancellationTokenSource();
            var allDone = new CountdownEvent(threads);
            var workers = new List<Thread>();
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            for (int k = 0; k < 20000; k++) { }
                        }
                    }
                    finally
                    {
                        // 〔修復 v3.20〕主流程 Wait(2000) 逾時後就會 Dispose——晚到的 Signal
                        // 不得拋 ObjectDisposedException（背景執行緒未觀察的例外會炸掉整個行程）。
                        try { allDone.Signal(); }
                        catch (ObjectDisposedException) { /* 收尾已放棄等待，無事可做 */ }
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.Normal };
                workers.Add(t);
                t.Start();
            }
            await Task.Delay(300).ConfigureAwait(false);   // 沉降：讓調度與頻率授權穩定
            var l1 = readEnergy();
            var lt1 = readTemp();
            sw.Restart();
            await Task.Delay(3000).ConfigureAwait(false);
            var l2 = readEnergy();
            var lt2 = readTemp();
            stop.Cancel();
            // 收尾改單次有界事件等待（CountdownEvent）：原 t.Join(500) 迴圈最壞 32×500ms=16 秒
            await Task.Run(() => allDone.Wait(2000)).ConfigureAwait(false);
            allDone.Dispose();
            stop.Dispose();
            if (l1 is null || l2 is null) { Verdict = RaplGateVerdict.NoCounter; Note = "負載窗讀取失敗。"; return; }
            double loadRate = Rate(l1.Value, l2.Value, sw.Elapsed.TotalSeconds);
            LastRawCount = l2.Value;

            double baseTemp = lt1 ?? 0, loadTemp = lt2 ?? 0;
            bool tempKnown = lt1.HasValue && lt2.HasValue;

            var (trusted, text) = CeilingDecoder.ValidateEnergyCounter(
                baseRate, loadRate, (int)baseTemp, (int)loadTemp, tempKnown);
            Verdict = trusted ? RaplGateVerdict.Trusted : RaplGateVerdict.NotResponding;
            Note = text;
            if (Environment.GetEnvironmentVariable("XINSPECT_GATE_DEBUG") == "1")
                Console.WriteLine($"[gate] base {baseRate:0} cps / load {loadRate:0} cps / ratio {(baseRate > 0 ? loadRate / baseRate : 0):0.00} / temp {baseTemp:0}->{loadTemp:0}C -> {Verdict}");   // 修復前在賦值前印 Verdict，永遠印 Unknown——順手修正次序
        }
        catch (Exception ex)
        {
            // 探測本身炸了：無法驗證＝不顯示瓦數（寧可缺席不冒充），原因如實帶回
            Verdict = RaplGateVerdict.NotResponding;
            Note = "能量計探測失敗：" + ex.Message;
        }
    }

    private static double Rate(ulong a, ulong b, double seconds)
    {
        ulong delta = b >= a ? b - a : b + (ulong.MaxValue - a) + 1;   // 32 位計數器繞回
        return seconds > 0 ? delta / seconds : 0;
    }
}
