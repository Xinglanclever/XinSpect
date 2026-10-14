## 第三十八章　安全政策與端點

### 38.1 事實範圍

本領域涵蓋裝置層級的安全狀態與組織層級的政策事實。其與第十四章的差異在於：第十四章說明的是信任鏈的觀測機制（TPM、Secure Boot、加密），本章說明的是**政策與端點管理**層面的事實，其來源多為登錄檔與管理系統的註冊資訊。

| 子領域 | 模組 | 內容 |
|---|---|---|
| 本機安全政策 | `LocalSecurityAuditService`、`AuditPolicyService` | 本機的安全性設定、稽核政策 |
| 安全稽核 | `SecurityAuditFactsService`、`SecurityAuditDeepFactsService`、`SecurityAuditSupplementFactsService` | 安全設定的分層稽核 |
| 端點政策 | `EndpointPolicyFactsService`、`DeviceComplianceFactsService` | 端點政策的符合性、裝置合規基線 |
| 零信任 | `ZeroTrustFactsService`、`TrustOpsFactsService` | 註冊狀態、加入狀態、信任操作 |
| 資料保護 | `MediaProtectionFactsService`、`VolumeEncryptionFactsService`、`BitLockerRecoveryFactsService` | 內容保護、卷宗加密、復原資訊 |
| 憑證 | 憑證存放區相關模組 | 憑證的列舉與屬性 |
| 執行痕跡 | `ShimcacheFactsService`、`PrefetchFactsService`、`RecycleBinFactsService` | 執行痕跡與檔案系統痕跡 |
| 網路安全盤點 | `NetworkSecurityInventoryFactsService`、`LocalSecurityAuditService` | 網路層的安全相關設定 |

### 38.2 政策事實與其判讀

**稽核政策。** `AuditPolicyService` 提供稽核政策的解碼。其判讀要點是：政策的存在不代表事件被記錄——政策需與稽核子系統的實際運作狀態並列。此為本領域反覆出現的模式：**設定與實際行為是兩件事**。

**本機安全設定的分層稽核。** `SecurityAuditFactsService`、`SecurityAuditDeepFactsService` 與 `SecurityAuditSupplementFactsService` 構成三層的稽核。分層的理由是事實的取得成本與深度不同：基礎層涵蓋常見設定，深入層涵蓋需要更多權限或更複雜解析的項目，補充層涵蓋其餘。

`LocalSecurityAuditService` 的豁免理由為：「稽核政策事實來自 LSA 與事件記錄，單一來源。」此條說明本領域的政策事實多為單一來源，因此其對帳條件有限。

**端點政策與合規基線。** `EndpointPolicyFactsService` 與 `DeviceComplianceFactsService` 處理端點政策的符合性。裝置合規基線（規格編號 ZR-004）的設計約束已在 14.5 說明：使用者提供合規清單，逐項比對，且**缺資料不能偽裝成通過**，快照日期必印，不列條目文字。

**零信任相關的註冊狀態。** `ZeroTrustFactsService` 與 `TrustOpsFactsService` 提供裝置的管理註冊狀態（規格編號 ZR-001 的 MDM 註冊、ZR-002 的 Entra 加入、ZR-005 的 Passkey 支援）。其共同的設計約束是「只報狀態，不讀識別資料」（見 24.2）。

### 38.3 資料保護

**卷宗加密。** `VolumeEncryptionFactsService` 提供卷宗加密的狀態，`BitLockerRecoveryFactsService` 提供復原資訊的狀態。

後者的判讀要點具有實務重要性：**具備復原資訊與具備可用的復原資訊是兩件事**。復原金鑰的託管狀態（是否已備份至目錄服務、是否已列印、是否僅存於本機）決定在硬體故障或設定變更時資料是否可復原。工具陳述託管狀態，不陳述金鑰本身。

**內容保護。** `MediaProtectionFactsService` 提供媒體內容保護的狀態（見 34.3）。

### 38.4 執行痕跡

`ShimcacheFactsService`、`PrefetchFactsService` 與 `RecycleBinFactsService` 處理執行痕跡與檔案系統痕跡的事實。這些痕跡的來源是作業系統的內部記錄，其用途是重建「這台機器曾經執行過什麼」。

此類事實的判讀要點是**痕跡的保存期限與覆寫**。痕跡記錄的容量有限，舊項會被新項覆寫，因此痕跡清單反映的是**近期**的活動，而非完整歷史。工具陳述痕跡的內容與其時間範圍，不推定未出現於痕跡中即代表未曾發生。

### 38.5 憑證

憑證存放區的列舉提供憑證的存在與屬性。其豁免理由為：「憑證存放區列舉，單一來源（CertOpenStore）。」

憑證事實的診斷價值在於判斷信任設定的狀態：異常的根憑證、過期的憑證、以及與憑證相關的服務設定。此類事實與 20.4 的審計日誌共同構成「這台機器的信任狀態」的證據。

### 38.6 已知限制

**單一來源。** 本領域的政策與設定事實多為單一來源（登錄檔或系統 API），因此不具備跨來源對帳條件。

**設定的可改寫性。** 政策事實位於作業系統層，其可改寫性最高（見 7.3）。

**痕跡的不完整性。** 見 38.4。

**政策與行為的分離。** 見 38.2。

---
