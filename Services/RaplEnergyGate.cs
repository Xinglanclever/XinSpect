using System.Diagnostics;
using System.Threading;

namespace XinSpect;

/// <summary>
/// RAPL 能量計的誠實閘門——把 <see cref="CeilingDecoder.ValidateEnergyCounter"/> 的
/// 「基線窗 vs 負載窗」檢查從深測頁下沉到資料源（2026-11-07）。
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
/// 負載窗會讓 CPU 忙碌約 1 秒——一次性成本，探測結果全程緩存。
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

    private static readonly object Sync = new();

    /// <summary>執行一次性探測。readEnergy 回 null＝讀不到（探測即中止）。</summary>
    public static void RunOnce(Func<ulong?> readEnergy, Func<double?> readTemp)
    {
        lock (Sync)
        {
            if (Verdict != RaplGateVerdict.Unknown) return;
            RunOnceCore(readEnergy, readTemp);
        }
    }

    private static void RunOnceCore(Func<ulong?> readEnergy, Func<double?> readTemp)
    {
        try
        {
            // ── 基線窗：0.8 秒，閒置速率 ──
            var e1 = readEnergy();
            if (e1 is null)
            {
                Verdict = RaplGateVerdict.NoCounter;
                Note = "0x611 讀不到——本平台沒有可用的封裝能量計。";
                return;
            }
            var t1 = readTemp();
            var sw = Stopwatch.StartNew();
            Thread.Sleep(3000);
            var e2 = readEnergy();
            if (e2 is null) { Verdict = RaplGateVerdict.NoCounter; Note = "0x611 第二次讀取失敗。"; return; }
            double baseRate = Rate(e1.Value, e2.Value, sw.Elapsed.TotalSeconds);
            LastRawCount = e2.Value;

            // ── 負載窗：自旋執行緒（一般優先權、邏輯數−2、最少 4 條——CeilingService 同一紀律） ──
            int threads = Math.Max(4, Math.Clamp(Environment.ProcessorCount - 2, 4, 32));
            var stop = new CancellationTokenSource();
            var workers = new List<Thread>();
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        for (int k = 0; k < 20000; k++) { }
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.Normal };
                workers.Add(t);
                t.Start();
            }
            Thread.Sleep(300);          // 沉降：讓調度與頻率授權穩定
            var l1 = readEnergy();
            var lt1 = readTemp();
            sw.Restart();
            Thread.Sleep(3000);
            var l2 = readEnergy();
            var lt2 = readTemp();
            stop.Cancel();
            foreach (var t in workers) t.Join(500);
            if (l1 is null || l2 is null) { Verdict = RaplGateVerdict.NoCounter; Note = "負載窗讀取失敗。"; return; }
            double loadRate = Rate(l1.Value, l2.Value, sw.Elapsed.TotalSeconds);
            LastRawCount = l2.Value;

            double baseTemp = lt1 ?? 0, loadTemp = lt2 ?? 0;
            bool tempKnown = lt1.HasValue && lt2.HasValue;

            var (trusted, text) = CeilingDecoder.ValidateEnergyCounter(
                baseRate, loadRate, (int)baseTemp, (int)loadTemp, tempKnown);
            if (Environment.GetEnvironmentVariable("XINSPECT_GATE_DEBUG") == "1")
                Console.WriteLine($"[gate] base {baseRate:0} cps / load {loadRate:0} cps / ratio {(baseRate > 0 ? loadRate / baseRate : 0):0.00} / temp {baseTemp:0}->{loadTemp:0}C -> {Verdict}");
            Verdict = trusted ? RaplGateVerdict.Trusted : RaplGateVerdict.NotResponding;
            Note = text;
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
