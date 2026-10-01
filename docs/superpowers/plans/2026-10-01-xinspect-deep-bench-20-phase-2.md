# XinSpect Deep Bench 20 — Phase 2 執行計畫

Spec：`docs/superpowers/specs/2026-10-01-xinspect-deep-bench-20-design.md`

## 目標

把 Phase 2 拆成可單獨驗證的垂直切片；第一批先做既有 Top-down 服務接入。
每個切片必須同時更新 Catalog、Planner、Orchestrator、Help/Changelog（需要時）、
測試、報告能力與 Release build；不得改版號、不得引入加權總分。

## Slice 1 — Top-down Adapter

### Task 1：Catalog 與 Planner 紅燈

1. 更新 `DeepBenchCatalogTests`：  
   - `cpu.top-down` 必須是 `Integrated` 且 `Runnable=true`；  
   - Quick/Full 執行集變成 9 項，包含 `cpu.top-down`；  
   - Skipped 30 項變 29 項。
2. 執行 Deep Bench focused tests，確認新斷言失敗。

### Task 2：TopDownService 可等待取樣

1. 新增 adapter 測試：  
   - fake 取樣結果必須逐核心保留四桶樣本；  
   - aggregate/核心證據都保留，不伪造缺失資料；  
   - 不支援與未產生資料分別回 `Unsupported` / `NotRun`；  
   - 已取消回 `Cancelled`。  
2. 為 `TopDownService` 增加可被 adapter 等待的 public sampling API；  
   保留既有 `Start()` 行為給效能頁使用。
3. 實作 `TopDownAdapter`。

### Task 3：Hub 與共用服務整合

1. `MainViewModel` 必須把既有 `TopDown` 交給 `DeepBenchViewModel`，  
   測試證明是同一實例。
2. Hub orchestrator 註冊 `TopDownAdapter`。
3. Scope 文案從八項改九項並明示 Intel/PMU 限制；  
   Help 補 Top-down 已接入 Hub；Changelog 折疊進 2.1.0。
4. Focused Deep Bench tests → full suite → Release build。

## 後續 Phase 2 切片

- CPU：Load-to-use / ILP / branch / RDRAND-RDSEED。  
- Topology：core bandwidth、SMT、false sharing、lock scaling。  
- Memory：TLB / NUMA / large page、ECC/WHEA 壓力關聯。  
- GPU：FP64/integer、VRAM、PCIe、dispatch jitter。  
- Storage：SLC adapter、write integrity、IOCP、flush durability。

## 驗證命令

```powershell
dotnet test Tests\XinSpect.Tests.csproj --filter "FullyQualifiedName~DeepBench"
dotnet test Tests\XinSpect.Tests.csproj
dotnet build XinSpect.csproj -c Release
git status --short
```
