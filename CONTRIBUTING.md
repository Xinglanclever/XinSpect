# 參與 XinSpect

感謝你有興趣。這個專案是一支以「讀到什麼就說什麼」為核心的 Windows 硬體檢視工具：
它把硬體、韌體、作業系統三方的陳述並列，讀不到就寫原因，矛盾就具名列出，**不替使用者下結論**。
貢獻的門檻不在於寫得多，而在於**每一句宣稱都能被查核**。

- 授權：從 v2.69 起為 [AGPL-3.0-or-later](LICENSE)。送出的貢獻以相同授權釋出，不需要簽 CLA。
- 行為守則：[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
- 安全性問題：**不要開公開 Issue**，請走 [SECURITY.md](SECURITY.md) 的私密通報。
- 使用問題與判讀討論：優先用 [Discussions](https://github.com/Xinglanclever/XinSpect/discussions)；
  確定是缺陷再開 Issue。

---

## 一、先選對入口

| 你想做的事 | 去哪裡 |
|---|---|
| 問「這個數字是什麼意思」 | Discussions（Q&A／判讀） |
| 回報當掉、畫面空白、讀值與文件不符 | [錯誤回報](https://github.com/Xinglanclever/XinSpect/issues/new?template=bug_report.yml) |
| 回報某顆硬體讀不到或解碼錯 | [硬體判讀回報](https://github.com/Xinglanclever/XinSpect/issues/new?template=hardware_report.yml) |
| 建議新能力 | [功能建議](https://github.com/Xinglanclever/XinSpect/issues/new?template=feature_request.yml) |
| 回報安全邊界問題 | [私密安全公告](https://github.com/Xinglanclever/XinSpect/security/advisories/new) |
| 送程式或文件 | 開 PR，並先讀完這份文件 |

**回報時請勿附上**序號、UUID、MAC、資產標籤、使用者名稱或完整路徑。
需要附 `--json` 輸出時，先用 `--only` 縮小範圍，並自行遮蔽敏感值。專案的資料主權立場見
README 第十四章與 `docs/DATA-SOVEREIGNTY.md`。

---

## 二、開發環境

| 需求 | 說明 |
|---|---|
| Windows 10 22H2 以上／Windows 11 x64 | WPF 與特權讀取路徑只在 Windows 上成立 |
| .NET SDK 10.0 | `dotnet --version` 應顯示 10.x |
| Git | 建議 `core.autocrlf=true`（本庫在 Windows 上開發） |
| PowerShell 5.1 | 發版與部分工具腳本使用；檔案必須是 UTF-8 BOM |

不需要 Visual Studio。`Bridge/` 是 .NET Framework 4.8 的 XTU 橋接程式，
透過 `Microsoft.NETFramework.ReferenceAssemblies` 建置，不需要另外安裝開發包。
主專案建置時會自動以巢狀 MSBuild 建置並內嵌它。

```powershell
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet restore Tests/XinSpect.Tests.csproj
```

---

## 三、建置與測試

```powershell
# 建置（含 Bridge 與解碼器）
dotnet build Tests/XinSpect.Tests.csproj -c Release

# 全套測試（目前基線見 Tests/TestSuiteBaseline.cs；v3.03 之後為 7,522 條）
dotnet test Tests/XinSpect.Tests.csproj -c Release

# 只跑文件與目錄守門（CI 跑的就是這一組）
dotnet test Tests/XinSpect.Tests.csproj -c Release --no-build `
  --filter "FullyQualifiedName~ChangelogTests|FullyQualifiedName~FactKeyCatalogTests|FullyQualifiedName~Ultimate10000CatalogTests|FullyQualifiedName~ReleasePipelineTests|FullyQualifiedName~ReleaseIntegrityTests"
```

- 全套測試會碰到真機硬體（SMBIOS、SPD、NVMe 等），在 GitHub 的 hosted runner 上不保證成立，
  所以 CI 只跑不依賴硬體的建置與守門測試。**送 PR 前請在本機跑完整套。**
- 測試數的唯一來源是 `Tests/TestSuiteBaseline.cs` 的 `ProjectTests`。加測試就改它，
  `ChangelogTests` 會同步要求三份 README 徽章一致。
- 測試數要從 `dotnet test` 的實際輸出行抄，**不要從上一筆紀錄推算**（本庫真的發生過 +31 對不上帳）。

---

## 四、五種常見變更的流程

### 1. 新增事實服務

1. 先寫測試（紅），確認讀不到時會落在哪一種六態，以及原因是什麼。
2. 事實必須帶 `source`；值與狀態分開，不把 0 當成「沒有」。
3. 接進 `EvidenceCollection.LoadUsermodeFacts` 或驅動組的具名入口；
   `ServiceOrphanGateTests` 會抓「有實作但沒有生產引用」。
4. 在 `Services/FactKeyCatalog.cs` 登記鍵（整塊插入後**整體重排**，不要手找位置），
   補 `docs/` 的覆蓋申報所需資訊。

### 2. 新增解碼器

1. 放進 `XinSpect.Decoders`（純函式、零 WPF 相依）。
2. 每個公開方法附 `SpecRef`（文件名稱、章節、暫存器、位元位置）。
3. 登記進 `SpecRefRegistry.CoveredDecoders`；註冊表釘住測試會要求同步。
4. 對 `DecoderHardeningTests` 補逐臂斷言，包含邊界與不合法輸入。

### 3. 新增對帳規則

1. 在 `FactRelationEngine.cs` 的 `FactRelationRules.All` 新增條目，附 `SpecRef` 與誤報條件。
2. 同步登記輸入鍵。
3. 在 `FactRelationTests` 補逐情境斷言（相符、矛盾、缺一邊）。
4. **不要新增第二份規則表**——v2.87 的匯流排表缺陷就是兩份表各自漂移的結果。

### 4. 新增頁面或控制項

1. 在 `PageRegistry` 註冊導覽，或在 `WiringDecisions` 明文登記為獨立視窗／卡片。
2. `ViewEntryGateTests` 會抓「寫完但沒人進得去」；`UiSmokeTests` 會抓綁定路徑錯誤。
3. 可視化的色階、規格區間、統計方法必須重用既有單一來源；控制項不內建規格值。

### 5. 文件與數字

- 三份 README（`README.md`、`README.zh-CN.md`、`README.en.md`）共用同一組事實；
  現在式的數字（版號、測試數、位元組數、規則數、鍵數）只能有一個來源。
- 版號變動時，`Nav/ChangelogCatalog.cs` 最上面補一筆；`ChangelogTests` 會比對 csproj 版號。
- 改數字時連守門一起改，不要只改文字。

**README 的逐域展開（6.3／6.5／附錄 F／F-2）與服務檔地圖（附錄 H）由工具重生，不要手改：**

```bash
python Tools/gen-readme-domains.py          # 重生（只動那五個區段）
python Tools/gen-readme-domains.py --check  # 只比對；有差異 exit 2
```

`--check` 是 `ReadmeDomainTablesTests` 的同一件事的手動版——測試紅燈時先跑它看差在哪。
它**只動它負責的五個區段＋目錄的 6.3 與附錄 H 條目**，其餘一個字都不碰；既有的列與條目原樣保留
（名稱、來源、可信度、註解、原始碼片段都是人工與工具協作的結果，重跑不該洗掉），
只有目錄裡新出現、文件裡還沒有的鍵才由原始碼抽後設資料補上——抽不到就留「—」，
那是如實的「這個抽取方式抓不到」，不是遺漏。

附錄 H 是第 318 輪接上來的：那張「服務檔地圖」原本由一次性腳本寫成，於是
**檔案加了、地圖沒加**（實測：磁碟 639 個檔、表上 615 列，還有 1 列指向已刪除的檔案），
而標題的「（N 個檔案）」停在 618。現在它的列集合直接從 `Services/` 走訪而來，
行數與類別數當場算，沒有第二份名冊可以漂移。

目錄那一塊是第 283 輪補進來的：條目編號、字數與錨點原本靠手維護，於是「章節重編號」
與「目錄」各自漂移（v2.99 時目錄寫 `6.3.57 域 kp`、章節已經是 `6.3.58`，沒有測試看得出來）。
現在編號、字數、錨點都由章節推導，`ReadmeDomainTablesTests` 逐條比對。

---

## 五、誠實契約（會直接退回的寫法）

以下任何一項都會被要求重做，不是風格問題：

1. 用 0、典型值、上一個值或「N/A」填補讀取失敗。
2. 把「未跑」說成「已量測」、把「能力支援」說成「目前生效」。
3. 沒有 `source` 的讀值，或沒有 `SpecRef` 的解碼器。
4. 把 `NotSupported`（平台不提供）與 `NotPresent`（這台機器沒有）混成同一種。
5. 合成總分、排名、紅綠燈或健康結論。
6. 預設開啟網路請求、背景上傳或遙測；連網功能必須由使用者主動觸發並在文件說明。
7. 繞過守門測試、放寬斷言或加抑制來讓 CI 變綠。

---

## 六、Commit 與 PR

- Commit 訊息用**繁體中文**，說明「為什麼」而不是只列「改了什麼」。
- 一個 PR 一件事；重構與行為變更分開。
- PR 範本裡的清單請逐項確認，特別是測試實測數字與敏感資料。
- 不要提交 `bin/`、`obj/`、`publish-*`、`work/` 或個人快照。
- 不要改動 `LICENSE`、`THIRD-PARTY-NOTICES.md` 的授權層次，除非 Issue 已明確同意。

發版（tag、Release、上傳三資產、位元組數 docs commit）由維護者依 `Tools/release.ps1` 執行，
不在一般 PR 的範圍內。

---

## 七、贊助

專案免費且以 AGPL 釋出。若你希望支持維護，GitHub Sponsors 入口在
[github.com/sponsors/Xinglanclever](https://github.com/sponsors/Xinglanclever)。
贊助不影響 Issue 排序、功能取捨或審查標準；沒有贊助也一樣歡迎貢獻。

---

## 八、行為

參與本專案即表示同意 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
對事不對人：可以強烈質疑一個數字的出處，不要質疑提出數字的人。
