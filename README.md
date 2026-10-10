[简体中文](README.zh-CN.md) · 繁體中文

# 曦覽 XinSpect

一款免費開源、運行於 Windows 的原生硬體驗機、監控與安全稽核工具。以單一執行檔發佈，免安裝；對硬體與系統的讀取以唯讀為原則，少數涉及寫入的功能均設有同意閘門並明確標註風險。本程式不收集、不上傳任何使用者資料。

![版本](https://img.shields.io/badge/version-2.54-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![測試](https://img.shields.io/badge/tests-3679%20passed-3FB950)
![突變分數](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![授權](https://img.shields.io/badge/license-MIT-green)

## 一、專案定位

曦覽（XinSpect）以 WPF（.NET 10）撰寫、MVVM 架構，整合 LibreHardwareMonitor 感測引擎、Intel XTU 超頻橋接、NVIDIA NVML／NVAPI 顯卡控制、WebView2 內建瀏覽器，以及本專案自寫的 WinRing0 事實讀取層與 XsRegProbe 白名單唯讀驅動。名稱中的「Spect」取自拉丁語的檢視者：本工具不替使用者的硬體下結論，而是把三個來源的陳述並列攤開——

1. **硬體自己說的**：MSR（型號特定暫存器）、PCI 設定空間、SMBUS 上的 SPD／TSOD、ATA SMART 與 NVMe 健康紀錄、Super I/O 暫存器、CMOS；
2. **韌體說的**：ACPI 表（MCFG、HEST、BERT、SLIT、CEDT、HPET、FADT）、SMBIOS 結構、UEFI 變數；
3. **作業系統說的**：WMI（CIM 類別與 MSFT 進階類別）、登錄檔、事件記錄、效能計數器、ETW 追蹤、X509 憑證儲存區。

三個來源對同一件事的說法可能不同。讀得到就列出數值與來源欄位；讀不到就標示原因（平台不支援、權限不足、讀取失敗）；當兩個來源對同一件事有不同的說法，交叉對帳引擎會把矛盾列為紅字並具名列出雙方。判讀留給使用者。

適用場景：

- **二手電腦買賣前的驗機**：機箱開啟偵測（SMBIOS 入侵事件）、HPA 隱藏容量（翻新碟與容量竄改的直接證據）、SMART 現正低於門檻判定、微碼修訂交叉核對、驅動簽章稽核、Defender 排除清單稽核、USBSTOR 使用痕跡、非微軟根憑證稽核、假容量寫入驗證；
- **組裝與升級後的硬體確認**：SPD 直讀與插槽配置、記憶體通道、PCIe 鏈路協商速度、USB 鏈路速度、顯示鏈路色彩編碼、ReBAR 生效狀態；
- **系統不穩定時的歸因**：瓶頸診斷、效能天花板（溫度牆／功耗牆／電流牆歸因）、隱形停頓（SMI）、DPC 延遲肇事驅動、執行緒遷移、機器檢查紀錄；
- **安全狀態稽核**：韌體安全暫存器（BIOS_CNTL、SMRAMC）、藍色中隊六防線態勢、BYOVD 封鎖清單比對、深層存取豁免開關；
- **長期效能監控**：即時感測、歷史回放、CSV 匯出。

## 二、設計原則

以下原則不是宣言，每一條都有單元測試或機器檢查守著。

### 2.1 三態標示

每筆事實帶 `availability` 欄位，取值五種：`Present`（讀到）、`NotSupported`（平台不提供）、`InsufficientPrivilege`（權限不足）、`ReadError`（讀取失敗）、`NotApplicable`（本機無此硬體）。讀取失敗時介面顯示原因文字，數值欄位留空。

程式中不存在以 0、0xFF、典型值或上一次讀值填補缺口的路徑。三個例子說明這條規則的落實方式：Super I/O 風扇轉速的計數值 0 與 0xFFFF 在 ITE 規格中是無效值，程式顯示「無效計數（停轉或未接）」而不是換算出 0 RPM；登錄檔微碼修訂的兩個 DWORD 皆非零時屬於佈局歧義，程式顯示「歧義——不解碼」並附上原始 hex 供人工稽核；NVMe 的 WCTEMP 欄位為 0 時表示控制器未提供，程式標示「未提供」而不是拿 0 去做比較。

### 2.2 來源可稽核

每筆事實的 `source` 欄位記錄資料出處：暫存器位址與位元位置（例如「MSR 0x8B bits[63:32]，逐實體核綁定讀取」）、WMI 類別名稱、ioctl 名稱、API 名稱。暫存器解碼器的方法必須附加 `SpecRef` 屬性，內容為文件名稱、章節、暫存器與位元位置；`SpecRefRegistry` 以反射逐一檢查已註冊解碼器的每個公開方法，缺引用即測試失敗。目前已註冊並受檢查的解碼器涵蓋 SPI 快閃、晶片組安全、平台安全 MSR、PCIe AER、ACPI 表、CMOS、TSOD、Super I/O、平台可信度、PCI 知識庫、PCI BAR、Super I/O 知識庫、Wi-Fi BSS、螢幕連接介面、SuperIO HWM、CPU 拓撲、SLIT、PMU。

### 2.3 非自造驗證

量測類功能在輸出數字之前先驗證量測本身：

- 棋類跑分以 perft 葉節點數為檢核碼。perft 是數學常數，算出別的數字代表這台機器算錯了，而不是比較慢；
- 記憶體頻寬換算前以已知大小的負載自我驗證，對不上時只輸出原始計數器值，不換算成頻寬；
- L3 未命中計數同樣先自我驗證再換算；
- PMU 編程驗證逐輪確認寫入讀回一致與還原完整；
- 統計引擎以已知答案合成樣本集逐案例驗證分類行為（緊密樣本應分類為 High、發散應為 Low、NaN 不得滲漏），不符整場拒收。

### 2.4 突變測試

純解碼器抽成獨立類別庫 `XinSpect.Decoders`（無 WPF 相依、無特權呼叫），Stryker 突變測試對其執行，分數 82%。突變測試在開發過程中抓到過真實缺陷：`CpuGeneration.DecodeSignature` 的文件聲稱處理 extended family 進位，實作卻漏做——這個缺陷對所有 Family 6 的消費級處理器沒有影響，但在 base family 為 0xF 的場合會給出錯誤的家族判定。知識表資料檔（SuperIoKnowledge、PciKnowledge）刻意排除在突變範圍外：對查表資料逐條做字串斷言等同快照重複，保護價值低於維護成本。

### 2.5 查不到不等於沒有

查詢語言與本機 API 對「存在但讀不到」的條目照樣返回匹配並附原因；語法錯誤返回 400 與修正指引。把查錯偽裝成空結果、把讀不到偽裝成沒有，都屬於本專案定義的誠實違反。

## 三、系統架構

| 元件 | 形態 | 職責 | 權限 |
|---|---|---|---|
| 曦覽主程式 | WPF（.NET 10，單檔發佈） | 介面、事實收集、對帳、報告、深測、超頻 | 多數功能不需要；MSR／SMART／Security log 需系統管理員 |
| WinRing0 | 既有核心驅動（反射載入 LibreHardwareMonitor 的 Ring0 模組） | MSR／PCI 設定空間／實體記憶體 MMIO 讀取主力 | 系統管理員 |
| XsRegProbe | 本專案自寫的白名單唯讀驅動（C 源碼在 `XsRegProbe/`） | 允許清單內的 MSR／MMIO 讀取備援；允許清單外一律拒絕；進程退出即卸載 | 系統管理員；.sys 需使用者自行編譯簽章 |
| Intel XTU 橋接 | net48 獨立進程（Release 建置自動內嵌） | 承載 XTU SDK（.NET 10 已移除的 WCF 舊版堆疊）供 CPU 超頻 | 系統管理員 |
| 藍色中隊守護進程 | .NET 10 console（Release 建置自動內嵌） | ETW 即時威脅偵測、驅動基線比對、以 stdin/stdout JSON IPC 與主程式通訊 | 使用者權限 |

**內嵌機制**：Release 建置時，MSBuild Target `BuildAndEmbedXtuBridge` 與 `BuildAndEmbedBlueSquadronBridge` 分別發佈兩個橋接程式為單檔並內嵌為資源。執行期首次使用時解壓至 `%LOCALAPPDATA%\XinSpect\` 下的專屬目錄：先以 SHA-256 對內嵌正本驗證、目錄 ACL 收緊為 SYSTEM＋Administrators（停用繼承），驗證不過即拒絕執行。這道防線的理由：程式以高權限執行而解壓目錄在使用者設定檔內，若僅以檔案大小判斷是否沿用既有副本，同一使用者的中完整性程序可預先植入同長度的惡意執行檔等待被高權限執行。Debug 建置不內嵌，改以逐層向上搜尋專案輸出。

**事實模型**：全部事實統一為 `HardwareFact`（key、category、name、value、numericValue、unit、source、trust、availability、measuredAtUtc）。`trust` 欄位標示可信層級：`Measured`（本工具量到）、`Reported`（系統或韌體自述）、`Derived`（由其他事實推導）、`Unknown`（不可用）。時間膠囊、查詢語言、本機 API、HTML 報告全部消費同一模型。

## 四、下載與系統需求

| 檔案 | 大小 | 用途 |
|---|---|---|
| [XinSpect.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.54/XinSpect.exe) | 31,286,571 bytes | 主程式。藍色中隊守護進程已內建 |
| [BlueSquadronBridge.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.54/BlueSquadronBridge.exe) | 6,502,948 bytes | 獨立守護進程。僅在需要脫離主程式單獨運行防護時使用 |

> **上表的位元組數為本版（v2.54）實際發佈的檔案大小**；請以 Release 頁面列出的檔案為準。

系統需求：Windows 10 1903 或更新、Windows 11 x64；.NET 10 Desktop Runtime（自包含發佈則免裝）。顯示卡深測需要 D3D11 相容裝置；MSR 讀取、SMART ioctl、Security 事件記錄需以系統管理員執行——沒有權限時相關項目標示「權限不足」，程式不會假裝成功，也不會靜默降級。

## 五、快速上手

1. 以系統管理員身分執行 `XinSpect.exe`（不提權也可以執行，多數 usermode 功能照常，特權功能會如實標示）。
2. 左側導覽的「**韌體安全**」頁：查看 BIOS 寫入保護、SMRAM 鎖定、交叉對帳判決卡；啟用「深層存取」可讓 SPI 快閃與 PCIe AER 事實翻成真值。
3. 「**儲存裝置**」頁：逐碟 SMART 屬性與 failing-now 判定、NVMe 健康與 WCTEMP、HPA 隱藏容量。
4. 「**深測中心**」：設定儲存根後按 Quick 或 Full 執行 38 項量測；結果為原始樣本並列，不含加權總分。
5. 「**防護**」頁：藍色中隊態勢評估與即時威脅時間軸；守護進程開關在此頁。
6. 「**證據實驗室**」：建立時間膠囊（快照），之後可逐欄差分、匯出 HTML 報告或原始暫存器快照。

## 六、功能詳述

以下依主介面左側導覽的群組與頁面順序說明。每一節先列資料來源，再說明顯示內容與邊界（不做什麼、為什麼）。

### 6.1 總覽群組

**我的電腦（簡易模式首頁）**。健康度、溫度、硬體清單與故障排查。為不熟悉硬體術語的使用者提供單頁結論；進階模式下不列在側邊欄，命令面板仍可搜尋。

**總覽**。整機規格與即時狀態一覽：CPU（型號、核心執行緒、時脈、負載）、主機板（廠商徽章與型號）、記憶體（容量、通道、速率）、顯示卡（型號、顯存）、儲存（容量與活動）。CPU 型號旁展示官方 logo；主機板廠商以徽章呈現。

記憶體插槽配置圖依 SMBIOS 的實體排列繪製：實心代表有模組（標容量與速率）、虛線代表空槽。通道字母僅在韌體插槽命名可辨識時標示（例如 `ChannelA-DIMM0`）；只有 `DIMM0/DIMM1` 命名的板子會標明「無法辨識通道」。不做推論的理由：猜錯通道會導致使用者把記憶體插到錯誤的插槽。

**瓶頸診斷**。把散在各頁的讀值合起來回答「現在卡住這台機器的是什麼」。指標涵蓋：溫度牆、功耗牆、單執行緒、記憶體、儲存、顯示卡、驅動 DPC、電源政策、MCA 平台事件，依「該先看哪一條」排序。每條判斷附產生它的原始數字與對應的深入量測頁面連結。未被量測涵蓋的項目列於「還沒納入判斷的部分」——沒量到的資料不會被當成沒問題。

**AI 評價**。接 Ollama 或任意 OpenAI 相容端點，把本機硬體摘要交給語言模型生成評語。提示詞可自訂；API 位址與金鑰由使用者設定。此為全程式少數會產生網路流量的功能之一，僅在使用者主動呼叫時發生。

### 6.2 處理器

資料來源：CPUID（usermode）、MSR（經 WinRing0 逐實體核綁定讀取）、登錄檔。

- **規格與拓撲**：家族／型號／stepping（含 extended family 進位——此進位曾因實作漏做由突變測試發現並修正）；指令集支援（含 AVX-512、AES-NI 等）；快取階層；die 拓撲（CPUID leaf 0x1F 的 Module/Tile/Die 層級；leaf 未支援時標 NotSupported，不退回 leaf 0xB 推測——0xB 沒有 die 層級，推測出來的「單 die」是編造）。
- **微碼修訂（雙來源交叉對帳）**：CPU 側讀 MSR 0x8B 高 32 位，逐實體核綁定讀取，各核不一致時列出逐核清單而不取單核值冒充全機；Windows 側讀登錄檔 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision`，實測存在 8 位元組與 4 位元組兩種佈局，兩者都支援；雙 DWORD 皆非零的歧義標示「不解碼」並附原始 hex。兩來源的對照結果由交叉對帳規則裁決。
- **TjMax**：MSR 0x1A2 bits[23:16]，單位 °C。
- **溫度／頻率真相**：MPERF/APERF 比值與 Turbo 階梯量測，取樣前後完整還原計數器。
- **TME／SGX 記憶體加密**：TME 以 CPUID leaf 7 ECX bit25 判斷支援、MSR 0x982（TME_ACTIVATE）bits[3:0] 判斷啟用、bits[7:4] 解演算法（0＝AES-XTS-128、1＝AES-XTS-256、其餘未收錄如實標）；SGX 以 leaf 7 ECX bit30 判斷。平台不支援標 NotSupported。
- **C-state 駐留**：Package 層級的 C2（0x60D）、C3（0x3FC）、C6（0x3F9）、C7（0x3FA）累計駐留微秒。MSR 讀取失敗或平台未實作時逐項三態。
- **PMU**：能力探索（CPUID leaf 0xA：PMU 版本、每邏輯 CPU 通用計數器數量與位寬、固定計數器數量與位寬）為唯讀且已出貨；編程驗證為同意閘門功能——寫入範圍僅限 IA32_FIXED_CTR_CTRL（0x38D）的固定計數器 0 使能位、以 OR 併入原值（不覆寫其他計數器的既有設定）、不觸及 PMI 位元（不使能中斷即無打斷風暴）；每輪「啟用→讀回一致→已知工作量→計數器活動確認→還原原值→讀回確認」，3 輪聚合，任何路徑（含例外）以 finally 還原。介面與結果標註「多輪測試・不保證可用」：與 wpr 等參考工具的對照（沙箱方案 S5）尚未執行。
- **NPU 檢測**：偵測 Intel／AMD／Qualcomm NPU 並回報驅動與估計算力。

**效能天花板**（同群組的深度頁）回答「為什麼跑不到該有的頻率」：TCC 節流溫度、PL1/PL2 功耗牆與時間窗、電流限制（IccMax）與供電警報、Turbo 倍頻限制表全部直接讀自 MSR——不是規格書數字；限制原因暫存器（MSR 0x1FC）為黏滯紀錄位，記錄自開機以來撞過的牆；再加使用者親自觸發的逐窗撞牆量測（基線／整數／AVX2／AVX-512）以 APERF/MPERF 量有效倍頻與作用中核心數，最後歸因成一句判決：溫度牆、功耗牆、電流牆、供電模組過熱、自主 P-state、多核渦輪上限，或「缺口不在硬體」。全程唯讀，不寫入也不清除任何黏滯位；能量計未通過自我驗證時只給原始計數、不換算成瓦。

### 6.3 記憶體

資料來源：SMBUS（白名單位址直讀）、SMBIOS、WMI、ACPI SLIT、Windows 記憶體 API。

- **SPD 直讀**：PCH SMBus 與處理器 iMC SMBus 逐條嘗試，讀到的每筆標明來源匯流排；解出製造商、序號、模組型號、標稱與實際速率、主要與次要時序（tCL/tRCD/tRP/tRAS 及子時序）。讀不到的匯流排記原因，不把空插槽當故障。
- **TSOD 溫度感測器**：TSE2004 相容溫度（bits[15:4] 二補數 1/16°C），白名單唯讀位址 0x18–0x1F。
- **ECC 現況**、插槽配置與通道辨識（辨識不出就直說，見 6.1）。
- **Rowhammer**：程式提供風險聲明與壓力探測，不做正規施測。壓力探測在自擁有的連續緩衝區內以兩個熱點高頻讀寫，xorshift 樣本（chunkIndex 混入種子）逐位元組驗證，熱點偏移排除於驗證之外；需要使用者明確同意（UI 勾選＋服務層對未同意呼叫直接丟例外）；介面與結果均標註「未經過校驗」——usermode 無法執行 clflush，此探測不保證觸發 Rowhammer，結果不構成記憶體可靠性的結論。多輪模式（10 輪獨立探測聚合）同樣標註：多輪零翻轉不表示具備 Rowhammer 抗性。
- **NUMA**：拓撲（GetNumaHighestNodeNumber／GetNumaNodeProcessorMaskEx；刻意不走 GetLogicalProcessorInformationEx 的變長結構解析——GROUP_AFFINITY 偏移隨 Windows 版本演進，解析錯位會把遮罩當事實）、節點距離矩陣（ACPI SLIT：宣告節點數與實際資料不符時拒解）、TLB 與大分頁成本（同一份亂序指標鏈在 4 KB 頁與 2 MB 大頁各跑一次，唯一變數是頁面大小；「配不出大頁」與「大頁沒有效益」分開陳述）、跨 NUMA 對照。

### 6.4 主機板

資料來源：SMBIOS（MSSmbs_RawSMBiosTables）、ACPI（GetSystemFirmwareTable）、Super I/O 設定埠 0x2E/0x4E、PCI 設定空間（WinRing0）、CMOS 埠。

- **機箱開啟偵測**：SMBIOS Type 3（System Enclosure）offset 12 的 Security Status。值 5 表示韌體記錄了機殼開啟事件——這是拆機的韌體級證據，介面以警示色呈現並建議追問來源。offset 5 的機箱類型（Tower／桌面／Rack Mount 等）一併解出。
- **BIOS／晶片組**：版本、日期、廠商。
- **Super I/O**：0x2E/0x4E 設定埠以兩種進入序列嘗試，唯讀晶片 ID 與廠商 ID（名稱對照取自 coreboot superiotool 的 ite.c/nuvoton.c，出處標明），設定模式在任何路徑（含例外）以 finally 退出——把晶片留在設定模式是系統風險。HWM 感測器：選 LDN 4（環境控制器）取基址後，以 base+5（index）／base+6（data）讀取——風扇 RPM（ITE 公式 1,350,000÷(divisor×count)，count 0/0xFFFF 為無效，不回 0 RPM）、溫度（8-bit 二補數）、電壓（LSB 16 mV，標明「未經主機板校準」——晶片端讀值不等於實際電壓）。
- **PCI Bus 0 盤點**：三十二槽掃描，多功能位元決定 function 掃描深度；PCI-SIG 類別碼知識庫轉角色名稱（未收錄標「未收錄」）；每裝置列出 BAR 位址與類型。BAR 大小需寫入探測（寫全 F 讀遮罩），本工具對 PCI 設定空間唯讀，故不出大小。
- **ReBAR 實況**：查 Windows 實際指派給裝置的記憶體範圍——那是生效的視窗而非能力宣稱值。刻意不走 PCI 設定空間（ReBAR 能力結構在 0x100 之後，傳統 CF8/CFC 機制到不了），也不需要驅動或管理員權限。覆蓋率只當事實列出：BAR 尺寸是 2 的次方，12 GB 的卡最大只拿得到 8 GB 視窗，屬規格使然，不以此扣分。
- **SPI 快閃稽核（三層）**：第一層暫存器旗標——HSFSTS 的 FLOCKDN/WRSDIS/FDOPSS（只報位元不判決）、FRAP 的 BIOS 區寫入權限、PR0–4 保護範圍；第二層 FREG 地圖推導快閃總大小與 BIOS 區位置；第三層 BIOS 區 SHA-256（單次大讀優先，失敗退 4 KB 分塊，標明「雜湊＝可讀面」）；可與使用者提供的參考映像逐 4 KB 塊比對（大小不符誠實拒比）。全部需驅動，未載入時帶已知 SPIBAR 位址三態。
- **ACPI 表列**：表頭校驗和驗證；MCFG（PCIe ECAM 基址與匯流排範圍）、HEST（硬體錯誤源表）、BERT（開機錯誤記錄區）、SLIT（節點距離矩陣）、CEDT（CXL 固定記憶體窗口，CFMWS 逐欄：BaseHPA／WindowSize／InterleaveWays）、HPET（計時器存在）、FADT（PM timer 區塊位址）。平台沒有對應表時標 NotApplicable——無此硬體不是錯誤。
- **CMOS/RTC**：VRT 電池電壓（0x0D）、即時時鐘（BCD 與 12/24 小時解碼）、世紀位元組、PC-AT 校驗和；位址 0x70 的 bit7 保留 NMI，絕不寫 0x71。
- **UEFI 開機設定**：Secure Boot 四態、AuditMode、DeployedMode、SetupMode（GetFirmwareEnvironmentVariableEx，SeSystemEnvironmentPrivilege 啟用含 ERROR_NOT_ALL_ASSIGNED 檢查）。
- **POST 代碼**：I/O 0x80 讀取，0xFF 與 0x00 不解碼。

### 6.5 儲存裝置

資料來源：SMART_RCV_DRIVE_DATA ioctl（disk.sys 代理——刻意不用 ATA_PASS-THROUGH，曾實測卡 IRP；由磁碟類別驅動代理的 SMART 通路久經驗證且無弄掛使用者磁碟的風險）、NVMe Storage Query Property、WMI Win32_DiskDrive。

- **SMART 屬性與門檻（failing-now）**：READ DATA（features 0xD0）取 30 筆屬性（ID、現值、最差、原始六位元組）；READ THRESHOLDS（features 0xD1）取門檻表。「現值 ≤ 門檻且門檻非 0」判定為現正低於門檻（failing now），逐項攤開並標注「現值 X ≤ 門檻 Y」。門檻 0 依 SMART 規範為無門檻，不參與評比。重新配置磁區（0x05）、待對映磁區（0xC5）、無法修正磁區（0xC6）另有健康裁決。
- **NVMe**：健康紀錄（log page 0x02）全解——合成溫度、可用備用、已用壽命百分比、資料單位讀寫量；WCTEMP 警告（Identify Controller offset 0x14A 的 u16 對照合成溫度，達標即警告；0＝未提供如實標）；錯誤紀錄（log page 0x01）；電源狀態表（各階功耗與進入／離開延遲）對實測「刻意閒置 N 毫秒後第一筆 4K 讀取要多久」——閒置後第一筆為什麼慢，兩者同一量級才敢歸因於省電狀態。
- **HPA 隱藏容量**：ATA IDENTIFY 的最大 LBA（words 100–103）對照 Win32_DiskDrive.Size——OS 可見少於韌體聲明即 HPA 作用中，換算隱藏磁區數與 GB。這是翻新碟與容量竄改的直接證據。DCO 需廠商私有命令（無公開 usermode 通路），標 NotSupported 並說明原因。
- **磁碟表面掃描**：循序讀取逐塊（預設 1 MB，上限可調）量延遲，超過 100 ms 標慢、讀取失敗標錯誤。SMART 是韌體的自述，這是程式自己讀到的——兩者互補。以 `\\.\C:` 開啟邏輯卷不需管理員權限，僅能讀不能寫。
- **假容量寫入驗證**（H2testw 式）：在目標磁碟以可重現樣本（xorshift，chunkIndex 混入種子）寫入至指定上限或寫滿，FlushToDisk 後讀回逐位元組驗證，結束刪除暫存檔。「寫不進去／讀回不一致」是假容量卡（標 512GB 實為 8GB 的刷板卡）與劣化碟的直接證據。需要明確同意——寫入量可觀且可能加劇瀕死媒體損耗，介面與說明均標註風險。此功能未接入任何自動流程，僅在明確呼叫時執行。
- 通電時數與機齡推估（SMART Power-On Hours＋出廠日期啟發式）、QD 梯度效能曲線、容量／韌體／序號、NVMe 識別資料。

### 6.6 顯示卡

資料來源：NVML（NVIDIA Management Library）、NVAPI、D3DKMT、EDID、登錄檔。

- **NVML**：溫度、頻率、功耗（毫瓦）、風扇、溫度閾值、**退休頁**（nvmlDeviceGetRetiredPages_v2，single-bit 與 double-bit ECC 合計——NAND 瑕疵退休計數；本測試機實測該卡不支援退休頁報告，標 ReadError 並寫明原因）。
- **GPU 深測**：光柵填充率（全螢幕三角形＋取樣）、紋理取樣（單／八取樣）、H.264 編碼（Media Foundation SinkWriter）、計算管線（D3D11 buffer SRV）。所有 GPU 輸出經 readback 對 CPU 參考值逐位元組比對——防止全零輸出被誤判為有效量測。
- **HDR 能力**：EDID CTA-861 HDR Static Metadata（亮度範圍與支援旗標）＋DisplayConfig 查 Windows HDR 開關；讀不到標未知不猜。
- **顯示鏈路真相**：連接技術、實際像素時鐘、色彩編碼（RGB/YCbCr 4:4:4/4:2:2/4:2:0）、位元深度、所需影像資料率。頻寬不足時驅動會自行降色度，Windows 設定介面照樣寫著 4K144——這裡列出的是實況。
- **TDR 設定**：TdrLevel/TdrDelay/TdrDpcDelay 登錄檔值；未設定時標明 Windows 預設值（Level 3、Delay 2 秒），不把「未設定」說成「已設定」。
- **超頻**：NVML 功耗／風扇／溫度監控＋NVAPI 時脈偏移；鐵律為快照→回讀驗證→看門狗→自動還原。

### 6.7 網路

資料來源：WMI（MSFT_NetAdapterStatistics、Win32_NetworkAdapter、MSFT_NetAdapterChecksumOffload/RSS）、wlanapi、登錄檔。

- **介面統計**：錯誤與丟棄計數非零的介面逐條攤開（RX/TX 錯誤、RX/TX 丟棄）——驅動劣化、線材、交換器埠問題的第一指紋。全部歸零明確標示「乾淨」，與「讀不到」分開。
- **MAC OUI 對照**：IEEE 登記的知名前綴子集（Intel、Realtek、ASUS、GIGABYTE、Microsoft Hyper-V 虛擬、VMware 虛擬、Apple、NVIDIA 等），未收錄標「未收錄」、格式壞標「無法解析」。
- **Wi-Fi**：RSSI（附解讀文字：-30 極佳到 -85 微弱，中間值不硬貼等級）、頻道（WLAN_BSS_ENTRY 的 ulChCenterFrequency 依 IEEE 802.11 Annex E 換算——2.4 GHz 等差、ch14 特例、4.9/5/6 GHz 各自公式，頻率在等差之外回 null 不猜）、認證類型（WPA2/WPA3 Enterprise/Personal 等）、BSSID。介面未連線時如實列示「未連線」，不與「無介面」混報。
- 網路卸載實際啟用狀態（Checksum Offload、RSS——防禦式屬性讀取，屬性未提供標「屬性未提供」不冒充停用）、網卡進階屬性、網速測試、網路延遲量測。

### 6.8 安全（韌體安全頁＋防護頁）

資料來源：MSR、PCI 設定空間、UEFI 變數、WMI（Win32_DeviceGuard、Win32_Tpm）、X509Store、事件記錄、登錄檔、使用者提供的清單檔。

**韌體安全暫存器**（逐項下裁決，不利裁決以警示色呈現）：

- BIOS_CNTL（0xDC）：SMM_BWP、BLE、WE——BIOS 寫入保護綜合裁決；
- SMRAMC（0x88）：D_LCK、D_OPEN——SMRAM 鎖定狀態；鎖定下 D_OPEN=1 的非法組合由對帳規則抓出；
- ME 狀態（HFSTS1）：先驗 PCI 0:16.0 為 Intel HECI 才解讀；
- IA32_FEATURE_CONTROL（0x3A）：Lock 與 VMX 位；
- IA32_DEBUG_INTERFACE（0xC80）：ENABLE／LOCK／DEBUG_OCCURRED（鑑識線索：本機曾被除錯器附著）；
- SPI 旗標與區域權限（見 6.4）。

**交叉對帳判決卡**：26 條規則（外部化於 `Rules/builtin.json`，每條附 SpecRef 與誤報條件，可直接分享）對同一批事實做語義與管線一致性檢查——硬體語義族（微碼、SMRAM、UEFI 狀態機：SB=1 而 SetupMode 金鑰未部署等）、管線一致性族（BIOS_CNTL↔寫入面、FRAP↔暴露面、SPI↔MMIO 後端、MSR↔後端、微碼 Windows↔CPU）、來源交叉族（Secure Boot 雙來源、UEFI 變數 vs 登錄檔平台）。判決三種：一致（綠）／矛盾（紅，具名列出雙方）／無法驗證（灰，任一輸入缺席）。實機曾抓到真矛盾：SMM_BWP=1 但 SMRAM 鎖定交叉不符。

**平台可信度**：hypervisor 存在位（CPUID 1 ECX bit31）與簽章（0x40000000，僅在位 31 為 1 時讀取——不支援的葉會回最大標準葉的內容並解出假廠商）、VBS/HVCI（Win32_DeviceGuard：「已設定」≠「執行中」，只有 Running 才生效）、核心程式碼完整性選項（NtQuerySystemInformation 103）、Invariant TSC。VBS 執行中時明確告知：Windows 本身是 Hyper-V 上的一個分割區，MSR 可能被攔截、遮罩或回虛擬值，MSR 類卡片只能當參考。

**BYOVD 逐驅動比對**：載入中核心模組對微軟「建議的驅動程式封鎖規則」比對。清單 XML 由使用者提供（自微軟文件下載後放入程式目錄 `Rules\byovd-blocklist.xml` 或以 CLI 指定），零網路存取；比對兩道——檔名（不分大小寫）與 SHA-256/SHA-1（對實檔計算，雜湊快取）。命中列出模組路徑與規則依據；語意是「在微軟建議封鎖清單上的攻擊面事實」，不是中毒判決——這些驅動多半是廠商正常工具（超頻、燈效、診斷），問題在於其任意讀寫能力可被濫用。真正的阻擋要靠 Windows 內建的弱點驅動程式封鎖清單。

**安全鑑識**：

- Defender 排除清單（MSFT_MpPreference 的 ExclusionPath/Process/Extension）逐條攤開——排除就是「掃毒永遠不看這裡」；零排除明確標示「掃毒涵蓋完整」；
- 事件記錄清除偵測（Security log Event 1102）：歷史清除次數與最近一次時間；Security log 需提權，權限不足與查詢失敗分開標示；
- 非微軟本機信任根：X509Store（Root／LocalMachine）中主體不含 Microsoft 的憑證——Superfish 一類 MITM／監控憑證的風險面，列出主體與有效期供判讀，不下中毒結論；
- USBSTOR 使用痕跡：`Enum\USBSTOR` 下的裝置安裝記錄（含早已拔除的）；
- 驅動簽章稽核：Win32_PnPSignedDriver 的 IsSigned，未簽章逐條。

**藍色中隊（防護頁）**：六防線即時安全態勢評估——DMA 與記憶體保護、韌體與啟動鏈、CPU 緩解與記憶體防護、儲存與資料、驅動與對抗、系統攻擊面，每防線有分數、嚴重度與摘要；加強建議逐條列出。ETW 即時威脅偵測時間軸（驅動載入、可疑進程）。守護進程已內建主程式（見系統架構節），安全頁滑動開關控制啟停並記入設定檔；關閉時唯讀態勢評估照常。

**深層存取豁免開關**：產生自簽 CA（RSA-4096、CA=TRUE，存於 ACL 限定的 ProgramData 路徑）→ 裝入 Root＋TrustedPublisher（只放行這一張，不開全機 test-signing）→ 安裝並啟動 XsRegProbe 服務。關閉＝停止並刪除服務、只移除自己 CA 的 thumbprint（找不到憑證檔就不掃庫誤刪）。非提權時不做任何變更。啟用後 SPI 快閃、PCIe AER 等驅動相依事實免重啟翻成真值。

### 6.9 系統與軟體層（事實實驗室的軟體面）

資料來源：WMI、登錄檔、事件記錄、LSA、COM（WUA）、psapi。

- **Windows Update 歷史**：WUA COM（Microsoft.Update.Session）——最新一筆（標題＋安裝日期）、總筆數、近 30 天安裝數、失敗／中止計數含最近一次標題；無日期不猜，空歷史是「0 筆」不是讀不到；COM 不可用（服務未啟動）三態。
- **服務盤點**：Win32_Service——總數、執行中、自動啟動、停用、**非系統目錄服務**（執行檔路徑不在 \Windows\ 下，引號感知解析，例舉前三名；這是第三方常駐面，數量本身不下安全結論）。
- **事件記錄摘要**：System log 反向走訪 7 天內嚴重＋錯誤，最常見「來源 事件ID×次數」前 3；讀到 7 天外即停不整表掃；匯出 canonical JSON。
- **稽核政策**：LSA LsaQueryInformationPolicy（PolicyAuditEventsInformation，唯讀）——稽核總開關與九類別等級逐類描述（0 未設定不列、規範外等級如實標「等級 N」）；x64 結構手算偏移。
- **機器原則檔**：Registry.pol 的存在與最後寫入時間指紋——只指紋不解析二進位；無檔標 NotSupported（可能從未被網域或本機群組原則下過設定），不是錯誤。
- **選用功能**：Win32_OptionalFeature——Hyper-V Hypervisor／虛擬機器平台／WSL／容器四目標的啟用／停用／不存在（照抄系統 InstallState 口徑）；**WMI 沒回報的功能標「未回報」，不等於停用**。
- **核心模組載入清單**：psapi EnumDeviceDrivers——\Windows\ 下模組只計數（簽章面由驅動稽核的 Win32_PnPSignedDriver 涵蓋）；**非系統目錄的載入模組逐檔 wintrust Authenticode（DRIVER_ACTION_VERIFY）**——未通過帶原始 NTSTATUS 碼、檔案不存在等無法驗證者如實列名，通過者只計數。
- **開機參數**：SystemStartOptions 原樣解析——核心除錯（DEBUG／DEBUGPORT）、測試簽章（TESTSIGNING）；沒有關鍵字＝「未啟用」（Present 的沒有），整個讀不到才是 ReadError；原始字串全文附上可稽核。
- **開機計時**：Diagnostics-Performance Event 100 的 BootTime（毫秒，Windows 自己量的，只解讀不評級）；最近開機＝Win32_OperatingSystem.LastBootUpTime。事件缺席＝NotSupported、查詢失敗＝ReadError，兩種「沒有」分得清楚。
- **USB 拓撲**：Win32_USBControllerDevice 相依對——控制器數、裝置數、最忙碌控制器（供電與頻寬衝突排查指紋）；WMI 相依對只有一層，更深 hub 樹不猜。
- **攝影機列舉**：PNPClass Camera/Image 逐台名稱與狀態照抄系統口徑——「存在但狀態 Error」與「不存在」是兩回事。
- **企業儲存**：iSCSI（MSiSCSI 服務狀態）、MPIO（服務存在與否）、FC HBA（WMI root\wmi）、NVMe-oF（沒有公開偵測 API，標「偵測路徑未實作」）——無此硬體 NotApplicable、偵測路徑不存在 NotSupported，兩者都不是錯誤。

### 6.10 交叉對帳引擎

26 條規則分三族：

- **硬體語義族**：微碼一致性（Windows 說的 vs CPU 說的）、SMRAM 鎖定與 D_OPEN 的非法組合、UEFI 狀態機（Secure Boot=1 而 SetupMode 金鑰未部署＝狀態機警訊；Audit×Deployed 互斥）；
- **管線一致性族**：BIOS_CNTL↔BIOS 寫入面、FRAP↔BIOS 暴露面、SPI 控制器↔MMIO 後端、MSR↔MSR 後端、MCHBAR 基底↔主機橋盤點、TjMax↔MSR 後端、HVCI↔環境裁決、SPI 雜湊↔地圖、雜湊↔MMIO 後端、PCIe AER 掃描↔ECAM；
- **來源交叉族**：Secure Boot 雙來源（UEFI 變數 vs 登錄檔）、UEFI 變數存在 vs 登錄檔否定。

方法學備註：涉及「A 缺席」且兩側都可能缺席時，單條規則無法兩向涵蓋（引擎守衛把缺席鍵轉 Unverifiable）——拆成兩條方向性規則，各宣告保證 Present 的一側。管線規則的「後端事實缺席＝矛盾條件」者刻意不列輸入鍵，規則內 TryLookup 自行裁決。規則以 JSON 外部化，每條附 SpecRef 與誤報條件；行為等價定義為 Relation 層逐條逐情境一致（68 情境），由測試釘住。

## 七、深測中心（38 項 Run Session）

深測中心是一場 Run Session 的集中量測介面：選擇 Quick 或 Full 檔案、指定儲存根與暫存預算，逐項執行並即時回報進度。**閱讀界線**先講清楚：只並列各項量測的原始樣本、可信度（High/Medium/Low/Insufficient）與限制，不加權合成單一總分；跨域、跨數據體的排名不成立（GPU 的 200 分和磁碟的 200 分沒有共同單位）。混合不同量測配置的樣本會被混池判定攔下——梯子型指標（STREAM kernel×threads、快取工作集、磁碟 QD ladder）各點是不同配置，平均無意義，混池時改逐點列。38 項全量登記，可執行與延後項目都會攤開，不把未跑的說成量測。

測項全表（依目錄順序，節錄）：

| 測項 | 內容 |
|---|---|
| cpu.aes-sha | AES-NI 與 SHA-NI 吞吐 |
| cpu.load-use / ilp / branch | Load-to-use 延遲、ILP、分支延遲與誤預測 |
| cpu.branch-matrix | 分支式矩陣乘法 |
| cpu.rdrand-rdseed | 亂數指令吞吐 |
| cpu.pmu-topdown | Intel PMU Top-down 管線歸因（唯讀取樣） |
| cpu.core-latency | 核心間延遲矩陣 |
| cpu.core-to-core | 核心到核心搬運頻寬 |
| cpu.smt-sibling | SMT 兄弟執行緒競爭 |
| cache.bandwidth / latency | 快取階梯頻寬與延遲（L1/L2/L3/DRAM） |
| cache.lock-scaling | lock 前綴指令的擴展性 |
| numa.* | NUMA/TLB/大分頁對照（TLB working-set 掃描以互質 stride 逐頁掃） |
| memory.dram-mapping | DRAM 位址映射推論（循序 stride 走訪曲線，僅推論不宣稱） |
| memory.bandwidth | 記憶體頻寬（多執行緒 STREAM） |
| memory.numa-tlb-largepage | 三子項獨立判定（本機 Administrator 無 SeLockMemoryPrivilege 時大分頁項如實標未執行） |
| ux.synthetic-workloads | 合成 JSON 往返、SHA-256、資料轉換序列 |
| gpu.raster-texture | D3D11 全螢幕三角形填充率＋單／八取樣紋理（readback 對 CPU 參考色逐位元組比對） |
| gpu.codec-throughput | Media Foundation H.264 編碼（記憶體內，不攝影） |
| gpu.compute-pipeline | D3D11 計算管線（buffer SRV） |
| storage.composite | 暫存檔 FlushToDisk→讀回→GPU FNV-1a→回讀與 CPU 逐元素比對 |
| storage.qd-ladder | 磁碟 QD 梯度 |
| storage.io-gpu-pipeline | 儲存→GPU 管線 |
| audio.wasapi | WASAPI 訊號級 |
| gauntlet.multi-domain | CPU／記憶體／儲存三域並行分窗取樣＋early/late 保留率 |
| confidence.engine | 統計引擎自我稽核（7 個已知答案合成樣本集逐案例驗證，不符整場拒收） |

高負載警告：執行期間 CPU／記憶體／GPU／儲存測試會建立 `XinSpect.deepbench.tmp` 與 `XinSpect.loop.tmp`，不觸既有檔案；結束、例外或取消都會刪除，啟動前會檢查剩餘空間。深測歷史只保存在本機設定資料夾，不上傳。

## 八、監控與量測工具

**感測器**。LibreHardwareMonitorLib 引擎，所有感測器的完整明細總表：溫度、時脈、電壓、風扇、負載。迷你懸浮視窗與系統匣模式；超標警示（可設定門檻，橫幅＋氣泡通知）；CSV 匯出；每秒更新。

**歷史回放**。數週的溫度／負載走勢回放與統計。

**健康**。溫度／負載／容量彙整的健康總評，含磁碟表面掃描入口。

**效能**。跑分（棋類 perft 檢核——中國象棋／西洋棋的葉節點數是數學常數，算出別的數字是這台機器算錯了）、烤機、快取延遲、磁碟效能。

**算力圖**。CPU／記憶體／GPU／儲存／NPU 各維度算力視覺化。

**繪圖管線測試**。WPF 繪圖管線效能：填充率、3D 幾何、文字渲染；走軟體光柵化，量的是 WPF 管線而非 GPU 硬體——與深測中心的 GPU 深測（D3D11）互補。

**幀時間監測**。任何程式的真實幀時間與 1% Low。ETW 事件收數，不注入目標程式。

**DPC 延遲**。排出造成音訊爆音／輸入停頓的肇事驅動（ETW）。

**執行緒遷移**。執行緒在核心之間彈跳的頻率與每一跳丟掉哪一層快取。ETW Context Switch 事件（零驅動、需管理員），記下每條執行緒上次落在哪顆核，換核即一次遷移，依拓撲四層歸因：同實體核心（SMT 兄弟，L1/L2 都在）、同末級快取內換核、跨末級快取（L3 要重拉）、跨 NUMA（記憶體變遠端）。邊收邊累加不留原始事件，並扣掉量測自身執行緒。行程排名依遷移率而非絕對次數。刻意不下判決：遷移多寡取決於工作型態，不是缺陷。

**隱形停頓（SMI）**。系統管理中斷的次數與封裝／核心 C-state 駐留。SMI 由韌體在 OS 看不見的模式處理，工作管理員恆為 0% 但音訊會爆；硬體只留下次數，沒有每次待了多久，所以只給頻率，不乘一個猜出來的耗時。

**NVMe 電源狀態**。碟宣告的電源狀態表（各階功耗、進入與離開延遲）對實測「刻意閒置 N 毫秒後第一筆 4K 讀取要多久」。兩者同一量級才敢歸因於省電狀態。

**Resizable BAR 實況**。顯示卡的記憶體視窗被撐開了沒有。ReBAR 要 BIOS 開、驅動支援、CSM 關掉、純 UEFI 開機、開機碟是 GPT，缺一個就不生效，而 Windows 沒有任何地方告訴你現況。程式問 Windows 實際指派給裝置的記憶體範圍——生效值而非能力宣稱值，不需驅動或管理員權限。

**大頁與位址轉換成本**。同一份亂序指標鏈、同樣大小的工作集，在 4 KB 頁與 2 MB 大頁上各跑一次；唯一變數是頁面大小，兩者的差就是走表代價。配不出大頁與大頁沒有效益分開講。

**L3 未命中與 DRAM 實際流量**。架構效能事件數出真的去了記憶體幾次，先以已知大小負載自我驗證；對不上只給原始計數，不換算成頻寬。

**執行緒遷移與排程落點**。見上。

**算力圖／NPU 檢測**。見 6.2 與 6.7。

## 九、證據實驗室

**時間膠囊**。全機事實快照：所有來源的事實併入單一 JSON（3094 項測試背後的事實模型），附 SHA-256 完整性信封（canonical UTF-8 JSON 逐位元組雜湊）與匿名機器識別。敏感值預設遮蔽；「保留敏感值」必須由使用者主動指定。跨快照逐欄比較：不同機器（匿名識別不同）直接拒比，避免把兩台機器的差異誤認成硬體變更；變更分四種（新增／消失／變更／未變），其中**某來源這次讀不到會明確列為消失，不以舊值填補**。資產生命週期事件自動分類：差分結果按 key 前綴映射到資產類（記憶體 mem./spd.、處理器 cpu.、顯示卡 gpu.、儲存 disk./nvme./smart.、主機板 board.），其餘變更照列但標「狀態」——韌體設定變了不是硬體變了，不冒充。比較動作寫入審計日誌（雜湊鏈：Sequence 連續、PreviousHash 串接、逐筆重算，改中間一筆必失敗）。

**原始暫存器快照**（.xinraw）。PCI 安全暫存器、ACPI 整表、MSR、SPIBAR、MCHBAR 的原始位元組；SHA-256 信封，載入時逐位元組驗完整性——被竄改或損毀的檔案拒載。揮發位元（SMI 計數、DEBUG_OCCURRED、HSFSTS 狀態）標注為揮發。raw 不匿名化，分享前請自行確認內容。

**HTML 報告**。單檔自足（無外部資源）、五特殊字元自訂轉義（WebUtility.HtmlEncode 會把 ° 轉數字實體破壞可讀性，故自寫）、讀不到／警示列樣式、尾端 SHA-256 標記——`Verify()` 離線重算，雜湊蓋「值挖空的完整文件」。

**corpus 貢獻包**（格式 v1 骨架）。只接受遮蔽版快照（敏感保留版拒收）、身份鍵逐鍵排除（serial/uuid/mac/asset.tag/system.product/user.，比 Sensitive 旗標更嚴的雙保險）、SHA-256 信封。上傳通路刻意未實作——重開前需四項條件：逐次同意、本地預覽、資料主權聲明、伺服端不改寫。

## 十、風險與同意閘門

下表列出所有涉及寫入或潛在風險的操作。除此之外的功能全部唯讀。

| 功能 | 寫入內容 | 防護機制 |
|---|---|---|
| Rowhammer 壓力探測 | 自擁有緩衝區內高頻讀寫 | 同意閘門（UI 勾選＋服務層對未同意呼叫丟例外）；「未經過校驗」標註；無自動接線 |
| 多輪壓力模式 | 同上，10 輪獨立探測 | 同上；明寫「多輪零翻轉不表示具備 Rowhammer 抗性」 |
| PMU 編程驗證 | IA32_FIXED_CTR_CTRL 使能位（OR 併入原值，不碰 PMI 位元） | 同意閘門；每輪 finally 還原原值；「多輪測試・不保證可用」標註；S5 對照未執行 |
| 假容量寫入驗證 | 目標磁碟大量寫入（至上限或寫滿） | 同意閘門；危險聲明（加劇瀕死媒體損耗）；結束刪檔；無自動接線 |
| 磁碟表面掃描 | 無（純讀取） | 僅讀取；可取消；邏輯卷不需管理員 |
| 深層存取開關 | 安裝自簽 CA 與驅動服務 | 非提權拒做；關閉完全移除（服務＋只移除自己 CA）；不開全機 test-signing |
| CPU 超頻 | 倍頻／電壓（經 XTU 橋接） | 快照→回讀驗證→看門狗→自動還原 |
| 顯示卡超頻 | 時脈偏移／功耗／風扇 | 同上 |
| 系統風扇調速 | Super I/O 風扇暫存器 | 一鍵還原自動；與感測共用同一引擎 |
| 系統引導修復 | SFC/DISM/CHKDSK 執行 | 執行並記錄輸出；命令為微軟官方工具 |

## 十一、CLI 與自動化

```
XinSpect.exe --json evidence [--query <查詢>] [--out <檔案>]
XinSpect.exe --compare-flash <參考映像> [--out <檔案>]
XinSpect.exe --verify-audit [日誌路徑] [--out <檔案>]
```

退出碼：0＝全部事實 Present（或審計鏈完整）；2＝部分事實為三態／審計鏈斷；1＝致命錯誤。CLI 在單一實例邏輯之前分支——不建具名信號、不觸發多開對話框，適合腳本與排程。`--verify-audit` 逐筆重算審計日誌的雜湊鏈並輸出 `fileExists／chainValid／checkedEntries` 與斷點原因（日誌不存在如實標注，不假稱「驗證通過」）。

查詢語言：一行一子句；`#` 開頭為註解；值含空格吃到行尾。欄位七種（key／category／source／value／availability／since／until），運算子三種（`=` 精確、`~=` 子字串包含不分大小寫、`^=` 前綴）；availability 支援別名（ok／error／no-permission）。子句之間為 AND。語法錯誤丟 ParseException 帶修正指引。範例：

```
key ^= spi.
category = 韌體安全
availability = error
since = 2026-10-01
value ~ FLOCKDN
```

PowerShell 模組 `XinSpect.psm1`：`Get-XinSpectEvidence` 封裝 CLI 呼叫（檔案必須 UTF-8 BOM，機器檢查守住）。

本機 API handler：`GET /api/facts` 返回全部事實（canonical JSON，與時間膠囊同一形狀）；`POST /api/query` 接受查詢語言全文（單一含點 token 相容舊前綴語意；無運算子的 nonsense 輸入回 400 帶修正指引）。設計為 loopback 唯讀、匿名機器識別；HTTP 監聽殼刻意不啟動（判定登记表有決定與條件：開監聽＝新增本機攻擊面，屬使用者決定）——headless 的出口由 CLI 承擔（`--json evidence`），審計日誌的 App 外驗證走 `--verify-audit`。

## 十二、公開規格與文件

| 文件 | 內容 |
|---|---|
| `docs/spec/snapshot.schema.v1.json` | 時間膠囊 JSON Schema（2020-12）：頂層與 fact 定義、三態與可信度列舉；numericValue/unit 條件忽略成文。與實際序列化逐鍵機器對帳 |
| `docs/spec/query-language.v1.md` | 查詢語言 v1 規格：七欄位三運算子、別名表、「查不到≠沒有」語意、ParseException 拒靜默 |
| `docs/spec/METHODOLOGY.md` | 事實來源契約：誠實契約五條、量測路徑五層、交叉對帳、突變測試、效能預算 |
| `docs/MEASUREMENT-METHODOLOGY.md` | 量測方法學：量不到是三態不是零、混池禁令、單調鐘與牆鐘分工、臨界值成文、flaky 處理 |
| `docs/spec/LIMITATIONS.md` | 公開限制：七項設計裁決＋六項能力邊界＋兩項環境相依 |
| `docs/EC-RISK-ASSESSMENT.md` | EC 埠存取風險評估：交易交錯、burst 破壞、症狀延遲顯現；結案裁定不實作，重開條件成文 |
| `docs/PMU-SANDBOX-PLAN.md` | PMU 編程沙箱驗證方案 S1–S5（最小寫入回讀、已知工作量核對、三次清除無殘留、與 wpr 並行不干擾） |
| `docs/DATA-SOVEREIGNTY.md` | 資料主權聲明：資料位置、零網路 API 機器檢查、匿名化、刪除即終結 |
| `docs/DEPLOYMENT.md` | 部署與企業維運：發佈形態、CLI、資料位置、升級回滾、HVCI 限制 |
| `docs/ITERATIONS.md` | 完整迭代帳本：60+ 批次、逐輪內容、測試數、提交雜湊 |
| `docs/ROADMAP-G6.md` | G6 路線圖與誠實界線（EC 直寫、Rowhammer、PMU 各有閘門） |
| `docs/feature-backlog.md` | 110 條候選功能的盤點 |

## 十三、品質保證

- **單元測試**：3094 項，全部通過。涵蓋每個解碼器的金標向量（手算位元組級）、每個服務的三態行為、每條對帳規則的逐情境行為（68 情境）、每個查詢語言子句。
- **突變測試**：Stryker 對 `XinSpect.Decoders` 類別庫執行，分數 82%。曾抓到「文件說有 extended family 進位、實作漏做」的真 bug。
- **SpecRef 覆蓋**：反射檢查已註冊解碼器的每個公開方法都有規格引用（文件、章節、暫存器、位元）。
- **Property 測試**：FsCheck 對遮罩差分永不入列性質、MCFG 編碼往返等做隨機驗證——曾當場抓到遮罩位元心算錯 0xA007 vs 0xA807。
- **差分測試**：SMBIOS 記憶體解碼與微軟 Win32_PhysicalMemory 對照（同表雙實作：模組數、總容量、標稱與實際速度）。fixture 過期（換 RAM）會紅，訊息已寫明先查 fixture。
- **文件對帳**：README 版號、Changelog、csproj 三處一致由測試把關；規格文件與解析器的漂移會紅燈（PublicSpecTests、DataSovereigntyTests 掃原始碼）。
- **金標向量的實績**：TPM rc 偏移兩次寫錯靠金標抓回；IPMI SEL Generator ID 是 u16 被長度檢查抓回；TPM2 大端序 count/eventSize 在 offset 11/49——測試向量寫錯兩次靠測試互抓。

## 十四、隱私與資料主權

- **事實蒐集與解碼層零網路 API**：`DataSovereigntyTests` 掃描 Services 與 Decoders 的全部原始碼，HttpClient/TcpClient/UploadString 等出現即紅燈。全程式僅有的網路功能：AI 評價、意見回饋、網速測試、網路延遲量測——全部需要使用者主動觸發，且都在允許清單內（清單與資料主權文件同步釘死）。
- **資料僅存本機**：審計日誌 `%ProgramData%\XinSpect\Audit\audit.json`（雜湊鏈，可離線驗證）、驅動憑證 `%ProgramData%\XinSpect\Driver`（ACL 限 SYSTEM/Administrators）、時間膠囊與原始快照在使用者指定路徑。刪除檔案即完成刪除，沒有雲端副本、沒有備份同步、沒有遠端殘留。
- **匿名化**：機器識別為單向雜湊派生（`sha256:`＋64 hex），不含序號原文。敏感值預設遮蔽，保留必須使用者主動指定。corpus 貢獻包只收遮蔽版，身份鍵比 Sensitive 旗標更嚴地逐鍵排除。
- **自我遙測**預設關閉、匿名、只記數字與時間、本機存取、環形上限 500。
- **審計日誌**只記中繼資料（動作、範圍、結果摘要、雜湊），不記事實內容；不上傳雲端、不做區塊鏈。

## 十五、已知限制

- 突變測試僅涵蓋純解碼器類別庫；主程式的特權通路層由注入式測試與金標向量承擔，無法突變測。
- PMU 編程驗證尚未與參考工具對照（沙箱方案 S5 未執行），標「多輪測試・不保證可用」。
- Rowhammer 壓力探測非保證觸發（usermode 無 clflush），不能作為記憶體可靠性的結論。
- DCO 需廠商私有命令，未實作。
- MCHBAR 記憶體時序（tCL/tRCD/tRP/tRAS）屬 MRC 訓練結果區，Intel 公開文件未定義，不出值——這是「不是待辦遺漏」的設計裁決。
- PCH 世代名稱對照未對準出處前不出值。
- Wi-Fi 連線態欄位需介面實際連線；未連線時如實標示。
- HVCI／VBS 開啟時 MSR 可能被攔截或回虛擬值，可信度裁決在平台可信度頁明示。
- ATA_PASS-THROUGH 刻意不用（曾實測卡 IRP）；SMART 走 disk.sys 代理的 SMART_RCV_DRIVE_DATA。
- IPMI 帶外管理通路（KCS/SSIF/NCSI）與 MegaRAID BBU/VD/PD 佈局未驗證——本機無硬體可驗證的通路不出貨，只出訊息解碼。
- EC 埠存取結案不實作（見風險評估文件）。
- 規則市集（需簽章信任模型）與二手平台 API、SDK/模擬器（M9/M10）刻意不做。

## 十六、從源碼建置

```
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build XinSpect.csproj -c Release
dotnet test Tests/XinSpect.Tests.csproj
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- Release 建置會自動發佈並內嵌 XTU 超頻橋接（net48）與藍色中隊守護進程；Debug 建置不內嵌以縮短建置時間（開發環境由 BlueSquadronBootstrap 逐層向上搜尋專案輸出）。
- 測試命令建議加 `-p:BaseOutputPath=obj/_verify/`——若使用者正在跑已發佈的 XinSpect.exe，apphost 檔案鎖會擋預設輸出路徑。
- XsRegProbe 驅動：`XsRegProbe/` 內為 C 源碼與建置手冊（`BUILD-給使用者.md`），需 WDK 編譯並簽章（窄路自簽 CA 即可，與深層存取開關同一流程）。
- 專案結構：`XinSpect/`（主程式）、`XinSpect.Decoders/`（純解碼器類別庫，突變測試對象）、`Tests/`（單元測試）、`BlueSquadron/`（守護進程）、`Bridge/`（XTU 橋接）、`XsRegProbe/`（驅動源碼）、`docs/`（規格與帳本）、`Rules/`（外部化對帳規則）。

## 十七、版本沿革

- **v2.54 Olympus**（2026-10-10）：發版流水線 Tools/release.ps1——預檢（版號六處＋三份 README 逐項）→ 全綠（trx 計數器把關）→ tag 顯式推＋ls-remote 核對 → publish 單檔 → README 位元組自動同步 → REST 三資產上傳逐檔驗大小 → verify-release 收尾；任一步紅即中止。本版即由腳本自身發佈；並併入關於頁「檢查更新」按鈕（opt-in 僅查版本號、查不到不假裝最新）。測試 +8（3679 綠）。
- **v2.53 Olympus**（2026-10-10）：算繪快照守門——熱區圖／逐核液柱／空板誠實路徑三場景算繪成固定點陣圖（MILSW 不經 D3D）取 SHA-256 比對倉庫基線，輸入全凍結（假資料、深色佈景、停動畫），負對照驗證會抓漂移；XINSPECT_RENDER_SNAPSHOT_UPDATE=1 為故意更新機制。測試 +4（3671 綠）。
- **v2.52 Olympus**（2026-10-10）：能力矩陣 cap.*——MSR／MMIO／PCI／I/O 埠／SMBus／TPM／UEFI 變數／WMI／PMU 九條彙總事實併入 AllFacts，韌體安全頁自動呈現。不新增探測：來源三態 cap 跟著翻、來源量到才可用、來源缺席如實 Unknown（缺席不讀成不支援）。鍵走 MatrixRow 字面值＋掃描樣式，覆蓋申報罩住彙總層。測試 +6（3667 綠）。
- **v2.51 Olympus**（2026-10-10）：寫入閘門最小版——全庫 17 個硬體寫入呼叫點（PMU 編程、RDT、DRAM 流量、TopDown、SMN、SMBus、CMOS 端口…）全部經過 WinRing0Bridge 三個寫入 API，閘門因此做在底層：每次 MSR／PCI 設定空間／I/O 埠寫入自動記帳（時間、目標、呼叫者、寫入值、成功或失敗）。同意閘門（使用者點頭才跑）不變，這裡補的是『點頭之後到底寫了什麼』可回答——CLI --json evidence 輸出新增 writeAudit 段。守門：源碼斷言橋接三個寫入方法各自記帳＋Services/ 內不得有第二組 Ring0 原生寫入入口。測試 +4（3661 綠）。
- **v2.50 Olympus**（2026-10-10）：建置警告清零＋基線守門——主專案 13 條警告全數修掉（CS8604/CS8629 可空dereference、CS0219 未用變數、SYSLIB0057 X509Certificate2 過時構造改 X509CertificateLoader、CS9191 ref→in、CS8600/CS8602）；SmartFailingNow 的 probe() 空值原本是會真炸的缺陷（.Value 直解）順手收口成三態。新增 WarningBaselineTests：真重建主專案、去重數警告、基線 0——任何人新增一條警告，全套就紅。警告沒有基線就和沒有守門一樣：這 13 條存在了數十個版本而沒人看見，因為沒有東西在數。測試 +1（3657 綠）。
- **v2.49 Olympus**（2026-10-10）：三方對帳收口——新增執行期對帳：用 App 啟動與 CLI 同一条唯讀路徑跑完三個入口（驅動組／usermode 組／平台組），把「真正產生的鍵集合」與「申報的鍵集合」逐鍵比對。第一次上線就抓出兩批共 214 把「天天在生產、覆蓋申報完全看不見」的鍵： pci.dev.／reconcile.／sio.hwm. 等 137 把（驅動組）＋ asset.field.／role.installed.／monitor. 等 77 把（平台組）。其中 106 把其實靜態可枚舉——補進全鍵目錄（153 → 170，掃描器加六個定向樣式追上 helper 傳鍵的寫法）；13 個真動態家族（成員由機器決定）進新設的 FactKeyDynamicCatalog 登記——每個前綴要有「為什麼動態＋成員由什麼決定」的理由，且前綴本身必須在源碼掃得到（殭屍前綴紅燈）。對帳類跑真 WMI／MMDevice／驅動會話，歸入禁並行集合。測試 +4（3656 綠）。
- **v2.48 Olympus**（2026-10-10）：審計鏈的 App 外驗證入口——CLI 新增 --verify-audit：逐筆重算審計日誌雜湊鏈、輸出 fileExists／chainValid／checkedEntries 與斷點原因，退出碼 0＝鏈完整（日誌不存在如實標「還沒有審計事件」，不假稱通過）、2＝鏈斷或損毀、1＝致命。能產日誌卻不能在 App 外證明日誌沒被改，『可證明』的主張就打了折——本機現況實測 687 筆鏈完整。LocalApiHandler 寫入判定登记表（HTTP 殼刻意不啟動是決定不是遺漏：開監聽＝本機攻擊面；headless 出口由 CLI 承擔），登记表驗證同步支援非 Service 結尾的能力類別。測試 +4（3652 綠）。
- **v2.47 Olympus**（2026-10-10）：Uncore 事實接線＋孤兒服務完整性網——UncorePmuService 接進證據實驗室（pmu.uncore.platform／ratio_limit／perf_status 三鍵，目錄 150 → 153；平台白名單判定先行，未收錄平台一個 MSR 都不碰，三態各附原因）；新增 WiringDecisions 判定登记表（刻意不接線要寫名字與理由：SMN 寫入面、WASAPI 主動捕獲、上傳通路刻意不實作、假容量寫操作）與 ServiceOrphanGateTests：每個公開服務要嘛被生產引用、要嘛在登记表有判定，登记表過時也紅燈。掃描踩坑：同檔宣告＋同檔真實引用（ScmDriverService）不能整檔排除，改行級判斷。測試 +10（3648 綠）。
- **v2.46 Olympus**（2026-10-10）：發佈治理——新增 Tools/verify-release.ps1 發佈後驗證腳本（README 下載連結逐一查 Release 存在、資產大小與表列位元組數對帳、版號六處一致；匿名 API 限流自動改向 Git Credential Manager 取 token；任一失敗非零退出）與 ReleaseIntegrityTests 三條（「位元組數為本版實際發佈」宣稱與下載連結同版本——防「文字跟著跳、數字沒跳」再現；宣稱版本等於專案版號；驗證腳本存在且帶 UTF-8 BOM——PS 5.1 讀無 BOM 的 UTF-8 會把中文當 ANSI 炸）。本版不加功能：先把發佈管線修好再走。測試 +3（3638 綠）。
- **v2.45 Olympus**（2026-10-10）：環境假設守門——清掉生產碼兩處寫死開發機路徑（PaddleOCR-VL 目錄、CPU-Z 搜尋變體），改環境推導、找不到如實回 null；新增守門測試掃「磁碟機:\Users\<使用者名>」與「:\Desktop\」字面值，角色與 ACL 名（WindowsBuiltInRole.Administrator、BUILTIN\Administrators）不誤報、SpecialFolder 推導寫法不誤報，白名單每筆要理由、上限 5（目前為空）。與 v2.43 的 WinRing0 同族缺陷：使用者端的靜默失靈長得像「沒有這個功能」，不像「路徑假設錯了」。測試 +3（3635 綠）。
- **v2.44 Olympus**（2026-10-10）：時間炸彈守門——測試方法不得在同一個方法體裡同時寫絕對日期與 UtcNow 查詢窗（已炸過一次：TrendSentinel 的資料基準寫死 2026-10-08 配相對窗，隔天 8 條斷言同時紅，2.43 已修）。守門做在方法級而不是檔案級：檔案級掃描的「高危」三檔，方法級逐一核對後兩條是誤報（寫入讀回的往返與合成序列不與查詢窗相遇），正對照測試釘住掃描器真抓得到炸彈。測試 +2（3632 綠）。
- **v2.43 Olympus**（2026-10-10）：核心熱區圖重做——格數改由拓撲的實體核心數決定（新增 PhysicalCores），感測器少報幾顆時缺的位置照樣佔一格並如實留白（灰色格與「—」代表拿不到讀值，不是 0 °C、也不是涼）；兩種逐核來源（感測器列／逐實體核心摘要）收斂到同一份格子契約與同一支控制項，色階收斂成單一來源 HeatScale（核心熱區圖、逐核液柱與圖例同一個溫度就是同一個顏色，不隨主題變），圖例與統計列（最熱／最冷／平均／覆蓋率）由錨點與資料推導；修掉逐實體核心編號從 0 起算、與全站 1 起算差一號的缺陷。另新增 ESP 檔案層掃描（列舉 .efi 檔 SHA-256 並與 dbx 交叉引用，命中是攻擊面事實不是中毒判決）與 WinRing0 來源內嵌（先前單檔發佈在使用者端找不到驅動，MSR／PCI／I/O／MMIO 全數讀不到；現在 0.9.4 跟著執行檔走）。測試 +40（全套 3630 綠）。接線守門：三支事實服務（音訊端點／開機計時／網路卸載）接進共用入口，並加「事實服務必須被生產碼引用」的守門測試。
- **v2.42 Olympus**（2026-10-09）：.etl 內容讀回——本專案寫得出（EtwTraceService）、認得出（IsValidEtl）、這一版起讀得回來：TraceEvent 檔案模式把落地軌跡解析回逐提供者事件數、未解事件數（如實計數不以 0 補）與時間範圍。測試過程抓到會炸行程的真缺陷：垃圾位元組餵給 ETWTraceEventSource 會踩 TraceEvent finalizer 缺陷炸掉測試主機——修法是建構前先驗檔頭。接進 LoadUsermodeFacts 共用入口。真機以系統 .etl 實測閉環。測試 +5（全套 3591 綠）。
- **v2.41 Olympus**（2026-10-09）：裝置安裝時間線——唯讀解析 setupapi.dev.log（peripheral-forensic 能力的唯讀版）：區段標記、裝置實例 ID、起訖時間戳、結束狀態、錯誤行、開機段歸屬。解析只取語言中立欄位（標記符號／裝置 ID 語彙／時間戳格式），在地化標籤混入照樣解出（測試釘住）；接進 LoadUsermodeFacts 共用入口。真機實測 376 區段、36 錯誤行。測試 +9（全套 3586 綠）。
- **v2.40 Olympus**（2026-10-09）：驅動檔靜態檢視（DrvEye／DriverSight 能力的唯讀版）——對非系統目錄的載入模組逐顆讀回 .sys、解析 PE 結構（機器／子系統／區段／匯入表）、掃內嵌裝置字串與 CTL_CODE 編碼候選（Function 落自訂範圍才收，並明說「候選不是確認」），與既有 BYOVD 封鎖清單雙道交叉（SHA-256 主、檔名輔）。全程不載入驅動、不呼叫 IOCTL。接進 LoadUsermodeFacts 共用入口（UI 與 CLI 同源）。真機實測：235 顆非系統模組、逐顆解出匯入與裝置字串。測試 +14（全套 3577 綠）。
- **v2.39 Olympus**（2026-10-09）：UEFI 韌體磁碟區結構解析——唯讀讀回 BIOS 區、依 UEFI PI 規格解出 FV → FFS 檔案 → 區段頂層樹（GUID／型別／大小／使用者介面名），壓縮區段只列出不解壓、表頭校驗和不符合的檔案如實計數、找不到 _FVH 如實回 0。新純解碼器納入 SpecRef 覆蓋檢查；韌體安全頁新增卡片。金標向量按規格逐欄編碼，測試 +15（全套 3564 綠）；MMIO 後端不可用時三態如實顯示原因，驅動載入後真值翻轉。
- **v2.38 Olympus**（2026-10-09）：OS 內建查詢接縫化（INativeToolSource 接縫，powercfg 原樣輸出誠實帶進畫面，宣告與執行分離）＋儲存可靠性計數器 19 欄（storage.reliability.*，值為 0 就是 0、提供者沒給的欄位數出並具名列出）＋三個真缺陷修復：整合沒接線（LoadUsermodeFacts 具名入口＋守門測試）、三態被寫錯（先 Read 再判可用性，例外一律 ReadError）、CLI 進入點在 .NET 10 上 StartupUri 崩潰（改 Shutdown 機制，端到端實測通過）＋虛擬化卡片繫結路徑修正與「有資料時」樣板渲染檢查。README 測試徽章改由 TestSuiteBaseline 單一來源保管。測試 3548 綠。
- **v2.37 Olympus**（2026-10-08）：修 v2.36 的覆蓋申報缺陷——發佈版掃不到原始碼時會把「0 個事實鍵」讀成「全部都覆蓋了」。事實鍵目錄改為編譯期固定（134 鍵）＋規則改用執行期那一份（26 條），並加三條迴歸測試。另修正說明文件對 Rules/builtin.json 的承諾（該檔沒有隨程式出貨）。測試 +11：專案測試 3524 綠（全套是 3525，差的那 1 條是本機未追蹤的探針）。
- **v2.36 Olympus**（2026-10-08）：地基——可用性由五態擴為六態（新增 Unknown＝有值但未確認）並定義偏序格與合取傳播；覆蓋申報（設定頁）申報事實鍵的對帳覆蓋與知識表收錄率；版號守門擴大到三份 README 的下載連結與版本沿革。測試 +59，全套 3514 綠。
- **v2.35 Olympus**（2026-10-08）：第四梯收官——音訊端點緩衝區（獨占模式延遲下限，四級用途判讀）、識別與資產（SMBIOS 識別欄位與韌體未填標注、機箱類型）、已安裝角色與功能的攻擊面（裝了但沒在用，服務沒在跑不等於不會跑）；IPMI／MegaRAID／Redfish 三個既有解碼器納入 SpecRef 覆蓋檢查。測試 +76，全套 3455 綠。
- **v2.34 Olympus**（2026-10-08）：IOCP 佇列深度曲線判讀（還有餘裕／已飽和／加深反而下降三種形狀，並說明延遲隨 QD 上升是排隊的必然結果而非故障）、NUMA 跨節點標為「不適用」並明說未驗證不代表可用。新增純解碼器 StorageQdJudge，測試 +16，全套 3379 綠。
- **v2.33 Olympus**（2026-10-08）：記憶體通道配置（插槽命名推斷的通道數 vs 每通道模組數，並明說理論上限的「每支各佔一通道」假設站不站得住）、螢幕組成（真實螢幕／軟體虛擬螢幕／作業系統預設物件三分）。新增純解碼器兩個，測試 +26，全套 3363 綠。
- **v2.32 Olympus**（2026-10-08）：伺服器與工作站視角——虛擬化平台三態（元件／服務／虛擬層分離，「裝了但開機未載入」不再被講成「已啟用」）、網卡兩條鏈路落差（PCIe 供給 vs 線路速率，供給不足時明說「跑不滿」）、顯示轉接器真偽（真實 vs 軟體 vs 基本顯示驅動）、SMBIOS 補完（Type 1 UUID 小端序、Type 3 機箱類型、Type 28／29 感測器的「值未知」位元）、記憶體錯誤更正三層分離（ECC／Registered／平台能力不互相推論）。新增純解碼器四個，測試 +105，全套 3337 綠。
- **v2.31 Olympus**（2026-10-08）：PCIe 落差判讀——PCIe 鏈路頁新增「落差」欄，把「裝置宣告的能力」與「實際跑到的鏈路」之間的差距依成因分成相符／寬度受限／速度待確認／省電設計／上游上限／未判定，並附上實際欄位值當依據。會往上游埠讀它的鏈路能力，上限在上游就明說「這張卡不是瓶頸」；讀 Link Control 2 的 Target Link Speed，被 BIOS 或驅動壓低的協商上限直接指出來；上游讀不到一律標未判定，不當成「上游沒有限制」。**純唯讀，不寫任何暫存器**——PCIe 主動重協商會動到運作中的鏈路，本專案不跨這條線。測試 3218 綠。
- **v2.30 Olympus**（2026-10-08）：SPI 快閃熵圖——唯讀讀回整顆快閃、逐 4 KiB 塊算 Shannon 熵、依 FREG 區域切分，看出內容組成（高熵＝壓縮／加密、低熵＝空白或規律、全 F＝抹除）；只描述分布不判斷好壞或是否原廠，PRx 讀保護攔截範圍會被算成抹除區且報告會標明。驗證套件 3195 綠。
- **v2.29 Olympus**（2026-10-08）：歷史倉擴充——長期追蹤指標由 7 項增至 13 項（新增處理器功耗／電壓、VRM 溫度、顯示卡功耗、記憶體用量、儲存溫度）；磁碟格式升為 v2 並改為硬性版本檢查（欄位數不同時整份忽略而非錯位讀成假讀值）；既有欄位順序未動並加測試釘住。驗證套件 3174 綠。
- **v2.28 Olympus**（2026-10-08）：統計哨兵——歷史回放頁新增時序統計判讀：Theil–Sen 穩健斜率（趨勢，附 95% 信賴區間）、CUSUM 累積和（變化點）、Pearson 前後半相關性（關係變化）；只陳述數列本身的變化，不對硬體健康下因果結論；資料不足／無感測器一律如實回報；斜率信賴區間改用中位絕對差（MAD）常態近似（Sen 無母數區間在配對數大時會寬到失去判別力）；新增純統計核心 TrendSentinel 與服務層，測試 +30。驗證套件 3170 綠。
- **v2.27 Olympus**（2026-10-08）：語言模式收尾——自繪控制項（FieldRow／RadialGauge／HistoryGraph／Oscilloscope／AnalogVoltMeter／DonutChart／SectionHead）的文字是自訂相依屬性、不在視覺樹上，過去三種語言模式都碰不到，現在逐一轉換；狀態列與時鐘的複合字串改為逐段翻譯；修正有繫結的 TextBlock 被當行內文字寫入（1015 處繫結的內容從此不再被寫死，時鐘與即時讀值恢復跟著來源更新）；修正簡體模式夾英文（英語模式產生的字串存槽前先反查回繁中）；翻譯表補 308 條；已知未收錄：安裝精靈與彩蛋頁的長篇文案。驗證套件 3140 綠。
- **v2.26 Olympus**（2026-10-08）：語言模式修復——設定頁 English 正式接線、英語模式真的能用，簡體模式與語言切換卡住的問題一併修掉；繫結字串 StringFormat 缺口補完（48 處，切換語言後格式也一起改）；一鍵部署器下載檔名錯字修正（XinSect.exe → XinSpect.exe）；README 下載連結與版本沿革修正（先前落在 v2.2.0）；新增落差分析與擴展總藍圖文件；驗證套件 3134 綠。
- **v2.25 Olympus**（2026-10-06）：硬體深化缺口清空版——Super I/O 多晶片家族（ITE／Nuvoton NCT67xx／Fintek）與 0x2E／0x4E 雙埠探測（名稱有收錄但佈局無出處者如實不解）、DDR5 記憶體 SPD 直讀（SPD5118 hub、MR11 切頁、8 頁×128 位元組、JESD400-5 解碼、CRC-16）、AMD 平台安全（SME／SEV／SEV-ES／SNP 支援與啟用位＋PSP 偵測）與 Radeon ADL 唯讀遙測、Intel 顯示卡 Level Zero 事實、PMBus 電源軌與 UPS 監控、UEFI db／dbx／KEK／PK 簽章庫接上韌體安全列；非本機硬體路徑全數如實標「未在本機驗證」、無硬體時整組「不適用」；驗證套件 3126 綠。
- **v2.2.0 Olympus**（2026-10-04，FileVersion 2.2.0.1）：系列更名 Everest→Olympus；WinRing0 回歸主力、XsRegProbe 轉白名單備援；交叉對帳 26 條外部化＋判決卡；驗機殺手級（SMART failing-now、機箱開啟、HPA、假容量驗證、WCTEMP、TDR、退休頁、NPU）；安全鑑識（BYOVD、Defender 排除、1102、信任根、USBSTOR）；處理器深化（TME/SGX、C-state、PMU 能力＋編程驗證、die 拓撲、SLIT）；系統軟體層（Update 歷史、服務盤點、事件記錄摘要、稽核政策、選用功能、核心模組 Authenticode、開機參數、開機計時、USB 拓撲、攝影機、企業儲存、網卡健康＋OUI、Wi-Fi 頻道）；藍色中隊內嵌本體（可開關）；深測中心 38 項；公開規格與文件十份；審計日誌、時間膠囊生命週期事件、corpus 骨架、本機 API、查詢語言；DeepBench 啟動崩潰修復；突變測試解鎖 82%；FeDevOps：3094 測試。
- **v2.1.0 Everest**（2026-10-02）：韌體安全頁、深層暫存器三態、XsRegProbe 驅動源碼、深層存取豁免開關、Deep Bench 20（10→20 項）、守護進程三版、藍色中隊態勢評估、一鍵裝機、硬體檢測、工具箱。

## 十八、常見問題

**為什麼有些項目顯示「權限不足」？** 該項事實需要 ring0（MSR／PCI 設定空間讀取）或管理員權限（Security log）。以系統管理員重新執行即可翻成真值；程式不會靜默降級或以預設值冒充。

**為什麼 SPI 快閃稽核全部是三態？** 這些暫存器位於 SPIBAR，需要 MMIO 讀取。WinRing0 載入後由 MmioBackendSelector 裁決通路；企業環境若攔截 WinRing0，可改走 XsRegProbe 白名單驅動（深層存取開關）。

**交叉對帳說「矛盾」是不是中毒了？** 不一定。矛盾的意思是「兩個來源對同一件事說了不同的話」，可能是韌體實作差異、量測時機差異，也可能是竄改。判決卡會列出雙方的數值與來源，判讀留給你。

**突變分數 82% 夠嗎？** 知識表資料檔刻意排除（逐條字串斷言＝快照重複）；主程式無法突變測（WPF＋特權相依），由注入式測試與金標向量承擔。82% 是「已測範圍內」的分數，帳本有逐批記錄。

**會收集我的資料嗎？** 不會。事實蒐集層零網路 API（機器檢查掃描源碼釘死）。四個會上網的功能（AI 評價、回饋、測速、網路延遲量測）全部需要你主動觸發。

**Rowhammer 探測安全嗎？** 它在自擁有的緩衝區內操作，但 usermode 無法完全隔離相鄰實體列——程式以「危險」標註並建議使用專用測試機。它也不是正規的 Rowhammer 施測（無 clflush），結果標「未經過校驗」。

**藍色中隊是防毒軟體嗎？** 不是。它是六防線的安全態勢評估（唯讀）加 ETW 即時威脅偵測時間軸（觀察）。它不隔離、不刪檔、不擋程式——它把看到的事實攤開。

## 十九、授權

本專案以 [MIT License](LICENSE) 釋出。內建第三方元件（Intel XTU SDK、LibreHardwareMonitor、TraceEvent、NAudio 等）受其各自授權條款約束，不在本專案 MIT 授權範圍內。WinRing0 為雙用途驅動，本程式的 MSR 讀取亦依賴它；使用者應僅在自有或獲授權的機器上使用本工具。

## 二十、作者

By：Xinglanclever

## 附錄 A、實用工具逐項說明

「實用工具」群組收錄的每一項都注明資料來源與權限需求：

- **連接埠占用**：查出是哪個行程占用了連接埠（GetExtendedTcpTable/UdpTable 對照行程清單）。
- **Hosts 編輯器**：編輯系統 hosts 檔；寫入需提權，編輯前自動備份。
- **藍屏分析**：解讀 minidump 與停止碼（本機 dump 檔案解析，不上傳）。
- **垃圾清理**：暫存、快取與更新殘留；逐項列出大小後才刪除，不靜默清。
- **電池分析**：電池健康度與循環次數（WMI BatteryStaticData／CycleCount）。
- **右鍵選單**：管理檔案總管右鍵選單項目（Shell extension 列舉與停用）。
- **網速測試**：上下行頻寬與延遲——全程式僅有的主動出網功能之一，使用者點擊才發生。
- **記憶體整理**：釋放工作集與待用清單（EmptyWorkingSet）。
- **開機啟動項**：登錄檔 Run 鍵與啟動資料夾列舉與停用。
- **驅動稽核**：已安裝驅動的簽章狀態與驅動日期（唯讀 WMI Win32_PnPSignedDriver）；「驅動程式稽核」頁把未簽章與關鍵類別老舊驅動攤開——裝置管理員要一個一個點進內容才看得到的資訊。
- **DNS 切換**：一鍵切換公用 DNS 並測延遲。
- **大檔掃描**：找出占空間的大檔與資料夾。
- **運算穩定性壓測**：以 y-cruncher 驗運算穩定性，抓 CPU／記憶體在壓力下的靜默運算錯誤。偵測已安裝的 y-cruncher，不內含執行檔。
- **幀時間監測**：任何程式的真實幀時間與 1% Low（ETW，不注入）。
- **DPC 延遲**：排出造成音訊爆音／輸入停頓的肇事驅動（ETW）。
- **執行緒遷移**：核心間彈跳與快取層級損失（ETW 四層歸因）。
- **PCIe 鏈路**：目前協商速度／寬度對裝置能力上限（唯讀 PCI 設定空間）——供電或插槽問題導致的降速在這裡現形。
- **Windows 授權**：授權狀態、通道（OEM/Retail/Volume）、重裝與移轉可行性（唯讀）。
- **網卡進階屬性**：RSS 接收佇列與驅動開放的每一項設定（唯讀 WMI）。
- **睡眠與喚醒**：喚醒來源、喚醒計時器與電源請求（powercfg 資料唯讀呈現）——半夜自己醒來或睡不下去的排查。
- **顯示鏈路**：色彩編碼、位元深度與所需頻寬（見 6.6）。
- **NVMe 電源狀態**：宣告延遲對實測（見 6.5）。
- **USB 鏈路**：裝置能力對目前速度——掉到 480 Mb/s 時分清是埠還是線（唯讀查詢 USB 集線器）。
- **系統引導修復**：執行 SFC、DISM、CHKDSK 等官方修復命令並記錄輸出（命令本身是微軟官方工具，本程式只代為呼叫與記錄）。
- **硬體檢測**：螢幕壞點／滑鼠（按鍵、滾輪、回報率）／鍵盤（逐鍵、NKRO）／喇叭（聲道、掃頻）／動態（拖影、幀間隔）五項全螢幕檢測，純原生輸入事件、零外部相依、不連網。
- **瀏覽器／終端機**：WebView2 內嵌瀏覽與常駐 cmd／PowerShell 真實終端機。
- **進階驅動分析**：驅動分類彙總、年齡分布、重複偵測與已知問題驅動（基於驅動稽核，唯讀）。
- **作業系統分析**：版本、授權、更新與安全態勢（Secure Boot／VBS／HVCI／UAC）收攏一頁（唯讀）。
- **螢幕色域**：EDID 色域覆蓋率＋CIE 1931 色度圖（2D）。
- **電池分析**：見上。
- **場景**：一鍵切換靜音／均衡／效能——風扇曲線＋電源計劃＋顯示卡上限的組合套用。
- **拜神**：一包綠色乖乖，保佑機器乖乖。娛樂功能。

## 附錄 B、設定頁

- **更新間隔**：感測器每秒更新的輪詢間隔。
- **外觀**：語言（繁體中文／简体中文）、迷你模式、主題。
- **記錄**：歷史保留與 CSV 匯出。
- **警示**：溫度／負載門檻與通知方式。
- **AI**：端點位址、金鑰、模型與提示詞。
- **一鍵初始化**：重跑各模組偵測（不重建每秒脈動）。
- **藍色中隊開關**：守護進程啟停，選擇記入設定檔——上次關閉的話下次開機不再自動拉起。

## 附錄 C、審計日誌

時間膠囊的建立與比較動作寫入審計日誌（`%ProgramData%\XinSpect\Audit\audit.json`）。設計目標是不可否認中繼資料：每筆含 Sequence（序號連續）、Timestamp、Operator（機器雜湊，序號原文不落日誌）、Action、Scope、ResultSummary、ResultHash、PreviousHash→EntryHash（GENESIS 創世筆起始）。驗證器檢查序號連續、前筆串接、逐筆重算——修改中間任何一筆會失敗，重算該筆雜湊也逃不過 PreviousHash 斷裂。與證據時間軸的關係：Timeline 是事件呈現 UI，審計是不可否認中繼資料鏈，並存不重疊。只記中繼資料不記內容；不上傳雲端、不做區塊鏈。

## 附錄 D、疑難排解

**啟動即出現「權限不足」？** 以系統管理員執行。多數 usermode 功能不需提權，特權功能會逐項標示而非整頁失效。

**SPI／MCHBAR 相關事實全為三態？** 這些暫存器需要 MMIO 讀取通路。WinRing0 載入後由 MmioBackendSelector 裁決（WinRing0 主力→XsRegProbe 備援→三態）；企業環境的應用程式控制可能攔截 WinRing0，此時改用深層存取開關載入 XsRegProbe。

**交叉對帳顯示「無法驗證」？** 任一輸入缺席（讀不到）時該規則無法裁決——這不是矛盾，是資料不足。把缺席的原因修掉（載入驅動、提權）後規則會重新評估。

**深測中心啟動失敗？** 檢查儲存根（預設 `%TEMP%\XinSpectDeepBench`）是否可寫、剩餘空間是否足夠；錯誤詳情已寫入 `%APPDATA%\XinSpect\crash.log`。

**SMART 讀不到？** SMART_RCV_DRIVE_DATA 由磁碟類驅動代理，多數環境可用；NVMe 走 Storage Query Property；USB 外接碟通常不轉發 SMART 命令——屬平台限制，程式如實標示。

**測試跑不起來（檔案鎖定）？** 使用者正在跑已發佈的 XinSpect.exe 時，apphost 檔案鎖會擋預設輸出路徑——加 `-p:BaseOutputPath=obj/_verify/` 繞開，不要殺行程。

## 附錄 E、給貢獻者

- 新增事實的流程：TDD 紅→綠、三態＋原因、SpecRef（暫存器解碼器）、非自造驗證來源、全套＋Release 綠、繁中 commit＋帳本記錄（`docs/ITERATIONS.md` 逐輪）。
- 新增解碼器的流程：放 `XinSpect.Decoders` 類別庫（純函式、零 WPF 相依）、每個公開方法附 SpecRef、註冊進 `SpecRefRegistry.CoveredDecoders`（註冊表釘住測試會要求同步更新）、對 `DecoderHardeningTests` 補逐臂斷言。
- 新增對帳規則的流程：`Rules/builtin.json` 新增條目（附 SpecRef＋誤報條件）、`RuleValidator` 檢查鍵名登記與預設分支、行為等價以 Relation 層逐情境比對。
- 誠實契約是貢獻的驗收條件：以 0 或典型值填補讀取失敗、把「未跑」說成「已量測」、把能力宣稱當生效事實——任何一項都會被要求重做。
