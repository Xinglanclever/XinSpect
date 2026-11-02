# XinSpect 合規匯出格式 v1

本規格定義 `XinSpect --compliance` 的五種輸出：JSON（預設）、SARIF 2.1.0、JUnit XML、CEF 與 OTLP/JSON logs。
五者都只轉譯 `ComplianceMappingService` 的既有判定，**不重新量測、不驗簽、不連網**。

## 1. 共同契約

### 1.1 判定與嚴重度

| 判定 | 意義 | SARIF | JUnit | CEF | OTel |
|---|---|---|---|---|---|
| `Satisfied` / `Mitigated` | 本次看到的事實支持這條控制／緩解成立 | 不產生 result | pass | severity 2 | INFO (9) |
| `NotSatisfied` / `Exposed` | 事實顯示控制不成立／曝險存在 | `error` | `failure` | severity 8 | ERROR (17) |
| `Unverifiable` / `Unknown` | 缺資料或工具沒有驗證 | `warning` | `failure` | severity 4 | WARN (13) |
| `NotApplicable` | 平台不適用 | 不產生 result | `skipped` | severity 0 | DEBUG (5) |

**`Unverifiable` 永遠不是通過。** 它可以在不同格式裡是 warning、failure、severity 4 或 WARN，
但不得被寫成 skipped、INFO 或「無 finding」。把「工具沒驗」洗成綠色，是本規格唯一禁止的錯誤。

### 1.2 順序與識別

- 控制項依 `ComplianceMappingService` 的固定順序輸出，再輸出 ATT&CK 技術。
- 控制項 id 形如 `NIST-800-147-WP`；ATT&CK id 形如 `T1542.001`。
- 同一次輸出中 id 不重複。
- 輸出不含事實值；只含鍵名、判定、原因與（CEF／OTel 的）嚴重度。

### 1.3 退出碼

| 退出碼 | 條件 |
|---|---|
| 0 | 所有控制項為 `Satisfied`／`NotApplicable`，且所有技術為 `Mitigated`／`NotApplicable` |
| 2 | 有任何 `NotSatisfied`／`Unverifiable`／`Exposed`／`Unknown` |
| 1 | 收集失敗、寫出失敗、`--format` 缺值或不認得的值 |

## 2. JSON

根物件欄位：

| 欄位 | 型別 | 說明 |
|---|---|---|
| `generatedAtUtc` | string (date-time) | 產生時間 |
| `scope` | string | 固定 `compliance` |
| `schemaVersion` | number | 目前為 `1` |
| `summary` | object | 控制項與技術的計數 |
| `controls` | array | 每項含 `id`、`framework`、`title`、`requirement`、`status`、`reason`、`evidence` |
| `attack` | array | 每項含 `id`、`name`、`exposure`、`reason`、`factKeys` |
| `note` | string | 說明本模式沒有驗簽、沒有 TPM Quote、沒有觸發復原 |

## 3. SARIF 2.1.0

根物件必須有 `$schema`（**帶錢號**）、`version`（`2.1.0`）與 `runs`。

- `runs[0].tool.driver.name` 為 `XinSpect`。
- `runs[0].tool.driver.rules` 包含全部控制項與 ATT&CK 技術，id 與 result 的 `ruleId` 對應。
- `runs[0].results` 只包含非通過項：`NotSatisfied`／`Exposed` 為 `error`，`Unverifiable`／`Unknown` 為 `warning`。
- `Satisfied`、`Mitigated`、`NotApplicable` 不產生 result。
- 每個 result 帶 `message.text`、`locations[0].logicalLocations[0].fullyQualifiedName` 與 `properties.status`／`reason`。

## 4. JUnit XML

根元素 `testsuites`，屬性 `name`、`tests`、`failures`、`skipped`、`time`、`timestamp`。

- 每個控制項／技術一個 `testcase`，`classname` 為 framework（ATT&CK 為 `MITRE ATT&CK`）。
- `NotSatisfied`／`Unverifiable`／`Exposed`／`Unknown` → `<failure>`，`type` 為判定名稱。
- `NotApplicable` → `<skipped>`。
- `Satisfied`／`Mitigated` → 沒有子元素（pass）。
- `failures` 計數包含 `Unverifiable`／`Unknown`；`skipped` 只計 `NotApplicable`。

## 5. CEF

每項一行，格式：

```text
CEF:0|XinSpect|XinSpect|<version>|<id>|<name>|<severity>|rt=<epoch-ms> <extension>
```

- 標頭中的反斜線與直線轉義為 `\\`、`\|`；換行轉義為 `\n`、`\r`。
- 延伸欄位為 `key=value`，值中的反斜線、等號與換行轉義；欄位之間以單一空格分隔。
- 控制項欄位：`externalId`、`cat`、`cs1Label=Status`、`cs1`、`cs2Label=EvidenceKeys`、`cs2`、`msg`。
- ATT&CK 欄位：`externalId`、`cat=MITRE-ATTACK`、`cs1Label=Exposure`、`cs1`、`cs2Label=FactKeys`、`cs2`、`msg`。
- `rt` 為 Unix epoch 毫秒。整份輸出不含網路位址、不主動連線。

## 6. OTLP/JSON（logs）

根物件 `resourceLogs`，第一筆的：

- `resource.attributes` 至少含 `service.name=XinSpect`、`service.version`。
- `scopeLogs[0].scope.name` 為 `XinSpect.Compliance`。
- `scopeLogs[0].logRecords` 每個控制項／技術一筆，欄位：
  - `timeUnixNano`：字串形式的 Unix epoch 奈秒。
  - `severityNumber`／`severityText`：依第 1.1 表。
  - `body.stringValue`：`<id>：<reason>`。
  - `attributes`：`compliance.id` 或 `attack.technique.id`、`compliance.framework`、`compliance.status`、
    `compliance.keys`（`arrayValue.stringValue[]`）。

## 7. 版本與變更

- 本規格版本為 v1，對應 `ComplianceMappingService.SchemaVersion = 1`。
- 新增欄位是相容變更；刪除欄位、改變判定語意或把 `Unverifiable` 改成通過，都是破壞性變更，必須升版。
- 文件與程式的一致性由 `Tests/ComplianceExportSpecTests.cs` 守門。