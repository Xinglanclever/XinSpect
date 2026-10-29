# 第 319 輪（U10 第三圈 SE-0005）：Credential Guard 從一個布林變成四層狀態鏈

> 這一輪只新增一把鍵 `sec.credguard.chain`，但順手修掉了一個**已經在出貨的標籤錯誤**：
> `ws.lsa.summary` 把 VBS 的總開關寫成「Credential Guard」。
> 這正是第三圈的主題——不是「多讀一個欄位」，而是「那句話指的是不是它說的那件事」。

## 一、為什麼要做（候選清單原本的判斷）

`CIRCLE-3-CANDIDATES` 對 SE-0005 的初判是「4 檔命中，狀態鏈未見」。實際查下去比那個判斷更嚴重：

- `VbsStateFactsService`（第 270 輪，U10-SE-0003）已有 `sec.vbs.state`／`sec.vbs.required`，
  內容包含狀態碼、Required／Available／Configured／Running 四張清單。
- 但 **Credential Guard 本身沒有自己的設定面**：`LsaCfgFlags`（0 停用／1 啟用含 UEFI 鎖／
  2 啟用不含鎖）與 `DeviceGuard\Scenarios\CredentialGuard\Enabled` 全庫 0 命中。
- 更糟的是 `FinalInventoryFactsService.LsaFact`（`ws.lsa.summary`）**讀的是
  `EnableVirtualizationBasedSecurity`——VBS 的總開關——卻印成「Credential Guard」**。
  於是一台「VBS 開著、Credential Guard 沒開」的機器會被讀成「Credential Guard 已開」。
  那不是少一個欄位，是**說了一句錯的話**。

## 二、做成什麼：四層各自陳述，不替機器歸因

一把鍵 `sec.credguard.chain`，值裡四層並列：

| 層 | 讀哪裡 | 為什麼單獨一層 |
|---|---|---|
| 設定面 | `Control\Lsa\LsaCfgFlags`＋`DeviceGuard\Scenarios\CredentialGuard\Enabled` | 這是 Credential Guard **自己的**政策，與 VBS 總開關不同 |
| 政策面 | `Control\DeviceGuard\EnableVirtualizationBasedSecurity` | 總開關；沒開時 Credential Guard 一定跑不起來 |
| 執行面 | `Win32_DeviceGuard.SecurityServicesRunning` 含不含代碼 1 | 設定是政策，執行才是生效 |
| 硬體面 | `Win32_DeviceGuard.AvailableSecurityProperties` 的 1（hypervisor）／2（Secure Boot） | 韌體自報的能力；缺它就永遠跑不起來 |

**三級 + 兩態**：`0` 未啟用／`1` 已設定但未執行／`2` 執行中；
設定讀取失敗＝`ReadError`、設定說已啟用而執行面讀不到＝`Unknown`（都**不帶數字**）。

**「已設定但未執行」是最常見的真實情境**（設定了但沒重開機、或 VBS 總開關沒開），
所以值裡把三個可能原因**並列**——VBS 總開關沒開、硬體韌體缺 hypervisor 支援、設定後尚未重開機——
**不選一個**。把「已設定但未執行」寫成「已啟用」或「不支援」，是這一項明文禁止的兩種混淆。

**「未設定」≠「明確關閉」**：`LsaCfgFlags` 值不存在＝未設定；值為 0＝明確關閉。
兩者都是 `Present`（都是可讀到的政策事實），但措辭不同，各有測試釘住。

**值表未收錄不猜**：`LsaCfgFlags` 出現 0／1／2 以外的值 → `Unknown` 並帶原值，
不猜它算不算啟用。

## 三、順手修掉的兩個真問題

1. **`ws.lsa.summary` 的標籤錯誤**（上面第一節）：改成
   「VBS 總開關（EnableVirtualizationBasedSecurity）…**這個總開關不是 Credential Guard 的設定**，
   Credential Guard 的設定面／政策面／執行面／硬體面見 `sec.credguard.chain`」。
   `LsaState` 的欄位也從 `CredentialGuard` 改名為 `VbsMasterSwitch`——**名字錯了，讀者就會誤解**。
2. **`VbsSnapshot` 的四張清單從「不可空」改成「可空」**：原本 `ToUIntList` 在欄位缺席時回空清單，
   於是「讀不到 RequiredSecurityProperties」被寫成「沒有任何屬性被要求」——
   那正是第 0.3 節明文禁止的混淆（0 與讀不到是兩個斷言）。
   現在 `null`＝欄位缺席（`sec.vbs.required` 轉 `Unknown`、不帶數字），空清單＝讀到且確實是空的。
   順帶把 `ToUIntList` 的 `catch { return []; }` 改成 `return null`——轉換失敗也是讀不到。

## 四、一次讀取，兩個消費者

`VbsStateFactsService.Collect` 新增多載 `Collect(at, probe, out VbsSnapshot? snapshot)`：
同一份 WMI 查詢的結果交給狀態鏈用（執行面與硬體面就在同一份快照裡）。
**兩支各自再查一次會有兩個答案的可能，而那種不一致在畫面上看起來像設定在變**——
這與第 314 輪安全開機四環、第 316 輪電池三欄是同一個形狀。

`EvidenceLabService.LoadVbsState` 現在一次做完兩件事，`CredentialGuardFacts` 也進 `AllFacts`。

## 五、新增的守門（16 條）

`Tests/CredentialGuardFactsTests.cs`（15 條）：讀取失敗是 `ReadError` 不是未啟用、
理由照讀取端原話、值表未收錄不猜、執行中要四層都寫出來、已設定但未執行是待查矛盾、
執行面讀不到要 `Unknown`、執行面欄位缺席也 `Unknown`、明確關閉不是不支援、
未設定不等於已關閉、政策說關而執行說在跑要並列不仲裁、Scenarios 開關也算設定面、
硬體面缺屬性要如實寫不在、四層名稱都要出現、值表對準 Microsoft Learn、
同一份快照要能同時餵兩個消費者。

`Tests/VbsStateFactsTests.cs`（+1 條）：清單欄位缺席要標 `Unknown`，不得代填成空清單。

## 六、帳面

- 事實鍵 **1,145 → 1,146**（`sec.credguard.chain`，落在既有 `sec` 域，該域 3 → 4 把）。
- 動態家族 59（不變）、服務檔 **639 → 641**（`CredentialGuardFactsService`／`CredentialGuardReader`）。
- 覆蓋基線 **419 → 420**：狀態鏈是「四層併起來算哪一級」的判定，沒有第二條獨立通路能再算一次
  （四層各自的值都還能被別的鍵再問一次，所以不是黑箱）。
- 測試基線 **7,478 → 7,494**（+16）。
- 全域 C# 檔 1,516 → 1,519；測試檔 602 → 603；`[Fact]` 6,151 → 6,167；方法數 6,420 → 6,436。
- 三份 README 的鍵數、徽章同步；`百科.html` 重生成；附錄 H 由產生器重生（641 列）。
- `python Tools/gen-readme-domains.py --check` 回零、`python Tools/gen-docs-html.py --check` 回零。

## 七、怎麼複算

```bash
# 這把鍵
grep -n 'sec.credguard.chain' Services/FactKeyCatalog.cs
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~CredentialGuardFactsTests"
# 文件與目錄同步
python Tools/gen-readme-domains.py --check
# 全套（單節點建置；每次換新的輸出目錄）
DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 \
  dotnet test Tests/XinSpect.Tests.csproj -m:1 -p:UseSharedCompilation=false \
  -p:BaseOutputPath="obj/_verify_$(date +%H%M%S)/"
```

## 八、如實申報的邊界

- **沒有驗證過真機上的 LsaCfgFlags 值分佈**：值表（0／1／2）取自 Microsoft Learn 的文件，
  本輪沒有在真機上逐一比對過「什麼設定會產生什麼值」。
- **沒有處理「Credential Guard 由 Intune／GPO 下發」的情況**：本服務只讀本機登錄檔，
  政策來自網域時本機值可能是 0 或不存在——那時值裡寫「未設定」或「明確關閉」，
  **不會假裝知道群組原則的內容**。
- **`Scenarios\CredentialGuard\Enabled` 的存在性依 Windows 版本而異**（較新的版本才會有），
  缺席時該欄寫「未設定（值不存在）」，不推測。
