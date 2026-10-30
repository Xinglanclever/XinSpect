# 第 324 輪（U10 第三圈 AC-0001＋AC-0002）：ACPI 根指標表這一層——驗不了的部分也要說清楚在哪一層

> AC-0001 要「自己驗 RSDP 的簽章與雙總和」。這一輪先量能不能驗，
> 答案是不能，而**原因是可以定位的**——那就把原因本身變成事實。
> 另外，一份外部審查指出上一輪 USB 埠數有三處實質缺陷，逐條回規格核對後確認成立，一併修掉。

## 一、先量，再寫

候選項 AC-0001 的前提是「usermode 讀得到 RSDP」。實測兩條路徑：

| 路徑 | 結果 |
|---|---|
| `\\.\PhysicalMemory` | `ERROR_FILE_NOT_FOUND`（2） |
| `\\?\GLOBALROOT\Device\PhysicalMemory` | `ERROR_INVALID_HANDLE`（6） |

而 `EnumSystemFirmwareTables('ACPI')` 回 **29 張表**，逐張看過簽章：

```
DBGP MCFG FACP SRAT MSCT SLIT DMAR APIC HPET FPDT FIDT MIGT PCAT PCCT RASF
SVOS WDDT OEM4 OEM1 SSDT NITR SSDT LPIT WSMT SSDT DBG2 WPBT TPM2 BGRT
```

**沒有 RSDT、沒有 XSDT。**

於是「缺的表是沒有還是看不到」的正確答案是：**這一層是 OS 沒給**——不是韌體沒有，
也不是本版漏讀。RSDP 的簽章與雙總和（前 20 位元組、前 36 位元組各自加總為 0）本版
**無法第一手驗證**，這件事寫成 `ac.root.boundary`，而不是假裝驗過、也不是留白。

## 二、能驗的部分照驗

FADT（簽章 `FACP`）是唯一會告訴我們「根以下那兩張表在哪」、而**自己列舉得到**的表。
新增解碼器 `XinSpect.Decoders/AcpiFadt.cs`（每個公開成員都有 `[SpecRef]`），解四件事：

| 欄位 | 位移 | 本機值 |
|---|---|---:|
| `Firmware Control`（FACS，u32） | 36 | `0x4D19C000` |
| `DSDT`（u32） | 40 | `0x4C031000` |
| `X_FIRMWARE_CTRL`（u64，ACPI 2.0 起） | 132 | `0x4D19C000` |
| `X_DSDT`（u64） | 140 | `0x4C031000` |

（FADT 修訂 6、長度 276。）

**這一格的重點是「兩欄是否一致」**：32 位與 64 位相同 → 這張表在 4 GiB 以下，
只走 32 位指標的舊 OS 也看得到；**不同** → 只有 64 位欄指得到，也就是 AC-0002 要區分的
「缺的是沒有還是看不到」在這一層的具體形狀。現在它是可查的一格，不是一段說明文字。

## 三、三把鍵

| 鍵 | 回答什麼 |
|---|---|
| `ac.root.summary` | 列舉到幾張表／幾種簽章；RSDT／XSDT 在不在（**四種情形分開寫**）；韌體自述的 ACPI 世代 |
| `ac.root.dsdt` | FADT 指到的 FACS／DSDT；兩欄是否一致；是否在 4 GiB 以下 |
| `ac.root.boundary` | 界線宣告：RSDP 驗不了、缺的是哪一層、往上需要什麼 |

`ac.root.summary` 的四種情形不是「有沒有」的布林，而是四句話：

- 兩者都不在 → 「**RSDT 與 XSDT 都不在列舉裡——根指標表這一層是 OS 沒給，不是韌體沒有**」
- 只有 RSDT → 「ACPI 1.0 形狀，或 OS 只給 RSDT」
- 只有 XSDT → 「ACPI 2.0 以後常見：OS 只給 XSDT」
- 兩者都在

## 四、界線寫在值裡

- 「位址是實體位址，**本版不去讀那些記憶體**——說得出位址不等於看得到內容」
- 「FADT 只告訴我們**這兩張**在哪；其餘表的位址在根指標表裡，而那一層讀不到，
  所以『全部表都在 4 GiB 以下』這種話本版說不出口」
- 「要真的驗 RSDP 需要核心層（或實體記憶體映射），那不在本專案的 usermode 範圍內」

## 五、順手收下另一份審查的 USB 修正

一份外部審查指出第 323 輪的三處實質缺陷。**逐條回規格核對後確認全部成立**：

### D1 — `bNumberOfPorts` 是單一位元組

`usbspec.h:902-910`（整份包在 `PSHPACK1.H` 內，位移＝宣告順序）：

```c
typedef struct _USB_HUB_DESCRIPTOR {
    UCHAR   bDescriptorLength;   // +0
    UCHAR   bDescriptorType;     // +1
    UCHAR   bNumberOfPorts;      // +2  ← 一個 byte
    USHORT  wHubCharacteristics; // +3
    ...
```

hub descriptor 從輸出緩衝區 **+4** 起算，所以埠數在緩衝區位移 **6**，而
`wHubCharacteristics` 從位移 **7** 起。原本讀 `AsSpan(6, 2)` 會把特性低位元組
黏進埠數：4 埠 + 特性 `0x0009` → `0x0904` ＝ **2308 個埠**。

### D2 — IOCTL 的 input 必須是 `USB_NODE_INFORMATION` 指標

`usbioctl.h:550`：「Input: `Irp->AssociatedIrp.SystemBuffer` - pointer to
`USB_NODE_INFORMATION` structure」。傳 `null` ＋ 長度 0 等於叫驅動猜，
回 `ERROR_INVALID_PARAMETER`（87）。同 repo 的 `UsbLinkService.WalkHub` 早就做對了
（同一個 buffer 當 input 與 output）。

**這件事最陰險的地方**：`ReadError` 是誠實的三態機制，它把一個**程式錯誤**
包裝成了「這台機器讀不到」——用正確的態度說錯誤的原因。

### D3 — USB 3.0 的 hub descriptor 是 `0x2A`

`usbspec.h:929`：`#define USB_30_HUB_DESCRIPTOR_TYPE 0x2A`。只認 `0x29` 等於
**現代機器上的每一個 xHCI 根永遠讀不到埠數**，而能讀到的是 USB 2 根——
偏偏那正是 D1 會算成兩千多的那一種。

### E6 — 為什麼測試一路綠燈

測試替身用 `WriteUInt16LittleEndian(AsSpan(6,2), ports)` 造資料，
**等於把實作的錯誤假設也寫進了替身**。替身必須跟**規格**一致，不能跟實作一致。
現在替身改成 `b[6] = ports` ＋ `AsSpan(7,2) = characteristics`（預設 `0x0009`，真機常見值），
並新增兩條測試：`埠數只取一個位元組_集線器特性不黏進來`、`超級速度集線器_0x2A也要認`。

## 六、另外五條（同一次審查的其餘條目）

| 代號 | 內容 | 這一輪的處置 |
|---|---|---|
| D5 | ghost 根集線器污染計數 | 只數 `IsPresent` 的根；殘影另計並在值裡寫「另有 N 顆不在場的殘影未計入」 |
| D6 | `LoadDeviceTruth` 失敗後 `UsbHubFacts` 留舊值 | `catch` 補上第三組重設——否則畫面上會出現「列舉失敗」與「上一輪的埠數」並存 |
| D7 | 系統碟寫死 `C:` | 由 `Environment.SystemDirectory` 推出代號；兩個 NTFS 服務**共用同一份推導**（各自寫一份遲早一個改一個沒改） |
| D8 | USB IOCTL 無逾時 | 包 `DiskIo.GuardedProbe`，2 秒上限；**逾時與 ReadError 分開講**（沒回應 ≠ 回了但不能用） |
| D12 | `LoadAcpi` 列舉 9 次 | `OnceAcpiTableSource` 裝飾器：各服務簽名一個沒動，只在 `LoadAcpi` 第一行包一層 |
| D13 | `LoadMultimedia` 重複呼叫 | 刪掉重複那行 |

D12 用**裝飾器**而不是改七支服務的簽名，理由寫在類別註解裡：改簽名要動七個檔案與它們的測試，
而「這一批事實來自同一次列舉」是**呼叫端**的責任，不是每支服務的。`ReadCount` 讓
「只列舉一次」變成可測的斷言，而不是靠讀程式碼相信。

## 七、帳面

| 項目 | 前 | 後 |
|---|---:|---:|
| 事實鍵 | 1,152 | **1,155** |
| `ac` 域 | 58 把 | **61 把** |
| 6.2 的 ≥20 鍵域合計 | 399 把 | **402 把** |
| 服務檔 | 645 | **646** |
| 全域 C# 檔 | 1,529 | **1,533** |
| 測試檔 | 607 | **609** |
| `[Fact]` 方法 | 6,225 | **6,245** |
| 專案測試基線 | 7,552 | **7,573** |
| 覆蓋基線 | 426 | **429** |

動態家族不變（62）——這一輪沒有新的動態前綴。

## 八、怎麼複算

```bash
# FADT 的四個位址欄（需管理員？不用——EnumSystemFirmwareTables 是零特權）
python - <<'PY'
import ctypes, struct
k = ctypes.windll.kernel32
k.GetSystemFirmwareTable.restype = ctypes.c_uint32
sig = int.from_bytes(b'FACP','little')
size = k.GetSystemFirmwareTable(0x41435049, sig, None, 0)
buf = ctypes.create_string_buffer(size)
k.GetSystemFirmwareTable(0x41435049, sig, buf, size)
b = bytes(buf)
print("rev", b[8], "FACS", hex(struct.unpack_from('<I', b, 36)[0]),
      "DSDT", hex(struct.unpack_from('<I', b, 40)[0]),
      "X_DSDT", hex(struct.unpack_from('<Q', b, 140)[0]))
PY

# 根指標表不在列舉裡（29 張，逐張看簽章）
```

「沒有 RSDT／XSDT」這件事本身就是**這一輪的結論**，所以它必須能被別人重跑出來。