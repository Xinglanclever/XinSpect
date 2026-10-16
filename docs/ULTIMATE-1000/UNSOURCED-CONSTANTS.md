# 未對準出處的常數表

> **這份檔案記的是「我知道那些數字，但本專案沒有它們的出處」的地方。**

## 為什麼要有這一份

本專案有一條規則，它在五個地方各自被驗證過一次：

> **如果我需要寫一段話去解釋「為什麼這個數字可能是錯的」，那它就不該被產出。**

而它的具體形式是：**程式碼裡的每一個常數，都要有 `[SpecRef]` 指得出它從哪來。**
沒有出處的常數不寫——**因為「填錯的編號會讓一個不存在的東西看起來存在」，那比「未收錄」更糟。**

## 而這一條規則的代價是：有些項目做不完

**這是那個代價的清單。** 每一筆都是一個「看起來做得到、但缺一張表」的項目。

| # | 項目 | 規格鍵 | 缺什麼 | 現狀 |
|---|---|---|---|---|
| ① | P2-055 | `bt.km` | **FIT 的 Key Manifest 型別編號** | `todo`（永遠回「未對準出處前不出值」） |
| ② | P2-072 | `tpm.log.histogram` | **TCG 的 `EV_*` 型別名** | **`ok`**——編號有值、**名字沒有** |
| ③ | P2-083 | `ev.var.attrs` | **`EFI_VARIABLE_*` 的位元名** | **`partial`**——原始值有、唯讀判斷沒有 |
| ④ | P2-069 | `tpm.owned` | **`TPM_PT_PERMANENT` 的屬性編號** | **`ok`（2026-10-17）**——`tpm.owned` 已出值 |
| ⑤ | P2-075 | `tpm.attest.capable` | **`TPM_PT_*` 的證明能力編號** | **`ok`（2026-10-17）**——`tpm.attest.capable` 已出值 |

**注意 ② 與 ③ 的形狀不同**：它們不是「什麼都沒做」，而是
**「有出處的那一半做了，沒出處的那一半明說不做」。**
那是這一條規則的另一種結局——**它不一定產生 a todo，也可以是 a partial。**

## ⚠ 2026-10-17：④⑤ 做完的同時，查出一件更該記的事

**這份文件自己抄錯了編號。** 下面那一節原本寫著「`Tpm2.cs` 已經為四個編號寫過 `[SpecRef]`：
`TPM_PT_REVISION＝0x101`、`TPM_PT_VENDOR_STRING_1..4＝0x104..0x107`」——**那組數字是錯的**，
而它是**從實作抄過來當佐證**的：

- 規格（TPM 2.0 Part 2 Structures）：`TPM_PT_FIXED＝0x100`，所以 `REVISION＝FIXED+2＝0x102`、
  `MANUFACTURER＝FIXED+5＝0x105`、`VENDOR_STRING_1..4＝FIXED+6..9＝0x106..0x109`；
  而 **`0x101` 是 `LEVEL`、`0x104` 是 `YEAR`**。
- `Revision()` 依「主版號在 bits 31:24、次版號在 bits 15:8」拆位也是錯的——規格對這一項寫的是
  **「the specification Revision times 100」**（Revision 01.01 → 101）。本機 `Win32_Tpm.SpecVersion`
  是「2.0, 0, 1.16」，所以真值是 **116（＝1.16）**；舊寫法會讀成「0.116」，
  而且因為它讀的是 LEVEL（真值 0）而顯示「未回報」，再把 YEAR 與 MANUFACTURER 當成廠商字串的前兩段。

**所以這一節原本的論證形式也是壞的**：它拿「實作裡有 `[SpecRef]`」當成「這張表已經對準了」的證據，
而實作正是錯的那一方。**出處要對規格，不能對實作**——實作錯了，引用它就等於替錯誤背書。
修法與細節見 `Tpm2.cs` 的常數收錄政策一節、`Tpm2Tests` 的編號 golden、
以及 `docs/ULTIMATE-1000/GATE-LOG-257.md` 第七節。

## 而五筆的共同解法是同一件事

**對準規格出處 → 把那張表加成一張有出處的表。**

而那不是五次工作，而是**一處改、多處受益**：

| 要加的 | 受益的項目 | 出處 |
|---|---|---|
| TCG 的 `EV_*` 表 | ② | TCG PC Client Platform Firmware Profile Specification |
| `EFI_VARIABLE_*` 表 | ③ | UEFI Specification §8.2 |
| `TPM_PT_*` 表 | ④ ⑤ | TPM 2.0 Part 2 Structures |
| FIT 的 Key Manifest 型別 | ① | Intel Platform Firmware Initialization（**`FitDecoder` 已引用它**） |

## 而 ④ 與 ⑤ 可以一起做（**2026-10-17 已完成**）

**它們是同一張表（`TPM_PT_*`），而 `Tpm2.cs` 已經寫過幾個編號。**
（原文在這裡抄了一份編號清單，那份清單本身是錯的——見上一節。以下是對準規格後的樣子。）

```
TPM_PT_FIXED＝0x100：FAMILY_INDICATOR＝0x100、LEVEL＝0x101、REVISION＝0x102、
                     DAY_OF_YEAR＝0x103、YEAR＝0x104、MANUFACTURER＝0x105、
                     VENDOR_STRING_1..4＝0x106..0x109、…、FIRMWARE_VERSION_2＝0x10C
TPM_PT_VAR＝0x200：  PERMANENT＝0x200、STARTUP_CLEAR＝0x201
```

擴充那張表、再補上 ④ ⑤ 需要的編號——**那是一個有界的、可查證的工作**，
而它在 2026-10-17 做完了：`tpm.owned`（讀 `TPMA_PERMANENT` 與 `TPMA_STARTUP_CLEAR` 的位元）與
`tpm.attest.capable`（以 family 與背書階層陳述能力，並明說不上網、不驗憑證鏈）都已出值，
而副產品是**查出 P2-068 的編號與解碼本來就是錯的**（見上）。

## 而與這五筆不同的另一類：**「已撤回」**

**上面五筆是「還沒做」，而下面是「做了又撤回」**——
撤回的理由不同（不是缺出處，而是**已經有實作了**），所以分開記：

| 項目 | 我做了什麼 | 為什麼撤回 |
|---|---|---|
| **P2-052** `fit.present` | 一個平行的 FIT 解碼器 | **`SpiFitFactsService` 早就有 `FitDecoder`**——**我沒查就自己寫** |
| **P2-079／P2-080** `ev.pk.count` | PK／KEK／db／dbx 的簽章計數 | **`UefiSignatureFactsService` 已在做，而且更完整**（見 `work/refresh_matrix.py` 的註解） |

**而兩者的教訓是同一條：**

> **「規格鍵名不存在」不等於「這件事沒人做」。**
> 鍵名前綴是**規格的分類**，而實作的分類可能不同
> （`ev.*` 的實作在 `uefi.*` 底下；FIT 的實作在 `SpiFitFactsService`）。

**所以 grep 要三條，不是兩條**：

1. **規格宣告的鍵名**
2. **該項目的英文領域詞**
3. **「這件事的動詞」**——不是鍵名、不是領域名詞，而是「做這件事」的動作
   （「簽章清單」／signature list、「FIT 表」／fit table）

## 更新這份檔案的時機

- **多一筆缺出處的常數** → 加一列（並在那一項的程式碼裡寫下同一件事）
- **某張表對準了出處** → 把那幾列移出本檔，並在 commit message 裡記下出處
- **又撤回了一個實作** → 加一列，並在 `work/refresh_matrix.py` 的 ALIASES 裡寫清楚

**而本檔本身不是承諾**——它是一份「下一個人可以直接去對準那五張表」的清單。
