# WP37 部署與企業維運

- **定位**：本工具是單機、免安裝的 WPF 應用（單 exe）——部署面刻意保持極簡。
  這份文件講「怎麼發佈、怎麼在腳本裡用、資料在哪、怎麼升級」。

## 1. 發佈形態

- **可攜式單檔**：`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`
  ——目標機需 .NET 10 Desktop Runtime。
- **自包含單檔**：`--self-contained true`——目標機零相依，體積較大。
- 藍色中隊（BlueSquadronBridge，守護進程 console app）與主程式同一款 publish 旗標，個別產出。
- 主程式有單一實例信號（CLI 分支除外，CLI 不觸發多開對話框）——企業腳本可安心並行調用 CLI。

## 2. CLI（適合腳本／遠端維運）

```
XinSpect.exe --json evidence [--query <查詢>] [--out <檔案>]
```

- 退出碼：0＝全部事實 Present／2＝部分三態／1＝致命。
- 查詢語言見 `docs/spec/query-language.v1.md`（CLI 與本機 API 同一語法）。
- 不需要管理員權限即可輸出證據組；驅動相依事實會如實標 InsufficientPrivilege。

## 3. 資料與日誌位置（企業盤點用）

| 項目 | 位置 |
|---|---|
| 審計日誌 | `%ProgramData%\XinSpect\Audit\audit.json`（雜湊鏈，可離線驗證） |
| 驅動憑證（若啟用深層存取） | `%ProgramData%\XinSpect\Driver` |
| 快照／原始暫存器 | 使用者指定路徑（`.json`／`.xinraw`） |

- 審計日誌格式與驗證器見 `Models/AuditEntry.cs`／`AuditVerifier`——竄改任何中間一筆都會被鏈檢查抓到。
- 資料主權（不出網、刪除即終結）見 `docs/DATA-SOVEREIGNTY.md`，有機器檢查釘死。

## 4. 升級與回滾

- 版號單一來源＝csproj `<Version>`；執行期由 AppInfo 讀組件資訊版本——替換 exe 即完成升級。
- 設定與資料不在安裝目錄（都在 %ProgramData% 與使用者路徑）——新舊版 exe 可共存、可隨時回滾。
- XsRegProbe 驅動的載入／卸載由深層存取豁免開關管理（`DeepAccessService`）：
  停用＝停刪服務＋只移除自己 CA——回滾乾淨。

## 5. 已知企業環境限制

- **HVCI／VBS 開啟的機器**：WinRing0 可能無法載入——驅動相依事實如實三態，
  XsRegProbe（白名單唯讀）為備援通路；MSR 卡片的可信度打折會在平台可信度頁明示。
- **群組原則封鎖自簽憑證**：深層存取的 CA 安裝會失敗——服務如實標，不降級宣稱。
