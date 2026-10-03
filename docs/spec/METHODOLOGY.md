# XinSpect 量測方法學（公開版）

- **狀態**：已實作的契約成文。本文件描述的是程式**實際遵守**的規則——有機器檢查對帳（`Tests/` 與 `Tests/PublicSpecTests.cs`），文件與程式漂移會紅燈。

## 1. 誠實契約（最高約束）

1. **三態**：每筆事實帶 `availability`——`Present`（讀到）／`NotSupported`（平台不提供）／
   `InsufficientPrivilege`（權限不足）／`ReadError`（讀取失敗）／`NotApplicable`（本機無此硬體）。
   **讀不到就說讀不到，不以 0、0xFF、典型值或舊值填補。**
2. **非自造驗證來源**：每筆事實標明來源（暫存器位址＋規格出處、WMI 類別、API 名稱）。
   沒有出處的值不出。
3. **SpecRef**：暫存器解碼器的方法必須附規格引用（文件、章節、暫存器、位元位置）；
   由反射機器檢查（`SpecRefRegistry`）——新解碼方法而未附引用會直接紅燈。
4. **可信度是標示不是保證**：`Unknown`／`Reported`（系統自己記的）／`Derived`（由其他事實推導）／
   `Measured`（本工具量到）。
5. **不解碼垃圾**：格式異常、長度不足、佈局歧義一律如實拒解（回 null／三態），不猜。

## 2. 量測路徑分層

| 層 | 通路 | 可信度標示慣例 |
|---|---|---|
| 特權層 | WinRing0（MSR/PCI/實體記憶體）／XsRegProbe（白名單備援驅動） | Measured |
| 感測層 | SMBus（SPD/TSOD）、SuperIO、CMOS | Measured |
| 系統層 | WMI／登錄檔／EventLog／LSA／wlanapi／psapi | Reported |
| 韌體仲介 | Windows TBS（TPM）、UEFI 變數 API | Reported（Windows 代轉） |
| 推導 | 交叉對帳規則引擎（26 條，外部化於 `Rules/builtin.json`） | Derived |

## 3. 交叉對帳

- 同一事實的多個獨立來源互相印證（例：微碼修訂版——CPU 自己說的 MSR 0x8B vs Windows 記的登錄檔）。
- 規則結論三種：**一致**（綠）／**矛盾**（紅，具名列輸入）／**無法驗證**（灰，任一輸入缺席）。
- **雙向矛盾拆方向性規則**：當兩側都可能缺席，單條規則無法兩向涵蓋——拆兩條，各宣告保證 Present 的一側。
- 判決不下「你安全／不安全」的結論，只指出事實之間的關係。

## 4. 突變測試

純解碼器抽 `XinSpect.Decoders` 類別庫後以 dotnet-stryker 對帳（門檻 low=70）；
資料型知識表（SuperIoKnowledge／PciKnowledge）刻意排除——逐條字串斷言＝快照重複。

## 5. 效能預算

冷啟動 ≤3 秒（實測 2.7 秒）；其餘門檻見 `Models/PerfBudget.cs`；自我遙測預設關閉、匿名只記數字與時間。
