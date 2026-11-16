# Round 345：Core Temp 熱力圖備援與 v3.30 發佈

日期：2026-11-16。候選 U10-TS-0191 維持進行中。使用者本輪明確要求修完上傳，取代前輪僅本機推進的限制；發佈以當次驗證與資產核對結果為準。

## 缺陷與修補

- `CoreTempMapService.FallbackCoreTemp` 原按列順序處理；讀取器的 TjMax 列在核心溫度後面，差值模式整批略過。現在先收 TjMax，再按唯一 `ChannelIndex` 對應 `PhysicalCoreId`，列序不決定核心。
- 官方 Core Temp SDK 的 `.Net/Code Samples/GetCoreTempInfoNET Demo/GetCoreTempInfoNET Demo/Program.cs` 為 TjMax 與核心溫度共用依 `IsFahrenheit` 決定的 `TempType`。撤回讀取器舊註解「TjMax 一律攝氏」：原值保留其華氏／攝氏單位，合理範圍檢查先換成攝氏。
- 絕對華氏溫度轉換為 `(F - 32) × 5 / 9`；華氏溫差轉換為 `deltaF × 5 / 9`，不能減 32。由同次讀取的 TjMax 重建差值，不沿用上次上限。
- 備援限 Windows 拓撲確認單插槽、LHM 單顆 Intel 且未呈現 P／E 核心標籤、型號字串吻合、完整唯一核心索引的情況。多插槽、未知拓撲、混合核心、缺核與重複索引不按位置猜測；原始外部感測器清單仍保留來源讀數。
- 缺 TjMax 不重建差值；非有限值、未知單位、無效範圍不顯示，移除舊溫度。保留零度與合法負溫，不以「非正數」代替失敗判斷。
- 與跨來源比較共用 `SensorService.HasComparableIntelCoreOrder` 的來源拓撲閘門；不新增硬體存取或寫入。

## 來源與限制

官方 SDK 證據沿 Round 344：`obj/audit/r344/CoreTempSDK.zip`，SHA-256 `DD7893A8051551947224822BF6060DD6160FA03C6D93FCE0ABB46DE46B2D37F2`，取得自 `https://www.alcpu.com/CoreTemp/main_data/CoreTempSDK.zip`；未執行包內二進位。欄位定義及每 CPU 索引見 SDK `GetCoreTempInfo.h`、上述 `Program.cs` 及 `CoreTempPluginProxy.XML`。

本輪匿名共享記憶體測試支持欄位、單位、順序與拒絕規則；不支持實機來源更新時刻、物理核心一一對應或獨立精度認證。熱力圖既有 LHM 主路徑的多插槽／混合架構仍須專項核定，本輪不宣稱已全面修正。CLI 候選比較入口仍待接線。

## 驗證與交接

- 感測比較／Core Temp SDK／熱力圖對位定向：19/19 通過，0 失敗／略過；`obj/audit/r345/r345-target.trx`。
- 安裝器從本工作樹重新編譯，PE／.NET 形態及本機 HTTP 完整下載、斷線清理、無長度回應回歸全部通過；大小 29,696 bytes。
- XsRegProbe 從既有來源重新編譯成功，未載入驅動，未執行硬體寫入。
- v3.30 定向與文件／版本／來源追蹤守門：71/71 通過，0 失敗／略過；`obj/audit/r345/r345-final-target.trx`。
- 最終完整測試：8,235/8,235 通過，0 失敗／略過，測試報告耗時 16 分 48 秒；`obj/audit/r345/r345-final-full.trx`。受測來源、專案、文件、HTML 及兩個 DLL 共 1,778 檔 SHA-256 漂移為 0。
- 全套結束後僅更新測試基線註解與交接紀錄、移除百科過期成品大小、重生 HTML；定向與文件守門重驗 71/71 通過，`obj/audit/r345/r345-release-docs.trx`。三語 README 的九件大小按最終資產填寫；內嵌百科不寫自身成品大小，避免自我引用。
- v3.30 整合 Round 335–345；發佈九件資產與 `SHA256SUMS.txt`，逐件對帳遠端大小與 SHA-256。實際發佈狀態以 GitHub `v3.30` Release／標籤及當次遠端驗證報告為準，不把本機建置當成上傳成功。Drive 掛載副本核對不能單獨證明雲端同步完成。

下一批：取得代表性實機快照並核定來源更新時間與拓撲映射；再接 CLI 候選比較入口。TS-0185／0187 的實機幾何、取消及斷路器行為仍未驗證，不增加候選完成數。
