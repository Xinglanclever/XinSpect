> **專案授權**：曦覽 XinSpect 自 v2.69 起以 **AGPL-3.0-or-later** 釋出（見 `LICENSE`）。
> 下表列出的第三方元件**不是**在本專案 AGPL 之下——它們各自維持原本的授權（含 MIT／MPL-2.0／LGPL 等），這些授權條款要求保留姓名標示與授權聲明，因此本檔中的第三方 MIT 段落**必須保留**，不屬「專案自己的 MIT 痕跡」。

# 第三方元件清單（THIRD-PARTY NOTICES）— v2.64

> 本清單由發佈一致性測試（`Tests/ThirdPartyNoticesTests`）與 `XinSpect.csproj`／`Bridge/*.csproj`／
> `BlueSquadron/*.csproj` 的 `PackageReference`／`PackageDownload` 逐項對帳——加套件沒寫進這裡會紅燈。
> **本清單是事實摘要，不構成法律意見**；各授權全文以上游連結為準。

## 執行期元件（隨程式分發）

| 元件 | 版本 | 授權 | 義務 | 來源 |
|---|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | 附授權文本連結；修改需聲明 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| LibreHardwareMonitorLib（WinRing0 來源） | 0.9.4（PackageDownload，僅取內嵌 Ring0→WinRing0x64.sys） | MPL-2.0；內含 OpenLibSys WinRing0（LGPL） | 以原樣載入、不修改驅動；附兩層來源連結 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/release/0.9.4 ；https://openlibsys.org |
| Microsoft.Web.WebView2 | 1.0.4129.50 | MIT | 附授權文本連結 | https://github.com/MicrosoftEdge/WebView2Browser |
| System.Management | 10.0.11（Bridge 9.0.4） | MIT | 附授權文本連結 | https://github.com/dotnet/runtime |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.16 | MIT | 附授權文本連結 | https://github.com/Microsoft/perfview |
| NAudio | 2.* | MIT | 附授權文本連結 | https://github.com/naudio/NAudio |
| System.ServiceProcess.ServiceController | 9.0.4（Bridge） | MIT | 附授權文本連結 | https://github.com/dotnet/runtime |
| PCI ID 資料庫（`pci.ids`，內嵌 `XinSpect.Decoders/Data/pci-ids.tsv.gz`） | 快照 2026.10.01 | BSD-3-Clause（上游自述為 GPL-2.0-or-later 或 BSD-3-Clause 雙授權，本專案採後者） | 保留姓名標示與授權聲明；衍生檔只抽廠商與裝置兩段、其餘丟棄；**不因此使本專案受 GPL 約束** | https://pci-ids.ucw.cz/ ；https://github.com/vcrhonek/hwdata |

## 建置期／測試期元件（不隨程式分發）

| 元件 | 版本 | 授權 | 用途 |
|---|---|---|---|
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | MIT | XtuBridge 建置參考組件（PrivateAssets=all） |
| xunit／xunit.runner.visualstudio | 2.9.x | Apache-2.0 | 測試框架 |
| FsCheck.Xunit | 3.4.0 | BSD-3-Clause | property 測試 |
| dotnet-stryker | 5.x | Apache-2.0 | 突變測試（開發工具） |

## 本專案自身

曦覽 XinSpect 的授權見倉庫 `LICENSE`（若未附，以倉庫根目錄聲明為準）。
