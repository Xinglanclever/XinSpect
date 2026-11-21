# 第 334 輪：BCD 與 EDID 精度收口

> 日期 2026-11-16 · 範圍：v3.28 稽核與 U10-GP/BD 補強 · 不新增硬體寫入面

## 找到並修正

- **BCD 狀態誤判**：`bcdedit` 非零結束碼即使有 stdout 也改標 ReadError；缺 `{bootmgr}`、載入器或 `{fwbootmgr}` 物件標 Unknown，不再推成「未設定／非 UEFI」。開機順序與 OS 元素共用 `/enum all`，韌體物件用 `/enum firmware`，共兩次讀取。
- **BCD 值解析**：`badmemorylist` 從全域 `{badmemory}` 讀；指出 Windows 10 20H2 起 PFA 頁框清單另存 `HKLM\SYSTEM\CurrentControlSet\Control\WHEA\BadPages`，本鍵不代表記憶體健康。`truncatememory` 拼字與 bytes 單位修正，`removememory` 保持 MB；路徑保留空白，移除未經證據支持的 BitLocker 推論。
- **DMT 位圖**：Established Timing I 實際位址更正為 EDID `0x23–0x25`（位元組 35–37），逐列暴露 byte offset 與 mask；Established Timing III 從 descriptor byte 6 起算。Apple 時序更正為 `1152×870@75`。完整時序比對多候選時拒絕任選；1366×768 的兩種 DMT 時序由 clock/sync/totals 區分。
- **EDID 解碼接線**：新增唯讀 `edid.timings`，由一份登錄檔快照解 Established I、Standard、Established III、base DTD、CTA DTD；DTD 比對完整欄位，Standard 只列幾何候選。顯示器尺寸與邊框保留原始宣告；交錯式 DTD 未做不可靠的幀時序換算。
- **CTA 完整性**：基本塊與每個 extension 都驗 checksum；無效、截斷或越界資料保留診斷並標 Unknown，不再轉成「沒有 CTA／沒有 HDR」。HDR 頁也採相同診斷；其他有效顯示器的部分資料會保留並標整體未確認。登錄檔存取失敗與空 EDID 清單分開。
- **啟動與工具回歸**：SPD 掃描超時保留已完成模組並等待背景讀取收尾；自訂工具 URL 保留 fragment、參數身份編碼不混同空參數；修正 WinRing0 `METHOD_BUFFERED` 請求回覆共用緩衝區覆寫高地址的問題。`EvidenceLabService.cs` 中造成檔案被當成 binary 的 NUL 改為 C# `\0`，並以檔案屬性保留既有 CRLF。
- **磁碟探測排程**：共享執行緒集區滿載時，磁碟探測可能未開始便越過 2 秒上限。每卷改用專用背景執行緒；即使底層 I/O 永不返回，也不阻止 UI 或行程關閉，並以回歸測試守排程與背景執行緒屬性。

## U10 對應

EDID 部分落在 U10-GP-0087、0089、0091–0095、0099 與 U10-VC-0040；BCD 對應第四圈 BD 批次三。目錄新增一把彙總鍵 `edid.timings`，現有 `edid.cta.*` 沒有增加。DTD/Standard 的各欄位目前合併呈現在同一事實值內，尚未拆成逐描述元動態鍵；交錯 DTD、DisplayID 與 CTA VIC 仍走各自界線。

下一批優先研究 U10-WM-0001（Win32_Bus）、0008（Win32_PortResource）、0010（Win32_IRQResource）、0012（Win32_AssociatedProcessorMemory）與 0036（官方清單對帳）。WM-0018 的 Win32_USBControllerDevice 已由既有 `UsbTopologyService` 讀取，先做查重，不另造重複通路。

## 驗證

- 定向回歸：210 項通過；包含 BCD、DMT、EDID/CTA checksum、README/百科守門、SPD 與 Volume 排程。
- `Tools/test-xrp-mmio.py`：9 項通過；`XsRegProbe` x64 編譯成功。
- 完整測試：`dotnet test` 8,132 通過、0 失敗、0 略過；讀寫路徑以唯讀和注入 fixture 驗證，未實機驗證硬體寫入，也未把 EDID fixture 當成目前顯示器實測。
