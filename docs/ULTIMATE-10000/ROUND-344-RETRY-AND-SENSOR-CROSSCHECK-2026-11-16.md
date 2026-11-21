# Round 344：十次重試、感測差異比較與工程憲法

日期：2026-11-16。需求：使用者要求自動重試 10 次，持續推進 10000 計劃，並為後續接手者成文航太／企業級／極客深度、真實、安全、來源與定義要求。候選：U10-TS-0191，維持進行中。

## 變更與定義

- `AiRetryPolicy.MaxAttempts` 為 11，即首次請求加最多 10 次重試；保留暫時性 HTTP 狀態碼、20 秒最大單次退避、取消與已輸出串流後不重試的原規則。端點 `Retry-After` 超出等待上限時可提早放棄；這不是所有錯誤都保證重試 10 次。
- `SensorCrossCheckService` 比較 LHM 與 Core Temp 的逐核心絕對溫度，檢查非空 CPU 型號名稱、唯一正規化核心序號、有限有效數值與 3 秒讀取時間差；華氏先轉攝氏，TjMax 差值不參與。
- LHM 0.9.6 `GenericCpu.CoreString` 將一般核心名稱從 1 起算，Core Temp 讀取陣列索引從 0 起算；先把完整 `CPU Core #N` 名稱轉為 N-1，不直接比較兩端原始數字。非完整名稱、混合核心、多 CPU 或來源歧義不配對。
- `SensorService` 不讓 `Distance to TjMax` 覆蓋同名核心絕對溫度；新比較入口限單顆非混合核心 Intel。Core Temp 回報多 CPU 時不提供可比較的裝置與核心欄位。
- 官方 Core Temp SDK 複核發現既有 `uiCoreCnt` 被誤當全機核心數；已改成每 CPU 核心數乘 CPU 數，先以 64 位乘法驗證不超過 256 筆。`uiTjMax[128]` 按 CPU 索引，每 CPU 一筆；多插槽讀數標 CPU 與該 CPU 的核心索引。CPU 數超過 128、乘積超過陣列容量或短快照一律拒讀，不回部分成功。
- 保留 Core Temp 讀取器自己的快照時間，不在合併所有來源後改寫成較新的時間。時間戳只代表本程式取得資料的時間，來源工具最後更新時間未知。
- 桌面感測器頁列候選差值並明示來源更新時間與核心對應未經實機驗證。相同 CPU 型號和序號不證明同一實體感測器或獨立性；目前 CLI 尚無此差異比較入口，不能宣稱桌面／CLI 已全部對等。
- 新增 `docs/ENGINEERING-CONSTITUTION.md` 及根目錄 `AGENTS.md` 必讀入口，更新 `docs/AGENT_CONTEXT.md` 的過期統計說法。三語 README、百科統計及生成附錄同步；不新增事實鍵或候選完成數。

## 原始來源與現有契約

- 本機 `XinSpect.csproj`：感測器套件鎖定 `LibreHardwareMonitorLib 0.9.6`。
- LHM v0.9.6 `GenericCpu.cs`，`CoreString`／`ThreadString`：`https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/GenericCpu.cs`。
- LHM v0.9.6 `IntelCpu.cs`，`_coreTemperatures` 與 `_distToTjMax`：`https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs`。
- Core Temp 索引是本版 `CoreTempSharedMem` 陣列遍歷的索引；尚缺官方 SDK 實機樣本證明與 LHM 的實體核心一一對應，不以程式自己的索引代替硬體證據。
- 2026-11-16 從官方 `https://www.alcpu.com/CoreTemp/developers.html` 取得 `https://www.alcpu.com/CoreTemp/main_data/CoreTempSDK.zip`；SHA-256 `DD7893A8051551947224822BF6060DD6160FA03C6D93FCE0ABB46DE46B2D37F2`，存於 `obj/audit/r344/CoreTempSDK.zip`。未執行包內二進位。
- SDK 原始欄位定義：`Shared Memory Framework for Native/Shared Memory Libraries/GetCoreTempInfo.h`；索引範例：`.Net/Code Samples/GetCoreTempInfoNET Demo/GetCoreTempInfoNET Demo/Program.cs`，`index = g + i * GetCoreCount`；`Plugin Framework for .Net/Core Temp Plugin Libraries/v4.0/CoreTempPluginProxy.XML` 明載 `uiCoreCnt` 為每處理器核心數、`uiTjMax` 依處理器排列。這些證據支持佈局與索引定義，不支持跨工具物理核心映射或實際精度。
- 既有規則：`docs/spec/METHODOLOGY.md`、`docs/MEASUREMENT-METHODOLOGY.md`、`docs/ENTERPRISE-BASELINE-PACKAGES.md` 與使用者本輪指示。

## 驗證狀態

- 第一輪定向 AI 政策／感測配對／磁碟回歸 65/65 通過；輸出 `obj/_verify_r344_target/`。此結果早於本輪核心序號與快照時間修補，不能代替修補後驗證。
- 修補後定向／11 次請求整合／文件守門第一輪：134 通過、2 項統計守門失敗（總數 136）；守門指出 C# 檔 1,638、測試檔 665，已按實際數字修正。原始結果 `obj/audit/r344/r344-target.trx`，不得列為全綠。
- SDK 修正前全套：8,225 通過、1 失敗、0 略過，總數 8,226，15 分 24 秒；`obj/audit/r344/r344-full.trx`。唯一失敗是新增來源檔未追蹤，已納入本機 Git 索引；守門補驗 2/2 通過（`r344-tracked.trx`）。未 commit、push 或上傳。
- `gen-readme-domains.py --check` 與 `gen-docs-html.py --check` 回零。額外 `check-html.py` 仍報兩項既有清單標籤結構問題；舊主工作樹同樣含這兩類問題，另有一項舊錨點失效。該檢查即使有問題也回零，不能只看退出碼宣稱 HTML 潔淨；修補另行追蹤。
- SDK 修正前 CLI 建置成功，0 錯誤、13 個既有警告。SDK 修正後新增三項匿名共享記憶體快照回歸：雙 CPU 的四核心／兩 TjMax、單 CPU 華氏差值隔離、畸形拓撲與短快照拒讀；不建立或佔用 Core Temp 的正式共享記憶體名稱。
- SDK 修正後定向及文件／來源追蹤守門：70/70 通過、0 失敗／略過；`obj/audit/r344/r344-sdk-final-target.trx`。CLI 建置成功，0 錯誤、13 個既有警告。
- SDK 修正後最終全套：8,229/8,229 通過、0 失敗、0 略過，測試摘要 15 分 1 秒；`obj/audit/r344/r344-final-full.trx`。受測原始碼、統計文件、生成 HTML 及兩個 DLL 共 17 個檔案的 SHA-256 與執行前一致，漂移 0。基線 8,229 已由此次實際結果確認。
- 未觸發磁碟掃描負載、硬體寫入或外部 AI 請求；沒有 Core Temp 實機比較證據。TS-0185／0187／0191 均不因本輪建置或注入測試而標為硬體驗證完成。

## 下一批

官方 SDK 佈局已核；下一批取得代表性實機快照，核定單插槽、異質核心及多插槽的實體識別和來源更新時間，再設計 CLI 對等入口。證據不足時維持候選差異展示，不將數字接近稱為精度驗證。

另列既有 `Services/CoreTempMapService.cs` 的 `FallbackCoreTemp` 專項：目前先遍歷核心溫度、之後才取得 TjMax，差值模式可能整批略過；華氏差值換算及多插槽對應也須核定。這條備援消費路徑本輪未修補／專項驗證，不能因共享記憶體讀取器已修便宣稱全部 Core Temp 路徑完成。
