# ULTIMATE-10000 候選能力目錄

本目錄收納 XinSpect 的 10,000 級候選能力規格庫。

| 檔案 | 性質 |
|---|---|
| `ULTIMATE-10000-EXPANSION-2026-10-25.md` | **主目錄（唯一正典）**——39 域、10,022 列，十二批展開＋五輪收尾（含 27 列勘誤植入、鍵消歧、待辦處置、79 列判定回寫）。 |
| `DEDUP-VERDICTS-2026-10-25.md` | 79 列〔查重〕標記之**程式碼級**判定（四證據面：鍵目錄／Services 原始碼／測試／UI；收口 49、新列 30）。與主目錄鍵級稽核（AUDIT-dedup）互補——鍵級看「鍵存在與否」，此表看「能力是否已在程式碼裡」。 |
| `AUDIT-dedup.md` | 鍵級查重稽核報告（對 FactKeyCatalog 1,044 鍵＋28 家族：淨新 9,930／收口 71／重複 4）。 |
| `AUDIT-semantic.md` | 語意抽審（五域 30 列，正確率 30/30）。 |
| `AUDIT-pending.md` | 待核 75 列攻堅（定案 65 含 27 列勘誤、部分 7、仍待核 3——缺口為微軟未公開 manifest）。 |
| `CIRCLE-2-CANDIDATES-2026-10-25.md` | 第二圈 30 項候選（使用者功能層：檢測／圖表／介面／匯出）。 |
| `CIRCLE-2-VERDICTS-2026-10-25.md` | 第二圈 30 項逐項收口判定（收口 6／部分收口 16／新列 8）＋對上一輪判定的三條更正。 |
| `CIRCLE-3-CANDIDATES-2026-10-29.md` | **第三圈 30 項候選（可信度層：說清楚為什麼讀不到＋量測嚴謹化）**，附每列初判線索與建議批次；尚未圈選。 |
| `ROUND-276-EDID-CTA-2026-10-27.md` | 第 276 輪工作紀錄：GP 域十列（`edid.cta.*`）落地、VIC 知識包（154 條，唯一分母可確認）、以及順手修掉的兩個實質缺陷（HDR 區塊標籤判錯、`nw.edid.summary` 三個數字沒來源）。 |
| `CIRCLE-3-PROGRESS-2026-10-29.md` | 第三圈（第 309–317 輪）進度報告：主題、逐輪一覽、九輪抓到的八個真問題、固定方法、還剩什麼、怎麼複算。 |
| `ROUND-318-README-RECONCILIATION-2026-10-30.md` | 第 318 輪：**沒有新增硬體通路**，只清掉 README 裡「同一批數字的第二個答案」——附錄 H 接進產生器（615 列→639）、正文九個舊數字回填、6.2 補第 12 個 ≥20 鍵域（`os`）、新增 11 條守門。 |
| `ROUND-319-CREDGUARD-CHAIN-2026-10-30.md` | 第 319 輪（U10-SE-0005）：Credential Guard 從一個布林變成四層狀態鏈（`sec.credguard.chain`），並修掉 `ws.lsa.summary` 把 VBS 總開關標成「Credential Guard」的標籤錯誤、以及 `VbsSnapshot` 把「清單讀不到」寫成「清單是空的」。 |
| `ROUND-320-VBS-SERVICES-2026-10-30.md` | 第 320 輪（U10-SE-0004）：VBS 安全服務清單從一句人話變成逐服務的鍵（`sec.vbs.services.summary`＋`sec.vbs.svc.*`）；代碼 0（無服務）是哨兵不進家族、兩張清單的成員取聯集、缺一張時只出讀得到的那一半。沒有新增讀取面。 |
| `ROUND-321-DEVTREE-2026-10-30.md` | 第 321 輪（U10-OS-0001）：裝置樹全圖（`os.devtree.summary`＋`os.devtree.root.*`）——層級分布、「根」有兩種（真的沒有父／父不在清單）分開計數、成環節點如實申報。樹在第 309 輪就建好了，這一輪只多一個消費者。 |
| `ROUND-322-NTFS-BOOT-2026-10-30.md` | 第 322 輪（U10-FS-0001＋FS-0002）：NTFS 開機磁區的 BPB 逐欄（`fs.ntfs.bpb`）與 MFT／MFTMirr 起點對帳（`fs.ntfs.mft.layout`）——把「磁碟自己寫的字」跟 `FSCTL_GET_NTFS_VOLUME_DATA` 並排；順手釘住有號位元組的 −10＝2^10 位元組陷阱，並拒絕為 0x50 的校驗和欄編一條規則。 |
| `ROUND-323-USB-HUB-PORTS-2026-10-30.md` | 第 323 輪（U10-NP-0003）：USB 根集線器埠數（`usb.hub.summary`＋`usb.hub.ports.*`＋`usb.hub.boundary`）——「空埠」＝埠數（hub descriptor）−在場子裝置數（PnP 樹），減法不成立時不給數字；NP-0002 的控制器版本在 MMIO、本版讀不到，寫進界線宣告。 |
| `ROUND-324-ACPI-ROOT-2026-10-30.md` | 第 324 輪（U10-AC-0001＋AC-0002）：ACPI 根指標表這一層（`ac.root.summary`／`.dsdt`／`.boundary`）——實測 RSDP 與 RSDT／XSDT 在 usermode 都拿不到（29 張表逐張看過，兩張都不在），所以「缺的表」的原因是 OS 沒給；能驗的照驗：FADT 的 FACS／DSDT 32 與 64 位位址。同輪修掉外部審查指出的 USB 埠數三處缺陷（D1 位元組寬度、D2 IOCTL input、D3 0x2A）與 D5／D6／D7／D8／D12／D13。 |
| `ROUND-325-PCIE-ACCESS-2026-10-30.md` | 第 325 輪（U10-PC-0001）：PCIe 設定空間的存取方法宣告（`pcie.access.summary`／`.agreement`／`.boundary`）——舊式 0xCF8／0xCFC（前 256 位元組）與 ECAM（4 KiB/功能）兩條通道的可用性分開寫，並比對 0:0.0 那一格；「一致」只證明兩條通道通到同一台裝置，不證明 ID 正確。沒有新增讀取後端。 |
| `ROUND-326-WHEA-MEMORY-2026-10-30.md` | 第 326 輪（U10-WH-0003＋WH-0004）：WHEA 記憶體節區（`whea.mem.summary`／`whea.mem.rec.*`／`whea.mem.boundary`）——CPER 逐欄位移照 cper.h 的 CPER_FIELD_CHECK；**ValidBits 沒設的欄位不印成 0**（「Module 0」與「韌體沒說」是兩件事）；錯誤類型碼只收 0x00–0x0C。事件 data 段的外層包裝沒有可核出處，**因此本版不拆 blob**——列得出筆數，說不出哪一條 DIMM，並把這件事寫成事實。 |
| `ROUND-327-XHCI-2026-10-30.md` | 第 327 輪（U10-NP-0002）：xHCI 版本與支援速度（`usb.xhci.summary`／`usb.xhci.ctrl.*`／`usb.xhci.boundary`）——能力區逐欄照 xHCI 規格 1.1 §5.3；控制器由 ROOT_HUB30 的父節點找出（不靠 class code 猜）。**速度值不換算成 Mb/s**：PSIM／PSIS 的位界在規格表與 Linux 標頭互相矛盾，只輸出原值。讀不到時只說缺哪一步，不從驅動版本反推。 |
| `ROUND-328-FLEET-BASELINE-2026-10-30.md` | 第 328 輪（U10-EN-0001）：同型號族群基線（`fleet.summary`／`fleet.peer.*`／`fleet.boundary`）——只讀本機快照目錄、**不上傳**（型別層有守門：兩個檔案不得出現 HttpClient／WebRequest／Socket）；型號是字串相等不是 SKU 知識；樣本少於 4 份只給中位數不給 z 分數；只比每份都有數值的鍵。 |
| `ROUND-329-NVME-RESERVED-2026-10-30.md` | 第 329 輪（U10-NV-0029）：NVMe Identify 保留區非零掃描（`nvme.rsvd.summary`／`nvme.rsvd.disk.*`／`nvme.rsvd.boundary`）——八段保留區共 1,530 位元組（邊界由 nvme.h 逐欄累加）；廠商區 vs 不算保留區。**同輪抓到已在出貨的缺陷**：WCTEMP 位移寫成 0x14A（實際落在 sanicap 裡），正解是 0x10A——程式與測試用了同一個錯誤假設，所以一路綠燈。 |
| `ROUND-330-PROCESS-RUNNER-2026-10-30.md` | 第 330 輪（修外部缺陷報告 D4／D9／D10／D11）：① `Services/ProcessRunner.cs`——兩個串流都用非同步讀**且在 `WaitForExit` 之前就開始讀**，逾時殺整棵行程樹（舊寫法下 `timeoutMs` 是安慰劑：子行程灌爆 stderr 時 `WaitForExit` 那行永遠走不到）；② `AtomicWrite` 暫存名由固定 `.tmp` 改為含行程 ID 與序號（多實例併發會把別人的半成品搬成正式檔）；③ `HistoryStore` 尾端垃圾在載入當場重寫（否則下一次 append 接在垃圾後、新的一筆讀在錯位移而丟失）；④ `ExternalReportService` 加 64 MiB 上限並把界線寫進 Status。19 條新測試，含會灌爆 pipe、併發 300 次、造 64 MB 檔案的情境。 |
| `dedup_check.py`／`sample_check.py` | 可重跑之稽核腳本（絕對路徑寫死）。 |
| `ULTIMATE-10000-EXPANSION-2026-10-24.md` | 歷史快照（十二批完成時點；**已被 10-25 版取代**，保留供對帳）。 |
| `ULTIMATE-5000-RESERVOIR-2026-10-24.md` | 前導文件（歸檔；已被主目錄取代）。 |

凡內容與主目錄牴觸，一律以主目錄（10-25 版）為準。

## 目錄性質：候選規格庫，不是待辦清單

主目錄的每一列（`U10-<域>-<編號>`）都是**候選**規格——寫進來不代表要動工。開工的觸發是**圈 ID**：
只有被明確圈選的 ID 才進入實作排程；沒圈的列只是知識庫的存貨。
同理，**展開不是完工**：把目錄寫滿 10,022 列是規格作業，與「這些能力已經在產品裡量得到」是兩件事。

## 既定開工順序

對任何一個被圈選的 ID，動工前依序走這四步：

1. **查重腳本**——比對既有事實鍵目錄（`Services/FactKeyCatalog.cs`）與 `docs/` 既有規格庫，確認候選鍵不重複、不撞鍵。
2. **TS 唯讀檢測**——用唯讀方式在真機上探測該能力可否取值（不寫入、不改狀態），留下探測證據。
3. **查重收口**——依探測結果修正鍵名與歸域，把查重結論釘死，才准寫進正典實作。
4. **知識包**——把能力包成可被取用、可被驗算的知識單元接進產品。

## 機器驗算

`Tests/Ultimate10000CatalogTests.cs` 對主目錄做結構驗算（條目數下限、每域編號連續、定義列的候選鍵覆蓋率、解析器自我檢查）。
以表格中以 `| U10-` 開頭的列為「ID 定義列」——同一 ID 在批次總帳或示例中的引用不計入。

> **2026-10-25 整合記錄**：主目錄更名為 10-25 版（內容含勘誤植入與判定回寫，ID 與項數不變，
> 結構驗算應照常通過）；79 列之程式碼級判定已以〔判定 2026-10-25：…〕形式回寫至主目錄各列，
> 原始宣稱文字保留。舊 10-24 檔轉為歷史快照。
