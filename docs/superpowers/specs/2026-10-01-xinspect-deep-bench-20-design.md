# XinSpect Deep Bench 20 設計規格

日期：2026-10-01
範圍：將 XinSpect 從「硬體資訊與多項基準」強化為「全方位硬體深測平台」，用實測量測能力邊界、瓶頸路徑與穩定度，而不是增加無法解釋的總分。

## 1. 目標與定位

Deep Bench 20 的目標是補齊七個互相支援的量測層：

1. CPU 微架構：指令吞吐、依賴鏈、分支、推測執行與硬體亂數。
2. 拓撲與併發：核心間延遲、頻寬、SMT/hybrid 衝突、cache line 與鎖競爭。
3. 記憶體與 RAS：TLB/NUMA、DRAM 行/rank 推論、ECC/WHEA 壓力關聯。
4. GPU：compute、VRAM、PCIe、fillrate/raster/texture 與 codec 實測。
5. 儲存與 I/O：QD 階梯、混合讀寫、IOCP、flush/durability、I/O→GPU 管線。
6. 系統強度：多域 Gauntlet、boost ramp/recovery、吞吐衰退與事件關聯。
7. 量測可信度：重複樣本、離群值、變異係數、量測條件與可信度標示。

核心價值是「別人顯示規格，XinSpect 量能力邊界」。每張卡片必須能回答：量什麼、怎麼量、瓶頸在哪、不可信的條件是什麼。

產品形態是「聚眾合一」的 Deep Bench Hub：功能可以堆料，但不能散落成一堆獨立按鈕。每個新測項必須註冊到統一測項目錄、使用共用量測模型、進入同一次 run session、能參與跨域瓶頸歸因，並在總覽、歷史與報告中出現。做不到這五點的功能先不進 Deep Bench 20。

## 2. 非目標

- 不做偽造或加權玄學總分。
- 不猜無法從作業系統或硬體 API 取得的值。
- 不捆綁第三方測試執行檔；不因新增測試引入必裝外部工具。
- 不修改使用者的 BIOS、驅動、超頻、檔案或分割區。
- 不把使用者模式壓力測試說成 MemTest86 或斷電級耐用性測試。
- 不做雲端百分位排名；此功能涉及隱私與防刷，必須另行設計。
- 不將 Windows ML 指派到 CPU 的結果標示為 NPU 實測。

## 3. 架構原則

### 3.1 共用量測模型

新增共通結果類型，所有深測服務回傳：

- 原始值與單位（GB/s、GFLOPS、IOPS、ns、µs、ms）。
- 樣本數、median、mean、p95、p99、max。
- 變異係數與離群值策略。
- 量測開始/結束時間、執行緒/佇列/core 組態。
- 量測條件：電源狀態、背景負載提示、時脈/溫度若已有可靠服務可讀。
- 限制文字：明確指出使用者模式、虛擬位址、驅動排程或其他不可忽略的限制。
- 錯誤分類：不支援、被驅動拒絕、權限不足、量測不穩定、取消。

不得只回傳單一數字。UI 與報告必須保留原始指標與可信度。

### 3.2 模組分層

- `Services/DeepBench/`：純量測核心與數學推導，避免 UI 依賴。
- `Services/DeepBench/Orchestration/`：測項目錄、run session、排程、資源預算與跨域結果聚合。
- `Services/DeepBench/Interop/`：Windows API、D3D、IOCP、NUMA 等 P/Invoke。
- `ViewModels/DeepBench/`：狀態機、進度、取消與 UI 聚合。
- `Views/DeepBenchView.xaml`：分區卡片，不在現有頁面無限堆疊。
- `Tests/DeepBench*.cs`：數學、格式化、狀態機、模型與安全行為測試。

每個功能都分為純邏輯、平台呼叫、UI 狀態三層。平台呼叫失敗時必須回傳可顯示原因，不得吞掉後假裝成功。

### 3.3 資料流

1. 使用者選擇測項與安全限制（例如磁碟暫存檔大小）。
2. ViewModel 建立 CancellationToken 與進度報告器。
3. Service 在背景執行 warm-up、量測、重複輪次與摘要。
4. 純數學層計算分佈、矩陣與瓶頸歸因。
5. ViewModel 更新卡片、熱圖或曲線。
6. `BenchLog` / History 保存可序列化結果。
7. Report 輸出包含方法論與限制文字。

### 3.4 聚眾合一 Orchestrator

- Test Catalog：每個測項宣告 id、領域、預估時間、資源需求、風險等級、並行安全性與需要的硬體能力。
- Run Session：一次深測產生唯一 session id，保存機器快照、測項版本、取消點、原始結果與可信度，不讓各卡片自建孤立歷史。
- Suite Planner：提供「單項、領域套餐、全機深測」三種入口；依資源與安全規則排程，磁碟寫入與高負載測試預設序列化，低風險偵測可並行。
- Cross-domain Synthesis：只使用同 session 或明確標示相容的歷史結果，推論 CPU↔記憶體、GPU↔PCIe、磁碟↔CPU↔GPU 等瓶頸路徑；不可把缺少的環節補成猜測。
- Unified Completion：總覽顯示完成度、失敗域、取消原因與尚缺測項；報告與歷史匯出以 session 為單位，而非以零散卡片為單位。

## 4. 功能設計

### 4.1 CPU 微架構

- AES-256、SHA-256、AVX2/AVX-512 FMA、整數依賴鏈、壓縮/解壓縮吞吐。
- Load-to-use、ILP 1/2/4/8、branch mispredict penalty、store-to-load forwarding、denormal penalty。
- 分支可預測/隨機/間歇模式、間接呼叫與 return 預測。
- RDRAND/RDSEED 吞吐、延遲、retry 與基本 sanity check；不宣稱密碼學認證。
- 單核/全核、P-core/E-core 分組，輸出原始吞吐與分佈。

### 4.2 拓撲與併發

- Core-to-core latency 與 bandwidth 矩陣，使用共享記憶體 ping-pong與受控搬運核心。
- 推導同 L3、跨 CCD/CCX、NUMA local/remote 分組；無法判定時顯示「無法判定」。
- SMT sibling 干擾與 P/E core 差異。
- False sharing、cache line 驗證、atomic/CAS scaling、spinlock/mutex/reader-writer 並行度。
- 背景負載矩陣，輸出建議 affinity mask，但不自動改系統排程。

### 4.3 記憶體與 RAS

- TLB 壓力曲線：4KB stride、2MB 對齊區域、context switch 後暖機。
- NUMA local/remote 與 first-touch 差異；作業系統有回報才顯示節點。
- DRAM stride/latency peak 推論 row/rank/bank 相對行為；明示不是 JEDEC 實體位址映射。
- ECC/WHEA/RAS 壓力：壓力期間收集 corrected/uncorrected error、每量測量錯誤率、首次錯誤時間；不注入故障。
- 與現有 SPD、記憶體頻寬、負載延遲與記憶體圖樣測試交叉對照。

### 4.4 GPU 深測

- D3D11 compute shader FP32 FMA、FP64（可用時）、integer、occupancy 與 dispatch jitter。
- VRAM read/write/copy/reduce 頻寬與延遲。
- PCIe CPU↔GPU 上傳、下載與雙向頻寬；與 negotiated link 對照。
- Pixel/texel fillrate、ROP、geometry、tessellation、blend、overdraw、texture cache。
- H.264/HEVC/AV1/VP9 硬體編解碼吞吐與延遲；以程式生成測試幀，不捆綁影片。
- 每張卡列 adapter、feature level、driver 模式與錯誤；不混用不同卡的結果。

### 4.5 儲存與 I/O

- 4K/128K block × QD1/2/4/8/16/32/64 矩陣，保持 `FILE_FLAG_NO_BUFFERING` 與 sector alignment。
- 100R/90R/70R/50R/30R/100W 混合掃描，保留讀寫分佈。
- IOCP completion throughput、worker scaling、completion latency 與 thread pool 抖動。
- Flush/fsync/WriteThrough/metadata/rename durability 成本；只使用暫存檔，不模擬斷電。
- I/O→GPU 管線：disk read、CPU upload、GPU kernel、readback 分段計時與瓶頸判定。
- 寫入完整性 pattern 驗證，錯誤只報告位置與摘要，不修復使用者資料。

### 4.6 系統強度 Gauntlet

- 模式：快速 5 分鐘、標準 15 分鐘、長跑 30 分鐘、地獄 60 分鐘。
- 同時或分階段施加 CPU、記憶體、GPU、磁碟、DPC/事件監看。
- 判定：PASS、PASS with degradation、FAIL、ABORTED。
- 衰退指標：吞吐衰減、p99 延遲、驅動重置、WHEA、錯誤資料、時脈/溫度可靠資料。
- 失敗必須歸因到具體域，不把整機壓縮成單一分數。

### 4.7 可信度引擎

- 嚴謹模式可選 5/10/20 次重複。
- Warm-up、樣本保留/捨棄規則、median、mean、p95、p99、max、CV、outlier count。
- 變異過大時不給比較結論，只提示背景干擾或量測不穩。
- 保存電源模式、可用記憶體、背景活動、降頻提示與 API 版本。
- 所有基準結果可進入同一歷史趨勢，前後比較必須顯示條件差異。

## 5. UI 設計

Deep Bench 使用獨立頁面，分為：

1. 總覽：Deep Bench Hub、全機深測入口、run session 進度、跨域瓶頸圖、可信度與最近結果。
2. CPU/微架構。
3. 拓撲/併發矩陣。
4. 記憶體/RAS。
5. GPU 深測。
6. 儲存/I/O。
7. Gauntlet。

矩陣以熱圖呈現；曲線保留原始資料點；每張卡說明方法論與限制。長時間測試必須有取消、進度、預估空間/時間與安全提示。

## 6. 安全與隱私

- 磁碟測試只使用明確命名的暫存檔，結束後刪除；寫入量受使用者選擇與剩餘空間保護。
- 不觸碰使用者既有檔案、分割區表、韌體或開機設定。
- 不預設上傳任何結果。
- WHEA/事件讀取以現有權限為限；權限不足時誠實顯示。
- 所有 P/Invoke 都要有失敗路徑與資源釋放測試。
- 不以管理員權限作為功能必要條件。

## 7. 實作分期

全方面強化必須分期，避免單一提交無法驗證：

- Phase 1：Test Catalog/run session/可信度模型、AES/SHA、QD階梯與混合讀寫、D3D11 FP32 compute、核心間延遲矩陣，以及第一版跨域結果聚合。
- Phase 2：TLB/NUMA、false sharing/lock scaling、VRAM/PCIe、IOCP、RAS整合。
- Phase 3：GPU fillrate、codec 實測、DRAM stride 推論、I/O→GPU pipeline。
- Phase 4：Gauntlet、可信度嚴謹模式、歷史趨勢與報告整合。
- Phase 5：效能調整、極端機器相容性、無障礙與多語言。

每一期都必須保持測試只增不減，並單獨提交、單獨驗證。

## 8. 測試與驗收

- 2195 個既有測試不得減少或失敗。
- 每個純數學層需涵蓋空樣本、單一樣本、離群值、取消與格式化。
- 每個平台服務需有不支援、API失敗、資源釋放與不丟例外的測試。
- 磁碟與長跑測試提供小樣本/快速模式，避免 CI 預設毀盤或拖慢。
- UI 測試覆蓋卡片存在、狀態切換、取消、錯誤訊息、session 完成度與不實承諾掃描。
- Orchestrator 測試覆蓋測項註冊、並行安全排程、資源預算、取消恢復、部分失敗與跨域結果只引用同 session/相容歷史。
- 驗收標準：使用者能從任一結果看到原始指標、分佈、條件、限制與瓶頸歸因。

## 9. 主要風險

| 風險 | 處理 |
|---|---|
| 功能堆料導致頁面失控 | 獨立 Deep Bench 頁與分期導入 |
| Windows API 差異 | feature detection、明確錯誤、單元與真機煙霧測試 |
| 長時間測試影響使用者 | 明示時間/空間/負載，提供取消與快速模式 |
| GPU driver 行為差異 | adapter/driver 條件保存，拒絕跨條件直接比較 |
| 使用者模式量測被誤讀 | 所有卡片保留限制文字與來源 |
| 測試時間膨脹 | 純邏輯單元測試為主，平台量測用可跳過的煙霧測試 |

## 10. 成功標準

Deep Bench 20 完成時，XinSpect 能對同一台機器回答：

- CPU 的指令、延遲、分支與拓撲強度。
- 記憶體與快取的頻寬、延遲、NUMA/TLB 與 RAS 行為。
- GPU 的 compute、VRAM、PCIe、圖形管線與 codec 實測。
- 儲存的 QD、混合、持久化、完整性與系統呼叫成本。
- 系統在長時間壓力下的衰退與失敗域。
- 每個結果的可信度與不可比較條件。
- 一次全機深測如何構成完整 run session，而不是一堆互相不知道彼此存在的分數。

最終定位：Windows 上少見的誠實硬體深測平台，而不是另一個彩色跑分工具。
