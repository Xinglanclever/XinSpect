# XinSpect 功能 backlog(2026-09-25)

> **這份怎麼來的、可信度多少**:六個領域各派一個代理去讀碼提案。處理器、儲存/驗機兩個領域**讀碼成功、對照過現有功能**,清單乾淨。顯示卡/網路/安全/感測四個領域的代理被中轉站(api.justwoker.icu)連續 522/524 打掛,改由**本機 grep 逐一核對**——過程發現那四個領域「知識推測」的提案大半**早就實作了**,已在下方「已存在」剔除,只留真缺口。
>
> 每一條都標了資料來源。全部符合誠實主軸:讀不到要能誠實標示,不以典型值代替。

## 一、已確認存在,不要重做(本機 grep 核對)

- **顯示卡**:`NvmlInterop`(84 處)已讀節流原因、PCIe replay、板功耗 vs 功率上限、顯存 ECC、序號/UUID;`Edid/DisplayLink` 已有色域/HDR/亮度/刷新率/YCbCr/4:2:0。
- **感測/報告**:RAPL 瓦數(`0x611`)、風扇 RPM 與零轉、電池 AC/充電/循環、CPU 逐核 VID 電壓(`0x198`)、JSON 匯出、全域搜尋、資料來源標注——全在。
- **安全**:`BlueSquadron`/`PlatformTrust` 已讀 TPM、Credential Guard、BitLocker、Secure Boot/dbx、Code Integrity/WDAC、DEP/ASLR/CFG 緩解。
- **網路**:連接埠占用頁已用 `GetExtendedTcpTable`(連線對應行程);DHCP/閘道/MTU/RSS/藍牙已有。

---

## 二、重要新功能(50)

### 處理器(讀碼成功,已去重)

| # | 功能 | 資料來源與價值 |
|---|---|---|
| 1 | 逐核微碼版本一致性核對 | `IA32_BIOS_SIGN_ID(0x8B)` 逐核比對,不一致=載入失敗或竄改 |
| 2 | 硬體式 Top-down | `PERF_METRICS(0x329)`+`TOPDOWN.SLOTS`,Ice Lake 後才拿得到正確四桶 |
| 3 | 逐核 HWP 黃金核心圖 | `IA32_HWP_CAPABILITIES` Highest Perf,挑弱核/單執行緒釘核 |
| 4 | 實測 IPC 與分支預測失誤率 | 通用計數器編程 INST_RETIRED/CPU_CLK/BR_MISP,量 MPKI |
| 5 | TLB 幾何與位址轉換規格 | CPUID `0x02/0x18`、AMD `0x80000005-6` |
| 6 | 記憶體加密狀態(TME/SGX/SME) | `IA32_TME_ACTIVATE(0x982)`、CPUID `0x7/0x12`、AMD `0x8000001F` |
| 7 | 完整逐核 C-state 駐留表 | 差分 `0x3FC–0x3FE`+封裝 C2-C10,診斷不睡的耗電 |
| 8 | OS 推測執行緩解實況 | `SystemSpeculationControlInformation`,補完緩解另一半 |
| 9 | x2APIC ID 逐核拓撲分解 | CPUID `0x0B/0x1F` EDX,驗核心沒被韌體停用 |
| 10 | 逐核 P-state 請求與 EPP | `IA32_PERF_CTL(0x199)`+`IA32_HWP_REQUEST(0x774)`,直指哪顆核被壓 |
| 11 | RDT L3 分配/頻寬管制偵測 | CPUID `0x10`+CLOS 遮罩,揪被 hypervisor 夾住的 L3 |
| 12 | TSC 跨核同步性量測 | 逐核背靠背讀 TSC,不同步會讓所有計時說謊 |


### 儲存/驗機(讀碼成功,已去重)

| # | 功能 | 資料來源與價值 |
|---|---|---|
| 13 | NVMe 溫度感測器 1–8 與熱節流統計 | 0x02 紀錄 byte 200–231,揭隱藏熱節流,零額外 IOCTL |
| 14 | SATA SMART 門檻與「現正低於門檻」判定 | SMART READ THRESHOLDS(feature 0xD1),補門檻才判得出 failing-now |
| 15 | 全碟只讀壞軌掃描 | 未緩衝 ReadFile 掃整碟,OS 層真量到媒體失敗,唯讀不寫 |
| 16 | 裝置自我測試紀錄(NVMe/SATA Log 0x06) | 廠商級健康裁決,含失敗 LBA |
| 17 | 假容量寫入驗證(H2testw 式) | 對可用空間寫唯一圖樣回讀,認假 U 盤/貼牌 SSD 的決定性測試 |
| 18 | DDR4 模組即時溫度(TSOD) | SMBus 0x18–0x1F 逐條讀;無則誠實顯示無 TSOD |
| 19 | 記憶體 ECC 狀態(啟用/型別+可修正計數) | SMBIOS Type16/17 + WHEA,工作站二手機最該驗 |
| 20 | NVMe 命名空間清單與格式(Identify NS) | 逐命名空間容量/NUSE/512 vs 4K,揭重格/縮容 |
| 21 | 雙/四通道實際生效偵測 | SMBIOS Type17 locator + configured MT/s,判插錯槽只跑單通道 |
| 22 | NVMe WCTEMP/CCTEMP 門檻對照實溫 | Identify Controller byte 266–269,離熱節流還有多遠 |
| 23 | HPA/DCO 隱藏容量偵測 | 比對 IDENTIFY 可定址與 READ NATIVE MAX,擦拭/翻新手法 |
| 24 | SATA WWN 序號交叉核對 | IDENTIFY words 108–111 唯一碼,與序號/OUI 不符=竄改 |

### 顯示卡(NVML 已很全,以下是本機核對後的真缺口)

| # | 功能 | 資料來源與價值 |
|---|---|---|
| 25 | 顯存退休頁計數 | `nvmlDeviceGetRetiredPages`,現讀了 ECC 卻沒讀退休頁 |
| 26 | 顯存圖樣寫回測試 | compute 寫唯一圖樣回讀,`GpuRenderTestService` 沒做壞位元實測 |
| 27 | GPU 熱點/顯存結溫 | NVML hotspot/memory junction,現只讀溫度門檻 |
| 28 | NVENC/NVDEC/AV1 編解碼能力 | 硬體編解碼引擎與格式清單 |
| 29 | VRR/G-Sync/FreeSync 支援與啟用 | NVAPI/驅動 registry |
| 30 | HDCP 版本與輸出埠保護能力 | 逐輸出埠型別 |
| 31 | 多螢幕排列座標 + 各螢幕 DPI 縮放 | `EnumDisplayMonitors`+`GetDpiForMonitor` |
| 32 | GPU 驅動 TDR 逾時設定 | registry `TdrDelay`,解釋畫面凍結重置與當機 |
| 33 | 顯卡驅動殘留/多版本偵測 | registry 掃殘留,解釋畫面異常與升級衝突 |

### 網路(這塊做得薄,大多是真缺口)

| # | 功能 | 資料來源與價值 |
|---|---|---|
| 34 | 網卡收發錯誤/丟棄計數 | `GetIfEntry2` InErrors/InDiscards/OutErrors,線材/埠品質實測 |
| 35 | Wi-Fi 實測 RSSI/SNR/PHY 速率 | `WlanQueryInterface`,現在完全沒有無線品質 |
| 36 | 對閘道/DNS 延遲與丟包 | ICMP RTT/抖動/丟包,現有網速但無延遲面 |
| 37 | 路由表與預設躍點 | `GetIpForwardTable2` |
| 38 | ARP 鄰居快取 | `GetIpNetTable2`,揪可疑閘道/重複 IP |
| 39 | 網卡卸載狀態(checksum/LSO) | OID_GEN,RSS 已有其餘無 |
| 40 | WoL/網路喚醒設定 | OID,解釋異常喚醒 |
| 41 | MAC OUI → 真實網卡晶片廠商 | OUI 查表 |
| 42 | traceroute 逐躍點延遲 | 遞增 TTL 的 ICMP,診斷瓶頸位置 |
| 43 | 周邊 Wi-Fi AP 掃描與頻道佔用 | `WlanGetAvailableNetworkList`,無線干擾診斷 |

### 感測/超頻(大多已存在,真缺口只剩)

| # | 功能 | 資料來源與價值 |
|---|---|---|
| 44 | 機箱開啟偵測(Chassis Intrusion) | SMBIOS Type3 / SuperIO intrusion bit,拆機的實體證據 |
| 45 | 電壓軌 ATX ±5% 容差判定 | SuperIO 3.3/5/12V 軌,現只列數字不下判斷 |

### 安全(刪掉重複後的真缺口)

| # | 功能 | 資料來源與價值 |
|---|---|---|
| 46 | 已載入驅動比對微軟 HVCI 易受攻擊驅動封鎖清單 | 逐一雜湊比對官方 blocklist,BYOVD 最直接的偵測 |
| 47 | 事件記錄清除/稽核關閉偵測 | Security 記錄 EventID 1102 / auditpol,反鑑識痕跡 |
| 48 | 系統根憑證異常項稽核 | 列 Root 存放區標出非微軟信任根,中間人/監控憑證 |
| 49 | USB 大量儲存使用痕跡 | registry USBSTOR,鑑識,唯讀 |
| 50 | Defender 排除清單稽核 | `MSFT_MpPreference` exclusions,本機曾被塞 9 條排除 |

---

## 三、普通新功能(讀碼去重後的真缺口)

> **誠實結論**:普通層原本規劃 100 條,但本機逐一核對後,顯示卡/安全/感測/報告四域的「普通」提案**大半已實作**(PDF/時間軸/健康評分/硬體快照變更稽核/防火牆/更新/管理員/風扇零轉/AC 充電…)。硬湊 100 條就得把已存在的塞回來,違背誠實主軸。以下只列**真的還沒有**的。

**處理器(已讀碼,22 條)**:CPUID 全葉原始傾印、MONITOR/MWAIT 子狀態(0x05)、硬體預取器開關(0x1A4)、AMD 專屬葉位(0x8000001D/1E)、RDRAND/RDSEED 功能自測、微架構代號與製程、TSC Deadline Timer、即時單核有效頻率磚、混合架構 P/E 版面圖、逐核 EPP 目前值、指令延遲微基準、BCLK/TSC 抖動量測、Hypervisor 葉位詳解、快取幾何視覺化、CPU 深度匯出報告、渦輪倍頻階梯圖、Processor Trace(0x14)、AMX tile(0x1D/1E)、SMT 兄弟配對表、逐核溫度折線、NUMA 距離矩陣、功能位元對基準快照差異。

**儲存/驗機(已讀碼,21 條)**:磁碟寫入快取狀態、NVMe 韌體插槽(Log 0x03)、SATA IDENTIFY 進階欄位(NCQ 深度/外形/SATA 世代)、UDMA CRC 錯誤(199)標為線材問題、NVMe 平均 I/O 大小、檔案系統 dirty bit、GPT/MBR 分割與磁碟簽章、4Kn/512e 分割對齊、USB 橋接碟明確標示、NVMe Sanitize 支援旗標、SATA 安全清除/凍結狀態、SMART 整體健康回傳(0xDA)、分頁檔配置、DDR4 SPD 512B hex 匯出、DDR5 SPD 原始位元組、SPD rank/電壓/組織、NVMe APST 門檻表、磁碟即時吞吐/IOPS、SD/eMMC CID、記憶體可升級性(空槽/上限)、NVMe 命名空間 EUI64/NGUID。

**網路(本機核對:此域最薄,以下為真缺口)**:各介面 DNS 伺服器與 DHCP 租約到期、per-adapter 累計收發位元組(`MIB_IF_ROW2` InOctets)、網卡驅動版本/日期、VPN/虛擬介面卡列舉、藍牙已配對裝置清單。

**顯示卡(真缺口)**:各螢幕目前解析度/刷新率/方向(`EnumDisplaySettingsEx`,零命中)、顯示卡 PCI VID/DID/SVID/SSID 精確辨識 AIB 廠商、GPU 顯存時脈/頻寬使用即時。

**安全(真缺口,防火牆/更新/管理員/自動登入已排除)**:UAC 等級(`ConsentPromptBehavior`,零命中)、密碼與帳戶鎖定原則(`NetUserModalsGet`)、服務清單與異常自啟服務簽章(現有 DriverAudit 只查驅動不查服務)、系統時鐘/NTP 同步偏移(`w32tm`)、PATH 劫持偵測(可寫目錄+順序)。

**報告/UX(真缺口,PDF/時間軸/快照/搜尋已排除)**:硬體規格分享碼/QR、常見問題自動診斷精靈、「讀不到」原因彙整頁(集中列缺權限/不支援/被虛擬化——最貼合誠實主軸)、純文字系統摘要一鍵複製。

> 合計普通層真缺口約 **43+5+3+5+4 = 60 條**(處理器/儲存 43 已讀碼確認、另三域 17 本機核對)。其餘四域若還要更多,只會開始碰到已存在的功能。

