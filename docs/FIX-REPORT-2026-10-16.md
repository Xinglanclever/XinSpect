# XinSpect 修復與稽核報告（2026-10-15／16）

本檔合併了這一輪的三份紀錄：
**缺陷修復**、**600 項規格對帳**、**ULTIMATE-1000 計畫自身的規格缺陷**。

**狀態**：建置 **0 警告 0 錯誤**；TPM 8/8、SecurityAudit+FirmwareAudit 69/69 綠；
**尚未跑完整套、尚未提交**。

> ## ⚠ 複核（2026-10-18）：本檔第三部與第四部已全部結案，請以這一節為準
>
> 這份報告的「未修／待辦」在寫下之後陸續被修完，但**它自己沒有更新**——
> 而一份說「未修」而其實已修的文件，比沒有文件更貴：下一個讀它的人會去修一個不存在的缺陷。
> 以 `Tests/PlanLibraryTests.cs`（把 `work/UltimateAudit` 的稽核搬進測試，附自我檢查）與
> 逐提交量測複核的結果：
>
> | 部位 | 本檔的宣稱 | 複核結果（證據） |
> |---|---|---|
> | 第二部 §2.2「C. 半實作 reader」 | ⚠️ 未修 | **已修**：`PlatformGovernanceFactsService.ReadSddl` 現在逐服務 `OpenServiceW` ＋讀安全描述元，讀不到一律回 null 走 `ReadError`（不編 0）。提交 `f1c5f6b` |
> | 第三部「計畫自身的 31 條規格缺陷」 | 31 條確認 | **全部已修**：`docs/ULTIMATE-1000/README.md` 第二輪的修法表列了每一類；稽核現在 0 問題——十階段各 105 列、合計 1,050 個互異 ID、編號連續、懸空依賴 0、依賴欄語法 0、未標「參照」的前向依賴 0、輸出鍵前綴未宣告 0、逐域區塊與前綴 0 例外 |
> | 第三部 P0-②（矩陣列數對不上 105） | 更正為「不成立」 | **更正正確，但理由要再更正一次**（見 §3.1）：不是引擎壞掉，是樣式本身寫錯 |
> | §4.1 `ReadSddl` | 未修 | **已修**（同 §2.2 那一列） |
> | §4.2「測試 +31 實際只有 18 條」 | 差 13 條來源不明 | **帳目沒錯，是稽核的方法錯了**：+31 是「執行數」的差，`f1c5f6b` 那個提交的**測試方法** +12（4,603→4,615），而 `AmcacheFactsTests`（18 條）根本不屬於那個提交。詳見 §4.2 的複核註 |
> | §4.3「20 段／183 鍵」註解 | 無法對帳 | **已修**：註解已改成明說「沒有留下可對帳的紀錄，不要在這裡補一個沒人能驗證的數字」（`GATE-LOG-257`） |
> | §4.4 ZCode 代理仍在改碼 | 需確認 | **已失效**：該代理（Freebuff／Codebuff）在 2026-10-18 16:54 因免費 session 結束停工；「只 `git add` 明確路徑」的紀律照舊沿用 |
> | §6.2 第 8 條：csproj 無效的 `Compile Remove` | 可刪可留 | 仍可刪可留（那一節的說明已足夠） |
>
> **這一輪（2026-10-18）真正新修的兩件事**（本檔沒有的）：
> ① 規定的閘門（`work/UltimateAudit`，**有入版控**）**沒有任何東西會自動跑它**，
> 且已因目錄長出第 11 份檔案而**連跑都跑不動**
> （自我檢查寫死「10 份階段檔」、`STAGE-02-*.md` 的 glob 命中兩份導致 `.Single()` 拋例外）
> → 搬進 `Tests/PlanLibraryTests.cs`，並補上「逐域區塊與輸出前綴對位」這條原本沒有的檢查；
> ② **「兩個正則引擎都壞了」是誤診**（見 §3.1 的第二次更正），這一條已經寫進
> `docs/ULTIMATE-1000/README.md` 與計畫庫的自我檢查裡，免得後人不敢再信能動的工具。

---

## 目錄

- [第一部　修好的東西](#第一部修好的東西)
- [第二部　600 項規格對帳與缺陷獵捕](#第二部600-項規格對帳與缺陷獵捕)
- [第三部　ULTIMATE-1000 計畫自身的 31 條規格缺陷](#第三部ultimate-1000-計畫自身的-31-條規格缺陷)
- [第四部　已發現但尚未處理的問題](#第四部已發現但尚未處理的問題)
- [第五部　環境問題](#第五部環境問題)
- [第六部　目前狀態與待辦](#第六部目前狀態與待辦)

---

# 第一部　修好的東西

## 1.1 建置死結：CS0579「屬性重複」（**最關鍵**）

**症狀**：`dotnet build` 失敗，16 個錯誤，全部是
`error CS0579: '...Attribute' 屬性重複`，指到的檔案每次建置都不一樣
（`XinSpect_birqfs4u_wpftmp.AssemblyInfo.cs`、`XinSpect.AssemblyInfo.cs`、
`.NETCoreApp,Version=v10.0.AssemblyAttributes.cs`……）。

**為什麼一直在漂**：增量建置走快取不會暴露；`obj/` 一被清空、第一次全量建置就會炸。
錯誤訊息裡的檔名每次不同（WPF 暫存專案的隨機名），**追不到根**。

**根因**（由 `dotnet msbuild -getItem:Compile` 挖出來，不是猜的）：

```
"Identity": "AssemblyInfo.cs"
"FullPath": "C:\Users\Administrator\XinSpect\AssemblyInfo.cs"

"Identity": "work\probe\obj\Debug\net10.0\.NETCoreApp,Version=v10.0.AssemblyAttributes.cs"
"Identity": "work\probe\obj\Debug\net10.0\probe.AssemblyInfo.cs"

"Identity": "obj\Debug\net10.0-windows\win-x64\.NETCoreApp,Version=v10.0.AssemblyAttributes.cs"
```

主專案的**預設 Compile glob 會掃整個 repo 樹**，於是吃了兩類不該進來的檔案：

1. **本次建置的產生物**（`obj/`、`bin/`）——與 SDK 當場生成的那份同名同內容，
   同一組 `[assembly: ...]` 在一次編譯裡出現兩次。
2. **`work/` 底下另一個獨立 .NET 專案（`work/probe/`）的 obj 產生物**——同樣造成屬性重複，
   而錯誤訊息裡完全看不出跟主專案的關係。

**修法**：新增 `Directory.Build.props`：

```xml
<PropertyGroup>
  <DefaultItemExcludes>$(DefaultItemExcludes);obj/**;bin/**;work/**</DefaultItemExcludes>
</PropertyGroup>
```

**為什麼是這個寫法**（九次失敗換來的）：

- 寫在 `.csproj` 裡的 `<Compile Remove>` **沒用**——SDK 的預設 glob 在 `.csproj` 讀完**之後**
  才展開，`Remove` 清單當下還是空的。只有 `Directory.Build.props`（更早讀入）能改到 glob 行為。
- **不能**把整個 `obj/` 從 Compile 排掉——`App.g.cs`／`MainWindow.g.cs` 等 XAML 產生物
  **必須**被編譯。`DefaultItemExcludes` 只擋產物「目錄」，SDK 明確加入的 `AssemblyInfo.cs`
  與 WPF 加入的 XAML 產生物都不受影響。

**決定性對照**（同一份原始碼、乾淨 obj、同樣的 `BaseOutputPath`，只換 props）：

| props | 建置結果 | 警告 |
|---|---|---|
| 無（＝原本的樣子） | **16 個錯誤，失敗** | — |
| 有 | **0 錯誤** | **0 警告** |

**⚠ 踩過的坑（留給後人）**：過程中一度對 WPF 暫存專案設
`GenerateAssemblyInfo=false` / `GenerateTargetFrameworkAttribute=false`。結果暫存專案失去
`SupportedOSPlatformAttribute`，它編譯主專案程式碼時**整套 CA1416 冒出來——實測 1665 條**，
`WarningBaselineTests` 的 0 條基線當場紅燈。
**光靠 `DefaultItemExcludes` 就夠了，不要動暫存專案的屬性。**

---

## 1.2 P0：SPI 權限回報「假 0」（`Services/FirmwareAuditFactsService.cs`）

**症狀**：`fw.spiperm.summary` 在**每一台機器上**都輸出
「master 0、唯讀 region 0、寫保護 0」，且標 `Present` + `Reported`
——**斷言了一次從未發生的硬體讀取**。

**根因**：

```csharp
private static SpiPermState ReadSpiPerm()
{
    // 本專案已有 SpiFlash 解碼；此處僅宣告計數接口，不重讀硬體。
    return new(0, 0, 0);          // ← 無條件回假值，因此下面那行 if (s is null) 是死碼
}
```

`SpiPermFact` 的 `if (s is null) return Unavailable(..., ReadError, ...)` **本來就在**，
只是被這個假 stub 弄成**永不觸發的死碼**。

**為什麼特別危險**：這是**「寫入保護」**。回一個看起來成功的 `0` 會被讀成「沒有保護」，
比誠實說「讀不到」更糟。

**修法**（改回傳型別為 `SpiPermState?` 並回 `null`，讓既有的 `ReadError` 分支復活）：

```csharp
private static SpiPermState? ReadSpiPerm()
{
    // 本服務尚未接上 SpiFlashService 的解碼器（DecodePrx／DecodeFrap／DecodeHsfsts 讀的是
    // 同一組暫存器）。在接線之前一律回 null 走 ReadError——「回 0 且標 Present」等於在每台機器上
    // 斷言一次沒發生過的硬體讀取，且「寫保護 0」會被讀成「沒有保護」，比誠實說讀不到更危險。
    // 接線時只要把下面換成 SpiFlashService 的實際讀取，不需要再想一次三態。
    return null;
}
```

**驗證**：`FirmwareAudit` 測試 15/15 綠（251 ms）。

---

## 1.3 P0：五個事實的探針是死路（`Services/SecurityAuditDeepFactsService.cs`）

**症狀**：五把鍵（`sa.uninst.deep`／`sa.logon.deep`／`sa.svcbin.deep`／`sa.path.deep`／`sa.sddl.deep`）
在**每一台機器上**永遠回 `ReadError`。

**根因**：

```csharp
UninstState? s;
try { s = (probe ?? (() => null))(); }   // ← 常數 lambda，永遠回 null
catch { s = null; }
```

五處都是這個形狀，而生產入口 `EvidenceLabService.cs:1275` 呼叫
`SecurityAuditDeepFactsService.Collect(DateTimeOffset.UtcNow)` —— **不傳任何探針**。
只有 `AdsFact`（第 97 行）是真的接線（`(probe ?? ReadAds)()`）。

**為什麼測試是綠的**：`Tests/SecurityAuditDeepFactsTests.cs` 全部**注入合成探針**，
所以它驗的是「給定資料時輸出對不對」，**從來沒驗過生產路徑**。
教科書級的「測試綠不代表功能可用」。

**修法**：五處探針改接真讀取器，並補上五個 `Read*` 實作
（移植自同專案已在跑的 `SecurityAuditSupplementFactsService` 與 `PlatformGovernanceFactsService`
——同一組能力已在 `.summary` 那組出貨，是可靠的來源）：

```csharp
UninstFact : (probe ?? new UninstProbe(ReadUninst))()
LogonFact  : (probe ?? new LogonProbe(ReadLogon))()
SvcBinFact : (probe ?? new SvcBinProbe(ReadSvcBin))()
PathFact   : (probe ?? new PathProbe(ReadPath))()
SddlFact   : (probe ?? new SddlProbe(ReadSddl))()
```

`ReadSddl` 尚未接上逐服務 SDDL 的讀取，因此**誠實回報「全部未檢查」**而不是假裝 0 個可重配置：

```csharp
if (services > 0 && denied == services)
    return new(services, 0, 0, services);   // 不以 0 冒充「沒有發現」
```

另外補上缺的 `using`：`System.Management`、`System.Security.AccessControl`、
`System.Security.Principal`、`Microsoft.Win32`。

**驗證**：`SecurityAudit` + `FirmwareAudit` 合跑 69/69 綠（607 ms）。

---

## 1.4 UI 凍結：「核心對核心延遲」量測期間視窗未響應（`Services/CoreLatencyService.cs`）

**症狀**：使用者提供的截圖（v2.84）顯示「開始量測」後視窗標題變成**「（未響應）」**，
而量測仍在進行。

**根因**（三個因素疊加）：

1. **跨執行緒寫繫結屬性**——`MeasureAll` 跑在 `Task.Run`（背景執行緒），卻直接寫
   `ProgressFraction` / `StatusLine`。WPF 的繫結不是執行緒安全的。
2. **每組都重算 O(n²) 的 `Stats()`**——

   ```csharp
   if (done % 32 == 0)
       StatusLine = $"量測中… {done} / {total} 組（目前最小 {Stats(m).min:0} ns）";
   ```

   64 邏輯處理器時矩陣是 4096 格、共 4032 組 → 約 126 次全矩陣掃描，
   每次還配置一個 `List<double>`。
3. **忙等執行緒 `ThreadPriority.Highest`** 把對應核心拉到 100%——這是量測本質，保留。

**修法**：改用 `IProgress<T>`（回呼自動 marshal 回建立它的執行緒），並把回報節流：

```csharp
var progress = new Progress<(double fraction, string status)>(p =>
{
    ProgressFraction = p.fraction;
    StatusLine = p.status;
});
var (matrix, lps) = await Task.Run(() => MeasureAll(ct, progress)).ConfigureAwait(true);
```

```csharp
if (done % 256 == 0 || lastReport.ElapsedMilliseconds >= 150)
{
    progress.Report((done / (double)total, $"量測中… {done} / {total} 組"));
    lastReport.Restart();
}
```

**這正是專案自己早就有的正確模式**——`CeilingService.cs:284` 一直都用 `Progress<T>`：

```csharp
var report = new Progress<(double Frac, string Text)>(t => { Progress = t.Frac; Status = t.Text; });
```

**所以 `CoreLatencyService` 只是漏了別處早就做對的事**，不是新問題。

**查過但不需要改的**：

- `DpcLatencyService.cs`——每個 `await` 都回到 UI 執行緒，無跨執行緒寫入。
- `CeilingService.cs`——本來就正確。
- 第二張截圖（「效能天花板」未響應）是 **v2.66 的舊畫面**，該版之後已修，不列為現行缺陷。

---

## 1.5 TPM 測試三紅（`Tests/TpmFactsTests.cs`）——**測試 fixture 錯，實作是對的**

這是 **ZCode 留下的半成品**：TPM2 解碼實作（`XinSpect.Decoders/Tpm2.cs` +139 行）、
服務（`Services/TpmFactsService.cs` +96 行）、測試（+146 行）都寫了，但三條測試紅。

**三條紅燈**：

| 測試 | 期望 | 實際 |
|---|---|---|
| `規格版本_由TPM自己的family與revision陳述` | `"revision 1.3"` | `"revision 0.1"` → 修 fixture 後 `1.0` |
| `規格版本_未回報時不以預設值代填` | `"不以 2.0 代填"` | `"GetCapability 回應格式不符——不解碼垃圾"` |
| `銀行清單_capability不符要如實標不支援` | `"不是查詢的"` | 同上 |

**判定：實作對、fixture 錯。** 依據是 `Tpm2.Revision` 自己的 `SpecRef`：

```csharp
[SpecRef("TPM 2.0 Part 2：TPM_PT_REVISION 為 UINT32，高 8 位（bits 31:24）為主版號、
          次 8 位（bits 15:8）為次版號，中 16 位保留")]
public static (int Major, int Minor)? Revision(uint raw)
    => ((int)((raw >> 24) & 0xFF), (int)((raw >> 8) & 0xFF));
```

程式碼與 SpecRef **完全一致**。所以 1.3 該是 `0x01000300`
（`0x01030000` 依此規則是 **1.0**——我第一次也算錯了，這是這一節最值得記下的一點）。

**修法（三處，都在 `Tests/TpmFactsTests.cs`）**：

1. **revision 位元擺法**：`0x00,0x00,0x01,0x03` → **`0x01,0x00,0x03,0x00`**，
   並把 doc comment 的例子寫對（1.0 = `0x01000000`、1.3 = `0x01000300`、1.16 = `0x01001000`）。
2. **空屬性集另立 fixture** `EmptyPropertyData() => [0,0,0,0]`——
   原本用 `CapabilityWith([0,0,0,0])` 且 `outputLen` 給 **19**，
   但 `CapabilityWith` 建的長度是 `19 + 4 = 23`，長度不符會先撞到「格式不符」那條，
   驗不到「不以 2.0 代填」。改成走 `EmptyPropertyData()` 且長度給 `23u`。
3. **capability 不符的 fixture**：原本餵 `[0,0,0,0]`，服務看到格式不符就回「不解碼垃圾」，
   驗不到「不是查詢的」那條。改成餵**一份格式正確的 TPM_PROPERTIES 內容**、
   但 capability 欄標成 `Tpm2.CapTpmProperties`（而服務問的是 `CapAlgorithms`），
   這樣才會走到「回應的 capability 是……不是查詢的」分支。

**驗證**：`TpmFacts` 8/8 綠（55 ms）。

---

# 第二部　600 項規格對帳與缺陷獵捕

以 102 個平行 agent 完成（12,806 秒、415 萬 subagent token、2765 次工具呼叫）。

## 2.1 Track A：真正還沒做的是 **33 項**，不是掃描器說的 148

| 狀態 | 數 |
|---|---:|
| DONE | 556 |
| **NOT_DONE** | **25** |
| PARTIAL（半項） | 8 |
| REPLACED（刻意不做／併入他項） | 9 |
| UNCERTAIN | 2 |

**已驗證的 NOT_DONE 範例**（皆 high confidence）：

- `UX-012` 鍵盤導航守門——帳本只列 UX-001/003/004/005/006/007/008/010/011/013，**UX-012 整條跳過**
- `UX-014` 頁級截圖匯出——無 `ux.screenshot`／`ux.shot` 鍵，Views 裡唯一的 `SaveFileDialog` 是超頻設定檔
- `UX-016` 深測歷史並排——深測歷史只是一個扁平清單（上限 20），無跨場次比較
- `UX-018` 語言資源外部化——語言是三種編死的變體，`LanguageService.cs`／`EnglishStrings.cs` 皆無檔案載入
- `EG-011` GitHub Actions CI（**PARTIAL**）——有 `eg.ci` 鍵，但 **`.github/` 目錄根本不存在**，
  `ReadCi()` 只做自我聲明，沒有真的檢查檔案

**⚠ 這與 `work/recon/` 底下那四份清單（`likely_todo.txt` 154 行等）嚴重牴觸。**
那些是**掃描器產物**：比對「目錄宣告的鍵字串 vs 實際生產者」，**命名不同就報成缺口**。
帳本自己早就記過這件事：

> `work/coverage.py` 比對的是「目錄宣告的鍵字串 vs 實際生產者」，所以**命名不同就報成缺口**……
> 這些是**假缺口**，不是待辦。**掃描結果要逐項核對後才當清單用。**

**那四份清單不可直接當待辦使用。**

## 2.2 Track B：49 條候選 bug，對抗驗證後留下 31 條真缺陷

依嚴重度：**P0 × 4、P1 × 20、P2 × 30**（另有 20 條被推翻）。

四個 P0 全部是**同一個 bug 家族**——「回一個看起來成功的值，但那次讀取根本沒發生」：

| 家族型態 | 例子 | 狀態 |
|---|---|---|
| **A. 死探針** | `SecurityAuditDeep` 五處 `() => null` | ✅ 已修（§1.3） |
| **B. 假 stub** | `FirmwareAudit.ReadSpiPerm` `return new(0,0,0)` | ✅ 已修（§1.2） |
| **C. 半實作 reader** | `PlatformGovernanceFactsService.ReadSddl` 只數服務數，另兩欄恆 0 | ✅ **已修**（原本寫「未修，見 §4.1」；複核見文首 §複核）：現在逐服務開控制代碼讀安全描述元，一個都沒讀到時回 null 走 `ReadError`，不拿服務數冒充 |

C 型最陰險：**它有真讀取器（服務數是真的），所以看起來很正常**，但另外兩個欄位永遠 0。

---

# 第三部　ULTIMATE-1000 計畫自身的 31 條規格缺陷

以 30 個 agent 稽核十份階段檔（`docs/ULTIMATE-1000/`），79 條候選中 **31 條確認**（61% 被推翻）。

> 為什麼要先修：ULTIMATE-1000 是**規格庫**，階段一的 Gate 明文要求「契約成立」。
> 若契約文件自己就有 31 條對不上的地方，照著它實作的每一項，都是**把缺陷複製進 C# 裡**
> ——而這個專案最貴的資產正是「目錄≡原始碼≡執行期」三方對帳。

**按類別分**：

| 類別 | 數 |
|---|---:|
| 前向依賴（依賴更後面的階段） | 10 |
| 輸出鍵前綴未在域碼表宣告 | 8 |
| 計數不符（總數／域項數／矩陣列數） | 5 |
| 誠實宣稱漂移（鍵名像判決） | 5 |
| 內部矛盾 | 3 |

## 3.1 P0 — 最嚴重：計畫自己的計數不成立

### P0-①　階段十：大膽區「不計入承諾」卻被算進 105

> **更正（2026-10-16，複核後）**：方向對，**但兩個數字都錯**。
> ① 域碼表的 BY 列不是 6 而是 **5**（矩陣裡帶 `bold.` 前綴的只有 P10-101–P10-105；
> P10-100 是「大膽區標記與不計」本身，屬治理域）。
> ② 真正讓合計對不上 105 的是**另一格**：「治理與計數」列寫 12、實際 **13**。
> ③ 因此「不計就得扣掉、總數變 **99**」也是錯的——應為 **104／1,045**。
> 兩格都已改，十階段域碼表合計現在逐階段等於 105。


**位置**：`STAGE-10-ULTIMATE.md` §二 Gate 第 6 條、§三 域碼表 BY 域、矩陣 P10-100–P10-105

**矛盾**：

- Gate 第 6 條：「**大膽區不計入承諾**」
- P10-100 名稱本身：「大膽區標記與**不計**」
- **但**域碼表的 BY 域列 **6 項**，且計入「合計 105」

**為什麼是 P0**：要嘛「計入總數」就不是「不計」，要嘛「不計」就得從 105 扣掉 → 總數變 99。
**兩種讀法不能同時成立**，而 README 的「1,050」建立在 105×10 之上。

### P0-②　多階段矩陣列數對不上 105

> **更正（2026-10-16，複核後）**：這一條**不成立**。以 `work/UltimateAudit`（C#，開頭跑自我檢查
> 與環境探針，離開碼 0）重測：**十階段各 105 列，合計 1,050 個互異 ID，編號連續 1..105，
> 跨階段引用全部可解析**。P3-031 與 P3-043 就在矩陣裡，寫成 `| ★P3-031 |` 與 `| ★P3-043 |`
> 兩列（★ 是錨點標記，夾在管線與 ID 之間）——所以「只存在於錨點區、不在矩陣裡」是兩件事被混為一談：
> **★ 的列既是矩陣列也是錨點**，而漏掉它們的掃描器會把 105 數成 103。
>
> **更正（2026-10-18，第二次複核）：上面那段第一版的理由（「兩個引擎都對這種樣式給出錯誤答案」）
> 是誤診，比原本的樣式錯更貴。** 稽核腳本用 `[A-Z]+\d?` 認域碼，而這個樣式**本身**就是錯的——
> `\d?` 只允許**一位**數字，`F10` 有兩位。實測（兩個引擎，2026-10-18）：
>
> ```
> Python 3.11.9：  re.fullmatch(r'[A-Z]+\d?', 'F10')               → None    ← 正確答案
> .NET 10：        Regex.IsMatch("F10", "^[A-Z]+\\d?$")             → False   ← 同上，一致且正確
> 同一個樣式換成：  re.fullmatch(r'[A-Z]+\d*', 'F10')               → 命中    ← 域碼真正的形狀
> ```
>
> 兩邊引擎的答案一致且正確，**壞的是那個樣式**（與拿它去測卻把預期寫成 `true` 的探針）。
> 當時接受「引擎壞掉」這個更好聽的解釋的代價是：**這台機器上明明能用的工具被寫成不可信**。
> 現在這一條被釘在 `Tests/PlanLibraryTests.稽核工具自我檢查` 裡（它會斷言
> `Regex.IsMatch("F10", "^[A-Z]+\\d?$")` 必須是 `false`），免得那個誤診再長回來。
> 認 ID 的樣式（`P\d{1,2}-\d{3}`）一直是好的，所以矩陣列數一直數對——
> **工具壞掉被讀成文件壞掉**，而「哪一個壞掉」正是這一節要說清楚的事。


**位置**：各階段 §五 矩陣 vs §三 域碼表 vs 檔頭「本階段 105 項」

> **更正（2026-10-18）：下表是那個壞掉的樣式產生的，已被取代。** 正確的數是**十個階段各 105 列**
> （`Tests/PlanLibraryTests` 逐階段斷言，含 ★ 列）。下表把「★ 開頭的三列」算成不存在，
> 所以階段二變成 102；保留它是為了留下「同一個錯誤會產生看似合理的分布」這個證據。

| 階段 | 檔頭宣稱 | 當時數到 | 複核（2026-10-18） |
|---|---:|---:|---:|
| 2 | 105 | **102** | **105**（漏掉 `★P2-034`／`★P2-036`／`★P2-064` 三列） |
| 3 | 105 | **103** | **105**（漏掉 `★P3-031`／`★P3-043` 兩列） |
| 7 | 105 | **103** | **105** |
| 1／4／5／6／8／9／10 | 105 | 105 | 105 ✅ |

**階段 3 的細節**：`P3-031` 與 `P3-043` **只存在於 §四的 ★ 錨點區，不在 §五矩陣裡**——
這兩項是階段三寫得最完整的兩個錨點（PMU 程式化與還原驗證、DDR5 SPD 解碼），
**讀者從矩陣圈選時會完全錯過它們**。

**階段 7 的細節**：`P7-051` 與 `P7-067` 同樣缺席矩陣；且 SEC／VBS／FOR／AUD／ZR／EV
六個域**各差一項**，而總數 105 卻還是對的——表示**有別的域的項數被多算**抵銷掉了。

**階段 2 的細節**：矩陣少了 `P2-022`／`P2-023`／`P2-024`／`P2-105` 四項中的三項。

**為什麼是 P0**：README §二 的第八條判準是「**做了必可對帳**」，第九條是「**重複必消滅**」。
**一份自己項數對不上的規格，無法用來對帳。**

## 3.2 P1 — 域項數不符、前向依賴、輸出鍵無域可歸

| # | 位置 | 問題 |
|---|---|---|
| P1-① | `STAGE-01` §三 F4（宣告 12 項）vs §五 | 矩陣只 11 列；多出的一列坐在 F5 區塊裡（`P1-010`），**兩個域都對不上** |
| P1-② | `STAGE-05` §三 AU（宣告只有 `audio.`） | `P5-045`／`P5-051`／`P5-052`／`P5-053` 輸出 `display.` 與 `media.`，**兩者都不在宣告內** |
| P1-③ | `STAGE-05` §三 NS（宣告 `fw.`／`listen.`） | `P5-085`／`P5-088`／`P5-089`／`P5-090`／`P5-091`／`P5-092` 輸出 `conn.`／`share.`／`smb.`／`net.` |
| P1-④ | `STAGE-05` §二 Gate 第 7 條 | Gate 說「熱插拔事件有時間戳」，但它點名的 `P5-076` **只輸出一個計數鍵，沒有時間戳** |
| P1-⑤ | `STAGE-08` §一（宣告前置為階段 1、3、4、5） | 矩陣依賴欄卻指向**階段 2 與 6**（例如 `P8-023`→`P2-049`、`P8-064`→`P6-095`） |

## 3.3 P2 — 前向依賴（10 條）

計畫的前提是「**階段依賴更早的階段**」（README §七、各階段檔頭的前置欄）。
以下**依賴指向更後面的階段**。

**注意**：部分跨階段交叉引用可能是刻意的（例如「信任根與 VBS 對照」本來就要看階段七），
但**「依賴」欄寫的是建置順序**，不是交叉引用，因此至少需要**改寫措辭或改列為交叉引用**。

| # | 位置 | 依賴 | 問題 |
|---|---|---|---|
| P2-① | `P2-062` | → `P7-001` | 階段二依賴階段七（差 5 個階段） |
| P2-② | `P3-040` | → `P8-020` | 階段三依賴階段八 |
| P2-③ | `P3-046` | → `P8-031` | 階段三依賴階段八 |
| P2-④ | `P3-071` | → `P9-030` | 階段三依賴階段九（差 6 個階段） |
| P2-⑤ | `P3-100`／`P3-101`／`P3-103` | → `P8-088`／`P8-001`／`P6-009` | 階段三依賴階段八／六 |
| P2-⑥ | `P5-084`／`P5-096`／`P5-100`／`P5-016`／`P5-090` | → 階段 6／7／9 | 階段五依賴更後面階段 |
| P2-⑦ | `P9-018`／`P9-024`／`P9-096` | → 階段內後序 | 檔案內前向引用 |
| P2-⑧ | `P1-010` | → `P1-049` | 依賴同檔更後面的項 |
| P2-⑨ | `P1-037` | → `P1-069` | 依賴同檔後面 32 列之後的項 |
| P2-⑩ | `P6-022`／`P6-033`／`P6-098` | ↔ 階段 5／7 | **互相依賴（雙向），形成循環** |

**特別注意 P2-⑩**：階段六與階段五／七之間是**互相依賴**——
`P6-022` 依賴階段七，而階段七又依賴階段六。**這是循環，不是排序問題。**

## 3.4 P2 — 輸出鍵前綴未在任何域碼表宣告（8 條）

**規則**：讀者應該能用「前綴 → 域」定位每一個輸出鍵。以下鍵的前綴**不在任何域的前綴表裡**。

| # | 位置 | 輸出鍵 | 問題 |
|---|---|---|---|
| P2-⑪ | `P3-069` | `irq.*` | NUMA 域只宣告 `numa.`／`cache.` |
| P2-⑫ | `P3-088`／`P3-089` | `volt.*`／`throttle.*` | OC 域只宣告 `freq.`／`wall.` |
| P2-⑬ | `P3-099` | `fan.*` | PO 域只宣告 `power.`／`thermal.` |
| P2-⑭ | `P7-003`／`P7-004` | `sec.hvci`／`sec.vbs` | VBS 域宣告的是 `vbs.`／`cvm.`，不是 `sec.` |
| P2-⑮ | `P9-030` | `hw.change.timeline` | 不在 RS 域宣告的 `report.` 內 |
| P2-⑯ | `P2-043`–`P2-045` | `boot.duration_ms` 等 | 不在 FW 域宣告的 `uefi.`／`bios.` 內 |
| P2-⑰ | `P2-096`–`P2-098` | `pdev.fw.*` | 不在 EC 域宣告的 `ec.` 內 |
| P2-⑱ | `P8-102` | **佔位符** | 應為點分鍵，卻填了「（測試）」 |

**同一個病的通報**：`P1-009`（`off.mount.kind`）與 `P1-010`（`file.input.zero_egress`）
也不屬於 F1 宣告的 `scan.`／`src.`——見 P1-①。

## 3.5 P2 — 誠實宣稱漂移（鍵名像判決）（5 條）

計畫反覆強調「**陳述非判決**」（README §二 第八條、階段二 §六、階段七 Gate 第 1 條
「零判決字串守門」）。**但以下鍵名本身就是判決語彙**：

| # | 位置 | 輸出鍵 | 為什麼漂移 |
|---|---|---|---|
| P2-⑲ | `P10-053` | `res.ruling` | **ruling＝裁定**；階段十自己的 §六 說不做判決 |
| P2-⑳ | `P1-054` | `report.class` | **class＝分級**；同庫的 `P2-100` 刻意用 `level` 而非 `verdict`，這裡卻用了 `class` |
| P2-㉑ | `P7-037` | `dma.risk.facts` | **risk＝風險**；該項自己的說明寫「是事實非判決」 |
| P2-㉒ | `P3-083`／`P3-095` | `ras.risk.facts`／`wall.perf.facts` | 同上 |
| P2-㉓ | `P3-054` | `mem.speed.delta` | **delta／降頻**是比較性判讀；該檔的誠實界線說不下結論 |

**這不是措辭潔癖**——階段二 §六 明文寫：

> 所有與信任相關的輸出都是陳述（狀態、存在性、雜湊），不是判決——
> 這一點在 P2-100 的輸出鍵命名上刻意反映（`level` 而非 `verdict`）。

**計畫自己把「輸出鍵命名」當成「陳述非判決」的機械化表達**，那就得全庫一致。

## 3.6 P2 — 其他內部矛盾

| # | 位置 | 問題 |
|---|---|---|
| P2-㉔ | `P2-036`／`P2-034`／`P2-064` | ★ 錨點慣例不符：README 說「只有 ★ 錨點寫滿九欄」，實際有 ★ 項沒有九欄區塊，也有九欄區塊不屬於任何 ★ 項 |
| P2-㉕ | `P2-070`／`P2-063`–`P2-065` | 涉及 PCR 的項在「依賴」與「三態」欄互相打架 |
| P2-㉖ | `P5-090` | §六 宣稱不做風險判讀，但項名與鍵（`net.risk.facts`）斷言 risk |
| P2-㉗ | `P6-036`／`P6-039` | **敏感度超上限**：兩項標 L2，但所屬域（SV）的敏感度上限是 L1 |
| P2-㉘ | `P7-054`／`P7-056` | Gate 第 3 條引用 P7-054 支持「明示哪些區塊讀不到」，但那句話的角色其實是 P7-056 |
| P2-㉙ | `P8-098`／`P8-015`–`P8-017`／`P8-028` | Gate 第 1 條說「每個量測項都有自驗方法」，但多個量測列沒有自驗依賴 |
| P2-㉚ | `P9-096`／`P9-097` | 圖表可重現與圖表一致性守門的依賴關係互相矛盾 |
| P2-㉛ | `P4-011`／`P4-061`／`P4-104` | 依賴欄格式不合規（`P1-103` 之類不合 `P{d}-{nnn}` 格式，或與 §一 宣告的前置不一致） |
| P2-㉜ | 階段 2（無 ID） | 成本欄與項數統計漂移 |

> **更正（2026-10-16，複核後）**：這一條**不成立**。
> 實測每階段 ★ 數為 2/2/3/3/4/2/2/2/2/2 = **24 個**，沒有任何階段是 0；
> 而 `docs/ULTIMATE-1000/README.md` **從來沒有寫過「全庫 20 個 ★ 錨點」**——
> 那句話不在檔案裡。沒有「宣稱與實際不符」這回事。


## 3.7 建議的處理順序

1. **先修 2 個 P0**——它們動搖的是「1,050」這個數字本身。
   修完要**重新計算總數**，README、各階段檔頭、域碼表**三處同步**。
2. **P1-④**（Gate 與矩陣項不符）與 **P1-⑤**（宣告前置漏列）——
   這兩條會讓讀者照著錯的前提開工。
3. **前向依賴（10 條）**——需要**逐條判斷**：是「建置順序」還是「交叉引用」。
   若是交叉引用，**改寫依賴欄的措辭**（例如標成「參照」），不要動階段順序。
4. **輸出鍵前綴（8 條）**——兩種修法：
   （a）把缺的前綴補進域碼表；或（b）把鍵改用已宣告的前綴。
   **建議（a）**，因為鍵名多半已反映語意（`irq.*`／`fan.*` 比硬塞進 `numa.*` 清楚）。
5. **誠實宣稱漂移（5 條）**——改鍵名。**這是產品立場問題**，`risk`／`delta` 這類字
   在同一份計畫的其他地方是被明文禁止的。

## 3.8 這份稽核沒有涵蓋的事

- **沒有**驗證 ULTIMATE-1000 與既有 600 項的**重複率**（README 說不重複，但沒有實際對過）。
- **沒有**驗證各項的「接縫」是否真的到 API 級（這是開工前的必要條件，需逐項查）。
- **沒有**驗證輸出鍵與既有 `Services/` 是否命名衝突。

上述三項都是**圈選開工前**該做的，不在這輪稽核範圍。

---

# 第四部　已發現但尚未處理的問題

## 4.1 `PlatformGovernanceFactsService.ReadSddl` —— C 型半實作（**同一個 P0 家族**）

> **更正（2026-10-18）：已修（提交 `f1c5f6b`）。** 現在逐服務 `OpenServiceW` 取得控制代碼、
> 讀 `QueryServiceObjectSecurity` 的安全描述元、數「非 admin 可重配置」與「World 可寫」，
> 打不開的服務另外計數；**一個都沒讀到時回 `null` 讓呼叫端標 `ReadError`**，
> 不再拿「服務數是真的」冒充另兩欄。下面保留原始碼片段作為這型缺陷的教材。

```csharp
private static SddlState ReadSddl()
{
    int services = 0, nonAdmin = 0, world = 0;
    try
    {
        using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Service");
        foreach (ManagementObject o in s.Get()) using (o) { services++; }   // ← 只數，不讀 SDDL
    }
    catch { }
    return new(services, nonAdmin, world);   // ← nonAdmin / world 恆為 0
}
```

值會說「非 admin 可重配置 **0**、World 可寫 **0**」——**與 §1.2 的 SPI 是同一種病**，
而且症狀更隱蔽（服務數是真的，所以看起來像在運作）。
此項同時涉及鍵重複（`sa.sddl.summary` 在 `PlatformGovernance`，
`sa.sddl.deep` 在 `SecurityAuditDeep`）。

## 4.2 帳本宣稱「測試 +31」，實際只有 18 條

> **複核（2026-10-18）：帳目沒錯，錯的是這一條的比法。**「+31」是**執行數**的差
> （5,476→5,507，減法成立），而 18 是 `AmcacheFactsTests` 的**方法數**——兩個不同的量。
> 逐提交量測那個節點的提交 `f1c5f6b`：測試方法 4,603→4,615（**+12**），
> 而 `AmcacheFactsTests.cs` **不在那個提交的變更清單裡**（它動的是
> `FactKeyCatalogTests`／`PlatformGovernanceFactsTests`／`SecurityAuditDeepFactsTests`／`TpmFactsTests`）。
> 「12 個方法跑成 31 個案例」正是 `[Theory]` 的資料列展開，帳本用的口徑（執行數）與徽章一致。
> 教訓與 §3.1 同型：**拿兩個不同口徑的數字相減，會生出一個不存在的差值。**

`docs/PROGRESS-PLAN-2026-10-11.md` 第 550 項寫「鍵 644→647、**測試 +31（5476→5507 綠）**」，
`Tests/TestSuiteBaseline.cs` 也改成 `5507`。但
`Tests/AmcacheFactsTests.cs` 只數得到 **18** 條 `[Fact]`/`[Theory]`。

**差 13 條，來源不明。** 可能是別的檔也加了測試而帳本混算，也可能數字錯。
**這正是本專案最忌的「無法對帳的宣稱」。**

## 4.3 `FactKeyCatalog.cs` 裡一條來源不明的註解

> **複核（2026-10-18）：已修。** 註解現在直接寫明「這個教訓來自一次真實的錯位，但『錯了幾段／幾鍵』
> **沒有留下可對帳的紀錄**（`git log -S` 追不到，首次出現這段話的 commit 也還沒進 repo）——
> 能確認的只有往後不會再無從舉證：`FactKeyCatalogTests.目錄排序壞掉時要指名錯位的鍵與位置`
> 會在紅燈訊息裡指出第幾位、哪兩把鍵。**不要在這裡補一個沒人能驗證的數字。**（`GATE-LOG-257`）

工作區版本新增了這段：

```csharp
/// <b>本清單一律以全字串字典序排列</b>——<c>FactKeyCatalogTests.目錄不得有重複鍵且要排序</c> 釘住這件事；
/// 插鍵時整塊重排，不要手找位置（找前綴會把 <c>gp.*</c> 插到 <c>gpu.*</c> 後面，這一類錯位在
/// 2026-10-15 曾一次累積 20 段／183 鍵才被發現）。
```

**「20 段／183 鍵」這個事故，在帳本、git log 都找不到對應紀錄。**
（`git log --grep=183` 只找到「第 183–192 項」那個批次，與鍵錯位無關。）
實際驗過目錄本身是乾淨的：**851 鍵、0 逆序、0 重複**。
所以註解裡的規矩是對的，但那條數字**無法對帳**。

## 4.4 ZCode 守護代理仍在跑，並同時改碼

> **複核（2026-10-18）：這一條已失效（代理停工），但它的紀律照舊。**
> 該代理（Freebuff／Codebuff 桌面版）在 2026-10-18 16:54 因免費 session 結束停工
> （日誌尾端 `409 session_superseded`）。它停工時留下的未提交工作樹由接手者逐檔檢視後提交
> （第 261／263 輪 → v2.86）。**「只 `git add` 明確路徑，絕不用 `git add .`」繼續沿用**——
> 它隨時可能被重新啟動並改同一棵樹。

檢查時有 **8 個 ZCode 行程**（當日 11:44 啟動）。
工作區有 6 個我沒改過的檔案出現修改（`AcpiTableService.cs`、`TpmFactsService.cs`、
`AcpiTableTests.cs`、`TpmFactsTests.cs`、`AcpiDeep.cs`、`Tpm2.cs`，共 +547 行）。

**提交時務必只 `git add <明確路徑>`，絕不用 `git add .`。**

---

# 第五部　環境問題（非程式缺陷）

## 5.1 全套測試會「當機」——其實是被殭屍 testhost 搶 CPU

先前跑全套 25 分鐘無輸出。以 `dotnet-dump collect` + `clrstack -all` 診斷後確認：
testhost 正在執行 `ServiceOrphanGateTests.ReferencedOutsideItsOwnDeclaration`（regex 掃原始碼），
**CPU 持續上升（15 秒內 +19 秒）——是餓死，不是死鎖**。

真正的原因是**八個前一天的殭屍 testhost**（PID 5964／8832／11820／12692／26936／30280／30696／30976），
其中三個各燒掉 390+ CPU 秒。清掉之後：

- 單獨跑 `FirmwareAudit`：**15 條 251 ms**
- 單獨跑 `SecurityAudit` + `FirmwareAudit`：**69 條 607 ms**
- 單獨跑 `TpmFacts`：**8 條 55 ms**

**對照組**：同一批程式碼，在全套裡要跑 25 分鐘還跑不完。
**測試快慢取決於環境，不是程式。**

## 5.2 `work/probe/` 底下有一個獨立 .NET 專案

它的 `obj/` 產生物會被主專案的 glob 吃進來（見 §1.1）。
這是一個**結構性地雷**：以後只要主專案建置，就可能被它污染。
`Directory.Build.props` 的 `work/**` 排除解決了這一輪，但**該專案本身仍在**。

---

# 第六部　目前狀態與待辦

## 6.1 已改動、尚未提交的檔案

| 檔案 | 內容 |
|---|---|
| `Directory.Build.props` | **新增**——修 CS0579 建置死結 |
| `Services/FirmwareAuditFactsService.cs` | P0：SPI 假 0 → 誠實 `ReadError` |
| `Services/SecurityAuditDeepFactsService.cs` | P0：五個死探針接真讀取器 + 五個 `Read*` 實作 |
| `Services/CoreLatencyService.cs` | UI 凍結：`IProgress<T>` + 節流 |
| `Tests/TpmFactsTests.cs` | 三條測試 fixture 修正（ZCode 的半成品收尾） |
| `docs/FIX-REPORT-2026-10-16.md` | 本檔 |

（另有 ZCode 未提交的 Amcache 批次與 ACPI/TPM 批次，共 20 個檔案、+808 行。）

## 6.2 待辦

> **複核（2026-10-18）：以下 1–5、7 全部結案，6 已失效。** 狀態見文首的複核表；
> 這一節保留原樣，是因為「待辦清單不會自己更新」本身就是這份報告最該被記住的缺陷。

1. **跑完整套測試**確認無回歸（全套約 10 分鐘；先確認沒有殭屍 testhost）——已跑，見複核表
2. **提交並推送**——只 `git add` 明確路徑
3. **§4.1 的 C 型半實作**（`PlatformGovernanceFactsService.ReadSddl`）——與已修的兩個 P0 同家族，未處理
4. **§4.2 的 +31 vs 18** 測試數落差——查清或更正帳本
5. **§4.3 的「183 鍵」註解**——查清或移除那個無法對帳的數字
6. **§4.4**：確認 ZCode 狀態後再決定它的 547 行是否一起提交
7. **第三部的 31 條 ULTIMATE-1000 缺陷**——圈選開工前應先修（尤以 2 個 P0 為先）
8. `XinSpect.csproj` 裡那段無效的 `Compile Remove`（第 100–103 行）——**已加註說明它無效**，
   可刪可留

---

## 一句話總結

這一輪修好的是**同一種病的三個實例**——「回一個看起來成功的值，但那次讀取根本沒發生」，
以及一個卡住所有建置的 glob 死結。
**真正的收穫不是那幾行 diff，是找到這個病還有第四個實例（§4.1）還沒治，
以及 ULTIMATE-1000 計畫自己在計數上就不成立（第三部）。**
