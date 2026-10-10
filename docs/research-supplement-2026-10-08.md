# 曦覽調研補充（2026-10-08）

> 目的：用外部資料**驗證／修正／深化**擴展總藍圖（`docs/xinspect-master-plan-2026-10-08.md`）。
> 方法：公開規格、廠商 datasheet、同業工具（HWiNFO／AIDA64／fwupd）、開源工具（f3／ipmiutil）交叉。
> 原則：只用來**修正我們的假設**，不引入任何需要連網的功能。

## 1. DDR5 SPD Hub —— 我們的實作對了，但要補三件事

查到的事實：
- DDR5 SPD Hub 的**裝置型別是 0x5118**（Bus Pirate 文件實測；Rambus SPD5118-Gxx、Montage M88SPD5118 皆同）。
- **MR11 是切頁暫存器，且它是揮發性狀態**——MSI 論壇 2026-07 有真實案例：某條 DDR5（SPD5118，Montage 晶粒）的 MR11
  在 S3 睡眠後殘留，導致模組被辨識成「2GB ghost DIMM」。**這是真機上會踩到的坑。**
- **MR5 用來偵測 SPD5 Hub 是否內建溫度感測器**（memtest86plus issue #302）。
- DDR5 SPD = 1024 bytes（16 區塊 × 64 bytes）；JEDEC 有專門的 **JESD300-5B01（SPD5118 HUB 標準）**，
  與我們引用的 JESD400-5（SPD 內容）是**兩份不同文件**。
- HWiNFO v8.45 之後新增 **DIMM SPD Write-Protection 管理**（DDR5）；DDR5 另含 **XMP 3.0** 設定檔。

對曦覽的具體修正／建議：
1. **補 SpecRef**：`SpdReader` 引用的應該是 **JESD300-5B01（Hub 介面）**，內容解碼才是 JESD400-5（SPD Contents）。目前只引後者。
2. **MR11 要當揮發狀態處理**：我們已有「讀完復位回第 0 頁」，這正確；但應再加**讀前先歸零**與「偵測到非預期頁」的裁決，
   因為別的軟體（或睡眠殘留）可能把 MR11 留在非 0 頁 → 會讀到錯的 128 bytes。這是三態誠實的題目。
3. **補 on-DIMM 溫度**：讀 **MR5** 判斷有無溫度感測器，有就讀 hub 溫度計（而非只做 DDR4 的 TSOD）。
4. **SPD 寫保護狀態**：可唯讀回報 SPD5118 的寫保護設定（對照 HWiNFO 的做法），作為「這條記憶體可被改寫」的事實。

## 2. Boot Guard／PSB —— Linux 有路，Windows 沒有現成 API

查到的事實：
- Linux 側靠 **fwupd 的 Device Security / `fwupdmgr security`** 呈現 Boot Guard／PSB 狀態（Fedora 討論串即為此）。
- Intel 有 Boot Guard 的 datasheet 章節；AMD PSB 有 IOActive 的逆向研究（PSB 由 OEM 熔斷決定）。
- 社群普遍結論：**Secure Boot 沒有 Boot Guard 打底是空的**（要靠 PCR 7 釘）。

對曦覽的建議：
- Windows 上**沒有官方直接 API** 讀 Boot Guard/PSB 熔斷狀態 → 維持 🧪，不要硬報值。
- 可落地的是**間接推斷**：以 **TPM PCR 0（CRTM）＋ measured boot log** 佐證「開機鏈自韌體量測起點可信」，
  並在報告中明寫「這是量測鏈推論，不是 Boot Guard 熔斷狀態」——符合三態誠實。
- 這正好支撐總藍圖 §三-1「開機鏈離線存證」：**我們做得到的是 PCR＋SPI 雜湊＋UEFI 變數的可驗證存證，不是讀熔斷位**。

## 3. 伺服器帶外（Redfish / IPMI）—— 重要警訊

查到的事實：
- **Redfish（DMTF REST/JSON）正在取代 IPMI**，主流伺服器（Dell/Supermicro/Intel OpenBMC）都提供；
  FRU 可讀序號／料號／asset tag，另有 firmware inventory 端點。
- 對等工具：`ipmitool`、`ipmiutil`。
- **關鍵警訊**：Redfish 與 IPMI-over-LAN 本質都是**對 BMC 的網路連線**；即使在帶內，
  也是走 Redfish Host Interface（仍是一個網路介面）。

對曦覽的建議（修正先前的樂觀）：
- 我先前說「Redfish 走 loopback 127.0.0.1 不違反零網路 API」——**這只在 BMC 提供本機 loopback 時成立，多數平台不是**。
  必須修正為：**帶外屬網路功能，要走「使用者主動觸發的白名單」**（與網速測試同一類），或明確列為不做的項目。
- 不變的是：**KCS/SSIF 帶內**仍受 LIMITATIONS #3 限制（無硬體可驗證不出貨）。

## 4. 工業 PTP／TSN —— 找到一個「現在就能做」的切入點

查到的事實：
- **Windows Time 服務會向 IP Helper 查詢「宣告支援 IEEE 1588 硬體時間戳」的網卡**（微軟官方問答，2026-03）。
  也就是說，「這張網卡有沒有宣告硬體時間戳」是**可唯讀列舉**的。
- TSN = IEEE 802.1AS（gPTP）＋ 802.1Qbv 等；工業剖面為 **IEC/IEEE 60802**。
- TI 的 SSZTAU5 說明為何要高精度就得把時間戳做進 PHY。

對曦覽的建議：
- **可立即落地**（純軟體、唯讀）：列舉各網卡是否宣告 IEEE 1588 硬體時間戳能力 → 作為「這台適不適合運動控制」的
  第一項事實，接進工業 Profile（總藍圖 §6.3）。
- PTP 同步偏移／主從狀態需要 PTP 堆疊或驅動支援 → 維持 🧪。

## 5. 新硬體速率對照（補進鏈路真相頁）

查到的事實：
- **Thunderbolt 5**：80 Gbps 雙向、Boost 可到 **120 Gbps**、內含 **PCIe 4.0 x4（64 Gbps）**。
- **USB PD 3.1**：可協商到 **240W**（48V EPR）。
- **CXL 3.1** 規格已公開（computeexpresslink.org）。
- 2026 主板主流仍為 **PCIe 5.0 ＋ USB4 ＋ Wi-Fi 7**；Intel Nova Lake 已通過 USB4 80Gbps／PCIe 5.0 合規測試。

對曦覽的建議：
- `TbUsb4Service`／`UsbLinkService`／`DisplayLinkService` 的速率表補上 **TB5／USB4 v2（80/120 Gbps）與 PD 3.1 240W**，
  這樣「鏈路真相」在新機器上才不會顯示成舊世代。

## 6. 儲存：NVMe 命名空間屬性與反仿冒

查到的事實：
- Windows 的 stornvme 支援 **NOIOB／NPWG／NPWA／NPDG／NPDA／NOWS**（Win11 與 Server 2022 起）
  → 這些是**可讀的**命名空間最佳 I/O 邊界屬性，對耐用度與對齊很有價值。
- **NVMe 2.0 的 ZNS** 旨在降低寫入放大；耐久度標準參考 SNIA 白皮書、TBW 定義。
- 反仿冒工具：**f3probe／f3fix（Fight Flash Fraud）、ValiDrive、h2testw**。
  **f3probe 的價值在於「非破壞性」**：用首／尾區塊快速判斷真實容量。

對曦覽的建議：
- 新增**唯讀**的 NVMe 命名空間最佳 I/O 邊界（NOIOB/NPWG/NPWA/…）事實。
- 現有 `FakeCapacityTestService` 是 H2testw 式**破壞性寫滿**；可**加一個 f3probe 式的快速唯讀預檢**
  （讀首／尾區塊比對），讓使用者先知道「這顆可疑」再決定要不要跑完整寫入驗證——**先唯讀、後寫入**，更符合同意閘門精神。
- ValiDrive 的「隨機區塊抽樣」可作為第三層，三者並列時標明各自能證明什麼。

## 7. 感測引擎現況

- 曦覽已用 **LibreHardwareMonitorLib 0.9.6**（本機 csproj 可證）；2026 年它仍是 .NET 生態的主要選擇。
- 沒有出現足以取代它的新 .NET 函式庫 → **維持現狀**，不必換引擎。

## 8. 對總藍圖的三處修正（重點）

1. **§三-1 開機鏈存證**：明確限定為「PCR＋SPI 雜湊＋UEFI 變數的可驗證存證」，**不是讀 Boot Guard/PSB 熔斷位**。
2. **§5.1 伺服器帶外**：Redfish/IPMI **是網路功能**，須列入「使用者主動觸發白名單」，不能當成本機唯讀。
3. **§1.3 DDR5**：`SpdReader` 的 SpecRef 應補 **JESD300-5B01**；並把 **MR11 揮發狀態**（ghost DIMM 案例）與 **MR5 溫度感測器偵測**納入。
