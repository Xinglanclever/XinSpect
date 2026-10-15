using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 守門的執行時間預算（ULTIMATE-1000 階段一 P1-064）。
///
/// <para>
/// <b>為什麼時間預算是守門方法論的一部分而不是效能調校：</b>
/// 一個跑 30 秒的守門會被跳過；一個跑 5 分鐘的守門會讓整個套件沒人願意跑，
/// 於是把「守門」變成「偶爾才做的事」——那等於沒有守門。
/// <b>時間預算是守門能不能活下來的條件。</b>
/// </para>
///
/// <para>
/// <b>這裡量的是「單一守門」而不是「整個套件」：</b>整輪時間會被機器負載與
/// 其他測試影響，拿它當紅燈會產生不穩定的判準（今天過明天不過）。
/// 單一守門的時間才是那個守門自己的性質。
/// </para>
/// </summary>
public static class GuardBudget
{
    public const string DurationKey = "test.duration";

    /// <summary>
    /// 一個守門的時間預算。<see cref="Hard"/> 是紅燈線，<see cref="Soft"/> 是提醒線。
    /// 兩段式是刻意的：只有硬線會讓「快要超支」完全沒有預警。
    /// </summary>
    /// <param name="Name">守門名稱。</param>
    /// <param name="Hard">超過就是缺陷——這個守門太貴了，要拆或要改做法。</param>
    /// <param name="Soft">超過只是提醒——記下來，下次看到就知道它在變貴。</param>
    public sealed record Budget(string Name, TimeSpan Hard, TimeSpan Soft)
    {
        public static Budget Standard(string name) =>
            new(name, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

        /// <summary>量一次執行。回傳耗時與是否違反預算。</summary>
        public (TimeSpan Elapsed, string? Verdict) Measure(Action body)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            return (sw.Elapsed, Judge(sw.Elapsed));
        }

        /// <summary>判定一次耗時。<c>null</c>＝在預算內。</summary>
        public string? Judge(TimeSpan elapsed)
        {
            if (elapsed > Hard)
                return $"{Name} 耗時 {elapsed.TotalMilliseconds:0} 毫秒，超過硬預算 "
                     + $"{Hard.TotalMilliseconds:0} 毫秒——太貴的守門會被跳過，等於沒有守門";
            if (elapsed > Soft)
                return $"{Name} 耗時 {elapsed.TotalMilliseconds:0} 毫秒，超過提醒線 "
                     + $"{Soft.TotalMilliseconds:0} 毫秒——記下來，它正在變貴";
            return null;
        }

        public bool IsHardViolation(TimeSpan elapsed) => elapsed > Hard;
    }

    /// <summary>產生 <c>test.duration</c>：這一輪各守門的耗時與超支情形。</summary>
    public static HardwareFact ToFact(DateTimeOffset at,
        IReadOnlyList<(string Name, TimeSpan Elapsed, bool HardViolation)> measurements)
    {
        if (measurements.Count == 0)
            return new HardwareFact(DurationKey, "守門方法論", "守門耗時",
                "這一輪沒有量到任何守門", "條", "守門預算", FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable, "沒有守門耗時可以申報");

        var over = measurements.Where(m => m.HardViolation).ToList();
        var slowest = measurements.OrderByDescending(m => m.Elapsed).First();
        double totalMs = measurements.Sum(m => m.Elapsed.TotalMilliseconds);

        string detail = over.Count == 0
            ? $"{measurements.Count} 條守門全在預算內；最慢的「{slowest.Name}」"
              + $"{slowest.Elapsed.TotalMilliseconds:0} 毫秒（合計 {totalMs / 1000:0.0} 秒）"
            : $"{over.Count}/{measurements.Count} 條超過硬預算：" +
              string.Join("、", over.Select(m => $"{m.Name}（{m.Elapsed.TotalMilliseconds:0} 毫秒）"));

        return new HardwareFact(DurationKey, "守門方法論", "守門耗時", detail, "條",
            "守門預算", FactTrustLevel.Measured, false, at, over.Count,
            over.Count == 0 ? FactAvailability.Present : FactAvailability.Unknown,
            over.Count == 0 ? null
                : $"{over.Count} 條守門超過硬預算——它們會被跳過，而會被跳過的守門等於沒有守門");
    }
}
