# 第 339 輪：儲存通道不再依虛擬層猜協定

日期：2026-11-16。推進下一批讀盤前，發現既有 `StorageSmartService.ChannelOf` 的兩個錯誤前提；本輪修讀取路由，不新增事實鍵或讀取命令。

## 問題與修正

- BusTypeSpaces＝16 只表示 Storage Spaces，不能據此當成 NVMe。舊路由會進 NVMe SMART／Identify 與韌體能力查詢；現在回 None，保留 Storage Spaces 名稱並說明尚無已核唯讀直讀通道。
- BusTypeSas＝10 表示 serial-attached SCSI，不能把所有 SAS 裝置當成能接受 ATA SMART／IDENTIFY。現在同樣回 None；SAS 控制器下 SATA pass-through 必須另行證明，不再由匯流排名稱猜測。
- 明確 NVMe＝17 才使用現有 NVMe 通道；明確 ATA＝3／SATA＝11 使用既有 ATA 通道。沒有直讀通道不代表裝置不支援 SMART。
- `NvmePowerService` 另有 `16 or 17` 的獨立判斷，也改用同一個 ChannelOf，避免 Storage Spaces 出現在 NVMe 電源狀態頁。

## 來源與驗證

- 2026-11-16 核對 Microsoft Learn `STORAGE_BUS_TYPE` 官方頁與本機 Windows SDK 10.0.28000.0 `um/winioctl.h:968–991`。官方頁：https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddstor/ne-ntddstor-storage_bus_type 。本輪 HTML 留在 `obj/audit/storage-bus-type-source.html`。
- 更新舊測試「維持舊常數」的錯誤預期，改為按明確協定路由；新增 SAS／Spaces 兩個注入回歸，確保共用 headless SMART 探針不呼叫 ATA 讀取委派、且如實保留略過原因。
- 不作實機 SMART 或 NVMe 日誌讀取；定向與最終全套結果記於交接文檔。

此輪不提供 SAS SMART 解碼或 Storage Spaces 成員映射。這兩者仍是企業／伺服器儲存能力的明確缺口，後續應建立具名成員、協定及可取消 pass-through 的讀取證據。
