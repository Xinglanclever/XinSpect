# Round 347：企業離線資料與拓撲邊界

日期：2026-11-17（Asia/Singapore）。延續 Round 346 的本機工作樹與使用者七路並行授權。未改版號、未提交或發佈。依 [工程憲法](../ENGINEERING-CONSTITUTION.md) 記錄實作、失敗與缺口，不把修補數或測試數當作完成率。

## 修改範圍

| 候選／路徑 | 本輪修正 | 仍有限制 |
|---|---|---|
| MM-0059／WH-0003、WHEA | ValidBits 0–17 對已解析基本欄位分別生效；無效 ErrorType 不命名、提示或分類。Requester／Responder／Target ID 保留精確 ulong；含位址／ID 的列標敏感，位址不再轉 double。事件時間先轉 UTC；截斷摘要保留節區不可解的界線。 | 生產事件外層 CPER 尚未解析，實際 DIMM 身份未驗證。Extended 欄位及錯誤處置提示仍需逐規格深化，沒有新增真機事件 fixture。 |
| EN-0002／0015／0016／0021／0023、Fleet | 型號改取有來源的製造商／型號，保留每鍵單位、六態、信任與來源；缺資料或來源／單位不同拒比。同一匿名識別組只取最新觀測，拒絕時間／型號歧義；識別碼可能是內容指紋，明示不證明不同實體機器。有限數字與縮放統計避免溢位，小樣本按鍵排序。 | 來源名稱相同不證明硬體實體、工作負載或取樣條件相同；沒有校準／群體健康判決。匯入內容沒有新增簽章認證。 |
| Fleet 讀取／CLI | 單檔 4 MiB、合計 16 MiB、最多 1,000 個目錄候選、200 份快照；拒絕重複鍵及損壞輸入。診斷透過 FleetReadResult 到共用事實與 CLI diagnostics；部分讀取不產生成功基線。 | 預算是本程式選定限制，超限不等於不存在快照。檔名順序不保證來源時間先後；達到選取上限即不作完整基線。 |
| EN-0015／0021、BT-0071、Redfish | 機箱序號標敏感，CLI 結構欄與事實值預設遮蔽序號。來源採集時間與本程式匯入時間分列；缺採集時間為 Unknown。風扇通用 Reading 須明示 RPM 才當轉速，其他單位保留原值。動態鍵合法化並處理標點、大小寫、截長與重複 ID。 | GeneratedAtUtc 是離線包自述；沒有新增 BMC 連線、簽章或時間可信性認證。同名原始 ID 的後綴只防鍵碰撞，不是實體身份證明。 |
| OS-0167／CP-0223／AC-0047／0048、拓撲 | 四個消費端共用有界 byte-buffer 解析；記錄 Size、affinity count、尾端、重複群組及空處理器 mask 驗證。native 配置與回傳長度分開，16 MiB 上限。異質 cache 按完整屬性分組，合計各群組共享數。NUMA 先查遮罩、1024 次枚舉預算，失敗／零 mask 不推斷節點數。 | 固定 x64 結構；目前兩個 CLI 專案亦為 x64。NUMA 單 group API、記憶體專用節點及 Windows 版本差異仍未收口；ThreadMigration 對多群組明確拒映射。多插槽／多群組實機矩陣未驗。 |
| DIMM／SPD | ScanBus 鎖存初次交易故障，停止 fallback、復位與下一插槽的新交易；MR0／MR1 不把傳輸錯誤當 DDR4。頁復位失敗附診斷，保留此前完整 DIMM。CPU-Z 與原生 SPD 使用同一 gate，等待逾時不提前釋放底層工作所有權。 | gate 僅在本程式內生效；其他程式與韌體競爭未解。ImcSmbusController.ReleaseBus 原有恢復寫入不受 ScanBus 包裝，底層 Timeout／模糊 NoDevice 分類仍須專項修正。不得稱原生 I/O 已取消或已具跨程序仲裁。 |
| EN-0008／0013／0022、KP-0019、知識包 | 8 MiB、有界讀取、64 表／合計 100,000 條目；正版本、ISO 日期／帶時區時間、快照日不晚於生成 UTC 日、必要欄位及表名不重複。 | 相容原 canonical hash 格式；upstream 文件／commit／抽取器 manifest 尚未建立，輸出明示來源未知，hash 不構成來源鏈或防偽。 |

## 驗證紀錄

- WHEA 最後定向組件：96 通過、0 失敗／略過；`Tests/TestResults/r346-whea-final.trx`。此後其他代理修改了不同檔案，不能代表本輪最終整合。
- 拓撲初版定向：32 通過；其後新增預算與 mask 邊界，最終需重驗。
- SPD 代理首次建置遇 WPF 暫存 `.g.cs` 缺失（CS2001），未進測試；當時數個建置曾並行，之後停止並行建置。沒有把此結果算成通過。
- 根代理首次 Fleet 整合建置發現缺 `System.IO` 導致 InvalidDataException 未解析；補引用後 Fleet 38/38 通過。
- 首次其他範圍整合：324 通過、1 失敗、0 略過；`obj/audit/r347/r347-target-initial.trx`。失敗揭示 Redfish 大寫鍵在真實時間膠囊匯出被轉為 rejected；已改合法小寫鍵與區分原 ID 的 hash，不放寬快照鍵格式。
- 第二次定向／文件整合：402 通過、3 失敗、0 略過；`obj/audit/r347/r347-target-final.trx`。3 項全為統計未同步，實際組件反射為 6,855 Fact／310 Theory／7,165 方法，667 測試檔、1,640 C# 檔。同步後重生 HTML，再跑完整回歸。
- **最終完整回歸：8,406 通過、0 失敗、0 略過，15 分 5 秒**；`obj/audit/r347/r347-full.trx`，主程序退出 0。涵蓋 Round 346–347 合併程式；全套既有唯讀系統收集不等於 DIMM／多插槽／PMU 等實機矩陣驗證。
- 1,760 個來源／專案／README／內嵌 HTML 檔及 3 個受測 DLL（共 1,763）在凍結取樣至全套結束間 SHA-256 變動為 0；清單見 `obj/audit/r347/source-freeze.json`、`assembly-freeze.json`。測試 DLL 為 `F1A8F7343EC555CE10B87D89781C7844BD3583997BF09877ECC73192B032F81D`。
- 全套完成後才將 TestSuiteBaseline、三語 README 與百科的現在式基線同步為 8,406；不改歷史 Round 345 的 8,235 結果。之後只重驗相關文件守門及 CLI，結果另列。
- 基線同步後文件守門首次 58 綠／2 紅（`r347-docs-final.trx`），兩項為 CONTRIBUTING、PR 範本與 CI 註解仍寫舊基線；已同步，保留原報告。最終 **60/60 通過、0 失敗／略過**（`obj/audit/r347/r347-docs-verified.trx`），涵蓋三語／百科統計、HTML、版本紀錄、發版腳本、來源追蹤及 10000 正典結構。這些是文件同步變更，沒有修改受全套驗證的產品邏輯。
- 最終 CLI 獨立建置：0 錯誤、13 個既有警告，1 分 29 秒；`obj/audit/r347/r347-cli-build.log`。實際啟動編譯產物的 6 項離線 smoke 均通過：help 退出 0、非法 sensor 引數 1、Fleet 缺目錄 2、損壞快照 2 並保留診斷、相容快照 0、Redfish 0 並確認序號不出現在結構欄或事實值。來源採集時間與敏感標記也有檢查；`r347-cli-smoke.json` 及 `cli-smoke.ps1` 位於同一 audit 目錄。
- 最終兩個 Python 生成器 `--check` 與 `git diff --check` 通過。未啟動真實 CPU-Z 報告、SPD 探測、PMU 寫入或原始磁碟測試；沒有提交、推送或發版。

完整驗證指令：

```powershell
dotnet test Tests/XinSpect.Tests.csproj --nologo -v:quiet `
  -p:UseSharedCompilation=false -m:1 -p:BaseOutputPath=obj/_verify_r347_fleet/ `
  --logger "trx;LogFileName=r347-full.trx" --results-directory obj/audit/r347
```

## 下一步

先完成此批整合及文件守門，再處理 iMC timeout 的原值恢復與控制器所有權、NUMA 多群組真實節點列舉，以及 CPER 外層 fixture／來源鏈。正典仍為 10,022 定義、39 域，這些修補沒有增加候選「已完成」數。

來源複核：本機 Windows SDK `10.0.26100.0/um/cper.h` 第 966–994 行（獨立有效位）、1009 後的基本節區欄位；`10.0.28000.0/um/winnt.h` 的 GROUP_AFFINITY、PROCESSOR_RELATIONSHIP、NUMA_NODE_RELATIONSHIP、CACHE_RELATIONSHIP。只用於結構與有效位邊界，不替代平台實測。
