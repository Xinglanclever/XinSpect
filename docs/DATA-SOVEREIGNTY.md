# WP39 資料主權聲明

- **原則**：資料在哪、誰能拿走、怎麼刪——每一項都說得出來。這份聲明有機器檢查
  （`Tests/DataSovereigntyTests.cs` 掃描原始碼），程式與文件漂移會紅燈。

## 1. 資料落地位置（全部在本機）

| 資料 | 位置 | 內容 |
|---|---|---|
| 審計日誌 | `%ProgramData%\XinSpect\Audit\audit.json` | 時間膠囊動作的中繼資料（雜湊鏈），不記事實內容 |
| 驅動憑證 | `%ProgramData%\XinSpect\Driver` | 深層存取的自簽 CA 與金鑰（ACL 限 SYSTEM/Administrators） |
| 時間膠囊／原始快照 | **使用者自己選的路徑** | 事實集合與原始暫存器位元組（raw 不匿名化） |
| 深測中心儲存根 | `%TEMP%\XinSpectDeepBench`（可改） | 量測結果 |

## 2. 網路行為：事實蒐集零出網

- **事實蒐集（所有 `Services/*Facts*`、解碼器、WMI／登錄檔／事件記錄讀取）零網路呼叫**——
  機器檢查掃描原始碼釘死；新增的蒐集路徑若引入網路 API 會直接紅燈。
- 全程式僅有的網路功能都是**使用者主動觸發**（機器檢查的允許清單）：
  1. `FeedbackService`——使用者主動送出回饋；
  2. `AiService`——使用者主動使用 AI 說明；
  3. `NetworkSpeedService`——使用者主動跑測速（量測對象就是網路本身）；
  4. `NetworkStackLatencyAdapter`（DeepBench）——使用者指定的目標位址量延遲；
  5. `UpdateCheckService`——使用者主動點「檢查更新」，只向 GitHub Releases API **查詢**
     最新版本號，不上傳本機任何資料；查不到時如實顯示，不假裝已是最新。
- Wi-Fi 訊號讀取（wlanapi）查詢的是本機無線電的狀態，**不連線、不上傳**。
- 自我遙測（SelfTelemetry）**預設關閉**、本機存取、匿名只記數字與時間。

## 3. 匿名化

- 時間膠囊的機器識別是單向雜湊派生（`anonymousMachineId`，格式 `sha256:`＋64 hex），
  不含序號／UUID／MAC 原文。
- 預設儲存即遮蔽（`SensitiveValuePolicy.Redact`）；「保留敏感值」必須使用者主動指定。
- corpus 貢獻包（WP48 骨架）**只收遮蔽版**、身份鍵逐鍵排除，且上傳通路刻意未實作
  （見 `docs/spec/corpus-upload.v1.md`）。

## 4. 刪除即終結

- 所有資料都在本機檔案——刪除檔案就是刪除資料，沒有雲端副本、沒有備份同步、沒有遠端殘留。
- 審計日誌是附加檔案（雜湊鏈偵測竄改），刪除後程式不重建歷史。
