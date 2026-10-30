# 第 326 輪（U10 第三圈 WH-0003＋WH-0004）：WHEA 記憶體節區——「哪一條 DIMM」與「韌體沒說」的差別

> 既有事實只回答「有幾筆 WHEA 事件」。而使用者看到「已修正的硬體錯誤」時問的是三件事：
> **哪一種錯、哪一條 DIMM、單一位元還是多位**。這一輪把那三件事的欄位都解出來，
> 並且在最關鍵的地方停手：**韌體沒填的欄位不印成 0**。

## 一、為什麼要做

`os.ev.whea.internal`／`.pcie`／`.misc` 三把鍵是「事件 ID 計數」。那個形狀有一個
本專案反覆警告的缺陷：**它把「有事件」與「知道錯在哪」壓成同一個數字**。

WHEA 事件的 data 段裡有 CPER 記錄，記憶體節區（`WHEA_MEMORY_ERROR_SECTION`）逐欄
寫著模組號、bank、device、row、column、出錯位元、錯誤類型。那些欄位才是「哪一條 DIMM」。

## 二、做成什麼

- `XinSpect.Decoders/WheaMemorySection.cs`——CPER 記憶體節區解碼器（有 `[SpecRef]`）。
- `whea.mem.summary`——幾筆、各類型幾筆、多少筆韌體給了位址／模組。
- `whea.mem.rec.{序號}`——逐筆：類型、位址、模組、出錯位元、ErrorStatus 旗標。
- `whea.mem.boundary`——界線宣告。
- `Services/WheaMemoryRecordsReader.cs`——從事件記錄列舉候選事件（17／19／23／46／47）。

## 三、位移不是算的，是抄的

`cper.h` 有一組 `CPER_FIELD_CHECK` 巨集，那是編譯器會驗的位移宣告——
**比 struct 宣告更硬**，因為 struct 有對齊填充而 `CPER_FIELD_CHECK` 是逐欄位元組數：

| 位移 | 欄位 | 大小 |
|---:|---|---:|
| 0 | `ValidBits` | 8 |
| 8 | `ErrorStatus` | 8 |
| 16 | `PhysicalAddress` | 8 |
| 24 | `PhysicalAddressMask` | 8 |
| 32–46 | `Node`／`Card`／`Module`／`Bank`／`Device`／`Row`／`Column`／`BitPosition` | 各 2 |
| 48/56/64 | `RequesterId`／`ResponderId`／`TargetId` | 各 8 |
| 72 | `ErrorType` | 1 |

`cper.h` 的 CHECK 只到 `ErrorType`，後面 `Extended`(73)／`RankNumber`(74)／
`CardHandle`(76)／`ModuleHandle`(78) 照 struct 宣告順序接——**這件事寫在
`SectionBytes` 的註解裡**，因為它是「算出來的」而不是「查來的」，下一個人要能複核。

## 四、這一輪最重要的一格：ValidBits

CPER 的每一欄都有一個 ValidBits 位元，韌體可以說「這一欄我沒填」。位元順序照
`WHEA_MEMORY_ERROR_SECTION_VALIDBITS` 的宣告序：bit0 ErrorStatus、bit1 PhysicalAddress、
bit2 Mask、bit3 Node、bit4 Card、**bit5 Module**、bit6 Bank、bit7 Device、bit8 Row、
bit9 Column、**bit10 BitPosition**、bit11–13 Requester/Responder/Target、
**bit14 ErrorType**、bit15 RankNumber、bit16 CardHandle、**bit17 ModuleHandle**。

不讀 ValidBits 就印欄位值，等於把「沒填」當成「填了 0」——於是「Module 0」看起來
像「第 0 條 DIMM」，而實際上是「韌體沒說」。**這兩件事在處置上完全不同**：
前者你會去拔第一條 RAM，後者你只能繼續觀察。所以值裡寫：

> 模組：**韌體沒給**——不能指到某條 DIMM，只能說「有記憶體錯誤」

## 五、錯誤類型碼 0x00–0x0C，之外不命名

`cper.h` 有 13 個 `WHEA_MEMERRTYPE_*` define（0x00 未知到 0x0C 記憶體備援切換）。
表只收這 13 個；**0x0D 以上回 `null`**，值裡寫「本版沒有這個碼的出處，不命名」。

特別注意 0x01 是 **NOERROR**——「記錄存在但這一節區沒有錯」。它與 0x00「未知」
是兩件事，值裡寫得很清楚。

`ErrorTypeHint` 是判讀輔助（「可更正且指出位置」「指向路徑或控制器」），
但每一筆記錄的值裡都跟著寫**「不判斷該換哪一條 RAM」**——避免這個輔助變成判決。

## 六、本版最可能的真實情形：一筆都解不開

事件 data 段把 `WHEA_ERROR_RECORD` **包起來**的那一層，SDK 標頭沒有宣告（只宣告結構，
不宣告事件裝載格式）。所以：

- 節區本身的佈局有 `cper.h` 可核 → 解碼器寫好了、有金標；
- 但「從事件 blob 的哪裡開始是節區」**沒有可核的出處** → **本版不拆**。

於是 `WheaMemoryRecordsReader` 列出事件（ID、時間），節區一律標 `null` 並附
`BlobNotParsed` 理由。這時彙總寫的是：

> N 筆 WHEA-Logger 事件落在時間窗內，但**一筆的節區都解不開**——原因：…
> 所以本鍵只說得出「有幾筆事件」，說不出「哪一種錯、哪一條 DIMM」
> ・事件 ID 的分佈（46×2、47×1）也只是入口，不是錯誤類型

**這比猜出來的「第 0 條 DIMM 單一位元錯誤」有用得多。** 而解碼器不是白寫的：
一旦有人補上外層佈局，`Collect(records)` 直接就能用，金標測試已經在那裡等。

## 七、帳面

| 項目 | 前 | 後 |
|---|---:|---:|
| 事實鍵 | 1,158 | **1,160** |
| 事實域 | 152 | **153**（新增 `whea` 域） |
| 一鍵域／≤3 鍵域／≥20 鍵域 | 34／64／12 | **34／65／12** |
| 動態家族 | 62 | **63**（`whea.mem.rec.`） |
| 服務檔 | 647 | **649** |
| 全域 C# 檔 | 1,535 | **1,539** |
| 測試檔 | 610 | **611** |
| `[Fact]` 方法 | 6,258 | **6,277** |
| 專案測試基線 | 7,585 | **7,605** |
| 覆蓋基線 | 432 | **435** |

## 八、怎麼複算

```powershell
# 列出時間窗內的 WHEA 事件（不拆 data 段）
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'} -MaxEvents 20 |
  Select-Object Id, TimeCreated
```

事件 ID 的語意要回 manifest（`wevtutil gp Microsoft-Windows-WHEA-Logger /ge:true /gm:true`）——
本機實測 17／19／23／47 的訊息都是同一句「發生已修正的硬體錯誤」，
**只有 46 是「發生嚴重的硬體錯誤」**。所以「看 ID 猜類型」不成立：
類型在節區裡，而節區要外層佈局才進得去。