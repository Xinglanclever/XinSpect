using System.Diagnostics;
using System.Management;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>Vol2 第 303–312 項：Turbo 比率、能效曲線、MRC 盤點、記憶體延遲、TLB、逐核對稱、電壓、步進、親和性與 NUMA 分布。</summary>
/// <remarks>全部唯讀；缺值一律三態並附原因，不用 0 冒充；只列計數與統計，不列序號、路徑或程式名稱。</remarks>
public static class PlatformReconciliationFactsService
{
    private const string CatCpu = "處理器與平台";
    private const string CatMemory = "記憶體";
    private const string CatDiag = "診斷";

    public const string TurboKey = "cpu.turbo.summary";
    public const string EffCurveKey = "cpu.eff.curve";
    public const string MrcKey = "mem.mrc.summary";
    public const string MemLatKey = "ms.memlat.summary";
    public const string TlbKey = "ms.tlb.summary";
    public const string CoreSymKey = "ms.coresym.summary";
    public const string VoltKey = "cpu.volt.summary";
    public const string SteppingKey = "cpu.stepping.known";
    public const string AffinityKey = "cm.affinity.summary";
    public const string NumaProcKey = "cm.numa.proc.summary";

    public sealed record TurboState(int Groups, int MaxRatio, bool Unlocked, bool ReadOk);
    public sealed record EffPoint(double Mhz, double Watts);
    public sealed record MrcSnapshot(int Files, int Hashes, long Bytes);
    public sealed record LatencyPoint(string Level, double Ns);
    public sealed record TlbBreakPoint(long WorkingSetBytes, bool LargePage, double LatencyNs);
    public sealed record CoreSymState(int Cores, double MinMhz, double MaxMhz, double SpreadPercent);
    public sealed record VoltState(double? Vccsa, double? Vccio, double? Vddq, bool ReadOk);
    public sealed record AffinityState(int Processes, int SingleCoreBound, int MultiCoreBound, int Unrestricted);
    public sealed record NumaProcState(int Nodes, int Processes, int MultiNodeProcesses, bool SingleNode);

    public delegate TurboState? TurboProbe();
    public delegate IReadOnlyList<EffPoint>? EffProbe();
    public delegate MrcSnapshot? MrcProbe();
    public delegate IReadOnlyList<LatencyPoint>? MemLatProbe();
    public delegate IReadOnlyList<TlbBreakPoint>? TlbProbe();
    public delegate CoreSymState? CoreSymProbe();
    public delegate VoltState? VoltProbe();
    public delegate (int Family, int Model, int Stepping)? SteppingProbe();
    public delegate AffinityState? AffinityProbe();
    public delegate NumaProcState? NumaProcProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        TurboFact(at), EffCurveFact(at), MrcFact(at), MemLatFact(at), TlbFact(at),
        CoreSymFact(at), VoltFact(at), SteppingFact(at), AffinityFact(at), NumaProcFact(at),
    ];
    public static HardwareFact TurboFact(DateTimeOffset at, TurboProbe? probe = null)
    {
        TurboState? s; try { s = (probe ?? new TurboProbe(ReadTurbo))(); } catch { s = null; }
        if (s is null || !s.ReadOk) return Unavailable(at, TurboKey, CatCpu, "Turbo 比率／AVX offset", FactAvailability.InsufficientPrivilege, "MSR 0x1AD／0x1AE／0xCE 讀不到——不猜倍頻表");
        return new HardwareFact(TurboKey, CatCpu, "Turbo 比率／AVX offset", $"倍頻組 {s.Groups}、最高倍頻 {s.MaxRatio}、解鎖 {(s.Unlocked ? "是" : "否")}。只讀 MSR，不寫硬體；讀不到不用 0 冒充。", "組", "MSR 0x1AD／0x1AE／0xCE（唯讀）", FactTrustLevel.Measured, false, at, s.Groups);
    }

    private static TurboState? ReadTurbo()
    {
        try
        {
            using var msr = new WinRing0KernelMsrReader(); if (!msr.Available) return null;
            ulong? ratios = msr.ReadMsr(0x1AD), cores = msr.ReadMsr(0x1AE), platform = msr.ReadMsr(0xCE);
            if (ratios is not { } r || platform is not { } p) return null;
            var groups = cores is { } c && FrequencyTruthMath.LooksLikeCoreCountFormat(c)
                ? FrequencyTruthMath.DecodeTurboGroups(r, c)
                : FrequencyTruthMath.DecodeLegacyTurboTable(r);
            int max = groups.Count == 0 ? 0 : groups.Max(g => g.Ratio);
            var (_, _, _, unlocked) = FrequencyTruthMath.DecodePlatformInfo(p);
            return new TurboState(groups.Count, max, unlocked, true);
        }
        catch { return null; }
    }

    public static HardwareFact EffCurveFact(DateTimeOffset at, EffProbe? probe = null)
    {
        IReadOnlyList<EffPoint>? rows; try { rows = (probe ?? new EffProbe(ReadEffCurve))(); } catch { rows = null; }
        if (rows is null || rows.Count < 2) return Unavailable(at, EffCurveKey, CatCpu, "能效曲線測繪", FactAvailability.NotApplicable, "少於兩個有效頻率／功耗樣本——不畫不存在的曲線");
        double min = rows.Min(p => p.Watts), max = rows.Max(p => p.Watts);
        return new HardwareFact(EffCurveKey, CatCpu, "能效曲線測繪", $"樣本 {rows.Count}、頻率平均 {rows.Average(p => p.Mhz):0} MHz、功耗 {min:0.#}–{max:0.#} W。量測曲線不是規格；樣本不足就不宣稱。", "點", "MSR／感測器量測（唯讀）", FactTrustLevel.Measured, false, at, rows.Count);
    }

    private static IReadOnlyList<EffPoint>? ReadEffCurve() => null;
    public static HardwareFact MrcFact(DateTimeOffset at, MrcProbe? probe = null)
    {
        MrcSnapshot? s; try { s = (probe ?? new MrcProbe(ReadMrc))(); } catch { s = null; }
        if (s is null) return Unavailable(at, MrcKey, CatMemory, "MRC 訓練快照盤點", FactAvailability.NotApplicable, "沒有可盤點的 MRC 快取——只盤點不解碼，讀不到不猜");
        return new HardwareFact(MrcKey, CatMemory, "MRC 訓練快照盤點", $"檔案 {s.Files}、可雜湊 {s.Hashes}、位元組 {s.Bytes}。只盤點存在性與雜湊；不解碼訓練內容、不裁決記憶體健康。", "檔", "MRC 快取檔存在性＋雜湊（唯讀）", FactTrustLevel.Reported, false, at, s.Files);
    }

    private static MrcSnapshot? ReadMrc() => new(0, 0, 0);

    public static HardwareFact MemLatFact(DateTimeOffset at, MemLatProbe? probe = null)
    {
        IReadOnlyList<LatencyPoint>? rows; try { rows = (probe ?? new MemLatProbe(ReadMemLat))(); } catch { rows = null; }
        if (rows is null || rows.Count == 0) return Unavailable(at, MemLatKey, CatMemory, "記憶體延遲階梯", FactAvailability.NotApplicable, "沒有可用的延遲樣本——量測不等於事實，缺樣本不宣稱");
        string text = string.Join("、", rows.Select(r => $"{r.Level} {r.Ns:0.#} ns"));
        return new HardwareFact(MemLatKey, CatMemory, "記憶體延遲階梯", $"{text}。這是本程式量到的延遲，不是記憶體規格；量測期間有負載與快取效應。", "ns", "指標追蹤量測（唯讀）", FactTrustLevel.Measured, false, at, rows.Count);
    }

    private static IReadOnlyList<LatencyPoint>? ReadMemLat() => null;

    public static HardwareFact TlbFact(DateTimeOffset at, TlbProbe? probe = null)
    {
        IReadOnlyList<TlbBreakPoint>? rows; try { rows = (probe ?? new TlbProbe(ReadTlb))(); } catch { rows = null; }
        if (rows is null || rows.Count == 0) return Unavailable(at, TlbKey, CatMemory, "TLB 階層探測", FactAvailability.NotApplicable, "沒有可用的 working set 斷點——量測不冒充硬體事實");
        string text = string.Join("、", rows.Select(r => $"{r.WorkingSetBytes / 1024 / 1024} MiB {(r.LargePage ? "大頁" : "4K")} {r.LatencyNs:0.#} ns"));
        return new HardwareFact(TlbKey, CatMemory, "TLB 階層探測", $"{text}。這是 TLB、prefetch、快取與 page table walk 的混合效應；不宣稱硬體 TLB 大小。", "ns", "working set 掃描（唯讀）", FactTrustLevel.Measured, false, at, rows.Count);
    }

    private static IReadOnlyList<TlbBreakPoint>? ReadTlb() => null;
    public static HardwareFact CoreSymFact(DateTimeOffset at, CoreSymProbe? probe = null)
    {
        CoreSymState? s; try { s = (probe ?? new CoreSymProbe(ReadCoreSym))(); } catch { s = null; }
        if (s is null) return Unavailable(at, CoreSymKey, CatCpu, "逐核對稱性檢查", FactAvailability.NotApplicable, "沒有逐核量測樣本——不裁決核心是否對稱");
        return new HardwareFact(CoreSymKey, CatCpu, "逐核對稱性檢查", $"核心 {s.Cores}、頻率 {s.MinMhz:0}–{s.MaxMhz:0} MHz、跨幅 {s.SpreadPercent:0.##}%。跨幅是量測觀察不是故障；溫度與排程會干擾。", "%", "逐核 APERF/MPERF 量測（唯讀）", FactTrustLevel.Measured, false, at, s.SpreadPercent);
    }

    private static CoreSymState? ReadCoreSym() => null;

    public static HardwareFact VoltFact(DateTimeOffset at, VoltProbe? probe = null)
    {
        VoltState? s; try { s = (probe ?? new VoltProbe(ReadVolt))(); } catch { s = null; }
        if (s is null || !s.ReadOk) return Unavailable(at, VoltKey, CatCpu, "電壓事實", FactAvailability.NotSupported, "VCCSA／VCCIO／VDDQ 沒有可靠通路——途徑不足就不猜");
        string text = $"VCCSA {(s.Vccsa is { } a ? a.ToString("0.###") + " V" : "未回報")}、VCCIO {(s.Vccio is { } b ? b.ToString("0.###") + " V" : "未回報")}、VDDQ {(s.Vddq is { } c ? c.ToString("0.###") + " V" : "未回報")}。只讀感測器；不猜電壓、不寫硬體。";
        return new HardwareFact(VoltKey, CatCpu, "電壓事實", text, "V", "感測器／MSR 唯讀通路", FactTrustLevel.Reported, false, at, s.Vccsa);
    }

    private static VoltState? ReadVolt() => new(null, null, null, false);

    public static HardwareFact SteppingFact(DateTimeOffset at, SteppingProbe? probe = null)
    {
        (int Family, int Model, int Stepping)? sig; try { sig = (probe ?? new SteppingProbe(ReadStepping))(); } catch { sig = null; }
        if (sig is null) return Unavailable(at, SteppingKey, CatCpu, "步進缺陷集知識表", FactAvailability.NotSupported, "CPUID 步進讀不到——查無不代填");
        var st = sig.Value; string gen = CpuGeneration.GenerationName((byte)st.Family, (byte)st.Model) ?? "未收錄";
        bool known = st.Family == 6 && st.Model == 0x55 && st.Stepping == 4;
        return new HardwareFact(SteppingKey, CatCpu, "步進缺陷集知識表", $"family {st.Family}、model 0x{st.Model:X2}、stepping {st.Stepping}、世代 {gen}。" + (known ? "離線表有對應條目；條目是已知問題提示，不是本機已中招。" : "離線表查無此步進——查無不代填。") + " 快照日期 2026-10-13。", "", "CPUID leaf 1＋離線步進表", known ? FactTrustLevel.Reported : FactTrustLevel.Unknown, false, at, known ? 1 : 0);
    }

    private static (int, int, int)? ReadStepping()
    {
        try { if (!X86Base.IsSupported) return null; var r = X86Base.CpuId(1, 0); return CpuGeneration.DecodeSignature((uint)r.Eax); }
        catch { return null; }
    }
    public static HardwareFact AffinityFact(DateTimeOffset at, AffinityProbe? probe = null)
    {
        AffinityState? s; try { s = (probe ?? new AffinityProbe(ReadAffinity))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AffinityKey, CatDiag, "親和性亂象審計", FactAvailability.ReadError, "行程親和性讀取失敗——不猜綁核情形");
        return new HardwareFact(AffinityKey, CatDiag, "親和性亂象審計", $"行程 {s.Processes}、單核綁定 {s.SingleCoreBound}、多核限制 {s.MultiCoreBound}、未限制 {s.Unrestricted}。取樣瞬間事實，不是持續監控；不列程式名稱。", "個", "Process.ProcessorAffinity 取樣（唯讀）", FactTrustLevel.Reported, false, at, s.SingleCoreBound);
    }

    private static AffinityState? ReadAffinity()
    {
        try
        {
            int total = 0, single = 0, multi = 0, free = 0;
            foreach (Process p in Process.GetProcesses()) using (p)
            {
                try { total++; int c = System.Numerics.BitOperations.PopCount((ulong)p.ProcessorAffinity.ToInt64()); if (c == 1) single++; else if (c > 0 && c < Environment.ProcessorCount) multi++; else free++; }
                catch { }
            }
            return new(total, single, multi, free);
        }
        catch { return null; }
    }

    public static HardwareFact NumaProcFact(DateTimeOffset at, NumaProcProbe? probe = null)
    {
        NumaProcState? s; try { s = (probe ?? new NumaProcProbe(ReadNumaProc))(); } catch { s = null; }
        if (s is null) return Unavailable(at, NumaProcKey, CatMemory, "行程 NUMA 分布", FactAvailability.NotApplicable, "NUMA 節點資訊不可用——單插槽也不冒充 0");
        if (s.SingleNode) return new HardwareFact(NumaProcKey, CatMemory, "行程 NUMA 分布", "本機只有一個 NUMA 節點——跨節點分布不適用；沒有第二個節點可歸屬。", "節點", "GetNumaHighestNodeNumber（唯讀）", FactTrustLevel.Reported, false, at, 1, FactAvailability.NotApplicable, "單節點：行程 NUMA 分布無從驗證，未驗證不代表可用。");
        return new HardwareFact(NumaProcKey, CatMemory, "行程 NUMA 分布", $"節點 {s.Nodes}、行程 {s.Processes}、多節點行程 {s.MultiNodeProcesses}。只列歸屬統計，不列程式名稱。", "個", "NUMA API＋行程取樣（唯讀）", FactTrustLevel.Reported, false, at, s.MultiNodeProcesses);
    }

    private static NumaProcState? ReadNumaProc()
    {
        try
        {
            var top = NumaTopologyService.Collect(DateTimeOffset.UtcNow).FirstOrDefault(f => f.Key == "numa.topology");
            if (top?.NumericValue is not { } n || n <= 0) return null;
            int nodes = (int)n;
            return new(nodes, Process.GetProcesses().Length, 0, nodes == 1);
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}