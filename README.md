[简体中文](README.zh-CN.md) · 繁體中文

# 曦覽 XinSpect

> 一款免費開源、專為 Windows 打造的原生硬體驗機／監控／安全工具——單一執行檔、免安裝、全程唯讀讀取、不收集任何資料。

![版本](https://img.shields.io/badge/version-2.5.0-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![測試](https://img.shields.io/badge/tests-3094%20passed-3FB950)
![突變分數](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![授權](https://img.shields.io/badge/license-MIT-green)

![總覽](gallery2.png)

曦覽（XinSpect）以 WPF（.NET 10）撰寫、MVVM 架構，整合 LibreHardwareMonitor 感測、Intel XTU 橋接、NVML／NVAPI 顯卡控制、WebView2 內建瀏覽器，以及本專案自寫的 WinRing0 事實讀取層與 XsRegProbe 白名單唯讀驅動。名稱裡的「Spect」是檢視者——它不替你的硬體下結論，它把硬體自己說的話、韌體說的話、作業系統說的話並列攤開，讀不到的如實標示，讓驗機的人自己下判決。

**[下載 v2.5.0 Olympus](https://github.com/Xinglanclever/XinSpect/releases/tag/v2.5.0)** — 單一執行檔免安裝，藍色中隊守護進程已內建。

## 核心設計哲學：誠實契約

這不是行銷詞，是**有機器檢查守著的工程約束**：

- **三態標示**——每一筆事實都帶 `availability`：讀到（Present）、平台不支援（NotSupported）、權限不足（InsufficientPrivilege）、讀取失敗（ReadError）、本機無此硬體（NotApplicable）。**讀不到就是讀不到，絕不以 0、典型值或舊值填補。**
- **來源可稽核**——每筆事實標明出自哪顆暫存器、哪個 WMI 類別、哪支 API；暫存器解碼器必須附規格引用（SpecRef），由反射機器檢查，漏寫直接紅燈。
- **非自造驗證**——測試結果附自我檢核：象棋 perft 是數學常數，算出別的數字是這台機器算錯了而不是慢；量測前用已知大小負載自我驗證；能量計對不上就不換算成瓦。
- **突變測試**——純解碼器抽成獨立類別庫，Stryker 突變分數 82%，抓到過「文件說有 extended family 進位、實作卻漏做」的真 bug。
- **查不到≠沒有**——查詢語言對「存在但讀不到」的項目照樣匹配並附原因，不把查錯偽裝成空結果。

## 下載

| 檔案 | 用途 |
|---|---|
| `XinSpect.exe`（約 29.7 MB） | **主程式**——單一執行檔免安裝，藍色中隊守護進程已內建（執行期自動解壓，SHA-256 驗證） |
| `BlueSquadronBridge.exe`（約 6.5 MB） | 獨立守護進程——只想單獨跑安全防護的人才需要 |

需求：Windows 10 1903+／Windows 11 x64，.NET 10 Desktop Runtime。部分功能（MSR 讀取、SMART、事件記錄 Security log）建議以系統管理員執行——沒有權限時相關項目會如實標「權限不足」，不會假裝成功。

## 功能總覽

### 總覽群組

- **我的電腦／總覽** — CPU／主機板／記憶體／顯示卡／儲存的完整規格摘要，主機板廠商徽章與 CPU 官方 logo、記憶體插槽配置圖（實心＝有模組、虛線＝空槽；看不出通道就直說，不硬湊 A/B——猜錯通道會害人把記憶體插到錯的槽）。
- **瓶頸診斷** — 把散在各頁的讀值合起來回答「現在是什麼在拖住這台機器」：溫度牆、功耗牆、單執行緒、記憶體、儲存、顯示卡、驅動 DPC、電源政策、MCA 平台事件，依「該先看哪一條」排序；**沒量到的資料列進「還沒納入判斷的部分」，不當成沒問題**。全程唯讀。
- **AI 評價** — 接 Ollama 或任意 OpenAI 相容端點對本機硬體給出評語（提示詞可自訂）。

### 處理器

- 完整 CPUID 解讀：世代判定（含 extended family 進位——突變測試抓出並修正過的真 bug）、指令集、快取拓撲、die 拓撲（CPUID 0x1F）。
- 微碼修訂雙來源交叉對帳：CPU 自己說的（MSR 0x8B 逐核讀取，逐核不一致如實標）vs Windows 記錄（登錄檔 Update Revision，8/4 位元組兩種實測佈局都解、歧義不解碼）。
- TjMax、溫度、頻率真相（MPERF/APERF 與 Turbo 階梯）、電壓、每核心負載。
- **TME／SGX 記憶體加密狀態**（MSR 0x982＋CPUID leaf 0x12）、Package C-state 駐留（µs）。
- **PMU**：能力探索（CPUID 0xA）＋固定計數器唯讀觀察；編程驗證（最小寫入→讀回→工作量→還原，3 輪聚合）——**多輪測試・不保證可用**，同意閘門，絕不碰 PMI 位元，每輪還原原值。
- **效能天花板** — 回答「這顆 CPU 為什麼跑不到該有的頻率」：TCC 節流溫度、PL1/PL2 與時間窗、電流牆、Turbo 倍頻表全部直接讀 MSR（不是規格書數字），加上限制原因暫存器的黏滯紀錄與使用者親自觸發的逐窗撞牆量測（基線／整數／AVX2／AVX-512），歸因成一句判決：溫度牆、功耗牆、電流牆、供電模組過熱、自主 P-state、多核渦輪上限，或「缺口不在硬體」。全程唯讀，不清任何黏滯位。

### 記憶體

- SPD 直讀：DIMM 完整資料（製造商、序號、時序），標明讀取匯流排；TSOD 溫度感測器。
- 通道／容量／速度（標稱 vs 實際）、ECC 現況。
- Rowhammer：風險聲明（為什麼工具不測）＋**壓力探測**（自擁有記憶體內錘擊、同意閘門、危險標註、**未經過校驗**——usermode 無 clflush，非保證觸發）＋多輪模式。
- NUMA：拓撲、節點距離（ACPI SLIT）、跨 NUMA 對照、TLB 與大分頁成本。

### 主機板

- 型號／BIOS 版本／序號、晶片組、SMBIOS 全解。
- **機箱開啟偵測**（SMBIOS Type 3 Security Status——「入侵偵測」＝拆機的韌體級證據，二手機驗機一翻兩瞪眼）。
- Super I/O 晶片識別（coreboot 出處對照）＋ HWM 感測器（風扇 RPM、溫度、電壓——電壓標「未經主機板校準」）。
- PCI Bus 0 裝置盤點（PCI-SIG 類別碼知識庫）、PCI BAR 資源、ReBAR 實況（問 Windows 實際指派的記憶體視窗，不是能力宣稱值）。
- SPI 快閃稽核三層：暫存器旗標（FLOCKDN/WRSDIS/PR）→ FREG 地圖 → BIOS 區 SHA-256 → 與參考映像逐塊比對。
- ACPI 表列全解：MCFG、HEST、BERT、SLIT 節點距離、CEDT（CXL）、HPET、FADT PM timer。
- CMOS/RTC（電池電壓、時鐘、校驗和）、UEFI 開機設定四態、POST 代碼。

### 儲存裝置

- **SMART 全屬性＋門檻 failing-now**：SMART READ THRESHOLDS（0xD1）對照 READ DATA——**現值 ≤ 門檻＝現正低於門檻**，逐項攤開（門檻 0＝無門檻不評比）。
- **NVMe**：健康紀錄全解、WCTEMP 溫度警告（Identify Controller 0x14A 對照合成溫度）、錯誤紀錄、電源狀態表（各階功耗與進出延遲）對實測閒置喚醒延遲。
- **HPA 隱藏容量**：ATA IDENTIFY 最大 LBA 對照 OS 可見——不一致即 HPA 作用中（翻新機／竄改證據）；DCO 需廠商私有命令，誠實聲明不實作。
- **磁碟表面掃描**：循序讀取逐塊量延遲，標記慢區／讀取錯誤——SMART 是韌體說的，這是自己讀的。
- **假容量寫入驗證**（H2testw 式）：可重現樣本寫滿→沖刷→讀回逐位元組驗證→刪檔；**同意閘門＋危險標註**（加劇瀕死媒體損耗）。
- 通電時數與機齡推估、磁碟 QD 效能曲線、容量／韌體／序號。

### 顯示卡

- NVIDIA NVML：溫度／頻率／功耗／風扇／溫度閾值／**退休頁**（NAND 瑕疵退休計數）；AMD／Intel 基本資訊。
- GPU 深測：光柵填充率、紋理取樣、H.264 編碼、計算管線、PCIe 頻寬。
- HDR 能力（EDID CTA-861）、**顯示鏈路真相**（像素時鐘／色彩編碼／位元深度——頻寬不夠時驅動偷偷降 4:2:2，設定裡照樣寫 4K144）。
- TDR 逾時設定（未設定＝Windows 預設並明說）。
- 顯卡超頻：NVML 功耗／風扇／溫度監控＋NVAPI 時脈調整。

### 網路

- 介面清單與速率、**錯誤／丟棄計數**（非零逐條攤開——驅動劣化／線材的第一指紋）、MAC OUI 廠商對照。
- Wi-Fi RSSI／頻道／認證（BSS list 中心頻率查表，換不出不猜）、網路卸載狀態、網卡進階屬性、網速測試、網路延遲。

### 安全（韌體安全頁＋防護頁）

- **韌體安全暫存器**：BIOS_CNTL、SMRAMC、ME 狀態（HFSTS1）、IA32_FEATURE_CONTROL、IA32_DEBUG_INTERFACE、SPI 鎖定旗標與保護範圍——每項都下裁決，不利裁決警示色。
- **交叉對帳判決卡**：26 條規則對同一批事實做語義與管線一致性檢查（規則外部化於 Rules/builtin.json），矛盾整列紅字——兩個來源對同一件事說不同的話。
- **平台可信度**：hypervisor 存在位／簽章、VBS／HVCI 狀態、Invariant TSC——MSR 類卡片在此情況下「只能當參考」明說。
- **BYOVD 逐驅動比對**：載入中核心模組對微軟「建議的驅動程式封鎖規則」做檔名＋SHA-256/SHA-1 雙道比對（使用者提供清單 XML，零出網）；命中＝攻擊面事實而非中毒判決。
- **安全鑑識**：Defender 排除清單逐條攤開、事件記錄清除（1102）偵測、非微軟本機信任根（MITM 憑證風險面）、USBSTOR 使用痕跡、驅動簽章稽核（未簽章逐條）。
- **藍色中隊（防護頁）**：六防線即時安全態勢評估（DMA／韌體／CPU／儲存／驅動／攻擊面）＋ETW 即時威脅偵測時間軸。**守護進程已內建本體**（SHA-256 驗證解壓＋ACL 鎖定），可開關——關閉＝只做唯讀態勢評估。
- **深層存取豁免開關**：產生自簽 CA（只放行這一張，不開全機 test-signing）→ 載入 XsRegProbe 白名單唯讀驅動；關閉＝完全移除不留痕跡。

### 系統與軟體層（事實實驗室／軟體面）

Windows Update 歷史、服務盤點（含非系統目錄服務）、事件記錄摘要（7 天嚴重＋錯誤）、稽核政策（LSA 九類別）、機器原則檔指紋、選用功能（Hyper-V／VM 平台／WSL／容器）、核心模組載入清單＋非系統模組 Authenticode、開機參數（核心除錯／測試簽章）、開機計時（Diagnostics-Performance Event 100）、USB 拓撲、攝影機列舉、企業儲存（iSCSI/MPIO/FC 三態分離）。

### 深測中心（38 項 Run Session）

CPU AES/SHA、Load-to-use/ILP/branch 延遲、分支式矩陣、RDRAND/RDSEED、Intel PMU Top-down、核心延遲、核心到核心搬運頻寬、SMT sibling 競爭、cache bandwidth/latency 階梯、lock-scaling、NUMA/TLB/大分頁、DRAM 映射推論、記憶體頻寬、合成 JSON 往返、3D 渲染 D3D11 光柵/紋理、GPU 光柵/紋理/編碼、儲存複合、QD 梯度、耦合 loopback、WASAPI 訊號級、多域 gauntlet、UX 合成負載、統計引擎自我稽核——**原 38 項全量登記，可執行與延後項目都會攤開，不把未跑的說成量測**。只並列原始樣本、可信度與限制，不加權合成單一總分；跨域排名不成立。

### 監控與量測工具

- **感測器**：溫度／時脈／電壓／風扇／負載即時儀表，迷你懸浮視窗與系統匣；超標警示；CSV 匯出；歷史回放。
- **效能**：棋類跑分（perft 數學常數當檢核碼）、算力圖（離線天梯）、幀時間監測、DPC 延遲、執行緒遷移（ETW 四層歸因＋扣掉自己）、L3 未命中與 DRAM 實際流量（已知負載自我驗證）、大頁與位址轉換成本、NPU 檢測。
- **硬核唯讀量測**：SMI 次數與隱形停頓、機器檢查（MCA/WHEA）、核心間延遲矩陣、RDT 快取佔用、電源政策、BIOS 與 ME 微碼、AVX-512 撞牆量測——全部唯讀，取樣前後完整還原計數器，**量不到的項目拒絕出數字**。

### 證據實驗室

- 時間膠囊：全機事實快照（SHA-256 完整性信封、匿名機器識別、敏感值遮蔽）、跨快照逐欄差分、**資產生命週期事件自動分類**（記憶體／處理器／顯卡／儲存的新增移除變更）。
- 原始暫存器快照（.xinraw）：PCI 安全暫存器／ACPI 整表／MSR／SPIBAR／MCHBAR 的原始位元組，竄改拒載。
- HTML 單檔報告（SHA-256 離線核驗）、corpus 貢獻包格式 v1 骨架（只收遮蔽版、身份鍵排除，上傳通路刻意未實作）。

### 工具箱與實用工具

- 工具箱：Windows 內建工具一鍵開啟＋八十餘款第三方硬體工具的**官方**下載捷徑（危險項掛徽章寫明最壞情況；無官方發佈站的不收）。
- 硬體檢測：螢幕壞點、滑鼠按鍵/滾輪/回報率、鍵盤逐鍵/防鬼鍵、喇叭聲道掃頻、動態拖影——純原生輸入事件。
- 一鍵裝機（winget 整合）、系統風扇控制（真實寫入硬體、一鍵還原自動）、CPU 超頻（XTU 橋接：倍頻與電壓同一張目標時脈規劃卡）、垃圾清理、大檔掃描、連接埠占用、Hosts 編輯器、右鍵選單管理、Windows 授權、睡眠與喚醒、DNS 切換、記憶體整理、開機啟動項、運算穩定性壓測、藍屏分析、系統引導修復、螢幕色域、USB 鏈路、PCIe 鏈路、進階驅動分析、作業系統分析。
- 內建瀏覽器（WebView2）與真實終端機（cmd.exe）。

### CLI 與自動化

```
XinSpect.exe --json evidence [--query <查詢>] [--out <檔案>]
```

- 退出碼：0＝全部事實可讀／2＝部分三態／1＝致命。
- `--query` 支援完整查詢語言（七欄位、三運算子，規格見 `docs/spec/query-language.v1.md`）；PowerShell 模組 `XinSpect.psm1`（Get-XinSpectEvidence）。
- 本機 API handler：`GET /api/facts`、`POST /api/query`（loopback 唯讀，與 CLI 同一語法）。

## 公開規格與測試品質

- `docs/spec/snapshot.schema.v1.json` — 時間膠囊 JSON Schema（與實際序列化逐鍵機器對帳）。
- `docs/spec/query-language.v1.md` — 查詢語言規格（解析器新增鍵而文件未更新會紅燈）。
- `docs/spec/METHODOLOGY.md`、`docs/MEASUREMENT-METHODOLOGY.md` — 事實來源契約與量測方法學。
- `docs/spec/LIMITATIONS.md` — 公開限制（設計裁決與能力邊界，全部指向帳本內既有裁決）。
- `docs/EC-RISK-ASSESSMENT.md` — EC 埠存取風險評估（結案：不實作）。
- `docs/PMU-SANDBOX-PLAN.md` — PMU 編程沙箱驗證方案（S1–S5）。
- `docs/DATA-SOVEREIGNTY.md` — 資料主權聲明（事實蒐集零網路 API，機器檢查掃描釘死）。
- `docs/DEPLOYMENT.md` — 部署與企業維運。
- 測試：**3094 個單元測試全綠**；Stryker 突變分數 82%（純解碼器類別庫）；FsCheck property 測試；SMBIOS↔WMI 差分測試；完整迭代帳本 `docs/ITERATIONS.md`（60+ 批次逐輪記錄）。

## 隱私與資料主權

- **事實蒐集零網路呼叫**——機器檢查掃描原始碼釘死；全程式僅有的網路功能（AI 評價、回饋、測速、網路延遲量測）都是使用者主動觸發。
- 資料全部在本機：審計日誌、時間膠囊、原始快照都在使用者自己的路徑；**刪除檔案就是刪除資料**，沒有雲端副本。
- 機器識別是單向雜湊派生；預設遮蔽敏感值，保留必須使用者主動指定。
- 自我遙測預設關閉、匿名、本機存取。

## 從源碼建置

```
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build XinSpect.csproj -c Release
dotnet test Tests/XinSpect.Tests.csproj
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

可選元件：XsRegProbe 白名單唯讀驅動（`XsRegProbe/`，需自行編譯簽章，見 `XsRegProbe/BUILD-給使用者.md`）；XTU 超頻橋接（`Bridge/`，net48，Release 建置自動內嵌）；藍色中隊守護進程（`BlueSquadron/`，Release 建置自動內嵌）。

## 授權

本專案以 [MIT License](LICENSE) 釋出。第三方元件（Intel XTU SDK 等）受其各自授權條款約束，不在本專案 MIT 授權範圍內。

## 作者

By：Xinglanclever
