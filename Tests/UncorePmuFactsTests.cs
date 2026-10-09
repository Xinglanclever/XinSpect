using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// UncorePmuService 的事實層接線（v2.47）：平台白名單判定、三態誠實、
/// 以及「讀不到一律不以 0 頂替」。
/// </summary>
public class UncorePmuFactsTests
{
    private sealed class FakeMsr : IKernelMsrReader
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason { get; init; }
        public string? LastFailReason { get; init; }
        public Dictionary<uint, ulong?> Values { get; init; } = [];
        public ulong? ReadMsr(uint index) => Values.TryGetValue(index, out var v) ? v : null;
    }

    private static readonly Func<(int Family, int Model, int Stepping)> SkylakeX = () => (6, 0x55, 4);
    private static readonly Func<(int Family, int Model, int Stepping)> UnknownCpu = () => (6, 0x9A, 1);

    private static DateTimeOffset At => new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 平台未收錄時整組NotApplicable且不讀MSR()
    {
        var seen = new List<uint>();
        FakeProbingMsr msr = new(seen);
        var facts = UncorePmuService.Collect(msr, At, UnknownCpu);

        Assert.Equal(3, facts.Count);
        var platform = facts.Single(f => f.Key == "pmu.uncore.platform");
        Assert.Equal(FactAvailability.NotApplicable, platform.Availability);
        Assert.Contains("未收錄", platform.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("0x9A", platform.Value, StringComparison.Ordinal);
        Assert.All(facts, f => Assert.Equal("Uncore", f.Category));
        // 「套錯位址會讀出垃圾值」——未收錄平台一個 MSR 都不該碰
        Assert.Empty(seen);
    }

    private sealed class FakeProbingMsr(List<uint> touched) : IKernelMsrReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public ulong? ReadMsr(uint index) { touched.Add(index); return null; }
    }

    [Fact]
    public void 收錄平台但驅動未就緒回InsufficientPrivilege帶原因()
    {
        var facts = UncorePmuService.Collect(
            new FakeMsr { Available = false, UnavailableReason = "ring0 驅動未載入" }, At, SkylakeX);

        Assert.Equal(FactAvailability.Present, facts.Single(f => f.Key == "pmu.uncore.platform").Availability);
        foreach (var key in new[] { "pmu.uncore.ratio_limit", "pmu.uncore.perf_status" })
        {
            var f = facts.Single(x => x.Key == key);
            Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
            Assert.Equal("ring0 驅動未載入", f.UnavailableReason);
            Assert.Equal("", f.Value);   // 沒讀到就沒有值，不編
        }
    }

    [Fact]
    public void 讀到有效值時文字與數值都如實()
    {
        var facts = UncorePmuService.Collect(new FakeMsr
        {
            Values = { [0x620] = (20ul) | (40ul << 8), [0x621] = 32 },
        }, At, SkylakeX);

        var limit = facts.Single(f => f.Key == "pmu.uncore.ratio_limit");
        Assert.Equal(FactAvailability.Present, limit.Availability);
        Assert.Equal("20x – 40x（× 100 MHz）", limit.Value);
        Assert.Equal((double)(20ul | (40ul << 8)), limit.NumericValue);

        var status = facts.Single(f => f.Key == "pmu.uncore.perf_status");
        Assert.Equal("32x（≈ 3200 MHz）", status.Value);
    }

    [Fact]
    public void 讀回全零標NotSupported不當真值()
    {
        var facts = UncorePmuService.Collect(new FakeMsr
        {
            Values = { [0x620] = 0, [0x621] = ulong.MaxValue },
        }, At, SkylakeX);

        foreach (var key in new[] { "pmu.uncore.ratio_limit", "pmu.uncore.perf_status" })
        {
            var f = facts.Single(x => x.Key == key);
            Assert.Equal(FactAvailability.NotSupported, f.Availability);
            Assert.Contains("未實作", f.UnavailableReason, StringComparison.Ordinal);
            Assert.Equal("", f.Value);
        }
    }

    [Fact]
    public void 個別讀取失敗標ReadError帶後端細節()
    {
        var facts = UncorePmuService.Collect(new FakeMsr
        {
            LastFailReason = "NTSTATUS 0xC0000022",
            Values = { [0x620] = null, [0x621] = 28 },
        }, At, SkylakeX);

        Assert.Equal(FactAvailability.ReadError, facts.Single(f => f.Key == "pmu.uncore.ratio_limit").Availability);
        Assert.Contains("0xC0000022", facts.Single(f => f.Key == "pmu.uncore.ratio_limit").UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(FactAvailability.Present, facts.Single(f => f.Key == "pmu.uncore.perf_status").Availability);
    }

    [Fact]
    public void 本機CPUID真探測不拋且必回三筆()
    {
        // 不注入 cpuIdProbe：用真實 CPUID 指令。本機是 7980XE（model 0x55）走收錄分支，
        // 其他機器走未收錄分支——兩條都是合法輸出，重點是永不拋、鍵齊三把。
        var facts = UncorePmuService.Collect(new FakeMsr { Available = false }, At);
        Assert.Equal(3, facts.Count);
        Assert.Equal(
            new[] { "pmu.uncore.perf_status", "pmu.uncore.platform", "pmu.uncore.ratio_limit" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }
}
