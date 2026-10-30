# 第 323 輪（U10 第三圈 NP-0003）：USB 根集線器埠數——「還能不能再接」與它不保證的事

> USB 事實在此之前只有三把：控制器數、裝置數、掛最多的控制器。
> 三把都在回答「現在掛了什麼」，沒有一把回答使用者最常問的那句「**還能不能再接**」。
> 埠數不在 WMI 裡、不在登錄檔裡——它只在 hub descriptor 裡。

## 一、為什麼要做

`usb.controllers`／`usb.devices`／`usb.busiest_controller` 三把全部來自
`Win32_USBControllerDevice` 的相依對。那個形狀是「誰掛在誰下面」的計數，
**沒有任何欄位是「這顆集線器有幾個埠」**。

而「還能接幾個」的答案需要兩個來源各答一半：

| 問題 | 誰知道 | 怎麼拿 |
|---|---|---|
| 這顆根集線器有幾個埠 | 硬體（hub descriptor） | `IOCTL_USB_GET_NODE_INFORMATION` |
| 已經接了幾個 | PnP 樹 | 第 309 輪就有的 `DeviceDiagnosticReport.Children` |

這一輪把兩邊並排，而「空埠」就是那個減法。

## 二、做成什麼

- `XinSpect.Decoders/UsbHubNode.cs`——`USB_NODE_INFORMATION` 的解碼器（有 `[SpecRef]`）。
- `usb.hub.summary`——幾顆根集線器、共幾個埠、在場子裝置幾個、空埠幾個。
- `usb.hub.ports.{序號}`——逐顆根：型別（xHCI／EHCI／UHCI-OHCI）、埠數、在場子裝置數、空埠。
- `usb.hub.boundary`——界線宣告（見第四節）。

**沒有新增列舉**：根集線器清單與子裝置數都來自 `DeviceDiagnosticReport`，
與 `os.dev.*`、`os.devtree.*` 同一次列舉。新增的只有每顆根一個唯讀 IOCTL。

本機實測（三顆根，全部回得到）：

| 根集線器 | 埠數 | 在場子裝置 |
|---|---:|---:|
| `USB\ROOT_HUB30\4&143F169D&0&0` | 26 | — |
| `USB\ROOT_HUB30\5&15606325&0&0` | 4 | — |
| `USB\ROOT_HUB30\5&7D03CA&0&0` | 4 | — |

## 三、這一輪最容易寫錯的一格：減法會變成負的

「空埠＝埠數−在場子裝置數」在正常情況下成立，但**複合裝置與內建裝置可以不佔獨立埠**，
於是子裝置數可能比埠數多。那時候：

- 印出 `-1` 是在說一個不存在的事；
- 印出 `0` 是把「算不出來」寫成「沒有空埠」。

所以這一輪的處置是：**不給數字，並說出為什麼**——
「在場子裝置 3 個比埠數多——空埠數在這裡不成立」。總覽在同樣情形下也**不給空埠總數**，
而是列出「哪一種情形讓這個數字不成立」。

「讀不到」也一樣：某一顆根集線器開檔失敗（非管理員會拿到 `ERROR_ACCESS_DENIED`）時，
**那一顆仍然佔一個家族成員**（標 `InsufficientPrivilege`）、**不計入總數**，
而總覽因此變成 `Unknown` 並寫明「已知的 N 個只是下限」。
讓它從家族裡消失，會讓「少了一顆」看起來像「只有兩顆」。

## 四、界線：埠數不是「可用頻寬」

`usb.hub.boundary` 寫四件事：

1. **控制器版本與支援速度位在 MMIO**（PCI BAR 上的 CAPLENGTH 與其後的第一個暫存器）——
   usermode 讀不到。本版**不從驅動版本反推**，也不猜。這一條直接對應候選項 NP-0002
   為什麼在本版做不到。
2. 不遞迴**外接**集線器（那要逐層開檔，屬明示觸發）。
3. 每個埠的**協商速度**（`USB_NODE_CONNECTION_INFORMATION_EX.Speed`）本版未讀。
4. 埠數是**硬體描述**，不是可用頻寬——20 個空埠不等於能插 20 顆大功率裝置。

## 五、一個順手發現的位元組陷阱（沒有寫進事實）

hub descriptor 的 `wHubCharacteristics` 在本機三顆根上都是 `0x0200`。
USB 2.0 的 hub descriptor 裡，那個欄位只定義到 bit 6；bit 9 不在定義範圍內。

**讀不出規則就不編規則。** 所以 `UsbHubNodeDecoder` 只解到「埠數」所需的欄位
（`bLength`／`bDescriptorType`／`bNumberOfPorts`），`wHubCharacteristics`、
`bPwrOn2PwrGood`、`bHubContrCurrent` 一律不解讀，並把這件事寫在 `[SpecRef]` 裡。
新一代 hub descriptor 的欄位語意本版沒有可核的出處，硬解只會製造看起來很專業的錯誤。

## 六、錯的時候怎麼說

| 情形 | `Availability` | 值裡寫什麼 |
|---|---|---|
| 開檔失敗（錯誤 5） | `InsufficientPrivilege` | 「埠數需要對 hub 介面的讀寫開檔權（管理員）」 |
| IOCTL 失敗或其他錯誤碼 | `ReadError` | 帶 Win32 錯誤碼 |
| 輸出解不成 hub 節點 | `ReadError` | 「輸出 N 位元組但解不成 hub 節點——不猜」 |
| 描述符型別不是 `0x29` | `ReadError` | 「不是 hub descriptor——不採信埠數」 |
| 這台機器沒有根集線器 | `NotApplicable` | 「不是讀不到埠數，是沒有根集線器可數」 |

## 七、帳面

| 項目 | 前 | 後 |
|---|---:|---:|
| 事實鍵 | 1,150 | **1,152** |
| 動態家族 | 61 | **62**（`usb.hub.ports.`） |
| `usb` 域 | 3 把 | **5 把** |
| 一鍵域／≤3 鍵域／≥20 鍵域 | 34／64／12 | **34／63／12** |
| 服務檔 | 644 | **645** |
| 全域 C# 檔 | 1,526 | **1,529** |
| 測試檔 | 606 | **607** |
| `[Fact]` 方法 | 6,213 | **6,225** |
| 專案測試基線 | 7,540 | **7,552** |
| 覆蓋基線 | 424 | **426** |

`usb` 域離開「≤3 鍵域」，所以那個統計從 64 掉到 63——它不是變少，是這一域長大了。

## 八、怎麼複算

```powershell
Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\ROOT_HUB*' } |
  Select-Object InstanceId
```

埠數本身要開裝置介面查 IOCTL（本輪的 `SystemUsbHubProbe` 就是那條路），
沒有等價的內建命令；`Get-PnpDevice` 只給得到「有幾顆根」，給不到「幾個埠」。