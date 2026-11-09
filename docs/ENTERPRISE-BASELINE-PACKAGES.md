# 企業離線基準套件

組織可在管理端簽發自訂硬體基準，端點以預先信任的 ECDSA P-256 公鑰驗證後，對已驗證的 XinSpect 快照評估。流程不需要伺服器或網路；快照的 SHA-256 只證明檔案未意外損壞，政策套件的數位簽章才識別簽發者。

## 政策格式

政策 JSON 使用 UTF-8、camelCase 欄位，schemaVersion 必須為 1：

```json
{
  "schemaVersion": 1,
  "policyId": "org-workstation-v1",
  "createdAtUtc": "2026-11-09T00:00:00Z",
  "expiresAtUtc": "2027-02-09T00:00:00Z",
  "rules": [
    { "key": "platform.secure_boot", "match": "Exact", "expectedValue": "是" },
    { "key": "cpu.cores", "match": "NumericRange", "minimum": 8, "maximum": 64 }
  ]
}
```

輸入大小上限：政策 JSON／簽章套件最多 1,048,576 UTF-8 bytes；ECDSA 私鑰與信任公鑰 PEM 各最多 16,384 bytes。CLI 在解析前檢查檔案大小，超限即拒絕。

事實鍵不得重複，規則數為 1 至 500。`Exact` 比對 Present 事實的值；`NumericRange` 使用快照的 numericValue，包含上下界。非 Present、缺鍵或沒有有效數值一律為 `Unverifiable`，絕不當成符合。規則只表達組織自訂基準，不是 XinSpect 的安全建議或整機安全分數。

## 管理端簽發

簽發私鑰只保留在組織控制的管理端或受控簽署環境，不部署到端點、不放在共用網路目錄，也不要把 PEM 內容放入命令列、腳本日誌或支援工單。政策套件本身含組織的預期設定值，也應按內部政策保護。管理者先依組織密鑰管理程序建立並保護 ECDSA P-256 金鑰；公鑰透過既有軟體部署信任通道預先送到端點。

先在管理端產生加密的 P-256 私鑰與公鑰；OpenSSL 會互動式要求私鑰密碼：

```text
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -aes-256-cbc -out baseline-signing-private.pem
openssl pkey -in baseline-signing-private.pem -pubout -out baseline-trusted-public.pem
```

將密碼由組織的秘密管理工具安全管線送入 stdin，再簽署政策：

```text
<組織秘密管理器輸出密碼> | XinSpect --enterprise-baseline-sign policy.json --signing-key baseline-signing-private.pem --password-stdin --out signed-policy.json
```

簽發命令只接受加密 PEM 私鑰與 stdin 密碼，只寫出簽章套件，不把密碼或私鑰印到 stdout。輸出不得覆寫政策或私鑰來源。此命令應由管理者在受控管理端執行；XinSpect 不替組織產生、備份或散佈私鑰。

## 端點評估

```text
XinSpect --enterprise-baseline signed-policy.json --trusted-key <預先部署公鑰.pem> --snapshot <硬體快照.json> [--out report.json]
```

端點先驗證政策簽章，再讀取快照並驗證快照完整性，最後執行規則。信任公鑰不從待驗政策取得；信任金鑰不符、簽章錯誤或套件格式不符時拒絕評估。過期套件會標示 `Expired`，不執行規則。

JSON 報告只輸出 policyId、簽發者公鑰 SHA-256 指紋、政策簽章／期限狀態、事實鍵、判定、可用狀態、來源、信任等級與採集時間，不輸出事實值或快照中的原始錯誤原因。欄位 `policySignatureValid` 只表示政策簽章已由預先信任的公鑰驗證；評估報告本身與快照來源沒有簽章，也沒有遠端證明。快照 SHA-256 只偵測內容變更，不證明快照由 XinSpect 或可信端點產生。退出碼為 0（全部符合）、2（不符合、資料不足或過期）、1（輸入／簽章錯誤）。報告不含整體安全分數；`Satisfied` 只代表符合該版組織政策。

## 信任與營運界線

- 公鑰信任指紋應經組織既有部署或設定管理通道核對；單獨收到的公鑰檔不會自動建立信任。
- 政策需設定到期日；變更規則時更新 policyId 或版本並重新簽署。
- 本版使用 ECDSA P-256／SHA-256 與離線 PEM 金鑰；不連線、不支援 TPM/HSM 簽署，也不提供集中派送、撤銷服務或角色權限系統。
- 端點評估既有快照，不觸發硬體寫入或重新採集。快照中的敏感值遵循快照原有遮蔽政策。
