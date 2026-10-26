# 第 276 輪：EDID CTA-861 擴充塊（U10-GP-0100 ～ U10-GP-0109）

日期：2026-10-27
範圍：主目錄 §GP 域十列（CTA 資料塊逐項）＋ VIC 知識包（§VC／§GP 的知識表需求）＋ 兩個實質缺陷修正

---

## 一、這一輪做了什麼

| 目錄 ID | 鍵 | 內容 | 狀態 |
|---|---|---|---|
| U10-GP-0100 | `edid.cta.rev` | CTA 修訂號、DTD 起點、資料塊數量、原生 DTD 數 | 實作 |
| U10-GP-0101 | `edid.cta.vic.{vic}`（家族） | 視訊資料塊的 VIC 清單，逐個解成時序（知識表 154 條） | 實作 |
| U10-GP-0102 | `edid.cta.ycbcr420` | Y420VDB／Y420CMDB 兩種塊（位圖對應視訊塊序號） | 實作 |
| U10-GP-0103 | `edid.cta.vcdb` | 視訊能力：可選量化、畫面比例、IT 內容、掃描過度 | 實作 |
| U10-GP-0104 | `edid.cta.vsdb.hdmi` | HDMI VSDB：實體位址 a.b.c.d、深色、最大 TMDS 時脈 | 實作 |
| U10-GP-0105 | `edid.cta.hfvsdb` | HDMI Forum VSDB：版本、最大 TMDS Character Rate、SCDC、FRL 速度代碼 | 實作 |
| U10-GP-0106 | `edid.cta.speaker` | 喇叭配置位圖（11 個具名喇叭組） | 實作 |
| U10-GP-0107 | `edid.cta.hdr` | HDR 靜態中繼資料：EOTF 四位元、靜態中繼資料類型、亮度碼值與換算 | 實作 |
| U10-GP-0108 | `edid.cta.colorimetry` | 色度學八個位元（xvYCC／sYCC／opRGB／BT.2020 等） | 實作 |
| U10-GP-0109 | `edid.cta.unknownblocks` | 本解碼器未實作的資料塊，**只列標籤號與長度，不命名** | 實作 |

新增檔案：

- `XinSpect.Decoders/Cta861.cs`——純解碼器，十一個公開方法全部帶 `SpecRef`，已註冊進
  `SpecRefRegistry.CoveredDecoders`（`DecoderHardeningTests` 的清單同步）。
- `XinSpect.Decoders/VicKnowledge.cs`——VIC 1–127＋193–219 共 154 條時序文字，
  出處＝Linux `drivers/gpu/drm/drm_edid.c` 的 `edid_cea_modes_1[]` 與 `edid_cea_modes_193[]`
  逐列註解（抓取日期 2026-10-27）。這是知識表裡**唯一分母可確認**的一張（規格定義的 VIC
  區間是封閉的：1–127 與 193–219，合計 154），因此 `KnowledgeCoverage` 給的是 100% 而不是留 0。
- `Services/EdidCtaFactsService.cs`——事實層，九把固定鍵＋逐 VIC 家族。

---

## 二、順手照出的兩個實質缺陷（都有真機證據）

### 1. `HdrCapabilityService` 從來沒找到過 HDR 區塊

舊碼在資料塊集合裡找 **資料塊標籤 6**：

```csharp
byte tag = (byte)((header >> 5) & 0x07);
if (tag == 0x06 && len >= 3)   // ← 舊碼
```

CTA-861-H Table 60 的標籤 6 是**保留值**；HDR 靜態中繼資料是 **標籤 7（使用延伸標籤）＋
延伸標籤 6**。本機顯示器 EDID 的那一段是 `E6 06 0D 01 A2 A2 10`——標頭 `0xE6` 的標籤是
**7**，所以舊碼一次都沒匹配到，HDR 卡永遠顯示「未找到」。

兩個附帶錯誤（同一個區塊）：

| 欄位 | 規格位置 | 舊碼讀的位置 |
|---|---|---|
| EOTF 位元 | `payload[1]` | `pos+2`（＝`payload[1]`，恰好對，但只在標籤判對時才輪得到） |
| 期望最大亮度碼值 | `payload[3]` | `pos+3`（＝`payload[2]`＝靜態中繼資料描述元 `0x01`）→ 算成 1 nit |
| 期望最小亮度碼值 | `payload[5]` | `pos+4`（＝`payload[3]`＝最大亮度碼值） |

以及換算式：CTA-861-G 是 **`50 × 2^(CV/32)`**，舊碼寫 `(CV+1)/2`。真機碼值 162 應該是
**1670.8 nits**，舊碼會算成 81.5。另外舊碼把 EOTF **bit1（傳統 gamma HDR 範圍）** 當成
「支援 HDR」——真正的 HDR 傳輸函數是 bit2（PQ／ST 2084）與 bit3（HLG）。

修法：整個 EDID 段改走 `Cta861`，與事實層共用同一份解碼器；
`Tests/EdidCtaTests.cs` 的 `HdrCapabilityRegressionTests` 用真機 EDID 釘住
「HDR 支援＝是、最大亮度 1671 nits、不再出現未找到」。

### 2. `nw.edid.summary` 的四個數字只有一個有來源

舊碼只從 `WmiMonitorID` 數了顯示器台數，另外三個（`ValidChecksums`／`HdrBlocks`／
`CtaExtensions`）**恆為 0**，而事實文字照樣寫成「checksum 合格 0、HDR 區塊 0、CTA 擴充 0」——
**沒讀被印成量到 0**，且剛好把上面那個 HDR 缺陷藏在「0」裡面。

修法：四個數字都由 `EdidService.ReadRawAll()` 的真 EDID 數出來（checksum 依 EDID 1.4
「128 位元組和 mod 256 ＝ 0」，HDR／CTA 由同一個 `Cta861` 解碼器判定）；
讀不到任何 EDID 時標 `ReadError` 並寫明「不是 0 台顯示器」。
數數字的核心抽成 `internal CountEdids(...)`，測試可直接餵假 EDID。

### 3. 三份「讀 EDID」的實作收斂成一份

`EdidService.Detect`（色域）與 `HdrCapabilityService.ReadFirstEdid`（只看第一份）各自走一次
`Enum\DISPLAY`；這一輪要第三個消費者，於是先收斂成 `EdidService.ReadRawAll()`
（依內容 SHA-256 去重，本機 6 筆登錄檔項目＝3 種內容）。
**同一個機碼三種走法，遲早會漂移成三種答案。**

---

## 三、刻意不做（寫在這裡，免得下一個人以為漏了）

- **VIC 只給時序文字，不給像素時脈與前後緣**：那些要走 DTD／DMT 那條路，兩者不得互相代言。
- **FRL 速率代碼不換算成 Gbps**：代碼→速率的對照表本版沒有出處收錄，值裡只寫代碼並明說。
- **未解讀的資料塊不命名**：只列標籤號／延伸標籤號與長度。音訊塊（SAD 解碼）也在其中——
  它不屬於 GP 域這十列，未來要收再收。
- **多顯示器用分段而不是逐台鍵**：事實鍵是機器層級的扁平字串，逐台鍵會讓鍵數隨插拔膨脹；
  逐 VIC 家族取聯集並在值裡點名是哪幾台宣告、哪幾台標原生。
- **不做 CTA 塊的寫入／模擬**：本輪全部唯讀，零網路。

---

## 四、帳面

| 項目 | 前 | 後 |
|---|---|---|
| 靜態事實鍵 | 1,099 | **1,108** |
| 動態家族 | 47 | **48**（`edid.cta.vic.`） |
| 未覆蓋基線 | 381 | **390** |
| 全套測試 | 6,999 | **7,040**（+41） |
| 知識表 | 5 張 | **6 張**（新增 CTA VIC，唯一分母可確認者） |

下一版 v2.92 的 preflight baseline＝**7,040**。
