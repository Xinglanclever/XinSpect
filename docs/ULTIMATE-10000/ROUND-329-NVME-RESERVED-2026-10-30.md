# 第 329 輪（U10 第三圈 NV-0029）：NVMe Identify 保留區——順手抓到一個已在出貨的位移錯誤

> 候選項寫得直白：「規格要求保留區為零；**非零就是硬體自述的矛盾**，
> 如實回報比修掉更有價值」。這一輪把它做出來，
> 而過程中在**既有的 WCTEMP 判定**上抓到一個真的、已在出貨的位移錯誤。

## 一、為什麼要做

`nvme.*` 在此之前**沒有任何一把鍵**——NVMe 的事實散在 `smart.*` 的識別列裡（那是文字，
不是可對帳的數值）。而「保留區非零」這種矛盾最適合做成事實：可複算、可指定位移、
不依賴任何廠商私有定義。

## 二、八段保留區的邊界是算出來的

`work/nvme.h` 的 `struct nvme_id_ctrl` 逐欄累加得出：

| 名稱 | 位移 | 長度 |
|---|---:|---:|
| `rsvd100` | 100 | 11 |
| `rsvd134` | 134 | 122 |
| `rsvd352` | 352 | 160 |
| `rsvd534` | 534 | 2 |
| `rsvd544` | 544 | 224 |
| `rsvd1024` | 1024 | 768 |
| `rsvd1804` | 1804 | 2 |
| `rsvd1807` | 1807 | 241 |

**合計 1,530 位元組。**

（第一次我手算成 1,528——漏了 `rsvd534` 那 2 位元組。測試當場紅了，
才回頭逐項核對。這件事本身值得寫下來：這些數字**不能憑印象**。）

## 三、`vs` 不是保留區——這一條決定這把鍵有沒有用

`vs`（3072–4095，1024 位元組）是**廠商區**，非零完全正常。
把它算進「保留區」，**每一顆硬碟都會「有問題」**——那這把鍵就廢了。

所以 `VendorSpecificOffset = 3072` 是個明確常數，且有一條測試把它釘住：
把整個 vs 區塞成 `0xFF`，掃出來的非零數必須是 **0**。

同樣地，有定義的欄位（`vid`／`sn`／`wctemp`／`mdts`）非零也不算——
測試裡也逐欄驗了一次。

## 四、順手抓到的真缺陷：WCTEMP 位移錯了

寫測試時要造一份「保留區全零但有定義欄位非零」的樣本，於是我去核對 `wctemp` 的位移。
既有程式碼（`StorageSmartService.EvaluateWctemp`）與它的測試都寫 **`0x14A`**。

但 `nvme.h` 的逐欄累加給的是 **`0x10A`**（266）：

```
oacs    256 0x100 len=2
...
apsta   265 0x109 len=1
wctemp  266 0x10A len=2
cctemp  268 0x10C len=2
...
sanicap 328 0x148 len=4
hmminds 332 0x14C len=4
```

**`0x14A` 落在 `sanicap` 裡面**——那是個 32 位元的 SANITIZE 能力位圖，
不是溫度門檻。`libnvme-types.h`（另一份獨立標頭）也給 `0x10A`。

**這意味著 `smart.wctemp.*` 這把已在出貨的鍵，一直在拿 SANITIZE 能力位圖當溫度門檻。**
它不會拋例外、不會紅燈，只會在畫面上印出一個看起來像溫度的荒謬數字
（或剛好是 0 時就說「未提供」——更難發現）。

已修正三處：`StorageSmartService.EvaluateWctemp`、`SmartFailingNowFactsService` 的來源字串、
`SmartThresholdTests` 的樣本建構。**測試原本也寫 `0x14A`** ——
和上一輪 USB 埠數那次一模一樣：**替身與實作用了同一個錯誤假設，所以一路綠燈**。

## 五、做成什麼

- `XinSpect.Decoders/NvmeReservedScanner.cs`——八段保留區掃描（`[SpecRef]` 逐段位移）。
- `nvme.rsvd.summary`——幾顆、掃了幾段、幾顆有非零、合計幾個位元組。
- `nvme.rsvd.disk.{序號}`——逐顆：哪一段非零、幾個位元組、首個非零在 `0x??`、值是多少。
- `nvme.rsvd.boundary`——界線宣告。

**沒有新增裝置查詢**：與 `smart.*` 同一條 `TryReadNvmeIdentify`，只是多掃八段。

## 六、值裡最重要的三句

> ・這是**硬體自述的矛盾**：規格說這些位移應該為零
> ・不代表硬碟壞了；也不代表廠商在藏東西——最常見的原因是韌體把某些內部欄位沿用在那裡
> ・本鍵只指出位移與值，**不猜那些位元組是什麼**

第三句是關鍵：非零的位元組**沒有公開定義**，硬要去解讀只會製造看起來很專業的錯誤。

## 七、「掃過且乾淨」與「沒掃到」是兩種斷言

讀不到 Identify 時標 `ReadError`，**不是全零**。
全零是「我掃了 1,530 位元組，每一個都是 0」；沒掃到是「我不知道裡面是什麼」。
把後者印成前者，正是這個專案最在意的那類錯誤。

## 八、帳面

| 項目 | 前 | 後 |
|---|---:|---:|
| 事實鍵 | 1,164 | **1,166** |
| 事實域 | 154 | **155**（新增 `nvme` 域） |
| 一鍵域／≤3 鍵域／≥20 鍵域 | 34／66／12 | **34／67／12** |
| 動態家族 | 65 | **66**（`nvme.rsvd.disk.`） |
| 服務檔 | 653 | **654** |
| 全域 C# 檔 | 1,546 | **1,549** |
| 測試檔 | 613 | **614** |
| `[Fact]` 方法 | 6,318 | **6,333** |
| 專案測試基線 | 7,645 | **7,666** |
| 覆蓋基線 | 441 | **444** |

## 九、怎麼複算

```bash
# 逐欄累加驗證保留區邊界（用 repo 內的 nvme.h）
python - <<'PY'
import re, io
s = io.open('work/nvme.h', encoding='utf-8', errors='ignore').read()
i = s.index('struct nvme_id_ctrl {'); j = s.index('};', i)
off = 0
for line in s[i:j].split('\n')[1:]:
    m = re.match(r'(?:__le16|__le32|__le64|__u8|char)\s+(\w+)(?:\[(\d+)\])?;', line.strip())
    if not m: continue
    name, arr = m.group(1), m.group(2)
    size = 4 if '__le32' in line else (8 if '__le64' in line else (2 if '__le16' in line else 1))
    n = int(arr) if arr else 1
    if name.startswith('rsvd') or name in ('wctemp','cctemp'):
        print(f"{name:10s} off={off:5d} 0x{off:03X} len={size*n}")
    off += size * n
PY
```

這個腳本就是這一輪的複算方式——它同時給出保留區邊界**與** WCTEMP 的正確位移。