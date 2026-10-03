# XinSpect corpus 貢獻包格式 v1（骨架）

- **狀態**：**骨架**——本文件定義「什麼可以成為社群貢獻包」；**上傳通路、伺服器、同意流程刻意不實作**（收集等社群，重開時先立同意閘門與資料主權審查）。
- **實作**：`Services/CorpusUploadService.cs`（機器對帳：`Tests/CorpusUploadTests.cs`）。

## 1. 派生規則（淨化是上傳前的第一道門）

1. **只收遮蔽版快照**：`sensitiveValuesPreserved = true` 的快照**拒收**（`Build` 回 null）——
   那代表使用者主動要求保留敏感識別，不可能同時是匿名貢獻。
2. **身份鍵逐鍵排除**：key 含 `serial`／`uuid`／`mac`／`asset.tag`／`system.product`／`user.`
   （不分大小寫）一律剔除——比事實自己的 `sensitive` 旗標更嚴（旗標漏標也不外洩）。
3. **匿名機器識別保留**：單向雜湊派生的 `anonymousMachineId` 是貢獻包的價值所在
   （同一台機器的時間序列），不含任何明文身份。

## 2. 格式

```json
{
  "schemaVersion": 1,
  "toolVersion": "2.5.0",
  "anonymousMachineId": "…64 hex…",
  "capturedAtUtc": "2026-10-03T00:00:00+00:00",
  "facts": [ { "key": "…", "value": "…", "…": "…" } ],
  "integrity": { "algorithm": "sha256", "hash": "…" }
}
```

`facts` 元素與快照 v1 的 fact 定義相同（`snapshot.schema.v1.json` 的 `$defs.fact`）。

## 3. 重開上傳前的必備條件（未滿足前不出貨）

1. 使用者主動的逐次同意（不是一次性授權）；
2. 上傳內容本地預覽（使用者看得到每個鍵）；
3. 資料主權聲明（保留期限、刪除管道）；
4. 伺服端不改寫、只存放（否則完整性信封失去意義）。
