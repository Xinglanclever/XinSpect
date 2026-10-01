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
        new(3, "cpu.branch-speculation", "分支預測與推測執行", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：always/alternate/short-loop/random 25-75% 分支模式矩陣", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "CPU", "Quick 約 1–5 秒；Full 較久"),
        new(4, "cpu.rdrand-rdseed", "RDRAND / RDSEED", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：x64 硬體亂數指令吞吐與 retry 觀察", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "支援 RDRAND 與 RDSEED 的 x64 CPU", "Quick 約 3–15 秒；Full 較久"),
        new(5, "cpu.top-down", "Top-down 歸因", DeepBenchDomain.Cpu, 2, DeepBenchTestStatus.Integrated, "Phase 2 已接入：既有 Intel PMU Top-down 服務", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "Intel CPU；4 個通用 PMU 計數器；WinRing0 可用", "約 2–10 秒"),
        new(6, "topology.core-latency", "Core-to-core latency", DeepBenchDomain.Topology, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有核心延遲服務", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "多核心 CPU；使用者模式親和性", "約 10–30 秒"),
        new(7, "topology.core-bandwidth", "Core-to-core bandwidth", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：使用者模式核心到核心共享 buffer 搬運矩陣", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "多核心 CPU；使用者模式親和性", "Quick 約 10–30 秒；Full 較久"),
        new(8, "topology.smt-contention", "SMT contention", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：單緒、獨立雙核與 SMT sibling 合併吞吐對照", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "至少兩顆實體核心；其中一顆有 SMT", "Quick 約 1–5 秒；Full 較久"),
        new(9, "topology.hybrid-placement", "Hybrid P/E placement", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：CPUID 0x1A 誠實 P/E 分類與五種放置吞吐對照", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "Hybrid CPU；CPUID 7.0 hybrid 宣告與逐核心 CPUID 0x1A 可讀；至少兩顆可驗證 P-core 與兩顆 E-core；使用者模式親和性；不自動改排程", "Quick 約 2–6 秒；Full 約 10–25 秒"),
        new(10, "topology.coherence-lock", "Cache coherence / lock scaling", DeepBenchDomain.Topology, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：分離 cache line、false sharing 與 lock 1→2 scaling", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "至少兩顆實體核心；使用者模式親和性", "Quick 約 1–5 秒；Full 較久"),
        new(11, "memory.numa-tlb-largepage", "NUMA / TLB / large page", DeepBenchDomain.Memory, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：遞增 working set TLB 掃描曲線＋大分頁與跨 NUMA 對照（各子項獨立判定適用性）", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；大分頁對照另需 SeLockMemoryPrivilege 且配置成功；跨 NUMA 對照另需至少兩個 NUMA 節點", "Quick 約 5–15 秒；Full 較久"),
        new(12, "memory.cache-latency", "Cache latency ladder", DeepBenchDomain.Memory, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有 CacheBenchService", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；高負載可取消", "約 10–30 秒"),
        new(13, "memory.stream-bandwidth", "STREAM bandwidth", DeepBenchDomain.Memory, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有 MemBandwidthService", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；高負載可取消", "約 10–30 秒"),
        new(14, "memory.loaded-latency", "Loaded latency", DeepBenchDomain.Memory, 1, DeepBenchTestStatus.Integrated, "Phase 1 已整合：既有負載延遲量測", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；高負載可取消", "約 10–30 秒"),
        new(15, "memory.dram-mapping-inference", "DRAM row/rank/bank 推論", DeepBenchDomain.Memory, 3, DeepBenchTestStatus.Implemented, "Phase 3 已實作：stride 掃描曲線推論；映射僅為推論不宣稱確定", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；結果僅為推論", "Quick 約 10–30 秒；Full 較久"),
        new(16, "memory.ecc-whea-stress", "ECC / WHEA integrated stress", DeepBenchDomain.Memory, 2, DeepBenchTestStatus.Integrated, "Phase 2 已接入：既有記憶體頻寬壓力與同窗 WHEA 事件關聯", DeepBenchResourceClass.MemoryLoad, DeepBenchParallelSafety.Exclusive, true, "記憶體；WHEA-Logger 頻道可讀", "約 10–30 秒，另含 1 秒 WHEA 觀察期"),
        new(17, "gpu.fp32-fp64-integer", "GPU FP32/FP64/integer compute", DeepBenchDomain.Gpu, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作 FP32；FP64/integer 後續擴充", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；Feature Level 11_0+", "約 10–30 秒"),
        new(18, "gpu.vram-bandwidth", "VRAM bandwidth / latency", DeepBenchDomain.Gpu, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：D3D11 串流讀寫頻寬（256 MiB 工作集，不足降 64／16 MiB）", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；Feature Level 11_0+；至少 16 MiB 可用顯示記憶體", "Quick 約 5–15 秒；Full 較久"),
        new(19, "gpu.pcie-transfer", "PCIe upload / download", DeepBenchDomain.Gpu, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：PCIe 上傳／下載頻寬（256 MiB，不足降 64／16 MiB）", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；Feature Level 11_0+", "Quick 約 5–15 秒；Full 較久"),
        new(20, "gpu.raster-texture", "Fillrate / raster / texture", DeepBenchDomain.Gpu, 3, DeepBenchTestStatus.Implemented, "Phase 3 已實作：D3D11 全螢幕三角形填充率與單取樣／八取樣紋理吞吐，含 readback 完整性檢查", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；Feature Level 11_0+；WARP 一律拒收", "Quick 約 5–15 秒；Full 較久"),
        new(21, "gpu.codec-throughput", "Codec throughput", DeepBenchDomain.Gpu, 3, DeepBenchTestStatus.Implemented, "Phase 3 已實作：合成 NV12 幀經 Media Foundation Sink Writer 編碼吞吐（硬體編碼器存在時）；不捆綁影片", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "MFTEnumEx 找得到硬體 H.264 編碼器；Sink Writer 全管線吞吐", "Quick 約 5–15 秒；Full 較久"),
        new(22, "gpu.dispatch-jitter", "GPU kernel jitter", DeepBenchDomain.Gpu, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：D3D11 dispatch-to-sync 延遲與 p50/p95/p99 抖動（32／128 輪）", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；Feature Level 11_0+", "Quick 約 5–15 秒；Full 較久"),
        new(23, "storage.qd-ladder", "Storage QD ladder", DeepBenchDomain.Storage, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作：block × QD 矩陣", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；保留 8 GB 空間", "Quick 約 1–3 分鐘；Full 較久"),
        new(24, "storage.mixed-rw", "Storage mixed R/W", DeepBenchDomain.Storage, 1, DeepBenchTestStatus.Implemented, "Phase 1 已實作：100R→100W", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；保留 8 GB 空間", "Quick 約 1–3 分鐘；Full 較久"),
        new(25, "storage.slc-sustained-write", "SLC sustained write", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：既有 SlcCacheBenchService 持續寫入核心與曲線分析", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；Quick 額外 4 GiB、Full 16 GiB，另保留 8 GB", "Quick 寫入 4 GiB；Full 寫入 16 GiB"),
        new(26, "storage.write-integrity", "Write integrity", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：三種圖樣寫入、FlushToDisk 與逐區塊驗證", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；預算後保留 8 GB", "約 2–10 秒"),
        new(27, "storage.iocp-engine", "IOCP engine", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已接入：原生 IOCP completion engine 寫入／讀回驗證與 QD 延遲", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；預算後保留 8 GB", "Quick 約 1–3 分鐘；Full 較久"),
        new(28, "storage.flush-durability", "Flush / durability", DeepBenchDomain.Storage, 2, DeepBenchTestStatus.Implemented, "Phase 2 已實作：逐 MiB FlushToDisk 與重新開檔讀回驗證", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, true, "可寫暫存檔；預算後保留 8 GB", "約 2–10 秒"),
        new(29, "storage.io-gpu-pipeline", "I/O → GPU pipeline", DeepBenchDomain.Storage, 3, DeepBenchTestStatus.Deferred, "Phase 3：跨域管線尚未實作", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, false, "可寫暫存檔與 D3D 硬體 GPU", "尚未提供"),
        new(30, "gauntlet.multi-domain", "Multi-domain Gauntlet", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Deferred, "Phase 4：多域壓力尚未實作", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, false, "長時間高負載；明確同意", "尚未提供"),
        new(31, "gauntlet.boost-recovery", "Boost ramp / recovery", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Implemented, "Phase 4 已實作：全核心脈衝與電源 API 頻率曲線", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "CPU；全核心 managed pulse；短時接近滿載", "Quick 約 3–8 秒；Full 約 11–18 秒"),
        new(32, "gauntlet.power-state-latency", "Power state latency", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Implemented, "Phase 4 已實作：Windows 電源 API 查詢延遲與 P-state／C-state 變化偵測", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, true, "電源 API；量不到韌體時間會明示", "Quick 約 6–10 秒；Full 約 20–30 秒"),
        new(33, "gauntlet.throughput-degradation", "Throughput degradation", DeepBenchDomain.Gauntlet, 4, DeepBenchTestStatus.Implemented, "Phase 4 已實作：全核心 managed 吞吐分窗長跑", DeepBenchResourceClass.CpuLoad, DeepBenchParallelSafety.Exclusive, true, "CPU；全核心 managed throughput；短時接近滿載", "Quick 約 4–8 秒；Full 約 15–25 秒"),
        new(34, "confidence.engine", "Benchmark confidence engine", DeepBenchDomain.Confidence, 1, DeepBenchTestStatus.Implemented, "Phase 1 已內建於每個結果，不單獨執行", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, false, "由每項結果樣本與限制組成", "無單獨執行"),
        new(35, "ux.present-frame-pacing", "Present latency / frame pacing", DeepBenchDomain.UserExperience, 4, DeepBenchTestStatus.Implemented, "Phase 4 已實作：D3D11 swap chain Present 逐幀間距、API 耗時與尖峰", DeepBenchResourceClass.GpuLoad, DeepBenchParallelSafety.Exclusive, true, "D3D11 硬體 GPU；短暫建立小型視窗", "Quick 約 3–10 秒；Full 較久"),
        new(36, "ux.audio-buffer-glitch", "Audio buffer / glitch", DeepBenchDomain.UserExperience, 4, DeepBenchTestStatus.Implemented, "Phase 4 已實作：WASAPI 靜音供樣回呼間距、速率與疑似掉樣", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, true, "主動音訊輸出裝置；共用模式", "Quick 約 4–10 秒；Full 較久"),
        new(37, "ux.network-stack-latency", "Network stack latency", DeepBenchDomain.UserExperience, 4, DeepBenchTestStatus.Implemented, "已接入：本機 127.0.0.1 TCP loopback 延遲；不含 LAN／Internet", DeepBenchResourceClass.ReadOnly, DeepBenchParallelSafety.ParallelSafe, true, "本機 127.0.0.1 TCP loopback；不外連", "Quick 256 次；Full 1024 次"),
        new(38, "ux.synthetic-workloads", "Real-world synthetic workloads", DeepBenchDomain.UserExperience, 5, DeepBenchTestStatus.Deferred, "Phase 5：合成流程尚未實作", DeepBenchResourceClass.DiskWrite, DeepBenchParallelSafety.Exclusive, false, "多域資源；明確同意", "尚未提供")
    ];
}

