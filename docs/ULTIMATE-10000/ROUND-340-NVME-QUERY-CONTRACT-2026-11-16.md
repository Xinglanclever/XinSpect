# 第 340 輪：NVMe 查詢封包與返回描述子

日期：2026-11-16。接續批次六，在擴充讀盤前修正既有 NVMe 讀取基礎；不新增事實鍵、不新增啟動探針。

## 已證實的錯誤

- 舊 `PropertyIdProtocolSpecificDevice=51` 實為 `StorageAdapterTemperatureProperty`，不是協定專用查詢。裝置協定屬性是 50，控制器／介面卡協定屬性是 49。
- 舊 Identify 的 DataType=0 實為 Unknown；正確為 1。CNS=1 是另一欄的 Controller 選擇，兩者不能混用。
- 舊 ProtocolDataOffset=48 把整包偏移填入相對偏移欄。該欄相對於位址 8 的 ProtocolSpecificData，通常應填 40；實際 payload 因而位於整包位址 48。
- 舊讀取只驗總長度，然後硬拷位址 48。現在驗返回描述子的 Version／Size、協定與資料類型、相對偏移、宣告資料長度及實際返回長度；合法填充依回覆偏移定位。截斷、超配置、UInt32 邊界與錯誤資料類型均拒報。

## 實作

- 新增純函式 `XinSpect.Decoders/NvmeProtocolQuery.cs`，三條生產讀取路徑共用請求／回覆契約。
- 健康日誌走裝置範圍；控制器 Identify、錯誤／韌體槽日誌與現有無 namespace 參數的 Get Features 走介面卡範圍。
- 日誌保留原有 overlapped I/O、取消寬限與斷路器；沒有以解析器代替 I/O 完成確認。Identify／Feature 仍沿既有同步來源與外層預算，本輪不宣稱它們已可原生取消。
- Feature 長度先驗證 1–4096，再開裝置；非法長度不被靜默當成不支援。
- `Tests/NvmeProtocolQueryTests.cs` 使用 SDK 版面與合成回覆驗證，不開磁碟。實機相容性、SAS pass-through、Spaces 成員映射仍未完成。

## 規格證據

- Microsoft Learn：Working with NVMe devices，2026-11-16 實際取得；頁面留存 `obj/audit/nvme-query-source.html`。
  https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices
- 本機 Windows SDK 10.0.28000.0：`um/winioctl.h` 的 STORAGE_PROPERTY_ID（約 1160）、STORAGE_PROTOCOL_NVME_DATA_TYPE（約 2588）、STORAGE_PROTOCOL_SPECIFIC_DATA／STORAGE_PROTOCOL_DATA_DESCRIPTOR（2690–2756）。

## 驗證與下一批

Round 339 全套真實結果：8166 案例＝8163 通過、3 項 ReadmeStatsTests 失敗；原因為全域 C#／測試檔／方法數統計未更新。本輪同步現行統計，歷史 Round 335 的 8139 案例維持原記錄。Round 340 的最終定向／全套證據另見交接文檔。

下一批仍是 TS-0185／0187：先裝置身分、協定與磁區大小，再做受預算限制的可取消唯讀通道，最後接分尺與同範圍重讀。4Kn 不偽造 512B 未緩衝支援；取消確認前不釋放 I/O 資源；不以兩次讀取的速度差猜快取容量。TS-0191 的獨立來源配對仍待做。此輪不算完成這三項候選。

最終整合驗證：Round 342 全套 8,216 通過、0 失敗、0 略過，13 分 31 秒；測試期間驗證輸入散列未變。`obj/audit/r342/r342-full.trx` 為原始證據。
