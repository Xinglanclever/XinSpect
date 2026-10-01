# XinSpect Deep Bench 20 Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立 Deep Bench Hub 第一期共通骨架與八個可執行深測入口，讓原 38 項全量進入同一 Test Catalog、Run Session、可信度與歷史系統。

**Architecture:** 先做共通結果模型、38 項目錄、Orchestrator、可信度與歷史，再以 Adapter 接入新的 CPU/GPU/儲存測試與既有 Cache/STREAM/Core-latency 服務。UI 新增獨立 Deep Bench 頁；Phase 2–5 後續各自成 plan，但本期就完成全量登記。

**Tech Stack:** .NET 10 WPF、xunit、System.Text.Json、BCL crypto、Windows `RandomAccess` 未緩衝 I/O、D3D11 compute shader 與現有 COM/PInvoke 慣例。

**Spec:** `docs/superpowers/specs/2026-10-01-xinspect-deep-bench-20-design.md`

## Global Constraints

- 既有測試基線為 **2195 passed / 0 failed**；每個工作完成後測試只能增加，不得減少既有測試。
- 目標框架維持 `net10.0-windows`；不新增第三方測試執行檔、不以管理員權限為必要條件。
- 原 38 項必須全量登記；狀態只能是 `Implemented`、`Integrated`、`NotSupported`、`Deferred`，並顯示可讀原因。
- 每個結果至少保留原始樣本或點位、單位、設定、起訖時間、限制與錯誤分類；不得只回傳單一分數。
- 不產生加權總分；Cross-domain Synthesis 只引用同一 Run Session，缺環節就明示缺少。
- 磁碟測試只寫入 `XinSpect.deepbench.tmp`，受使用者選擇與剩餘空間保護，`finally` 必刪除；不得碰既有檔案。
- D3D11 只報硬體裝置實測；WARP、驅動拒絕、裝置建立失敗都要進錯誤分類。
- AES/SHA 是 .NET crypto 在本機 API 的實測吞吐，不得宣稱保證 AES-NI 或密碼學認證。
- Legacy Adapter 保留現有 UI 與資料，只擴充 `public Task RunAsync()` 與快照，不重寫歷史。
- 空樣本、非有限值、取消、部分失敗、JSON 損毀、資源釋放皆必須有測試；不得吞例外後假裝成功。
- 未追蹤檔案 `CODE-REVIEW-2026-09-29.md`、`HANDOFF-*.md`、`PROGRAM-2026-09-29.md`、`Tests/TempGalleryShot.cs`、`verify-shots-210/` 不納入 commit。

## Review Focus

- **38 項漏項/重複：** `Tests/DeepBenchCatalogTests.cs` 驗證 1–38 連續、ID 唯一、狀態與資源聲明完整。
- **空樣本與非有限值：** `Tests/DeepBenchMeasurementTests.cs` 驗證空樣本、單一樣本、離群值與 NaN/Infinity 不被算成正常樣本。
- **取消與部分失敗：** `Tests/DeepBenchOrchestratorTests.cs` 驗證取消保存已完成結果、單項失敗不中斷整場、重複/未知 ID 先拒絕。
- **磁碟安全與釋放：** `Tests/DeepBenchDiskIoTests.cs` 以小暫存檔驗證空間上限、取消與例外後檔案刪除。
- **不實承諾：** `Tests/DeepBenchHonestyTests.cs` 掃描 UI/結果文字，禁止玄學總分、保證 AES-NI、WARP 偽稱 GPU、MemTest86 等說法。

---

### Task 1: 共用量測模型與可信度統計

**Files:**
- Create: `Models/DeepBench/DeepBenchModels.cs`
- Create: `Services/DeepBench/DeepBenchMeasurementStatistics.cs`
- Test: `Tests/DeepBenchMeasurementTests.cs`

**Interfaces:**
- `enum DeepBenchTestStatus { Implemented, Integrated, NotSupported, Deferred }`
- `enum DeepBenchDomain { Cpu, Topology, Memory, Gpu, Storage, Gauntlet, UserExperience, Confidence }`
- `enum DeepBenchFailureKind { None, NotRun, Unsupported, DriverRejected, InsufficientPermission, Unstable, Cancelled, PlatformError }`
- `enum DeepBenchConfidence { Insufficient, High, Medium, Low }`
- `enum DeepBenchRunProfile { Quick, Full }`
- `sealed record DeepBenchMetricPoint(double Value, IReadOnlyDictionary<string,string> Axes, IReadOnlyList<double> Samples)`
- `sealed record DeepBenchMetric(string Id, string Title, string Unit, bool HigherIsBetter, string Configuration, IReadOnlyList<double> Samples, IReadOnlyList<DeepBenchMetricPoint> Points)`
- `sealed record DeepBenchTestResult(string TestId, Guid SessionId, DeepBenchRunProfile Profile, DateTime StartedUtc, DateTime EndedUtc, string Configuration, IReadOnlyList<DeepBenchMetric> Metrics, IReadOnlyList<string> Conditions, IReadOnlyList<string> Limitations, DeepBenchFailureKind FailureKind, string? Error)`
- `sealed record DeepBenchMeasurementStatistics(int Count, int InvalidSampleCount, double Mean, double Median, double P95, double P99, double Max, double CvPercent, int OutlierCount, DeepBenchConfidence Confidence, string SummaryText)`
- `public static DeepBenchMeasurementStatistics FromSamples(IEnumerable<double>? samples)`

**Steps:**
- [ ] 新增失敗測試：空樣本/非有限值、單一樣本不可信、nearest-rank 百分位與 IQR 離群值、緊密樣本高可信、模型 JSON 往返保留原始樣本與軸。
- [ ] 執行 `dotnet test Tests\XinSpect.Tests.csproj --filter FullyQualifiedName~DeepBenchMeasurementTests`，期望編譯失敗。
- [ ] 實作模型與統計：百分位用最近排名；至少五個有限樣本才用 1.5×IQR 標離群；CV `<2% High`、`<8% Medium`、否則 `Low`；單樣本與零平均為 `Insufficient`；非有限值只計入 invalid，不可改成 0。
- [ ] 重跑聚焦測試，期望 PASS。
- [ ] Commit：`git add Models/DeepBench/DeepBenchModels.cs Services/DeepBench/DeepBenchMeasurementStatistics.cs Tests/DeepBenchMeasurementTests.cs`；`git commit -m "新增 Deep Bench 共用量測模型"`。

---

### Task 2: 原 38 項全量 Catalog 與 Suite Planner

**Files:**
- Create: `Services/DeepBench/DeepBenchCatalog.cs`
- Create: `Services/DeepBench/DeepBenchSuitePlanner.cs`
- Test: `Tests/DeepBenchCatalogTests.cs`

**Interfaces:**
- `enum DeepBenchResourceClass { ReadOnly, CpuLoad, MemoryLoad, GpuLoad, DiskWrite }`
- `enum DeepBenchParallelSafety { ParallelSafe, Exclusive }`
- `sealed record DeepBenchCatalogEntry(int MatrixNumber, string Id, string Title, DeepBenchDomain Domain, int Phase, DeepBenchTestStatus Status, string StatusText, DeepBenchResourceClass ResourceClass, DeepBenchParallelSafety ParallelSafety, bool Runnable, string Requirement, string QuickEstimateText)`
- `static class DeepBenchCatalog { IReadOnlyList<DeepBenchCatalogEntry> All { get; } }`
- `sealed record DeepBenchPlan(IReadOnlyList<string> SelectedIds, IReadOnlyList<DeepBenchSkippedEntry> Skipped)`
- `sealed record DeepBenchSkippedEntry(string TestId, string Reason)`
- `static class DeepBenchSuitePlanner { static DeepBenchPlan Plan(DeepBenchRunProfile profile, IReadOnlyList<DeepBenchCatalogEntry>? catalog = null); }`

**Exact Phase 1 IDs:** `cpu.aes-sha`, `topology.core-latency`, `memory.cache-latency`, `memory.stream-bandwidth`, `memory.loaded-latency`, `gpu.fp32-fp64-integer`, `storage.qd-ladder`, `storage.mixed-rw`.

**Steps:**
- [ ] 新增失敗測試：`All.Count==38`、矩陣編號連續 1–38、ID 唯一、每項狀態/資源/平行安全齊全、Quick 只選上述八項、Phase 2–5 每項出現在 skipped 且理由含 Phase 或尚未實作。
- [ ] 執行聚焦測試，期望編譯失敗。
- [ ] 依 spec 1.1 登記 38 項。Phase 1：#1 Implemented；#6/#12/#13/#14 Integrated；#17/#23/#24 Implemented；#34 Implemented 且 `Runnable=false`（內建於每個結果）；其餘 Deferred。`DiskWrite` 與高負載皆 `Exclusive`。
- [ ] Planner：Quick/Full 都選八個 Phase 1 runnable IDs；Full 只改樣本數/階梯深度；選序 CPU→拓撲→記憶體→GPU→儲存。
- [ ] 重跑聚焦測試，期望 PASS。
- [ ] Commit：`git add Services/DeepBench/DeepBenchCatalog.cs Services/DeepBench/DeepBenchSuitePlanner.cs Tests/DeepBenchCatalogTests.cs`；`git commit -m "登記 Deep Bench 38 項全量目錄"`。

---

### Task 3: Run Session Orchestrator、歷史與同場聚合

**Files:**
- Create: `Services/DeepBench/Orchestration/IDeepBenchTest.cs`
- Create: `Services/DeepBench/Orchestration/DeepBenchOrchestrator.cs`
- Create: `Services/DeepBench/Orchestration/DeepBenchRunStore.cs`
- Create: `Services/DeepBench/Orchestration/DeepBenchCrossDomainSynthesis.cs`
- Test: `Tests/DeepBenchOrchestratorTests.cs`

**Interfaces:**
- `sealed record DeepBenchRunContext(Guid SessionId, DeepBenchRunProfile Profile, IProgress<DeepBenchProgress> Progress)`
- `sealed record DeepBenchProgress(string CurrentTestId, int CompletedCount, int TotalCount, double Fraction, string Phase)`
- `interface IDeepBenchTest { string Id { get; } Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken); }`
- `enum DeepBenchRunState { Running, Completed, CompletedWithFailures, Cancelled }`
- `sealed record DeepBenchRunRecord(Guid SessionId, DeepBenchRunProfile Profile, DateTime StartedUtc, DateTime EndedUtc, DeepBenchRunState State, IReadOnlyList<DeepBenchTestResult> Results, IReadOnlyList<DeepBenchInsight> Insights)`
- `sealed record DeepBenchInsight(string Title, string Text, IReadOnlyList<string> EvidenceTestIds)`
- `DeepBenchOrchestrator.RunAsync(DeepBenchRunProfile profile, IReadOnlyList<string> requestedIds, IProgress<DeepBenchProgress>? progress, CancellationToken cancellationToken)`
- `DeepBenchRunStore.Save(DeepBenchRunRecord record)` / `LoadRecent(int maximum = 20)`
- `DeepBenchCrossDomainSynthesis.Summarize(DeepBenchRunRecord record)`

**Steps:**
- [ ] 新增失敗測試：重複/未知 ID 啟動前拒絕且無副作用；單項例外變 `CompletedWithFailures` 且下一項續跑；取消保存已完成結果；進度單調至 1；歷史 JSON 往返且損毀回空；聚合只引用同 session IDs、缺 GPU 明示缺域、不生成總分。
- [ ] 執行聚焦測試，期望編譯失敗。
- [ ] Orchestrator 每場生成一個 Guid；副作用前驗證 IDs；Phase 1 全部序列化執行；每項例外轉結果、取消回 `Cancelled` 不丟給 UI；取消/結束皆保存；每項前後報進度。
- [ ] Store 預設 `%APPDATA%\XinSpect\deepbench-history.json`，測試注入 temp folder，使用 `AtomicWrite.AllText`；損毀檔回空並報告，不虛造舊資料。
- [ ] Synthesis：CPU/記憶體只並列實測；儲存量 QD1→最佳 scaling；缺域列缺 IDs；嚴禁加權分數或排名。
- [ ] 重跑聚焦測試，期望 PASS。
- [ ] Commit：`git add Services/DeepBench/Orchestration Tests/DeepBenchOrchestratorTests.cs`；`git commit -m "新增 Deep Bench 單場排程與歷史證據模型"`。

---

### Task 4: 既有 Cache / STREAM / Loaded latency / Core latency Adapter

**Files:**
- Modify: `Services/CacheBenchService.cs`, `Services/MemBandwidthService.cs`, `Services/CoreLatencyService.cs`
- Create: `Services/DeepBench/Adapters/CacheLatencyAdapter.cs`, `StreamBandwidthAdapter.cs`, `LoadedLatencyAdapter.cs`, `CoreLatencyAdapter.cs`
- Test: `Tests/DeepBenchLegacyAdapterTests.cs`

**Interfaces:**
- Legacy services expose `public Task RunAsync()` without changing existing public results.
- Adapter cancellation: `using CancellationTokenRegistration reg = cancellationToken.Register(() => service.Cancel()); await service.RunAsync();`
- `MemBandwidthService` result is shared: whichever of STREAM/loaded-latency runs first invokes the shared service once; the second reuses the snapshot. Loaded-latency alone may run the shared service if no snapshot exists.
- Adapters never mutate legacy history; they convert the current snapshot into `DeepBenchTestResult` with raw samples/points where available.

**Steps:**
- [ ] Add failing adapter tests: cancellation calls legacy `Cancel()`; cache, STREAM, loaded-latency and core-latency snapshots map test IDs, metrics, units, configurations, limitations and failure state; shared memory service is invoked once for both tests; empty snapshots become `NotRun`/`Insufficient`, never fabricated values.
- [ ] Run focused tests and expect compile failure.
- [ ] Change only the private `RunAsync()` methods to public and add adapters.
- [ ] Preserve result ownership, cancellation and error text from legacy services; no second history service is created.
- [ ] Rerun focused tests and expect PASS.
- [ ] Commit: `git add Services/CacheBenchService.cs Services/MemBandwidthService.cs Services/CoreLatencyService.cs Services/DeepBench/Adapters Tests/DeepBenchLegacyAdapterTests.cs`; `git commit -m "接入既有深測到 Deep Bench"`.

---

### Task 5: AES-256 / SHA-256 CPU 深測

**Files:**
- Create: `Services/DeepBench/CryptoMicrobenchService.cs`
- Test: `Tests/DeepBenchCryptoTests.cs`

**Interfaces:**
- AES-256-CBC with PKCS7, round-trip verification before timing.
- SHA-256 managed by BCL.
- Quick: AES 2 x 2 MiB, SHA 2 x 4 MiB. Full: AES 5 x 16 MiB, SHA 5 x 32 MiB.
- Metric IDs: `cpu.aes.cbc.throughput`, `cpu.sha256.throughput`, unit MiB/s, higher is better.
- Preserve every finite sample; non-finite values are counted invalid rather than replaced by zero.

**Steps:**
- [ ] Add failing tests: metric IDs and units, sample counts, round-trip integrity, cancellation before work, non-finite rejection, and limitation text stating `.NET crypto API measurement` without claiming AES-NI or cryptographic certification.
- [ ] Run focused tests and expect compile failure.
- [ ] Implement allocation-stable buffers, warm-up, timed rounds, `Stopwatch.GetTimestamp`, statistics, and cancellation checks between rounds/blocks.
- [ ] Rerun focused tests and expect PASS.
- [ ] Commit: `git add Services/DeepBench/CryptoMicrobenchService.cs Tests/DeepBenchCryptoTests.cs`; `git commit -m "新增 CPU 加解密與雜湊深測"`.

---

### Task 6: 磁碟 QD ladder 與混合讀寫

**Files:**
- Create: `Services/DeepBench/DiskIoMatrixService.cs`
- Test: `Tests/DeepBenchDiskIoTests.cs`

**Interfaces:**
- Temporary file name only: `XinSpect.deepbench.tmp` under the user-selected storage root.
- `File.OpenHandle` with `FILE_FLAG_NO_BUFFERING`; aligned allocations via `NativeMemory.AlignedAlloc`.
- Full read ladder: blocks 4K/128K, QD 1/2/4/8/16/32/64.
- Quick read ladder: 4K, QD 1/4/16.
- Mixed test: 4K random, QD16, read percentages 100/70/50/30/0.
- At least 8 GB free space must remain after the temporary budget; budget and path are surfaced before start.

**Steps:**
- [ ] Add failing tests with a small injected file abstraction/temp root: space guard rejects unsafe roots; successful run creates only the expected temp name and deletes it in `finally`; exception and cancellation also delete it; read/mixed metric IDs and axes exist; non-finite throughput is rejected.
- [ ] Run focused tests and expect compile failure.
- [ ] Implement test-data generation, unbuffered aligned reads/writes, concurrent `RandomAccess.ReadAsync`/`WriteAsync`, random offsets, per-point samples, and explicit flush/close before deletion.
- [ ] Report IOPS, MiB/s, median/p95/p99 latency for every point; label OS cache/driver behavior limitations honestly.
- [ ] Rerun focused tests and expect PASS.
- [ ] Commit: `git add Services/DeepBench/DiskIoMatrixService.cs Tests/DeepBenchDiskIoTests.cs`; `git commit -m "新增磁碟佇列與混合讀寫深測"`.

---

### Task 7: D3D11 FP32 compute

**Files:**
- Create: `Services/DeepBench/Interop/D3D11Native.cs`
- Create: `Services/DeepBench/GpuFp32ComputeService.cs`
- Test: `Tests/DeepBenchGpuComputeTests.cs`

**Interfaces:**
- No new NuGet package. Reuse the COM/PInvoke style from `Services/GpuCodecService.cs`.
- Hardware adapter only; reject WARP before device creation.
- Feature Level 11_0+, HLSL `cs_5_0`, 64 threads/group, 32x1x1 dispatch, 4096 dependent FP32 FMAs per thread.
- UAV `RWStructuredBuffer<float>`, readback checksum, reject all-zero output.
- Quick 2 samples, Full 5 samples.

**Steps:**
- [ ] Add failing tests: shader source compiles as `cs_5_0`; sample count/config/metric IDs; all-zero checksum is `Unstable`; driver removal is `DriverRejected`; WARP is never selected; limitation text excludes driver-internal timing and does not call WARP a hardware GPU result.
- [ ] Run focused tests and expect compile failure.
- [ ] Implement native structs, COM wrappers, shader compilation, device/context/UAV/readback lifetime, cancellation, and `ObjectDisposedException`-safe cleanup.
- [ ] Rerun focused tests and expect PASS (native GPU execution tests may inject fake timing/readback where CI cannot create D3D).
- [ ] Commit: `git add Services/DeepBench/Interop Services/DeepBench/GpuFp32ComputeService.cs Tests/DeepBenchGpuComputeTests.cs`; `git commit -m "新增 GPU FP32 compute 深測"`.

---

### Task 8: Deep Bench ViewModel、獨立頁面與 PageRegistry

**Files:**
- Create: `ViewModels/DeepBenchViewModel.cs`
- Create: `Views/DeepBenchView.xaml`, `Views/DeepBenchView.xaml.cs`
- Modify: `ViewModels/MainViewModel.cs`, `Nav/PageRegistry.cs`
- Test: `Tests/DeepBenchViewModelTests.cs`, `Tests/DeepBenchHonestyTests.cs`

**Interfaces:**
- `MainViewModel.DeepBench` is a single shared `DeepBenchViewModel`.
- Page key `deepbench`, title `深測中心`, group `監控`, placed immediately after `bench`, `Advanced = true`.
- ViewModel shares the existing Cache/MemBandwidth/core-latency service instances and does not create a second copy/history.
- UI states: Quick/Full, storage root, temp-file budget, Start/Cancel, progress, all 38 catalog rows, result cards, insights, history, warnings.

**Steps:**
- [ ] Add failing tests: page registration/order; ViewModel default catalog has 38 unique rows and deferred rows remain visible; Quick/Full plan has exactly the eight Phase 1 runnable IDs; start without storage root or free-space guard blocks disk tests with actionable text; cancellation changes state and preserves completed results; result cards include samples/confidence/limitations/errors; history is local-only.
- [ ] Run focused tests and expect compile failure.
- [ ] Implement ViewModel state machine, command guards, progress marshalling, cancellation, run record construction, and historical summaries without weighted totals.
- [ ] Implement an independent WPF page with catalog, result, insight and history sections; keep high-load/temp-file warnings visible.
- [ ] Rerun focused and all PageRegistry UI tests; expect PASS.
- [ ] Commit: `git add ViewModels/DeepBenchViewModel.cs Views/DeepBenchView.xaml Views/DeepBenchView.xaml.cs ViewModels/MainViewModel.cs Nav/PageRegistry.cs Tests/DeepBenchViewModelTests.cs Tests/DeepBenchHonestyTests.cs`; `git commit -m "新增 Deep Bench 深測中心頁"`.

---

### Task 9: 歷史、報告、Help 與 Changelog 整合

**Files:**
- Modify: `Services/ReportService.cs`, `Nav/HelpCatalog.cs`, `Nav/ChangelogCatalog.cs`
- Test: `Tests/DeepBenchIntegrationTests.cs`

**Steps:**
- [ ] Add failing tests: report contains `Deep Bench 深測`, session ID, state, profile, completion, metric/configuration/confidence/error, insights and limitations; corrupt history is reported instead of silently treated as healthy; Help documents temp file, deletion, high load, cancellation, local-only history and no upload; Changelog folds Deep Bench into existing Everest v2.1 entry without bumping version.
- [ ] Run focused tests and expect compile failure.
- [ ] Integrate the current run and recent history into ReportService using the shared model, not duplicated strings.
- [ ] Add user-facing Help and v2.1 changelog bullets for the hub, eight Phase 1 measurements, 38-item coverage and honest deferred status.
- [ ] Rerun focused tests and expect PASS.
- [ ] Commit: `git add Services/ReportService.cs Nav/HelpCatalog.cs Nav/ChangelogCatalog.cs Tests/DeepBenchIntegrationTests.cs`; `git commit -m "整合 Deep Bench 歷史與報告"`.

---

### Task 10: 全量驗證與收尾

**Steps:**
- [ ] `dotnet test Tests\XinSpect.Tests.csproj --filter "FullyQualifiedName~DeepBench"`
- [ ] `dotnet test Tests\XinSpect.Tests.csproj`
- [ ] `dotnet build XinSpect.csproj -c Release`
- [ ] `git status --short`
- [ ] Confirm Deep Bench focused tests PASS, full suite has `Failed: 0` and more than 2195 passed, Release build has 0 errors, and only task files are committed.
- [ ] Update every plan checkbox after actual verification and commit the plan: `git add docs/superpowers/plans/2026-10-01-xinspect-deep-bench-20-phase-1.md`; `git commit -m "完成 Deep Bench Phase 1 實作紀錄"`.

---

## Self-Review Checklist

- [ ] 38 catalog entries are present exactly once, with no silent deletion and readable status/reason.
- [ ] Quick/Full execution set and phase behavior match the catalog; deferred items cannot be accidentally selected.
- [ ] Every result carries configuration, samples or points, confidence, timing, limitations and failure classification.
- [ ] Empty, cancelled, unsupported, driver-failed, corrupt-history and resource-release paths are tested.
- [ ] No weighted total score, fake percentile rank, WARP-as-hardware, MemTest86 claim, guaranteed AES-NI claim, or cloud upload is introduced.
- [ ] Legacy services remain compatible and share one instance/history.
- [ ] UI/report/help expose methodology and limitations rather than hiding them.
- [ ] Full test baseline increases beyond 2195 passed and remains at zero failures.
