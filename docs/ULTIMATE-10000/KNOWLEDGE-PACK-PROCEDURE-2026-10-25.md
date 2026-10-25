# 知識包版本演進與撤回流程（U10-EN-0012／kp.evolution.proc）

日期：2026-10-25（第 271 輪）。本文件把知識包（`knowledge-pack.json`）的
**版本演進**與**撤回**從「散落的現值鍵與先例文件」收斂成一段成文流程；
對應的機器守門是 `Tests/KnowledgePackEvolutionTests.cs`（常設閘，文件形狀變了會紅燈）。

## 現狀（本文件成文之前已存在的部分）

- `KnowledgePackService`：載入、序列號由內容雜湊推導（不含 `serialNumber` 欄位的
  canonical JSON 取 SHA-256）、缺 `snapshotDate` 整包拒收、序列號不符整包拒收。
- 現值鍵：`kp.version`／`kp.digest`／`kp.snapshot_date`／`kp.update_channel`／
  `kp.swap.audit`／`kp.miss`（`KnowledgePackReport`）。這些鍵描述**當下這一版**，
  不描述「怎麼從上一版走到這一版」與「怎麼退場」——那兩件事正是本文件要補的。
- 換包（swap）已有審計：`KnowledgePackService.RecordSwap` 把換包動作記進審計鏈
  （append-only，`--verify-audit` 可驗）。

## 一、版本演進（新 serialNumber 覆蓋舊版時帶前版 digest）

### 流程

1. 產生新內容（新版次 `packVersion`、新的 `generatedAt`、新的表集合）。
2. 呼叫 `KnowledgePackService.Evolve(prior, newPackVersion, generatedAt, tables)`：
   新包的 `previousSerial` 欄位**必須**填前版包的完整 `serialNumber`。
3. 換上磁碟的包檔；照既有流程呼叫 `RecordSwap` 記審計鏈。

### 機器形狀

- 包的 JSON 增一個**向後相容**欄位：`"previousSerial"`（可空）。
  舊格式包沒有此欄位＝null，序列號計算一個位元都不變。
- `previousSerial` 非空時**納入序列號雜湊**：篡改「前版指標」＝序列號不符＝整包拒收。
  演進鏈因此與完整性綁在一起，不是一個可以事後改寫的說明欄。
- 演進的第一版（沒有前版）＝`previousSerial` 缺省，與舊格式無異。

### 記錄格式（人讀）

每版次的演進記錄至少要有：版次、生成時間、完整 serialNumber、
前版 serialNumber（鏈首寫「無——首版」）、變更說明（哪張表、快照日期動了什麼）。
沒有更新通道是設計（`kp.update_channel` 如實聲明），但**手動換檔的每一步要有跡可循**。

## 二、撤回（withdrawal）

### 撤回聲明的形狀

撤回有兩個面，兩面都要有：

1. **機器面（審計鏈）**：呼叫 `KnowledgePackService.RecordWithdrawal(serialNumber, reason, auditPath, out _)`，
   記進一筆 append-only 的審計紀錄——
   `Action = "knowledge-pack-withdraw"`、`Scope =` 被撤回包的**完整 serialNumber**、
   `ResultSummary =` 撤回理由。撤回聲明缺 serialNumber 或理由，兩者缺一**不成聲明**（服務拒收）。
2. **人讀面（先例形狀）**：比照 `work/WITHDRAWN-P2-052-FIT.md` 的欄目——
   **徵狀**（為什麼要撤）、**處置**（退回未開工／移除哪些鍵與檔）、**修好的路徑**
   （什麼條件下才回來）。P2-052 的核心句是撤回的判準：
   「一個『測試會過但我不確定為什麼會過』的解碼器，會對真實映像給出看起來合理的錯誤結果——那比沒有更糟。」

### 撤回後載入行為

撤回聲明記完之後，把包檔移除。載入層（`KnowledgePackService.Load`）對缺檔的既有行為就是
撤回後的**契約**：如實回「沒有外掛知識包（檔不存在）——內建表繼續服役」，狀態鍵標
`NotSupported` 帶原因——**不假裝還有舊版可用，也不猜被撤回的內容**。撤回包的 serialNumber
仍可在 `kp.digest` 的拒收路徑看到（「包被拒收，但檔案宣告過這個雜湊」）——那是查證用的，
不是可用性的宣告。

### 撤回 ≠ 換包

換包（`RecordSwap`）是「換一份合格的上去」；撤回（`RecordWithdrawal`）是
「這一份內容本身不可信，退回內建表」。兩者都進同一條審計鏈，Action 不同、語意不同，
不可互相冒用。

## 三、先例引用

- `work/WITHDRAWN-P2-052-FIT.md`：撤回先例（第四次同型撤回）。確立了撤回的判準
  （「我沒有依據」而非「太難」）與成功路徑的對照（固定表頭＋固定長度子結構一輪就過）。
- `docs/ULTIMATE-1000/GATE-LOG-256.md`／`GATE-LOG-260.md`：知識包相關閘門的記錄所在
  （E 節：知識包——「可離線使用的清單；值讀得到，無清單可比」）。

## 四、守門（機器檢查什麼）

`Tests/KnowledgePackEvolutionTests.cs`（常設閘）：

1. 演進：`Evolve` 產生的新包帶前版完整 serialNumber；前版指標納入序列號雜湊
   （同一組內容、有無前版指標，序列號不同）；寫檔→載入往返成立；
   篡改 `previousSerial` 一個字元＝整包拒收。
2. 向後相容：沒有 `previousSerial` 的舊格式包照樣載入，且序列號與成文前一致。
3. 撤回：聲明進審計鏈且 `AuditVerifier` 可驗、`Scope` 帶完整 serialNumber；
   缺 serialNumber 或理由＝拒絕記錄；撤回後移除包檔→載入如實回內建表聲明。
4. 本文件存在且含本節標題（文件與守門一起搬才不會走散）。

追蹤鍵：`kp.evolution.proc`（字面常數在 `Tests/KnowledgePackEvolutionTests.cs` 與
`KnowledgePackService` 的註解中，供掃描）。
