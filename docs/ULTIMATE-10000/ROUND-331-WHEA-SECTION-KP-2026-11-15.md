# 第 331 輪（U10 第四圈批次一）：WHEA 節區知識包

> 日期 2026-11-15 · 性質：知識包（解碼表）＋守門 · **零新讀取面**（純資料與查表，不碰事件、不碰硬體）

## 功能

這一輪回答的問題：**「WHEA 事件說的『節區』『通知類型』是什麼意思？」**——既有 `os.ev.whea.*`
只答「幾筆事件」、第 326 輪的 `whea.mem.*` 只解記憶體節區的內容；本輪把解碼表本身攤開成事實，
讓 WHEA 的自述接得上「這是哪一類硬體、哪一種通知」。

兌現的 U10-ID 與鍵（六把，全數靜態）：

| U10-ID | 鍵 | 內容 |
|---|---|---|
| WH-0005～0010 命名層 | `whea.sec.kp.summary` | 收錄 **15** 條節區型別 GUID、**8** 條通知類型、**9** 格 PCIe 埠型別（數字由表 Count 算出） |
| 同上 | `whea.sec.kp.sections` | 節區型別對照表（UEFI 規格 v2.7 附錄 N.2.2；含 CXL 3.0 三條事件記錄、DMAR 三條） |
| WH-0015 | `whea.sec.kp.notify` | 通知類型對照表（CMC／CPE／MCE／PCIe／INIT／NMI／BOOT／DMAR） |
| WH-0007 | `whea.sec.pcie.devtype` | PCIe 埠型別碼表（SDK cper.h 列舉 0/1/4–10；**2／3 未定義如實標「不猜」**） |
| WH-0012 | `whea.sec.ipf.legacy` | IPF 遺產節區＝NotApplicable；**連 GUID 都不命名**（見可能問題①） |
| WH-0013／0014 | `whea.sec.boundary` | 界線宣告：不拆事件 data 段 blob、未收錄 GUID 原始列出不猜語意、GHES 只有出處宣告沒有解碼實作 |

出處：UEFI 規格 v2.7 附錄 N.2.1／N.2.2（經 Linux `include/linux/cper.h` 逐條對照，
kernel.org 2026-11-15 取得）＋Windows SDK 10.0.26100.0 `um/cper.h` 的
`WHEA_PCIEXPRESS_DEVICE_TYPE` 列舉。**23 條 GUID 的位元組序列全部與 canonical 值逐字核對過**。

## 位置

| 檔案 | 角色 |
|---|---|
| `XinSpect.Decoders/WheaSectionKnowledge.cs` | 新增：三張對照表＋查表函式（`SectionName`／`NotifyName`／`PciePortName`）；全公開成員帶 `[SpecRef]`；零 I/O |
| `Services/WheaSectionFactsService.cs` | 新增：六把鍵的純投影服務（`Collect(DateTimeOffset)`） |
| `Tests/WheaSectionKnowledgeTests.cs` | 新增：12 條測試（唯一分母、四條 GUID 逐字抽樣、大小寫／大括號容忍、未知回 null、IA64 故意不收、PCIe 2／3 未定義、摘要對帳、繁中無簡體、六鍵到齊） |
| `Services/FactKeyCatalog.cs` | +6 鍵（1,178→1,184；字典序插入 `whea.sec.*` 六行） |
| `Services/EvidenceLabService.cs` | `AllFacts` getter 的純陳述區接一行 `AddRange(WheaSectionFactsService.Collect(...))`（與 MeasurementBudget 同款） |
| `Services/FactCoverageReport.cs` | `ReviewedUncoveredBaseline` 448→450（見可能問題③） |
| `Tests/FactCoverageReportTests.cs` | 六把鍵的未覆蓋理由逐條寫明；淨額 +2 的帳目寫清 |
| `Tests/ReadmeStatsTests.cs` | 修兩處（見可能問題④）：6.2 守門改讀百科＋regex 容忍 CRLF |
| `Tests/TestSuiteBaseline.cs` | `ProjectTests` 8,059→8,071（+12 條實測） |
| `docs/百科-v3.17.md`＋`README.md`／`.zh-CN`／`.en` | 統計表全部對齊本版實況（鍵 1,184／域 157／服務檔 684／測試檔 654／`[Fact]` 6,680／方法數 6,957／全域 C# 檔 1,618／覆蓋基線 450／≤3 鍵域 66） |
| `百科.html`／`手冊.html` | `gen-docs-html.py` 重建 |
| `docs/ULTIMATE-10000/CIRCLE-4-CANDIDATES-2026-11-15.md` | 新增：第四圈候選清單（32 項、六批次）＋本輪圈選批次一 |

## 可能問題（誠實申報）

1. **IPF 節區的 GUID 本版不命名**：Linux cper.h 的 `CPER_SEC_PROC_IPF` 把 **9 個**位元組參數傳給
   只收 8 個的 `GUID_INIT` 巨集——IA64 支援移除後該巨集從未被展開，編譯器沒機會抓，位元組序列
   不可信。兩份公開源對不出一致值＝待核。x86 機器上此節區常態不存在，影響面為零。
2. **本輪沒有節區位元組的解碼器**：事件 data 段外層包裝無可核出處（第 326 輪已確立），
   逐欄解碼（處理器／PCIe／PCI-X 節區）留待下一輪，以純函式＋呼叫端提供位元組的形狀落地。
   現在這六把鍵是「表」，不是「行為」。
3. **覆蓋基線 448→450（淨額 +2）**：六把新鍵全部無第二條通路（解碼表的來源就是表本身）。
   設定基線 448 時的實況其實是 444（餘額 4），本輪 +6 進來後實況 450，上限照實況移。
   每把鍵的理由寫在 `FactCoverageReportTests`。
4. **接手一條既有紅燈（非本輪造成）**：`ReadmeStatsTests.第六點二節的域清單…` 在 HEAD 上就是紅的——
   v3.28 的 README 重構（`b2ac571`／`deaf314`）把 6.2 正文搬進 `docs/百科-v3.17.md`，但守門還在讀
   README.md（實測 HEAD 的 README.md 有 0 個 `#### 6.2.N` 標題）。本輪把讀取對象跟著搬到百科，
   並讓 regex 容忍 CRLF 行尾。**教訓：重構文件結構時，讀那份文件的守門要同一輪搬。**
5. **圈選前的一次工具誤判（記錄在案）**：候選清單圈選前做了一次「孤兒服務」全庫盤點，第一輪掃出
   68 個「零引用」——全部是 `grep` 對含中文內容的檔案回「binary file matches」並跳過匹配的誤判；
   加 `-a` 重掃後真孤兒為 0（本專案 2026-10-10 起就有 `WiringGuardTests` 守接線）。
   教訓：**引用數掃描一律 `grep -a`；「沒接線」的結論要過 `WiringGuardTests` 的口徑再說出口。**

## 怎麼複算

```bash
# 鍵數／域數／服務檔數
grep -c '^        "' Services/FactKeyCatalog.cs          # 1,184
python Tools/gen-readme-domains.py --check               # 逐域展開與文件同步（回零）
# 本輪 12 條測試
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~WheaSectionKnowledge" \
  -p:BaseOutputPath="obj/_verify_r331/"
```
