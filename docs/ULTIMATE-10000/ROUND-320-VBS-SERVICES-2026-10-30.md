# 第 320 輪（U10 第三圈 SE-0004）：VBS 安全服務清單從一句話變成逐服務的鍵

> 上一輪把 Credential Guard 的四層分開；這一輪處理同一個 WMI 類別裡**另一種塌陷**：
> `SecurityServicesConfigured` 與 `SecurityServicesRunning` 兩張清單被寫成一句人話。
> 人話好讀，但**不能查**——而「能不能查」正是第三圈的題目。

## 一、為什麼要做

`sec.vbs.state` 的值長這樣：

```
VirtualizationBasedSecurityStatus＝2（已啟用且執行中）；執行中的安全服務：2（記憶體完整性／HVCI）；
已設定：2（記憶體完整性）。…
```

想回答「Credential Guard 有沒有在跑」，得在那串中文字裡找「1（Credential Guard）」。
字串比對是這個專案最不信任的東西：**同一個答案，換一個寫法就找不到**。
`sec.credguard.chain`（第 319 輪）已經回答了 Credential Guard 那一個，
這一輪把**所有服務**都變成可查的鍵。

## 二、做成什麼

一把總覽 `sec.vbs.services.summary` ＋ 一個逐服務家族 `sec.vbs.svc.{代碼}`。

**成員＝兩張清單的聯集**，不是交集。只設定沒跑、只跑沒設定，兩種都要列出來，而且各自有措辭：

| 情形 | 值裡怎麼寫 |
|---|---|
| 設定且執行 | 「設定：是；執行：是。設定是政策，執行才是生效」 |
| 設定但沒跑 | 「**已設定但沒在跑**：這是待查的狀態，不是「已啟用」（可能是不支援、或設定後尚未重開機）」 |
| 跑但設定清單沒有 | 「**在跑但設定清單裡沒有**：兩個欄位互相矛盾，並列不仲裁」 |

**代碼 0 是哨兵，不是服務。** Win32_DeviceGuard 用 0 表示「無服務」，它會出現在清單裡
（「執行中的安全服務：0（無服務）」）。照字面出鍵會得到一把 `sec.vbs.svc.0`——
**那是在替「沒有」造一個東西**。所以 0 只寫在總覽的一句話裡，不進家族。
這是這一輪最容易被寫錯的一格，有兩條測試釘住。

**空清單不是讀不到。** 兩張清單都讀到、都是空的 → `Present`、數字 0，值裡明寫
「兩張清單都讀得到且都是空的，這是「沒有任何安全服務被設定或執行」，不是讀不到」。

**缺一張清單時只出讀得到的那一半，並在每一筆上寫明另一半讀不到。**
SE-0004 問的是「實際在跑哪幾個」，執行面讀得到就該回答；所以
`Configured` 缺席時仍逐服務列出 `Running` 的成員，每一筆寫「設定：讀不到（欄位缺席）」。
反過來 `Running` 缺席時，總覽**轉 Unknown 且不給數字**（非 `Present` 不得帶數字），
逐服務鍵仍列出設定面並寫明「執行：讀不到」。兩張都缺席 → 不逐服務出鍵。

**執行面整份不可用時沿用上游三態**（`NotApplicable`／`ReadError`／`InsufficientPrivilege`）
與上游的理由，**不自己重新判斷**「是沒有這個命名空間還是查詢失敗」——
那是 `VbsStateFactsService` 已經回答過的問題，兩支各自猜一次就會有兩個答案。

## 三、沒有新增讀取面

`VbsServiceFactsService` 是純函式，輸入是**同一份** `Win32_DeviceGuard` 快照
（第 319 輪為了狀態鏈已經把快照交出來了）。這一輪只是多一個消費者：

```
LoadVbsState()
  ├─ VbsStateFactsService.Collect(at, null, out snapshot)   ← 唯一的 WMI 查詢
  ├─ CredentialGuardFactsService.Collect(config, snapshot)  ← 第 319 輪
  └─ VbsServiceFactsService.Collect(snapshot, 上游三態, 上游理由)  ← 第 320 輪
```

三支各自再查一次 WMI 就會有三個答案的可能，而那種不一致在畫面上看起來像設定在變。
這與第 314 輪（安全開機四環）、第 316 輪（電池三欄）、第 319 輪（狀態鏈）是同一個形狀。

## 四、這一輪抓到的真問題

**「清單是空的」與「清單讀不到」在既有事實裡是同一個樣子。** 第 319 輪把
`VbsSnapshot` 的四張清單改成可空之後，這一輪才有辦法區分這兩件事；
如果沒有那一步，`Configured` 缺席會被寫成「設定 0 個」——那正是本專案踩過五次的同一種錯。
**兩個連續的輪次在同一個型別上各修一層，這不是重複，是前一層讓後一層看得見。**

## 五、新增的守門（14 條）

`Tests/VbsServiceFactsTests.cs`：執行面不可用時沿用上游三態（含 `InsufficientPrivilege` 與
`NotApplicable` 兩種）、逐服務一把鍵且成員是聯集、設定但沒跑要寫成待查、
跑但沒設定要並列不仲裁、**代碼 0 不得出現逐服務鍵**、空清單不是讀不到、
設定清單讀不到時執行面照樣列出、執行清單讀不到時總覽不給數字、
兩張都缺席時不逐服務出鍵、總覽的執行數要等於逐服務裡「執行：是」的個數、
服務代碼照值表解碼（未收錄帶原碼）、逐服務鍵要帶可信度與來源、
家族前綴必須在動態家族登記裡。

另加兩條既有守門的上限：動態家族上限 59 → 60（`sec.vbs.svc.`）、
覆蓋基線 420 → 421（總覽是「設定幾個、執行幾個」的自述，沒有第二條獨立通路能再問一次）。

## 六、帳面

- 事實鍵 **1,146 → 1,147**（`sec.vbs.services.summary`；`sec` 域 4 → 5 把）。
- 動態家族 **59 → 60**（`sec.vbs.svc.`）。
- 服務檔 **641 → 642**（`VbsServiceFactsService`）。
- 覆蓋基線 **420 → 421**；測試基線 **7,494 → 7,508**（+14）。
- 全域 C# 檔 1,519 → 1,521；測試檔 603 → 604；`[Fact]` 6,167 → 6,181；方法數 6,436 → 6,450。
- 三份 README 的鍵數／家族數／徽章同步；`百科.html` 重生成；附錄 H 由產生器重生（642 列）。
- `gen-readme-domains.py --check` 回零、`gen-docs-html.py --check` 回零。

## 七、怎麼複算

```bash
grep -n 'sec.vbs.svc\.\|sec.vbs.services.summary' Services/FactKeyCatalog.cs Services/FactKeyDynamicCatalog.cs
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~VbsServiceFactsTests"
python Tools/gen-readme-domains.py --check
```

## 八、如實申報的邊界

- **沒有在真機上驗證「設定／執行不一致」的實際比例**：本輪的測試全部餵合成快照。
- **服務代碼表仍是 Microsoft Learn 的 0–7**：出現 8 以上的代碼時如實寫「值表未收錄」並帶原碼，
  不猜它是什麼（那個代碼表可能隨 Windows 版本增加，本版沒有逐一追）。
- **逐服務鍵不判斷好壞**：只寫「設定是／否、執行是／否」與兩者不一致時的並列，
  **不評價這個組合安不安全**——那需要一份與機器無關的政策基線，本版沒有。
