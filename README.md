# 曦覽 XinSpect

> 一款專為 Windows 打造的原生硬體資訊總覽工具 — 即時感測、效能評測、超頻控制與一鍵裝機，全部整合於單一桌面應用程式。

![版本](https://img.shields.io/badge/version-1.2.0-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![授權](https://img.shields.io/badge/license-MIT-green)

繁體中文原生介面。以 WPF（.NET 10）撰寫,採 MVVM 架構,整合 LibreHardwareMonitor 感測、WebView2 內建瀏覽器,並以獨立 net48 橋接程式承載 Intel XTU SDK 進行 CPU 超頻。

![總覽](gallery2.png)

## 主要功能

- **硬體總覽** — CPU / 主機板 / 記憶體 / 顯示卡 / 儲存裝置的完整規格,含主機板廠商徽章與 CPU 官方 logo。
- **即時感測** — 溫度、時脈、電壓、風扇、負載即時儀表,支援迷你懸浮視窗與系統匣。
- **溫度負載警示** — 可設定門檻,超標時橫幅提示 + 系統匣氣泡通知。
- **感測記錄** — 一鍵匯出 CSV,長期追蹤。
- **效能評測** — 內建多項基準測試,並可啟動原版對照。
- **效能天梯** — 離線 CPU / 顯示卡天梯榜(資料來源 topcpu.net),快速定位自己的硬體排名。
- **CPU 超頻** — 透過內建 Intel XTU 橋接程式調整倍頻 / 電壓。
- **顯卡超頻** — NVML 功耗 / 風扇 / 溫度監控 + NVAPI 時脈調整。
- **一鍵裝機** — 整合 winget,勾選常用軟體批次安裝。
- **內建瀏覽器與終端** — WebView2 內嵌瀏覽,以及真實 `cmd.exe` 終端。
- **AI 評價** — 接 Ollama 或任意 OpenAI 相容端點,對本機硬體給出評語(提示詞可自訂)。
- **集中設定** — 所有偏好以 JSON 持久化,支援一鍵初始化。

## 系統需求

- Windows 10 / 11 或 Windows Server(x64)
- 部分功能(超頻、感測)需以**系統管理員**身分執行
- 顯卡超頻功能需 NVIDIA 顯示卡與對應驅動

## 建置

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build -c Release
```

執行:

```bash
dotnet run -c Release
```

發佈單一執行檔:

```bash
dotnet publish -c Release -r win-x64
```

> 建置時會自動以巢狀 MSBuild 編譯 `Bridge/`(net48 的 Intel XTU 橋接程式)並內嵌為資源,毋須手動處理。

## 技術棧

| 項目 | 說明 |
|------|------|
| UI | WPF (.NET 10),MVVM |
| 感測 | LibreHardwareMonitorLib |
| 系統資訊 | System.Management (WMI) |
| 內建瀏覽器 | Microsoft.Web.WebView2 |
| CPU 超頻 | Intel XTU SDK(net48 橋接程式) |
| 顯卡 | NVML / NVAPI |

## 授權

本專案以 [MIT License](LICENSE) 釋出。

> 注意:內建的第三方元件(Intel XTU SDK、內嵌評測執行檔等)受其各自授權條款約束,不在本專案 MIT 授權範圍內。

## 作者

By：Xinglanclever
