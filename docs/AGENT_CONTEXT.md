# XinSpect AI 開發上下文（快速上手筆記）

> 給 AI 代理：先讀這份，不需要掃整個專案。目標是 30 秒內知道改什麼、去哪改、怎麼驗證。

## 專案概要

- WPF 硬體資訊工具，.NET 10 (`net10.0-windows`)，單執行檔
- 版本 2.1.0，2389 個 xunit 測試，建置 7 秒、測試 14 秒
- 主要套件：LibreHardwareMonitorLib（感測器）、NVML/NVAPI（NVIDIA 深度資訊）、NAudio

## 關鍵路徑

| 要改什麼 | 去哪改 |
|---------|--------|
| 硬體資料模型 | `Models/HardwareModels.cs`（GpuRow、CpuRow、StorageRow） |
| 感測器讀取 | `Services/SensorService.cs`（LibreHardwareMonitor 綁定） |
| NVIDIA 深度資訊 | `Services/Gpu/NvmlInterop.cs`、`Services/Gpu/NvapiInterop.cs` |
| NVMe 健康解碼 | `Services/NvmeHealth.cs`、`Services/NvmeLogDecoder.cs` |
| 深度測試（DeepBench） | `Services/DeepBench/`、`Models/DeepBench/` |
| UI 頁面 | `Views/`（XAML + code-behind） |
| 單元測試 | `Tests/`（xunit，專案 `Tests/XinSpect.Tests.csproj`） |
| 功能待辦 | `docs/feature-backlog.md` |

## 建置與測試

```powershell
# 建置（約 7 秒）
dotnet build

# 全部測試（約 14 秒，2389 個）
dotnet test Tests\XinSpect.Tests.csproj

# 發佈單檔 exe
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true
```

## 開發原則

1. **誠實標示**：讀不到就顯示「—」或「不支援」，不用典型值代替
2. **每改一個功能就跑建置加測試**（完整週期 21 秒）
3. **UI 語言**：繁體中文（台灣用語）
4. **命名**：感測器服務用 `XxxService`，模型用 `XxxRow`/`XxxModels`
5. **不可破壞現有測試**：有任何紅燈就不算完成

## 已完成的關鍵整合

- LibreHardwareMonitor：CPU/GPU/儲存/主機板感測器
- NVML：NVIDIA 溫度、功耗、風扇、PCIe replay、ECC、序號
- NVAPI：Pstates20 超頻（頻率偏移寫入）
- NVMe Admin Command：健康狀態、溫度、SMART、Log Page 解碼
- ETW：幀時間、DPC 延遲（零驅動、零注入）
- BlueSquadron：安全資訊（TPM、BitLocker、Secure Boot）

## Bridge 子專案

`Bridge/` 是 net48 的 XTU 橋接程式，不納入主專案編譯。建置時由 MSBuild 巢狀呼叫並內嵌為資源。

## Tests 結構

測試在 `Tests/` 目錄，是獨立的 xunit 專案。主專案已排除 `Tests/**/*.cs`。每個 Service 通常有對應的 `XxxServiceTests.cs`。
