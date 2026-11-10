namespace XinSpect;

public static class DeepBenchTargetCatalog
{
    private static readonly string[] AllIds =
    [
        "cpu.aes-sha", "cpu.load-use-ilp-branch", "cpu.branch-speculation", "cpu.rdrand-rdseed",
        "topology.core-latency", "topology.core-bandwidth", "topology.smt-contention",
        "topology.hybrid-placement", "topology.coherence-lock", "memory.numa-tlb-largepage",
        "memory.cache-latency", "memory.stream-bandwidth", "memory.loaded-latency",
        "memory.dram-mapping-inference", "memory.ecc-whea-stress", "gpu.fp32-fp64-integer",
        "gpu.vram-bandwidth", "gpu.pcie-transfer", "gpu.dispatch-jitter", "gpu.raster-texture",
        "gpu.codec-throughput", "cpu.top-down", "storage.qd-ladder", "storage.mixed-rw",
        "storage.write-integrity", "storage.flush-durability", "storage.slc-sustained-write",
        "storage.iocp-engine", "storage.io-gpu-pipeline", "ux.network-stack-latency",
        "ux.audio-buffer-glitch", "ux.present-frame-pacing", "gauntlet.boost-recovery",
        "gauntlet.throughput-degradation", "gauntlet.power-state-latency", "gauntlet.multi-domain",
        "ux.synthetic-workloads", "confidence.engine", "cpu.vector-width-throughput",
        "memory.cache-bandwidth-ladder", "memory.copy-kernel-bandwidth", "topology.tsc-cross-core-sync",
        "topology.core-uniformity", "memory.dram-pattern-integrity"
    ];

    public static IReadOnlyList<DeepBenchTargetOption> All { get; } =
    [
        new("all", "全域通用", "所有已接入測項", AllIds),
        new("msdt", "MSDT 主流桌機", "一般桌機 CPU、記憶體、GPU 與互動延遲", [
            "cpu.aes-sha", "cpu.load-use-ilp-branch", "cpu.branch-speculation", "cpu.rdrand-rdseed",
            "topology.core-latency", "topology.smt-contention", "memory.cache-latency",
            "memory.stream-bandwidth", "memory.loaded-latency", "gpu.fp32-fp64-integer",
            "gpu.pcie-transfer", "storage.qd-ladder", "ux.network-stack-latency",
            "ux.audio-buffer-glitch", "ux.present-frame-pacing", "gauntlet.boost-recovery",
            "gauntlet.throughput-degradation", "gauntlet.power-state-latency", "ux.synthetic-workloads",
            "confidence.engine", "memory.dram-pattern-integrity"
        ]),
        new("hedt", "HEDT／高階工作站", "X58／X79／X99／X299、Core X 與 Threadripper", [
            "cpu.aes-sha", "cpu.load-use-ilp-branch", "cpu.branch-speculation", "cpu.rdrand-rdseed",
            "cpu.top-down", "cpu.vector-width-throughput", "topology.core-latency", "topology.core-bandwidth",
            "topology.smt-contention", "topology.coherence-lock", "topology.tsc-cross-core-sync",
            "topology.core-uniformity", "memory.numa-tlb-largepage", "memory.cache-latency",
            "memory.stream-bandwidth", "memory.loaded-latency", "memory.dram-mapping-inference",
            "memory.ecc-whea-stress", "memory.cache-bandwidth-ladder", "memory.copy-kernel-bandwidth",
            "gpu.pcie-transfer", "storage.qd-ladder", "gauntlet.boost-recovery",
            "gauntlet.throughput-degradation", "gauntlet.multi-domain", "confidence.engine", "memory.dram-pattern-integrity"
        ]),
        new("modt", "MODT 行動 CPU 桌機板", "行動處理器移植桌機板；以電源、睿頻恢復與持續吞吐為主", [
            "cpu.aes-sha", "cpu.load-use-ilp-branch", "cpu.branch-speculation", "topology.core-latency",
            "topology.smt-contention", "topology.hybrid-placement", "memory.cache-latency",
            "memory.stream-bandwidth", "memory.loaded-latency", "gpu.dispatch-jitter",
            "ux.network-stack-latency", "ux.audio-buffer-glitch", "ux.present-frame-pacing",
            "gauntlet.boost-recovery", "gauntlet.throughput-degradation",
            "gauntlet.power-state-latency", "ux.synthetic-workloads", "confidence.engine", "memory.dram-pattern-integrity"
        ]),
        new("server-workstation", "伺服器／工作站", "C602／C612／C621／C422、Xeon E5／Xeon W 與 EPYC", [
            "cpu.aes-sha", "cpu.load-use-ilp-branch", "cpu.branch-speculation", "cpu.rdrand-rdseed",
            "cpu.top-down", "cpu.vector-width-throughput", "topology.core-latency", "topology.core-bandwidth",
            "topology.smt-contention", "topology.coherence-lock", "topology.tsc-cross-core-sync",
            "topology.core-uniformity", "memory.numa-tlb-largepage", "memory.cache-latency",
            "memory.stream-bandwidth", "memory.loaded-latency", "memory.dram-mapping-inference",
            "memory.ecc-whea-stress", "memory.cache-bandwidth-ladder", "memory.copy-kernel-bandwidth",
            "gpu.pcie-transfer", "storage.qd-ladder", "storage.mixed-rw", "storage.iocp-engine",
            "ux.network-stack-latency", "gauntlet.boost-recovery", "gauntlet.throughput-degradation",
            "gauntlet.power-state-latency", "gauntlet.multi-domain", "confidence.engine", "memory.dram-pattern-integrity"
        ])
    ];

    public static DeepBenchTargetOption Default => All[0];

    public static DeepBenchTargetOption? Find(string? id) =>
        All.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    public static DeepBenchTargetOption Suggest(string? cpuName, string? socket, string? boardModel)
    {
        string evidence = $"{cpuName} {socket} {boardModel}".ToUpperInvariant();
        if (ContainsAny(evidence, "EPYC", "THREADRIPPER PRO", "XEON W", "XEON E5", "XEON SCALABLE", "XEON GOLD", "XEON PLATINUM",
                "C602", "C612", "C621", "C621A", "C422", "W790", "SP3", "SP5", "WRX80", "WRX90",
                "LGA 3647", "LGA3647", "LGA 4677", "LGA4677"))
            return Find("server-workstation")!;

        if (ContainsAny(evidence, "MOBILE ON DESKTOP", "MODT"))
            return Find("modt")!;

        if (ContainsAny(evidence, "THREADRIPPER", "CORE X", "X58", "X79", "X99", "X299", "TR4", "STRX4",
                "LGA 1366", "LGA1366", "LGA 2011", "LGA2011", "LGA 2066", "LGA2066"))
            return Find("hedt")!;

        return Find("msdt")!;
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term =>
    {
        int start = 0;
        while ((start = value.IndexOf(term, start, StringComparison.Ordinal)) >= 0)
        {
            int end = start + term.Length;
            bool leftBoundary = start == 0 || !char.IsLetterOrDigit(value[start - 1]);
            bool rightBoundary = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (leftBoundary && rightBoundary) return true;
            start = end;
        }

        return false;
    });
}
