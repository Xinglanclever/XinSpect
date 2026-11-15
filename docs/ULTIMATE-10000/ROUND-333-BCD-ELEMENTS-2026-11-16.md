# 第 333 輪（U10 第四圈批次三）：BCD 開機元素現值

> 日期 2026-11-16 · 性質：唯讀讀取面（bcdedit ×2）＋純解析 · 六態形狀

## 功能

回答的問題：**「BCD 裡其他的開機設定現在是什麼？」**——第 315 輪答了「先開誰、等幾秒」，
本輪補上使用者常問的其餘元素（U10-BD-0002／0004～0007／0011／0014／0023／0024／0030）：

| 鍵 | 內容 |
|---|---|
| `bcd.bootmgr.tools` | toolsdisplayorder（工具選單順序） |
| `bcd.bootmgr.bootsequence` | 一次性開機覆寫（下一次開機生效、開完即失效） |
| `bcd.bootmgr.device` | 開機管理器的 device／path |
| `bcd.loader.device` | default 載入器的 device／osdevice／path 三件並排（不一致＝開機/系統磁碟分離，如實並排） |
| `bcd.loader.nx` | DEP 政策（nx） |
| `bcd.loader.hvlaunch` | hypervisorlaunchtype（與 virt.* 各自獨立，不反推） |
| `bcd.badmemory.list` | badmemorylist／badmemoryaccess（「沒有標記」≠「沒有壞頁」） |
| `bcd.mem.trim` | removememory／truncatedmemory（「少 1GB」的第一個該看處） |
| `bcd.fwbootmgr.order` | {fwbootmgr} displayorder（**/enum firmware** 第二次呼叫；非 UEFI＝NotApplicable） |
| `bcd.access.statement` | BD-0030 界線：唯讀兩次呼叫、現讀無快取、不讀 hive、不寫 BCD |

## 位置

| 檔案 | 角色 |
|---|---|
| `Services/BcdElementsFactsService.cs` | 新增：十把鍵的純解析服務（解析共用 `BootOrderFactsService` 的公開助手——沒有第二個 BCD 解析器） |
| `Services/BootOrderReader.cs` | 重構：抽私有 `TryRead(args)`，新增 `TryReadEnumFirmware()`（同一執行檔、同一逾時、同一唯讀承諾） |
| `Services/BootOrderFactsService.cs` | 解析助手（ParseBlocks／Identifiers／Values／DescribeAll）轉 public 並寫明共用理由 |
| `Services/EvidenceLabService.cs` | `BcdElementsFacts` 屬性＋`LoadBcdElements()`＋AllFacts 串接 |
| `Services/EvidenceCollection.cs` | `Measure(svc, "LoadBcdElements", ...)` 步驟（有看門狗） |
| `Tests/BcdElementsTests.cs` | 新增：10 條測試（十鍵到齊、逐值對照、兩層讀取各自三態、非 UEFI＝NotApplicable、缺欄位＝Present 帶說明） |
| `Services/FactKeyCatalog.cs` | +10 鍵（1,192→1,202） |
| `Services/FactCoverageReport.cs` | 覆蓋基線 458→468（十把鍵同一讀取面的不同欄位，理由逐條寫明） |
| `Tests/TestSuiteBaseline.cs` | 8,084→8,094（+10） |
| `docs/百科-v3.17.md`＋三語 README＋CI/PR/CONTRIBUTING | 統計對齊實況（鍵 1,202／域 160／服務檔 686／測試檔 656） |

## 可能問題（誠實申報）

1. **/enum firmware 是第二次行程呼叫**：與 /enum 同一執行檔同一逾時，但每次收集多跑一次
   bcdedit（毫秒級、有 60 秒看門狗）。fwbootmgr 在非 UEFI（legacy MBR）機器上不存在——
   如實標 NotApplicable，不從 WMI 推測。
2. **韌體層與 os 層各自三態**：/enum 失敗不拖累 /enum firmware，反之亦然——兩次讀取的失敗理由
   分開寫進各自的鍵與存取宣告。
3. **default 指不到載入器區塊時**（{bootmgr}.default 不在 /enum 輸出裡）：loader 相關三鍵如實說
   「找不到系統載入器區塊」，不猜。
4. **badmemorylist 的值是頁框號**（十六進位、0x 前綴）——原樣列出；「沒有標記」與「沒有壞頁」
   的差別寫在值裡。
5. **多值欄位**（displayorder／bootsequence／badmemorylist）在 bcdedit 輸出是空白分隔——
   解析沿用第 315 輪的 `Values()`（空白切開）；續行欄位沿用既有邏輯。

## 怎麼複算

```bash
grep -c '^        "' Services/FactKeyCatalog.cs          # 1,202
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~BcdElements" \
  -p:BaseOutputPath="obj/_verify_r333/"
```
