# 企業離線基準套件

組織可在管理端簽發自訂硬體基準，端點以預先信任的 ECDSA P-256 公鑰驗證後，對 XinSpect 快照執行條件比對。流程不需要伺服器或網路。政策簽章識別持有受信金鑰的簽發者；快照的 SHA-256 只驗證內容完整性，兩者都不構成裝置來源或硬體狀態的遠端證明。

政策簽發與逐項報告仍由 CLI 提供。GUI 的「硬體證據實驗室」已接入已存快照評估：依序選擇組織預先部署公鑰、簽章政策與已存快照，使用共用簽章、期限及條件核心驗證，顯示符合／不符合／無法驗證計數、實際政策版本與語意，以及所選公鑰 SHA-256 指紋。公鑰信任來源仍須經組織通道另行核對；這個入口不重新採集硬體，也不是完整企業 GUI 流程。

## 政策版本與格式

政策 JSON 使用 UTF-8、camelCase 欄位。支援 `schemaVersion: 1` 與 `schemaVersion: 2`；新政策可使用 v2 明確指定控制識別碼與單位，既有 v1 套件仍可驗證。簽章套件外層與簽署的政策內容必須使用相同版本。未知版本、未知欄位、重複 JSON 欄位、整數形式的 `match` 或無效規則均拒收。

共同限制如下：

- `policyId` 必須為非空白的 1 至 128 字元。
- `createdAtUtc`、`expiresAtUtc` 必須為 UTC，且到期時間晚於建立時間。
- 規則數為 1 至 500，不接受 `null` 規則。
- `key` 為最多 160 字元的明確事實鍵，格式為 `[a-z0-9][a-z0-9._-]*`，逐字查找；不支援萬用字元、別名、大小寫正規化或自動挑選其他裝置實例。
- 政策 JSON／簽章套件最多 1,048,576 UTF-8 bytes；ECDSA 私鑰與信任公鑰 PEM 各最多 16,384 bytes。CLI 在解析前檢查檔案大小，超限即拒絕。

### v2：明確單位與控制識別碼

```json
{
  "schemaVersion": 2,
  "policyId": "org-secure-boot-v2",
  "createdAtUtc": "2026-11-17T00:00:00Z",
  "expiresAtUtc": "2027-02-17T00:00:00Z",
  "rules": [
    {
      "requirementId": "audit-secure-boot-text",
      "key": "platform.secure_boot",
      "match": "Exact",
      "unit": "",
      "expectedValue": "開啟"
    },
    {
      "requirementId": "acceptance-secure-boot-setting",
      "key": "platform.secure_boot",
      "match": "NumericRange",
      "unit": "",
      "minimum": 1,
      "maximum": 1
    }
  ]
}
```

此範例使用登錄檔來源的 `platform.secure_boot`：現行來源對啟用設定輸出文字「開啟」、`numericValue: 1` 與明確空單位；停用則為「關閉」與 0。可用性仍取決於當次快照，登錄檔自述也不是韌體量測或裝置證明。請先核對實際來源與快照欄位，不能把範例當成所有平台均有該證據的承諾。

v2 每條規則須指定非空白且逐字唯一的 `requirementId`，長度為 1 至 128 字元，不接受控制字元。不同控制項可引用同一把 `key`，會分別判定與計數；上例的兩條規則共用一份證據，不能據此宣稱有兩個獨立驗證來源。`requirementId` 是政策控制識別碼，不是新增的事實鍵。

`unit` 對兩種比對方式都必填，不可省略或為 `null`，最多 64 字元且不可含控制字元。`""` 明確表示無單位。比對使用逐字相等，不裁切空白、不改大小寫、不解讀同義詞，也不換算攝氏／華氏、Hz／MHz、百分比／比例等單位。快照單位缺失或為 `null` 代表未知，不會補成 `""`；即使數值或文字相同，單位不完全相同仍是 `Unverifiable`，不判成符合或不符合。

| `match` | 必要運算元 | 判定方式與限制 |
|---|---|---|
| `Exact` | 非 `null` 的 `expectedValue`，最多 4096 字元，可為空字串 | 對完整 `Value` 逐字比較，包含語言、大小寫、空白與格式；不把「是」、「開啟」、`true` 視為同義。v2 不可同時指定非 `null` 的 `minimum` 或 `maximum`。 |
| `NumericRange` | 有限的 `minimum`、`maximum`，且前者不得大於後者 | 僅使用有限的 `NumericValue`，包含上下界；不從顯示文字解析數字。`expectedValue` 須省略或為 `null`。 |

例如要求 MDM 記錄數至少為 1 時，可信且 `Present` 的數值 0 是 `NotSatisfied`，不能因為「有這把鍵」就算符合。缺數值或數值無效則是 `Unverifiable`。

### v1：保留原簽章格式與未綁定單位的語意

```json
{
  "schemaVersion": 1,
  "policyId": "org-secure-boot-v1",
  "createdAtUtc": "2026-11-17T00:00:00Z",
  "expiresAtUtc": "2027-02-17T00:00:00Z",
  "rules": [
    { "key": "platform.secure_boot", "match": "Exact", "expectedValue": "開啟" }
  ]
}
```

v1 規則仍以事實鍵唯一，不得重複 `key`，也不得夾帶 v2 的 `unit` 或 `requirementId` 欄位。v1 的 `Exact` 同樣逐字比對，`NumericRange` 同樣使用有限數值與包含上下界的範圍，但兩者均未綁定單位。報告會以 `semantics` 明示這項舊版限制，不會把 v1 自動解讀為有單位保護的 v2。

v1 簽發保留原有 canonical payload 的欄位集合、欄位順序、不適用運算元的 `null`、時間序列化方式及依 `key` 的 ordinal 排序；v2 欄位不會插入 v1 payload。驗證使用套件中原始 payload bytes 驗簽，再解析政策，不要求重建或重新排序既有簽署內容。v2 的 canonical 規則順序則按 `requirementId` ordinal 排序，簽章涵蓋該識別碼與單位。

**位元組與簽章相容不代表保留原先較寬鬆的證據條件。** 所有版本現在都拒絕 `Unknown` 信任、缺來源及 `zr.compliance.*` 合規輸出作為輸入證據；這些情況即使為 `Present` 也回 `Unverifiable`。`infer.*` 與 `Speculative` 的既有排除仍保留。因此，同一份已簽 v1 政策可能仍驗簽成功，但對原本信任或來源不充分的快照，會得到更保守的結果。

從 v1 遷移時，應核對每個來源的實際單位與文字、為控制項配置識別碼，再重新簽署 v2 政策。不能只改 `schemaVersion`，也不會由事實鍵或顯示文字自動猜出單位。

## 證據可用性與判定

條件判定有三態，與證據的六態分開保留：

| 判定 | 意義 |
|---|---|
| `Satisfied` | 可比較的證據符合該條組織政策。 |
| `NotSatisfied` | 可比較的證據與條件相反。 |
| `Unverifiable` | 缺少可比較的證據或單位等條件不成立；不能當成符合。 |

只有 `Present`、事實鍵完全一致、值未遮蔽、來源非空白、信任等級為 `Reported`／`Derived`／`Measured` 的證據才可進入比較；v2 另要求單位逐字相同。`Reported` 是來源自述，`Derived` 是推導，`Measured` 表示依來源方法取得，都不是精度或裝置真實性的認證。

其餘五種可用狀態 `Unknown`、`NotApplicable`、`NotSupported`、`InsufficientPrivilege`、`ReadError` 均為 `Unverifiable`，報告仍保留原本的 `evidenceAvailability`。缺鍵、鍵與證據身分不符、遮蔽值、`Unknown`／`Speculative` 信任、`infer.*`、缺來源與 `zr.compliance.*` 自引用也不能作為通過證據。CLI 快照格式驗證會拒收重複事實鍵，屬輸入錯誤；供其他入口使用的純 adapter 對重複鍵不任選來源，該鍵不參與比對。

本格式沒有設定證據的新鮮度門檻或跨裝置實體等價規則。政策期限有效不代表快照足夠新；採集時間與來源標示應由組織另行核對。動態索引鍵也不自動構成跨機裝置識別。

## 管理端簽發

簽發私鑰只保留在組織控制的管理端或受控簽署環境，不部署到端點、不放在共用網路目錄，也不要把 PEM 內容放入命令列、腳本日誌或支援工單。政策套件本身含組織的預期設定值與控制識別碼，也應按內部政策保護。管理者先依組織密鑰管理程序建立並保護 ECDSA P-256 金鑰；公鑰透過既有軟體部署信任通道預先送到端點。

先在管理端產生加密的 P-256 私鑰與公鑰；OpenSSL 會互動式要求私鑰密碼：

```text
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -aes-256-cbc -out baseline-signing-private.pem
openssl pkey -in baseline-signing-private.pem -pubout -out baseline-trusted-public.pem
```

將密碼由組織的秘密管理工具安全管線送入 stdin，再簽署政策：

```text
<組織秘密管理器輸出密碼> | XinSpect --enterprise-baseline-sign policy.json --signing-key baseline-signing-private.pem --password-stdin --out signed-policy.json
```

簽發命令只接受加密 PEM 私鑰與 stdin 密碼，只寫出簽章套件，不把密碼或私鑰印到 stdout。輸出不得覆寫政策或私鑰來源。此命令應由管理者在受控管理端執行；XinSpect 不替組織產生、備份或散佈私鑰。套件使用 ECDSA P-256／SHA-256 與 P1363 固定長度簽章格式，演算法識別為 `ECDSA-P256-SHA256`。

## 端點 CLI 評估與版本報告

```text
XinSpect --enterprise-baseline signed-policy.json --trusted-key <預先部署公鑰.pem> --snapshot <硬體快照.json> [--out report.json]
```

端點先驗證政策簽章、格式及有效期間，再讀取快照並驗證快照完整性，最後執行規則。信任公鑰不從待驗政策取得；信任金鑰不符、簽章錯誤或套件格式不符時拒絕評估。尚未生效的政策標示無法驗證，已過期的政策標示 `Expired`，兩者都不執行條件比對，也不讀取快照。評估既有快照不會觸發硬體重新採集。

JSON 報告輸出政策識別碼、簽發者公鑰 SHA-256 指紋、政策簽章／期限狀態、分項計數及證據摘要。`schemaVersion` 是實際通過驗證的政策版本，可能為 1 或 2，不是全域最新版本；`semantics` 說明該版比較及單位語意。拒收而未建立可信政策版本時，版本與語意為 `null`。

逐條結果包含 `key`、v2 的 `requirementId`、判定、`evidenceAvailability`、來源、信任等級、採集時間及證據單位。報告不複製原始 `Value`、`NumericValue`、期望值或快照中的原始錯誤原因，但政策控制識別碼與事實鍵本身仍可能是組織內部資訊，應妥善保護。

`policySignatureValid` 只表示政策已由預先信任的公鑰驗簽並通過格式檢查。評估報告本身與快照來源沒有簽章，也沒有遠端證明；快照 SHA-256 只偵測內容變更，不證明快照由 XinSpect 或可信端點產生。報告不含整體安全分數；`Satisfied` 只代表此快照在所述限制下符合該版組織政策。

| 退出碼 | 意義 |
|---|---|
| 0 | 全部條件符合。 |
| 2 | 有不符合、無法驗證、尚未生效或過期的政策／證據。 |
| 1 | 輸入、格式、快照驗證、政策簽章或報告寫出錯誤。 |

報告輸出不得覆寫政策套件、信任公鑰或輸入快照。不符合與無法驗證分別計數，不會合併成整機安全或故障判決。

## GUI、知識包與尚缺入口

既有 `device-compliance` 知識表只表達「要求識別碼 → 事實鍵」，沒有期望值、運算元與單位條件。非空舊表仍標示 `NotSupported`，不能因鍵存在就宣告符合；無表、空表等情形沿用知識包原本的可用狀態。這種知識包的完整性序號不會轉成政策簽章信任。

共用條件核心及 DeviceCompliance 的內部 v2 純 adapter 可處理呼叫端提供的快照，但純 adapter 本身不驗簽、不檢查政策是否已生效或過期，也不進行硬體採集。它排除 `zr.compliance.*` 舊合規輸出並拒用重複鍵。GUI 的已存快照匯入另由 `EnterpriseBaselineImportService` 呼叫共用套件服務：先驗證簽章及期限，再載入有大小上限與 SHA-256 完整性檢查的快照，並於讀取後再次檢查政策期限。匯入前清除舊結果，取消選檔不沿用前次成功；條件結果僅呈現彙總計數，不列控制識別碼、事實值、選定路徑或 PEM 內容。

GUI 已具備上述手動選檔的已存快照基本評估；尚缺受管理信任儲存區的佈署／撤銷／輪替、即時完整快照的評估與重評、政策歷程／遷移編修、逐項報告匯出及歷程等企業流程。檔案讀取與驗證在背景工作執行，單次匯入期間不啟動第二次匯入；目前沒有執行中取消或硬性執行期限，忙碌狀態會等到實際 I/O 完成。政策簽章不證明裝置來源，快照與畫面結果均未簽章，也未做端點遠端證明。本說明不宣稱已完成實機視覺或互動驗收；裝置合規、採購驗收及稽核控制映射的 GUI 全流程仍屬部分完成，亦不構成任何稽核框架認證。

## 信任與營運界線

- 公鑰信任指紋應經組織既有部署或設定管理通道核對；單獨收到的公鑰檔不會自動建立信任。
- 政策需設定到期日；變更規則時更新組織政策識別並重新簽署，勿將政策內容版本與格式 `schemaVersion` 混為一談。
- 本版使用離線 PEM 金鑰，不連線，不支援 TPM/HSM 簽署，也不提供集中派送、撤銷服務或角色權限系統。
- 不提供自動單位換算、語意布林解析、複合條件、跨機裝置身分解析、校準或端點證明。使用同一份證據符合多條控制項，也不增加其來源獨立性。
- 端點評估不執行硬體寫入或重新採集。敏感值依快照原有遮蔽政策處理；遮蔽值無法作為條件比對證據。
