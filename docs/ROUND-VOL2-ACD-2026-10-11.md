# Vol2 三批次（A／C／D）一次做完 — 2026-10-11

> **圈選範圍**：`docs/INDUSTRIAL-CATALOG-VOL2-2026-10-11.md` 的建議批次 **A（自我完整性）＋C（新維度質變）＋D（伺服器與情境）**，共 15 項。
> **一句話**：把「工具證明自己沒被動過」、「從讀值到判斷」、「伺服器與情境」三條線一次補上；**該誠實的地方全部如實**——
> CVE 表只交付框架不塞未經查證的條目、磁碟排行只讀不刪、事件只觀察不建立因果、基線樣本不足就說樣本不足。
>
> 全部數字可重跑；**這一輪沒有紅燈**（含被我修掉的兩處既有紅燈，見 §6）。

---

## 0. 交付一覽

| 批次 | 項 | 交付 | 事實鍵 |
|---|---|---|---|
| A | IN-001 | 自身二進位 SHA-256 ＋與審計基線對帳 | `in.self` |
| A | IN-002 | 設定檔反篡改（同一條基線機制；損毀→建議先備份再重建，不靜默） | `in.cfg.integrity` |
| A | IN-004 | token 特權報告（提權／完整性層級／啟用特權數） | `in.privs` |
| A | IN-007 | 依賴完整性（載入模組簽章＋側載候選，路徑判定不是惡意判決） | `in.deps` |
| A | IN-010 | 自我完整性報告（一頁摘要，含「不是防篡改保證」） | `in.report` |
| C | BL-001 | 自基線引擎（逐指標中位數／P95／平均±標準差＋樣本量） | `bl.baseline` |
| C | BL-004 | 異常評分卡（最新一點對基線的最大 z 偏差；分數≠診斷） | `bl.score` |
| C | BL-007 | 事件標註層（WHEA／TDR／非預期關機／藍屏映到時間軸，不建立因果） | `bl.events` |
| C | QS-001 | 事實鍵物理合理域檢查（超域標 Unknown、不改寫成 0；無規則≠沒問題） | `qs.range` |
| C | SE-002 | OS CVE 離線對照**框架**（格式／載入／比對；出貨表刻意是空的） | `se.cve` |
| D | SV-014 | 伺服器合規摘要（聚合既有角色事實；裝了≠配置好） | `sv.summary` |
| D | SG-002 | 磁碟為什麼滿（唯讀排行；不刪任何東西） | `sg.diskfull` |
| D | SG-011 | 電腦為什麼當（四類事件的事實面；觀察不是診斷） | `sg.freeze` |
| D | RS-002 | SBOM（CycloneDX 1.5，序號由內容雜湊推導、可重現） | `rs.sbom` |
| D | SV-001 | **早已存在**（`role.surface`／`role.installed.*`）→ 沒有重做，只補 SV-014（見 §1.2） | — |

外加兩條 CLI 命令（兩者都是**明示觸發**，不掛在啟動路徑上）：

```
XinSpect --integrity-baseline [日誌路徑] [--out <檔案>]   # 把自身二進位／設定檔雜湊記進審計日誌（唯一寫入路徑）
XinSpect --sbom [--out <檔案>]                            # 輸出 CycloneDX 1.5 JSON
```

---

## 1. 動工前的證據（這一段是這一輪存在的理由）

### 1.1 圈選規則

Vol2 §使用規則：**「圈了 ID 才開工，沒圈的只是候選」**。本輪先向使用者確認圈選範圍，答案是「三批一起同時、作一個 md」——
於是範圍＝ A／C／D 三批的 15 項，**不做跨到其餘 285 項的隨機子集**。

### 1.2 SV-001 不是新工作（實查）

```
grep -rn '"role\.' Services/        → RoleSurfaceFactsService 已生產 role.surface／role.surface.evidence
grep  -n 'role.installed.' Services/FactKeyDynamicCatalog.cs   → 動態家族早已登記
```

**結論**：Vol2 的 SV-001（伺服器角色盤點）在本專案 **v2.x 就已存在**（選用功能＋角色的服務狀態）。
這一輪**不重做**，只補原本缺的聚合層（SV-014），並在 `ServerComplianceFactsService` 的註解裡寫明這件事。
若照本宣科再寫一支盤點服務，就會多出兩份會互相漂移的真相——這正是 `WiringDecisions` 那套機制要防的。

### 1.3 動工前工作樹**本來就是紅的**（兩處，都不是本輪造成的）

| 紅燈 | 證據 | 歸屬 |
|---|---|---|
| 動態家族家數超上限 | `git show HEAD:Services/FactKeyDynamicCatalog.cs` = **16 家族**、`git show HEAD:Tests/FactKeyCatalogTests.cs` 上限 **16**（相符）；工作樹 **18 家族**、上限仍 16 → 紅 | 同時進行的 v2.55 工作新增 `audio.latency.`／`sa.lsp.` 兩個家族，未同步上限 |
| 事實鍵目錄未排序 | `FactKeyCatalogTests.目錄不得有重複鍵且要排序`：檔案順序 `sa.exposure.read` 在 `sa.exposure.rdp` **之前**，Ordinal 排序要求相反 | 同時進行的 v2.55 工作新增 `sa.exposure.*` 時插錯位置 |

我**沒有**無視它們（那只會讓下一個人以為是我弄壞的），也沒有偷偷用 `[Skip]` 蓋掉；
處理方式與理由寫在 §6，兩處都是**最小幅度**的修正。

---

## 2. 設計上刻意做的取捨（這一節是本輪最值得讀的部分）

### 2.1 逐項明細全部收斂回彙總值：**這一輪沒有新增任何動態家族**

原設計想為 `bl.baseline.m.*`、`se.cve.hit.*`、`sg.diskfull.item.*`、`sg.freeze.recent.*`、`in.deps.flag.*`
各登記一個動態家族。動手後撞上既有的上限守門（家數上限是「回頭數一數」的觸發器），於是反過來問：
**這些成員真的編譯期數不出來嗎？**

- `bl.baseline.m.*`：成員＝`HistoryMetrics` 的 13 項，**編譯期固定** → 不該走家族（走家族等於說謊）。
- `sg.diskfull.item.*`／`sg.freeze.recent.*`：數量是我們自己定的（TopCount／RecentCount），**也是固定的**。
- `in.deps.flag.*`：成員由「本行程載入了什麼」決定 → 真動態；`se.cve.hit.*`：由離線表內容決定 → 真動態。

最後**全部**收斂：逐項明細以 `；` 串在彙總值裡（可讀、可比較、進時間膠囊一樣可比），
**本輪新增動態家族數＝0**。代價寫在 §7：逐項明細不再是獨立的鍵（拿 `--query` 濾單一項時要自己從值裡讀）。

### 2.2 CVE 離線對照：只交付框架，**刻意不塞條目**

`se.cve` 的每一列都是一句關於真實世界的斷言（哪個組建受影響、修補在哪個組建）。
**沒有來源的 CVE 清單比沒有這張表更糟**：它會讓使用者以為自己剛剛被比對過。
所以本輪交付：離線表格式（`snapshotDate` 缺漏即**拒收**）、外部檔載入器（`cve-offline.json`，與執行檔同目錄）、
組建號**數字**比較（實測：字串比較會把 `26100` 判在 `9000` 之下）、命中一律標「待查證」。
隨程式出貨的表是空的 → `se.cve` 會如實說「收錄 0 條⋯這不是『沒有問題』，是『還沒有資料』」，
可用性標 `NotSupported`，理由「不適用，不是『沒有已知問題』」。

### 2.3 磁碟為什麼滿：**只讀排行、不刪任何東西**，預設不掃使用者設定檔

- 本模組**沒有任何刪除／移動路徑**，也不提供「一鍵清理」。
- 深度上限（預設 2 層）＋略過計數都寫進值裡：**略過多＝數字偏低＝可能低估**，不假裝掃得很完整。
- 使用者設定檔是真正吃掉空間的那一個，但整份遞迴要數十秒——那種成本不該掛在每次啟動上，
  所以預設根只有暫存與傾印目錄；要整份掃就把 profile 加進 `ScanRequest.Roots`（值裡會寫「掃了哪些根」）。

### 2.4 事件與基線：**觀察不是診斷**，樣本不足不給半條基線

- `sg.freeze`：四類事件計數＋最近五筆，並聲明「非預期關機也可能是停電、電源、硬體或驅動」。
- `bl.baseline`：樣本 < 30 點如實標「樣本不足，不給半條基線」；歷史倉的 0 是「沒讀到」的既有表示法，
  整段皆 0 的指標不進模型（`HistorySeries.HasData`）。
- `bl.score`：分數不是診斷——偏差大只代表「跟這台機器自己的過去不一樣」。
- `qs.range`：域是**物理合理域**不是健康範圍（70 °C 在域內不代表健康）；無規則**不等於**沒問題（分母誠實）。

### 2.5 基線存哪裡：**不新增第二套儲存**

IN-001／IN-002 的基線存進**既有審計日誌**（`AuditLogService`：append-only＋每筆雜湊鏈＋`--verify-audit` 可在 App 外驗）。
唯一寫入路徑是 `--integrity-baseline`（明示命令）。寫不進去時回「基線沒建立，不是已建立」，退出碼 2。

---

## 3. 逐檔改動

**新增服務（8 檔，共 1,513 行）**

| 檔案 | 行數 | 內容 |
|---|---|---|
| `Services/SelfIntegrityFactsService.cs` | 481 | IN-001/002/004/007/010 ＋ `TokenPrivilegeReader`（P/Invoke，失敗回 null 不冒充「未提權」） |
| `Services/BaselineLearningService.cs` | 261 | BL-001/004/007 ＋ `EventAnnotationLayer`（純函式分類） |
| `Services/KeyRangeGuardService.cs` | 110 | QS-001（16 條域規則，每條要寫物理依據） |
| `Services/CveOfflineFactsService.cs` | 166 | SE-002 框架 |
| `Services/ServerComplianceFactsService.cs` | 76 | SV-014 |
| `Services/DiskFullFactsService.cs` | 201 | SG-002（可注入 `DiskProbe`，測試餵假檔案系統） |
| `Services/FreezeDiagnosisFactsService.cs` | 65 | SG-011（重用事件標註層與真實事件通路） |
| `Services/SbomService.cs` | 153 | RS-002（CycloneDX 1.5，內容雜湊推導序號） |

**新增測試（6 檔，66 個 `[Fact]`／`[Theory]`，實跑 72 個案例）**

`SelfIntegrityFactsTests`(21)、`BaselineLearningTests`(13)、`ServerComplianceFactsTests`(15，含 DiskFull 與 SBOM)、
`CveOfflineFactsTests`(8)、`KeyRangeGuardTests`(5)、`CliIntegrityAndSbomTests`(4)。

**修改（10 檔）**

| 檔案 | 改動 | 為什麼 |
|---|---|---|
| `Services/EvidenceLabService.cs` | 7 個事實群組屬性＋`Load*` 方法；`AllFacts` 串接；`qs.range` 與 `cap.*` 同款派生 | 事實要進得了 UI／CLI／報告共用的集合 |
| `Services/EvidenceCollection.cs` | `LoadUsermodeFacts` 新增 6 個載入（自我完整性／CVE／伺服器摘要／磁碟／當機／SBOM） | UI 與 CLI 的**單一組合點**；磁碟留在便宜路徑的理由寫在註解 |
| `Services/CliService.cs` | `--integrity-baseline`、`--sbom`（含說明文字與退出碼語意） | 讓「寫基線」與「匯出 SBOM」有明示出口 |
| `ViewModels/StartupSequence.cs` | 基線學習吃 `vm.History.Query(30 天)` | 歷史倉住在 VM 上（有狀態），不能塞進「不需驅動的來源」組合點 |
| `Services/RoleSurfaceFactsService.cs` | 新增 `SurfaceKey` 常數並取代字面值 | 讓聚合層引用鍵名而不是再抄一次字串 |
| `Services/KernelModuleService.cs` | 抽出 `VerifyAuthenticodeWith(action, path)`＋公開 `VerifyGenericAuthenticode` | 驗 DLL 不寫第二份 wintrust P/Invoke |
| `Services/FactKeyCatalog.cs` | ＋13 把固定鍵、**重排為 Ordinal 排序**、註解計數 208 → 221 | 目錄＝編譯期全鍵；排序是守門要求（原為既有紅燈） |
| `Tests/FactKeyCatalogTests.cs` | 家族上限 16 → 18（附理由） | 見 §6 |
| `Nav/ChangelogCatalog.cs`、`Tests/TestSuiteBaseline.cs`、三份 `README`、`Views/AboutView.xaml`、`XinSpect.csproj` | 版號 2.56 六處一致＋沿革一筆＋測試基線 3699 → 3781 | 專案的發版規矩（由 `ChangelogTests`／`release.ps1` 預檢守著） |

---

## 4. 驗證（全部可重跑）

```bash
cd /c/Users/Administrator/XinSpect
set -o pipefail    # 保留被測命令的離開碼（管線不得吞掉失敗）

# 4.1 本輪所有新測試
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo --filter \
 "FullyQualifiedName~SelfIntegrityFactsTests|FullyQualifiedName~BaselineLearningTests|FullyQualifiedName~KeyRangeGuardTests|FullyQualifiedName~CveOfflineFactsTests|FullyQualifiedName~ServerComplianceFactsTests|FullyQualifiedName~DiskFullFactsTests|FullyQualifiedName~SbomTests|FullyQualifiedName~CliIntegrityAndSbomTests"
#  → 已通過! - 失敗: 0，通過: 72，總計: 72        離開碼 0

# 4.2 受影響的治理守門（目錄／接線／孤兒）
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo --filter \
 "FullyQualifiedName~FactKeyCatalogTests|FullyQualifiedName~WiringGuardTests|FullyQualifiedName~ServiceOrphanGateTests"
#  → 已通過! - 失敗: 0，通過: 18，總計: 18        離開碼 0

# 4.3 執行期三方對帳（真的跑三條入口，把執行期鍵集合與目錄／家族／白名單逐鍵比對）
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo --filter "FullyQualifiedName~FactKeyRuntimeReconcileTests"
#  → 通過（新增的 13 把鍵與 7 組事實都在對帳範圍內）

# 4.4 版號與文件一致性
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo --filter \
 "FullyQualifiedName~ChangelogTests|FullyQualifiedName~ReleaseIntegrityTests|FullyQualifiedName~ReleasePipelineTests|FullyQualifiedName~AppInfoTests"
#  → 已通過! - 失敗: 0，通過: 19，總計: 19        離開碼 0

# 4.5 主專案建置（警告基線是 0）
dotnet build XinSpect.csproj -v q --nologo
#  → 0 個警告、0 個錯誤

# 4.6 全套
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo
#  → 已通過! - 失敗: 0，通過: 3781，總計: 3781，持續時間: 4 m 49 s   離開碼 0
```

> **為什麼是 3781 而不是 3782**：同一棵樹先後兩次全套差了 1 條——差的是**別人的未追蹤本機探針**
> （`Tests/TempGalleryShot.cs`，第一次跑還在、第二次跑已被刪）。`TestSuiteBaseline` 的註解早就警告過這件事：
> 「不含未追蹤的本機探針⋯對外的數字要用乾淨 checkout 也對得上的那一個」。
> 所以基線與徽章採**乾淨樹**的 3781。

> **上一輪的三個環境紅燈這一輪沒有出現**：同一台機器、同一條命令，全套全綠。
> 也就是說上一輪的 GPU 實測（虛擬顯示轉接器）與效能預算（節電電源計畫）兩項已經回到可用狀態，
> 那兩條紅燈的歸因（環境）因此得到反證支持——不是程式面。

---

## 5. 這一輪的誠實性測試（專門盯「不准講漂亮話」的那些）

| 測試 | 它釘住什麼 |
|---|---|
| `沒有基線時_如實說還沒有基線而不是相符` / `審計讀不到時_不說相符也不說沒有基線` | 三態：**讀不到**不等於「相符」也不等於「沒有基線」 |
| `權限讀不到_標讀取錯誤不冒充未提權` | 「讀不到」不得畫成「未提權」 |
| `模組簽章無法驗證_如實寫無法驗證而不是未通過` | 「無法驗證」與「未通過」是兩件事 |
| `設定檔損毀_標Unknown並要求先備份` | 損毀要說出來並建議先備份，不靜默覆蓋 |
| `樣本不足時_如實標示而不是給半條基線` | 樣本量門檻寫進值裡 |
| `沒有可用基線時_說無法評分而不是給零分` | 沒有基線 ≠ 正常（不以 0 分冒充） |
| `本版出貨的表是空的_且如實說還沒有資料而不是沒問題` | 空表不得讀成「沒有已知問題」 |
| `組建號比較是數字比較_不是字串比較` | 附反例：字串比較會誤命中 |
| `事件記錄讀不到_標讀取錯誤不畫成零次` / `都沒有事件時_如實說…記錄可能被清理` | 讀不到 ≠ 0 次；也沒有事件 ≠ 沒問題 |
| `讀不到的目錄計入略過_不當成零用量` / `事實要說出只讀不刪與低估風險` | 磁碟排行不得把讀不到當 0 |
| `元件來源失敗時_標讀取錯誤不給空SBOM` | 空 SBOM 不冒充完成 |
| `報告_有待確認項時不得讀成全部通過` | 摘要不得把待確認吞掉 |
| `每一個自身二進位事實都要帶防篡改界線` | 「不是防篡改保證」必須出現在值裡 |

---

## 6. 我修的兩處**既有紅燈**（不屬於本輪，但擋著驗證）

| 修法 | 理由 | 反對意見（保留） |
|---|---|---|
| `Tests/FactKeyCatalogTests.cs`：家族上限 `16 → 18`，並在註解寫出兩個新家族（`audio.latency.` 逐端點延遲樣本／`sa.lsp.` Winsock 分層服務提供者）為何**真的無法靜態枚舉** | 上限的作用是「逼人回頭數一數」，不是永遠的常數；數過了、理由寫下來了，就該跟著事實走 | 這兩條家族的**作者**不是我，理由是我讀程式碼後補的——若不同意，回退這兩行即可，本輪交付不受影響 |
| `Services/FactKeyCatalog.cs`：把整個鍵清單**按 Ordinal 重排**（內容一字未改、無重複） | 守門要求「目錄要排序」；重排是純機械修正，不改任何語意 | 重排讓 diff 變大（但這是唯一能讓守門誠實通過的方式） |

> 這兩處我**沒有**用放寬斷言、`Skip` 或抑制警告的方式繞過：一處是把上限調到事實所在並寫下理由，一處是把資料修成守門要求的形狀。

---

## 7. 界線（這一輪**沒有**證明的事）

1. **沒有開 App 目視版面**：新事實走 `LoadUsermodeFacts` → `AllFacts` → 韌體安全頁的列／CLI／報告匯出。
   新增的分類是「自我完整性」「基線學習」「查詢與智慧層」「進階安全」「伺服器角色」「情境包」「報告輸出」，
   **那一頁的分組與排序會不會被新分類擠亂，需要人眼確認一次**（與上一輪同一條界線；本輪仍未目視）。
2. **BL-007 的「畫在時間軸上」只做到資料層**：`EventAnnotationLayer` 產出標註、`bl.events` 反應計數，
   但**沒有接進任何圖表控制項**（把 WHEA／TDR 標在溫度曲線上這件事尚未實作）。
3. **SBOM 未寫過實檔**：`--sbom --out` 有測（輸出、欄位、可重現），但沒有把真實機器產出的檔案拿去做外部工具驗證。
4. **CVE 離線表是空的**（見 §2.2），所以「命中」路徑只有測試替身走過，真表還沒有人做。
5. **磁碟排行沒有掃使用者設定檔**（見 §2.3）：目前的自動事實只涵蓋暫存與傾印根，`--deep` 之類的明示入口還沒做。
6. **README 下載表的位元組數仍是 v2.55 的實測值**：連結與註腳已指到 v2.56（守門要求），
   三個數字要等**真正發版**時由 `Tools/release.ps1` 自動同步——本輪**沒有**發版、沒有 commit。
7. **正對照只有一條**：家族上限與排序兩處去紅是「把既有工作修成守門要的形狀」，不是「守門在我手上真的咬過」；
   本輪新守門的咬人行為只在各自的否定案例（上表 §5）裡被證明。

---

## 8. 還沒做（下一批候選，依價值排序）

1. **把事件標註畫上時間軸**（BL-007 的 UI 面）：這是本輪唯一「交付了資料但沒交付畫面」的項目。
2. **磁碟掃描的明示入口**：`--disk-usage --deep`（含使用者設定檔）＋把 TopCount／根清單變成使用者可選。
3. **三方對帳再進一步**（上一輪留下的 §7.2）：目前是「目錄 ≡ 掃描」與「執行期 ≡ 目錄／家族／白名單」，
   還缺「申報的覆蓋率是不是拿真的執行期集合當分母」這一層的逐鍵證據。
4. **CVE 知識包流程**（F4）：離線表的來源、更新、快照政策——沒有這條流程，`se.cve` 就一直是空的（而那是誠實的）。
5. Vol2 其餘批次：B（檔案鑑識主線，含 FS-013 隱私禁項機制化）、以及 A／C／D 裡被我放掉的細項。
