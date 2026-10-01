namespace XinSpect;

public enum DeepBenchResourceClass { ReadOnly, CpuLoad, MemoryLoad, GpuLoad, DiskWrite }
public enum DeepBenchParallelSafety { ParallelSafe, Exclusive }

public sealed record DeepBenchCatalogEntry(
    int MatrixNumber,
    string Id,
    string Title,
    DeepBenchDomain Domain,
    int Phase,
    DeepBenchTestStatus Status,
    string StatusText,
    DeepBenchResourceClass ResourceClass,
    DeepBenchParallelSafety ParallelSafety,
    bool Runnable,
    string Requirement,
    string QuickEstimateText);

public sealed record DeepBenchSkippedEntry(string TestId, string Reason);
public sealed record DeepBenchPlan(IReadOnlyList<string> SelectedIds, IReadOnlyList<DeepBenchSkippedEntry> Skipped);

public static class DeepBenchCatalog
{
    public static IReadOnlyList<DeepBenchCatalogEntry> All { get; } =
    [
        new(1, "cpu.aes-sha", "AES-256 / SHA-256 吞吐", DeepBenchDomain.Cpu, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作：.NET crypto API 實測", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "CPU；無外部工具", "約 10–30 秒"),
        new(2, "cpu.load-use-ilp-branch", "Load-to-use / ILP / branch", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：依賴載入鏈、ILP 1/2/4/8 與分支圖樣", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "CPU", "約 1–5 秒"),
        new(3, "cpu.branch-speculation", "分支預測與推測執行", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Deferred, "Phase 2：分支模式矩陣尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "CPU", "尚未提供"),
        new(4, "cpu.rdrand-rdseed", "RDRAND / RDSEED", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Deferred, "Phase 2：硬體亂數尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "支援硬體亂數的 CPU", "尚未提供"),
        new(5, "cpu.top-down", "Top-down 歸因", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Integrated, "Phase 2 已接入：既有 Intel PMU Top-down 服務", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "Intel CPU；4 個通用 PMU 計數器；WinRing0 可用", "約 2–10 秒"),
        new(6, "topology.core-latency", "Core-to-core latency", DeepBenchDomain.Topology, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有核心延遲服務", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "多核心 CPU；使用者模式親和性", "約 10–30 秒"),
        new(7, "topology.core-bandwidth", "Core-to-core bandwidth", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Deferred, "Phase 2：搬運矩陣尚未實作", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, false, "多核心 CPU", "尚未提供"),
        new(8, "topology.smt-contention", "SMT contention", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Deferred, "Phase 2：sibling 干擾尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "SMT CPU", "尚未提供"),
        new(9, "topology.hybrid-placement", "Hybrid P/E placement", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Deferred, "Phase 2：分組量測尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "hybrid CPU；不自動改排程", "尚未提供"),
        new(10, "topology.coherence-lock", "Cache coherence / lock scaling", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Deferred, "Phase 2：false sharing 與 lock 尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "多核心 CPU", "尚未提供"),
        new(11, "memory.numa-tlb-largepage", "NUMA / TLB / large page", DeepBenchDomain.Memory, 2, DeepBenchTestStatus.Deferred, "Phase 2：行為差異量測尚未實作", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, false, "NUMA/TLB API 可讀", "尚未提供"),
        new(12, "memory.cache-latency", "Cache latency ladder", DeepBenchDomain.Memory, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有 CacheBenchService", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；高負載可取消", "約 10–30 秒"),
        new(13, "memory.stream-bandwidth", "STREAM bandwidth", DeepBenchDomain.Memory, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有 MemBandwidthService", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；高負載可取消", "約 10–30 秒"),
        new(14, "memory.loaded-latency", "Loaded latency", DeepBenchDomain.Memory, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有負載延遲量測", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；高負載可取消", "約 10–30 秒"),
        new(15, "memory.dram-mapping-inference", "DRAM row/rank/bank 推論", DeepBenchDomain.Memory, 3, DeepBenchTestStatus.Deferred, "Phase 3：stride 推論尚未實作", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, false, "記憶體；結果僅為推論", "尚未提供"),
        new(16, "memory.ecc-whea-stress", "ECC / WHEA integrated stress", DeepBenchDomain.Memory, 2, DeepBenchTestStatus.Deferred, "Phase 2：壓力與事件關聯尚未實作", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, false, "記憶體壓力與 WHEA 讀取", "尚未提供"),
        new(17, "gpu.fp32-fp64-integer", "GPU FP32/FP64/integer compute", DeepBenchDomain.Gpu, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作 FP32；FP64/integer 後續擴充", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；Feature Level 11_0+", "約 10–30 秒"),
        new(18, "gpu.vram-bandwidth", "VRAM bandwidth / latency", DeepBenchDomain.Gpu, 2, DeepBenchTestStatus.Deferred, "Phase 2：VRAM 矩陣尚未實作", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, false, "D3D11/D3D12 硬體 GPU", "尚未提供"),
        new(19, "gpu.pcie-transfer", "PCIe upload / download", DeepBenchDomain.Gpu, 2, DeepBenchTestStatus.Deferred, "Phase 2：傳輸矩陣尚未實作", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, false, "D3D 硬體 GPU", "尚未提供"),
        new(20, "gpu.raster-texture", "Fillrate / raster / texture", DeepBenchDomain.Gpu, 3, DeepBenchTestStatus.Deferred, "Phase 3：圖形管線尚未實作", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, false, "D3D11 硬體 GPU", "尚未提供"),
        new(21, "gpu.codec-throughput", "Codec throughput", DeepBenchDomain.Gpu, 3, DeepBenchTestStatus.Deferred, "Phase 3：生成影格 codec 尚未實作", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, false, "硬體 codec；不捆綁影片", "尚未提供"),
        new(22, "gpu.dispatch-jitter", "GPU kernel jitter", DeepBenchDomain.Gpu, 2, DeepBenchTestStatus.Deferred, "Phase 2：dispatch jitter 尚未實作", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, false, "D3D 硬體 GPU", "尚未提供"),
        new(23, "storage.qd-ladder", "Storage QD ladder", DeepBenchDomain.Storage, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作：block × QD 矩陣", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；保留 8 GB 空間", "Quick 約 1–3 分鐘；Full 較久"),
        new(24, "storage.mixed-rw", "Storage mixed R/W", DeepBenchDomain.Storage, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作：100R→100W", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；保留 8 GB 空間", "Quick 約 1–3 分鐘；Full 較久"),
        new(25, "storage.slc-sustained-write", "SLC sustained write", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：既有 SlcCacheBenchService 持續寫入核心與曲線分析", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；Quick 額外 4 GiB、Full 16 GiB，另保留 8 GB", "Quick 寫入 4 GiB；Full 寫入 16 GiB"),
        new(26, "storage.write-integrity", "Write integrity", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：三種圖樣寫入、FlushToDisk 與逐區塊驗證", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；預算後保留 8 GB", "約 2–10 秒"),
        new(27, "storage.iocp-engine", "IOCP engine", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Deferred, "Phase 2：completion 引擎尚未實作", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, false, "可寫暫存檔", "尚未提供"),
        new(28, "storage.flush-durability", "Flush / durability", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：逐 MiB FlushToDisk 與重新開檔讀回驗證", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；預算後保留 8 GB", "約 2–10 秒"),
        new(29, "storage.io-gpu-pipeline", "I/O → GPU pipeline", DeepBenchDomain.Storage, 3, DeepBenchTestStatus.Deferred, "Phase 3：跨域管線尚未實作", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, false, "可寫暫存檔與 D3D 硬體 GPU", "尚未提供"),
        new(30, "gauntlet.multi-domain", "Multi-domain Gauntlet", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Deferred, "Phase 4：多域壓力尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "長時間高負載；明確同意", "尚未提供"),
        new(31, "gauntlet.boost-recovery", "Boost ramp / recovery", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Deferred, "Phase 4：爬升恢復曲線尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "長時間高負載", "尚未提供"),
        new(32, "gauntlet.power-state-latency", "Power state latency", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Deferred, "Phase 4：恢復觀察尚未實作", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, false, "電源 API；量不到韌體時間會明示", "尚未提供"),
        new(33, "gauntlet.throughput-degradation", "Throughput degradation", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Deferred, "Phase 4：長跑衰退尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "長時間高負載", "尚未提供"),
        new(34, "confidence.engine", "Benchmark confidence engine", DeepBenchDomain.Confidence, 1, DeepBenchTestStatus.Implemented, "Phase 1 已內建於每個結果，不單獨執行", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, false, "由每項結果樣本與限制組成", "無單獨執行"),
        new(35, "ux.present-frame-pacing", "Present latency / frame pacing", DeepBenchDomain.UserExperience, 4, DeepBenchTestStatus.Deferred, "Phase 4：FrameTime 尚未接入 Hub", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, false, "可建立視窗/GPU", "尚未提供"),
        new(36, "ux.audio-buffer-glitch", "Audio buffer / glitch", DeepBenchDomain.UserExperience, 4, DeepBenchTestStatus.Deferred, "Phase 4：WASAPI 量測尚未實作", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, false, "音訊裝置", "尚未提供"),
        new(37, "ux.network-stack-latency", "Network stack latency", DeepBenchDomain.UserExperience, 4, DeepBenchTestStatus.Deferred, "Phase 4：本機/LAN socket 尚未實作", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, false, "本機或區域網路；不宣稱 Internet", "尚未提供"),
        new(38, "ux.synthetic-workloads", "Real-world synthetic workloads", DeepBenchDomain.UserExperience, 5, DeepBenchTestStatus.Deferred, "Phase 5：合成流程尚未實作", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, false, "多域資源；明確同意", "尚未提供")
    ];
}

