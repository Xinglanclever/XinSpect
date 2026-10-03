# XinSpect 事實查詢語言 v1

- **狀態**：已實作（`Services/QueryParser.cs`＋`Services/QueryExecutor.cs`；機器對帳見 `Tests/PublicSpecTests.cs`——解析器新增鍵而本文未更新會紅燈）。
- **對象**：時間膠囊（快照）內的 `HardwareFact` 事實集合。CLI `--query` 與查詢工具共用本語法。

## 1. 文法

一行一子句；`#` 開頭為註解；值含空格時吃到行尾（不需引號）。

```
<欄位> <運算子> <值>
```

### 欄位（七種）

- `key`：事實的穩定識別鍵（如 `msr.0x8b`、`spi.hsfsts`）。
- `category`：事實分類（如 `韌體安全`、`系統與軟體`）。
- `source`：資料來源文字。
- `value`：人可讀的值文字。
- `availability`：可用性三態（見 §2）。
- `since`：量測時間下界（ISO 8601，含當日）。
- `until`：量測時間上界。

### 運算子（三種）

- `=`：精確相等（`availability`／`since`／`until` 必用 `=`）。
- `~`：子字串包含（不分大小寫）。
- `^`：前綴匹配（`key` 最常用，如 `key ^ spi.`）。

## 2. availability 的值與別名

| 正式值 | 別名 |
|---|---|
| `Present` | `ok` |
| `ReadError` | `error` |
| `InsufficientPrivilege` | `no-permission` |
| `NotSupported` | （無別名） |
| `NotApplicable` | （無別名） |

## 3. 執行語意

- 子句之間為 **AND**（逐層篩選管線）。
- **查不到≠沒有**：0 筆匹配時，結果摘要明說「可能沒有這個事實或鍵名不同」；
  存在但讀不到的項目（availability 非 `Present`）是**正常匹配**——三態原因隨附，不會被過濾掉。
- 跨機器比較（`RunOnSnapshots`）以快照內的匿名機器識別分組，不明文輸出機器身分。

## 4. 範例

```
# 所有 SPI 相關事實
key ^ spi.

# 韌體安全分類下讀不到的項目（含原因）
category = 韌體安全
availability = error

# 2026-10-03 之後量到、值含 FLOCKDN 的項目
since = 2026-10-03
value ~ FLOCKDN
```

## 5. 錯誤處理

未知欄位、缺運算子、缺值都丟 `ParseException`，訊息帶修正指引——靜默忽略會把「查錯」偽裝成「沒有結果」，這是誠實契約的反面。
