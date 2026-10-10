# XinSpect 功能目錄・工業級規格 v1.0（2026-10-10）

300 項候選功能的工業級規格化目錄。收斂自 2026-10-10 三輪擴張（100→200→300）。

**使用規則**：本文件是規格庫，不是開工指令。**圈了 ID 才開工，沒圈的只是候選。** 每項開工前必須能把「接縫」欄補到 API 級；補不到的（檢索級/推測級）如實標註，不出半成品（PCH 世代裁定前例）。

---

## 一、規格標準 v1.0

### 1.1 域碼與 ID

| 域碼 | 域 | 項數 | 域碼 | 域 | 項數 |
|---|---|---|---|---|---|
| FW | 韌體與開機鏈 | 25 | SA | 安全審計（持久化/劫持面） | 20 |
| CM | CPU 與記憶體 | 20 | RC | 可靠性考古（崩潰/故障史） | 16 |
| ST | 儲存 | 20 | PW | 電源與喚醒 | 8 |
| NW | 網路與周邊 | 22 | GD | 圖形與顯示 | 8 |
| AU | 音訊與周邊電源 | 4 | DV | 開發與虛擬化環境 | 8 |
| WS | Windows 系統 | 13 | OF | 離線鑑識 | 8 |
| MS | 量測與深測 | 18 | IE | 整合與生態 | 8 |
| EV | 證據、報告與工作流 | 20 | DK | 資料與知識工程 | 10 |
| UX | UX 與工程品質 | 18 | SC | 使用者情境包 | 10 |
| EG | 策略與工程 | 30 | TM | 時間與時鐘 | 5 |
| — | — | — | PV | 隱私與遙測 | 5 |

合計 **300**（另有 GP 遊戲呈現 4 項，計入上表前 200 內）。

### 1.2 完整規格九欄（P0/P1 項目必備）

1. **目標**——回答什麼問題（一句話）
2. **接縫**——具體 API／檔案／表；不到 API 級不許開工
3. **方法**——讀什麼、怎麼讀、重試與放棄條件
4. **輸出鍵**——FactKey 命名＋value 形狀（進 FactKeyCatalog 對帳）
5. **三態行為**——六態各自觸發條件
6. **誠實界線**——不做什麼、為什麼（「是事實非判決」類聲明逐條成文）
7. **守門**——測試級防回歸（F6 五模式至少一種）
8. **依賴**——共用骨架、知識表、其他 ID
9. **驗收**——fixture 金標全綠＋實機記錄（或未施測聲明）

### 1.3 成本單位

S ≤0.5 輪｜M＝1 輪｜L＝2–3 輪｜XL ≥4 輪。一輪＝一次發佈迭代（現行 v2.x 節奏）。

### 1.4 敏感度分級（匿名化層的輸入）

- **L0** 硬體／版本類——預設輸出
- **L1** 本機元數據（路徑／行程名／埠）——匯出時遮蔽
- **L2** 安全相關（帳戶／憑證存在性／審計／監聽面）——匯出預設遮蔽、可選解除
- **L3** 禁項（密碼／金鑰／修復金鑰**值**）——**任何功能不得輸出**，測試機器釘死

### 1.5 優先級

P0 下一批｜P1 本季｜P2 本年｜P3 候補。

### 1.6 Trace 模型（航空級四級）

`REQ-{ID}`（本文件條目）→ `DESIGN`（服務/解碼器檔名）→ `TEST`（測試名）→ `VERIFIED`（實機記錄或未施測聲明）。

機器對帳：**規格聲稱的每個輸出鍵必須在 FactKeyCatalog 有帳**（DocumentationIntegrityTests 模式擴大）——規格與程式說謊一樣會紅燈。

---

## 二、橫切框架（七件）

### F1 唯讀掃描骨架 ScanSkeleton

```csharp
interface IReadOnlyScanner {
    string BackendName { get; }
    FactSet Scan();   // 內部固定序：發現→讀取→解碼→三態→鍵
}
```

強制：`finally` 還原任何暫態（SuperIO 進出序列前例）；寫入白名單（僅 MR11 同款）；每步超時上界；中斷點記錄並保留已得事實（ECAM-AER 前例）。新域服務一律實作本介面。**讀取自由、寫入必經 WriteGate**——寫入閘門守門不變。

### F2 三態六態契約

復用 `FactAvailability` 六態＋`FactStateLattice` 格與傳播。新增域規則：
- 取樣瞬間資料（行程/埠/視窗）標 `Unknown`，不冒充 `Present`；
- 外來匯入資料（EV-020）起步一律 `Unknown`；
- 「沒事件≠沒問題」「服務沒跑≠不會跑」類聲明逐條成文進規格第 6 欄。

### F3 SpecRef 出處鏈

每個解碼器方法必須 `[SpecRef]`，PublicSpecTests 對帳。域規：**未對準出處就不出值**——交接文件寫錯過三次（Fintek 12mV、DDR5 位移、SME MSR），這條規則是被證明的。

### F4 知識表框架 knowledge-pack-vN

OUI／PCI／微碼 CVE／dbx 代數／PSU 型號／KB 已知問題／bugcheck 代碼，統一成版本化離線快照包：SHA-256 宣告、表頭印快照日期、**沒有更新通道就明說**（零網路原則不破）、換包動作進審計鏈。收錄下限沿用 OUI 教訓：不收錄就顯示「未收錄」，不猜。

### F5 匿名化層與出網許可

敏感度 L0–L3（§1.4）逐欄標註；`DataSovereigntyTests` 允許清單擴充規則：任何出網／寫入新通路必須（a）opt-in（b）使用者主動觸發（c）清單第 N+1 條成文（d）聲明語意如實。UpdateCheckService（v2.54）是第一個範本。L3 欄位在任何匯出/顯示路徑被機器釘死（掃描守門）。

### F6 守門五模式（新守門必須宣告模式＋防什麼真風險）

| 模式 | 適用 | 前例 |
|---|---|---|
| 金標/往返 | 解碼器 | SPD、TPM2、EfiSigList |
| 掃描守門 | 介面契約 | TimeBomb、EnvironmentGuard、WriteGate、溯源錨點 |
| 基線守門 | 數字/像素/警告 | WarningBaseline、RenderSnapshot |
| 完整性網 | 接線/孤兒/對帳 | ServiceOrphanGate、三方對帳 |
| property 測試 | 不變式 | FsCheck 遮罩差分 |

防守門儀式化：每個新守門在規格第 7 欄寫明「防什麼真風險」，寫不出的不加。

### F7 審計鏈與寫入閘門

一切寫入經 `WriteGate` 記帳（512 筆帳本已有）；狀態變更動作進 AuditLog；證據包 manifest 雜湊與審計鏈互驗。使用者供檔一律「零出網」——BYOVD/dbx 模式推廣。

---

## 三、架構圖

### 3.1 全域資料流

```
┌ 來源層   WMI │ MSR/MMIO(WinRing0) │ SMBus │ ECAM │ 登錄檔 │ 事件日誌 │ ETW(.etl) │ 檔案系統 │ 韌體變數 │ 使用者供檔 ┐
│            ↓  全部走 F1 ScanSkeleton（發現→讀取→解碼→finally 還原）                                          │
├ 解碼層   XinSpect.Decoders（純函式＋[SpecRef]＋金標/往返/fuzz）                                              │
│            ↓  F2 六態契約（Present>Unknown>N-A>N-S>Priv>ReadError）                                          │
├ 事實層   FactKeyCatalog（EG-003 semver）→ 知識表交叉(F4) → 規則引擎 → 能力矩陣 cap.*                          │
│            ↓  F7 審計鏈＋WriteGate＋F5 匿名化 L0–L3                                                          │
├ 呈現層   WPF 頁面 → UX-001 逐值溯源卡 → UX-010 錯誤聚合頁 → SC 情境包精靈                                     │
└ 輸出層   JSON/HTML/PDF │ CLI(--json/--query/--verify-audit) │ 本地 API │ IE-001 OpenMetrics │ EV-018 .xinsig ┘
```

### 3.2 線上／離線雙模式（OF 域的架構定位）

```
              ┌─ 線上模式（現行）：活系統即時收集
ScanSkeleton ─┤
              └─ 離線模式（OF-001..008）：掛載 hive/.evtx/.dmp/裸碟 → 同一解碼層、同一六態、同一守門
                  「唯讀保證」機制化（OF-008）＝離線掃描器的寫入禁令掃描守門
```

### 3.3 批次依賴（四季路線圖的骨架）

```
F1骨架 ──┬─ SA 族(201–216 共用)          F4知識包 ──┬─ FW-007/008、CM-018、ST-014、DV-003
         ├─ RC 族(217–230)                          └─ RC-001 bugcheck 表、PW/WS 對照表
         └─ OF 族(255–262，離線另需 OF-008)
F5匿名化 ── EV-004、SA 匯出、EV-002 證據包
UX-001溯源 ←─ 全域（所有卡片）
SC 情境包 ── 依賴其聚合的各 ID（精靈只是組裝層，先有原料）
```

---

## 四、P0 批次完整規格（12 項，可開工級）

### P0-01｜FW-002 TPM 事件日誌重放比對｜P0｜M｜L0

- **目標**：回答「TPM log 與 PCR 實讀是否互相印證」——把兩個既有來源變成第三者驗證，斷鏈即紅牌。
- **接縫**：`Tbsi_Get_TCG_Log`（既有兩段式讀回，4 MiB 上限）＋ `TpmFactsService` 既有 PCR 三 bank 讀取。
- **方法**：走訪 TCG_PCR_EVENT2 → 取每事件 digest → 對 (pcrIndex, alg) 模擬 extend（SHA-1/256/384）→ 期望值與實讀逐顆比對。
- **輸出鍵**：`tpm.replay.pcr.{alg}.{i}`（一致/不一致）、`tpm.replay.summary`、`tpm.replay.mismatch.{n}`。
- **三態**：log 缺→NotApplicable；讀回截斷→ReadError（帶位移）；alg 不支援→NotSupported。
- **界線**：不解密封印資料；不一致＝事實非「被入侵」判決；不寫 TPM。
- **守門**：假 log 生成器＋重放器往返（F6 金標）＋固定事件序列已知向量。
- **依賴**：F2、TPM2 解碼器（rc 在 offset 6 教訓已釘）。
- **驗收**：本機 SHA-256 bank 逐顆比對報告；fixture 全綠。
- **風險**：廠商 log 異形→重放器寬容解析＋未解析計數如實。
- **Trace**：REQ-FW-002 → TpmReplayService → TpmReplayTests → 實機＋fixture。

### P0-02｜FW-001 FIT 表＋Boot Guard 判讀｜P0｜L｜L0

- **目標**：回答「這塊板子的開機信任根設定是什麼」——FIT 佈局與 Boot Guard profile。
- **接縫**：flash 尾端 0xFFFFFFF0 向下尋 FIT 指標 → FIT entries（type 0 微碼／type 2 ACM／type 7 BIOS startup）→ Key Manifest／Boot Guard Policy（flash descriptor 區內）。**接縫後半為檢索級**：ACM 讀取途徑未定，開工前先補證據。
- **方法**：SpiFlashHashService.ReadRange 唯讀讀回（>64 MiB 拒讀界線沿用）→ FitDecoder 純解碼。
- **輸出鍵**：`fw.fit.entries.{n}`、`fw.fit.acm.{n}`、`fw.bg.profile`（Verified/Measured/Off/Unknown）。
- **三態**：無 FIT→NotApplicable；讀取失敗→InsufficientPrivilege；type 未收錄→如實「未收錄」。
- **界線**：只呈現設定，**不判「是否被繞過」**；不驗 ACM 簽章（無公開驗證金鑰集）。
- **守門**：FitDecoder 金標＋SpecRef 引 Intel Boot Guard spec（F3）。
- **依賴**：SPI 讀取後端（WinRing0 內嵌 v2.43）。
- **驗收**：fixture 金標＋本機實測（RAMPAGE VI EXTREME OMEGA 實例記錄）。
- **風險**：老平台無 Boot Guard→NotApplicable 即正確答案。
- **Trace**：REQ-FW-001 → FitDecoder/BootGuardService → FitDecoderTests → 實機＋fixture。

### P0-03｜SA-008 監聽埠三方對照（SA 骨架代表）｜P0｜M｜L1

- **目標**：回答「這台機器在等誰連入」——socket↔行程↔簽章三方對照。
- **接縫**：`GetExtendedTcpTable`/`GetExtendedUdpTable`（TCP_TABLE_OWNER_MODULE_LISTENER，AF_INET+AF_INET6）→ `GetModuleFileNameEx` 取映像 → WinVerifyTrust 簽章（DRIVER_ACTION_VERIFY 口徑復用）。
- **方法**：單次快照列舉；對每個 LISTEN 條目產鍵；行程已退出記 Unknown（PID 競態）。
- **輸出鍵**：`net.listen.{proto}.{port}`（綁定位址）、`net.listen.{port}.pid/.image/.signer`。
- **三態**：表查詢失敗→InsufficientPrivilege（帶原碼）；映像讀不到→Unknown＋原因；IPv6 停用→NotApplicable。
- **界線**：只列舉不阻擋；**取樣瞬間**必標（F2 域規）；不主動連線。
- **守門**：假表 fixture 注入測試＋「鍵不得出現 Set/Write/Enable」掃描守門＋L1 遮蔽驗證。
- **依賴**：簽章核心（KernelModuleService 模式）、F5 匿名化。
- **驗收**：與 `netstat -ano` 交叉一致記錄；fixture 全綠。
- **風險**：高完整性行程讀不到映像→Unknown；被 hook 的表 API 偵測不做（超界聲明）。
- **Trace**：REQ-SA-008 → NetListenService → ListenPortFactsTests → 實機＋fixture。SA-001..016 其餘項共用此骨架（登錄檔掃描變體）。

### P0-04｜RC-001 Minidump 讀取＋bugcheck 解碼｜P0｜M｜L0

- **目標**：回答「這台機器藍過幾次、為什麼類型」——BSOD 歷史考古。
- **接縫**：`C:\Windows\Minidump\*.dmp`（PAGEDU64 header）＋離線 bugcheck 代碼知識表（F4）。
- **方法**：解析 DMP header（Signature/ValidDump/版號/體系結構）→ BugCheck code＋P1–P4 參數 → 知識表對照出已知代碼名稱；時間線入證據。
- **輸出鍵**：`rc.dump.{file}.bugcheck`、`rc.dump.{file}.params.{1..4}`、`rc.dump.{file}.known`、`rc.dump.summary`。
- **三態**：無 dump→NotApplicable（是好消息，如實列）；檔案損毀→ReadError；代碼未收錄→「未收錄」＋原碼十六進位。
- **界線**：**解碼非診斷**——不宣稱根因；金鑰/隱私欄位不輸出（L3 禁令）。
- **守門**：真 dump fixture（遮蔽後入版控，EG-024）＋解析器金標＋往返。
- **依賴**：F4 知識包框架；OF-003 離線批次共用此解碼器。
- **驗收**：本機若有 dump 逐份列；fixture 金標全綠。
- **風險**：dump 格式跨版本差異→版號分支＋未支援版如實。
- **Trace**：REQ-RC-001 → MinidumpDecoder/MinidumpService → MinidumpTests → fixture。

### P0-05｜MS-001 DPC/ISR 延遲量測｜P0｜M｜L0

- **目標**：回答「系統延遲卡在哪個驅動」——音訊/遊戲痛點的第一現場。
- **接縫**：ETW kernel session（Microsoft-Windows-Kernel-Interrupt/DPC provider）30 秒取樣；**降級路徑＝既有 .etl 讀回**（v2.42 能力）。
- **方法**：session 啟動→取樣→逐裝置聚合 p50/p99/max→排行。與 CM-002 SMI 計數交叉（DPC 高而 SMI 平＝軟體；兩者皆高＝韌體）。
- **輸出鍵**：`ms.dpc.{device}.p99`、`ms.dpc.summary`、`ms.dpc.session.elapsed`。
- **三態**：無權限開 session→InsufficientPrivilege；取樣不足→Unknown（樣本量聲明）。
- **界線**：session 本身有開銷，結果標「量測期間」；只觀測不調整親和性。
- **守門**：方法學測試（假事件流聚合）＋量測期間標註機器釘死。
- **依賴**：ETW 通路（.etl 已有，live session 新增——需評估聲明）。
- **驗收**：本機 30 秒實測排行與 SMI 交叉記錄。
- **風險**：session 權限→自動降級 .etl 路徑並明說。
- **Trace**：REQ-MS-001 → DpcLatencyService → DpcLatencyTests → 實機。

### P0-06｜UX-001 逐值溯源 UI｜P0｜M｜L1

- **目標**：把專案靈魂（六態＋出處）變成使用者摸得到的東西——點任何值展開其來源。
- **接縫**：既有材料已足（來源後端 `BackendName`、取得時間、availability reason、SpecRef）；新增 `FactProvenance` 呈現層。
- **方法**：事實產生時攜帶溯源資料（F1 骨架統一注入）→ UI 右鍵/點擊展開卡片：來源、時間、後端、六態理由、SpecRef 連結。
- **輸出鍵**：UI 層；JSON 輸出同步加 `provenance` 欄。
- **三態**：溯源資料缺席的舊快照→Unknown（「溯源資料不可用」如實顯示）。
- **界線**：溯源是「這個值怎麼來的」，不是「這個值對不對」。
- **守門**：**「每張卡片至少一個溯源錨點」掃描守門**（F6 掃描模式）——新頁面忘了接就紅燈。
- **依賴**：F1（注入點）、HelpCatalog。
- **驗收**：全部既有頁逐頁人工核對＋守門綠。
- **風險**：效能（快照攜帶溯源的記憶體增量）→ 效能預算測試（EG-014）把關。
- **Trace**：REQ-UX-001 → FactProvenance/ProvenanceCard → ProvenanceAnchorGateTests → 逐頁核對。

### P0-07｜UX-010 錯誤聚合頁｜P0｜S｜L0

- **目標**：三態哲學的 UX 收口——所有 ReadError/InsufficientPrivilege 集中一頁，逐項可重試。
- **接縫**：ScanSkeleton 產出的三態事實聚合（F1）。
- **方法**：全域掃一次三態清單→分組（缺權限/讀取失敗/不支援）→逐項「重試」按鈕（唯讀重掃經 F1，不經任何寫入）。
- **輸出鍵**：`ux.errors.summary`、`ux.errors.{key}.state/.reason`。
- **三態**：零錯誤→空清單（如實「全部 Present」，不是隱藏頁面）。
- **界線**：重試只是重讀；不提升權限、不提示提權操作步驟（那是文件的事）。
- **守門**：聚合測試（假三態注入）＋「重試不得呼叫任何寫入」掃描守門。
- **依賴**：F1、F2。
- **驗收**：本機實測（本機天然有 GPU 三態紅——現成測資）。
- **風險**：無。
- **Trace**：REQ-UX-010 → ErrorAggregatorView → ErrorAggregationTests → 實機。

### P0-08｜EG-001 第二平台驗證計畫｜P0｜M｜L0

- **目標**：把「未施測聲明」從美德變成可執行清單——第二台機器來了照單驗收。
- **接縫**：能力矩陣 cap.*（v2.52）＋六態掃描（N-A/N-S/「未施測」文案掃描）。
- **方法**：產生器掃全部事實鍵與聲明→`UNTESTED-CLAIMS.md` 自動產生，每條附最小驗證腳本（CLI `--json evidence` 在目標機跑、兩機 diff 報告）。
- **輸出鍵**：文件產物＋CLI 子命令 `--untested-claims [out]`。
- **三態**：N/A。
- **界線**：產生的是清單不是承諾；「本機未施測」與「平台不支援」分欄。
- **守門**：產生器測試（掃描樣式與 CoverageService 同步——兩套掃描漂移是老坑）。
- **依賴**：F2、EG-027（鍵文件產生，共用掃描）。
- **驗收**：本機產出一次存檔比對。
- **風險**：文案掃描脆→與 F6 掃描守門同一正則源。
- **Trace**：REQ-EG-001 → UntestedClaimsGenerator → UntestedClaimsTests → 產物比對。

### P0-09｜EG-002 知識表離線更新包｜P0｜M｜L0

- **目標**：知識表（OUI/PCI/CVE/dbx/PSU/KB/bugcheck）版本化離線分發，零網路不破。
- **接縫**：現有 OuiKnowledge/PciKnowledge/SpecRefRegistry 收編成 pack 載入層。
- **方法**：`knowledge-pack-vN` 檔（JSON＋SHA-256＋快照日期）→ 載入時驗完整性→表頭印快照日期→換包動作進審計鏈。
- **輸出鍵**：`knowledge.pack.version/.sha256/.snapshot_date`。
- **三態**：pack 缺→用內建表＋「內建快照於 vX.XX」聲明；SHA 不符→拒載＋ReadError。
- **界線**：**沒有更新通道**——使用者手動換包；內建表過期聲明成文。
- **守門**：pack 完整性往返測試＋「載入層不得出網」掃描守門（F5）。
- **依賴**：F4、F7。
- **驗收**：換包前後知識條目對帳記錄。
- **風險**：無。
- **Trace**：REQ-EG-002 → KnowledgePackService → KnowledgePackTests → 換包演練。

### P0-10｜EG-018 第三方授權清單＋授權頁｜P0｜S｜L0

- **目標**：法務必需——WinRing0/LHM 內嵌（v2.43）與其他元件的授權義務成文。
- **接縫**：csproj PackageReference/內嵌資源清單＋人工授權檔。
- **方法**：元件清單↔授權類型↔義務（署名/來源）對照表→About 頁授權卡→發佈一致性測試掛鉤。
- **輸出鍵**：UI＋`THIRD-PARTY-NOTICES.md`（入 Release 資產）。
- **三態**：元件授權未確認→**不得發佈**（紅燈，不是三態——法務界線硬性）。
- **界線**：本工具授權聲明不構成法律意見（頁面明示）。
- **守門**：清單對帳測試（csproj↔NOTICES 逐項）。
- **依賴**：發佈一致性（v2.46）。
- **驗收**：NOTICES 與 csproj 對帳綠。
- **風險**：無。
- **Trace**：REQ-EG-018 → ThirdPartyNoticesTests → 發佈物檢查。

### P0-11｜SC-001 二手電腦驗機精靈｜P0｜L｜L1

- **目標**：最大眾化入口——把 300 項中既有驗機能力聚成一鍵流程。
- **接縫**：純聚合層——FW-010 GPT、RC-005 pending、ST-002 SMART、CM-007 混插、WS-001 供電史、RC-003 WER、FW-012 ELAM、SA-013 帳戶等既有/同期 ID。
- **方法**：固定檢查清單逐項跑→結算頁（綠/黃/紅＋每項溯源）→ 匯出驗機報告（EV-002 證據包格式）。
- **輸出鍵**：`sc.verifycheck.{item}.verdict`。
- **三態**：清單項來源缺席→該項灰「未收集」，**不冒充通過**。
- **界線**：精靈不修復、不清除、不「優化」；結論是事實摘要非買賣建議。
- **守門**：清單↔實作對帳測試（清單引用的鍵必須存在——DK-010 精神）。
- **依賴**：被聚合項至少 60% 已落地才開工（進入準則）。
- **驗收**：本機完整跑一次產報告。
- **風險**：範圍膨脹→清單凍結機制（改清單必過測試）。
- **Trace**：REQ-SC-001 → VerifyWizard → VerifyWizardTests → 本機全跑。

### P0-12｜EV-020 統一匯入框架｜P0｜M｜L0

- **目標**：交叉驗證哲學的極致——HWiNFO/AIDA64/OpenHardwareMonitor 輸出匯入成事實鍵對照。
- **接縫**：各工具 CSV/報告格式解析器（每工具一檔，金標 fixture）。
- **方法**：使用者供檔→解析→映射到對應事實鍵→**外來值一律 Unknown 起步**（F2 域規）→與本機值並排差異卡。
- **輸出鍵**：`import.{tool}.{key}＝Unknown`＋`import.{tool}.diff.{key}`。
- **三態**：格式不認→ReadError（「不是這個格式」如實）；欄位映射缺→不導入該欄。
- **界線**：匯入值不進規則引擎當 Present 輸入（污染決策）；只並排對照。
- **守門**：每工具真實樣本金標（EG-024）＋「匯入值不得覆蓋本機值」掃描守門。
- **依賴**：F2、F5（檔案內可能含主機名——L1 遮蔽）。
- **驗收**：三工具各一份真實樣本對照記錄。
- **風險**：格式版本漂移→寬容解析＋未解析欄位計數如實。
- **Trace**：REQ-EV-020 → ImportFramework → ImportFormatTests → 樣本。

---

## 五、全目錄矩陣（300 項）

欄位：ID｜名稱｜接縫→輸出鍵｜誠實界線｜守門｜成本｜敏感度。「—」＝無需守門或 N/A。

### FW 韌體與開機鏈（25）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| FW-001 | FIT 表＋Boot Guard 判讀 | flash 尾端 FIT 指標→entries(0/2/7)＋KeyManifest→`fw.fit.*`/`fw.bg.profile` | 只呈現設定不判繞過 | FitDecoder 金標 | L | L0 |
| FW-002 | TPM 事件日誌重放比對 | TCG log 逐事件 extend↔PCR 實讀→`tpm.replay.*` | 不解密封印資料 | 假 log 往返 | M | L0 |
| FW-003 | BitLocker 狀態與保護器 | Win32_EncryptableVolume→`fw.bitlocker.*` | 金鑰值永不輸出 | 鍵禁寫掃描 | S | L2 |
| FW-004 | UEFI Capsule/FMP 解析 | Capsule header＋FMP 簽章鏈（使用者供檔）→`fw.capsule.*` | 不上傳；壞包如實 | 金標＋拒收測 | M | L0 |
| FW-005 | SPI Descriptor 權限矩陣 | descriptor master 讀寫權＋region 鎖→`fw.spiperm.*` | 唯讀 | 逐 bit 金標 | M | L0 |
| FW-006 | SMM 防護深化 | SMM_FEATURE_CONTROL＋TSEG＋SMRAMC→`fw.smm.*` | 列事實不判等級 | 既有測試擴充 | S | L0 |
| FW-007 | dbx 落後代數＋SBAT | dbx 版本↔離線釋出表＋SBAT 變數→`fw.dbx.generation` | 表過期明說 | 知識包框架 | S | L0 |
| FW-008 | 微碼 CVE 對照表 | revision↔CVE 離線表→`fw.ucode.cve.{n}` | 快照日期必印 | 知識包框架 | S | L0 |
| FW-009 | CSME/PSP 韌體足跡 | flash 版本字串掃描＋PSP 目錄→`fw.me.*`/`fw.psp.*` | 未施測聲明 | 掃描 fixture | M | L0 |
| FW-010 | GPT/分割區審計 | 重疊/對齊/備份頭/隱藏分區→`fw.gpt.*` | 只審計不修復 | GPT 金標 | S | L0 |
| FW-011 | BCD 全量審計 | nx/testsigning/debug/hypervisor 組合→`fw.bcd.*` | 與 DebugConfig 交叉 | 組合判定表 | S | L0 |
| FW-012 | ELAM 驅動清單 | ELAM 註冊＋wintrust→`fw.elam.{n}` | 裝了≠在跑 | 簽章復用 | S | L1 |
| FW-013 | WDAC/AppLocker 來源層級 | 韌體鎖/企業/本機何者生效→`fw.wdac.source` | 判讀不執行 | 層級判定表 | M | L2 |
| FW-014 | 開機鏈證據聚合卡 | 韌體→bootmgr→winload→核心逐環→`fw.bootchain.*` | 缺口列「未收集」 | 聚合鍵守門 | M | L0 |
| FW-015 | CSME FTPR manifest | manifest 結構＋SVN→`fw.me.manifest` | 未施測聲明 | 金標 | M | L0 |
| FW-016 | AMD PSP Directory | entry 類型/位址/大小→`fw.psp.dir.{n}` | 標未施測 | 金標 | M | L0 |
| FW-017 | UEFI 變數空間健全性 | varstore 完整性＋認證/一般分類→`fw.varstore.*` | 不刪不改 | 解析往返 | M | L0 |
| FW-018 | Runtime Services 表列舉 | 指標落點/大小→`fw.rt.*` | N/A 如實 | — | S | L0 |
| FW-019 | TCG log 分階段視圖 | firmware/post/OS 邊界標記→`fw.tcgl.phase.{n}` | 邊界誤判率聲明 | 分類測試 | S | L0 |
| FW-020 | FV 空間統計 | free/填充率/多 FV→`fw.fv.{n}.usage` | 統計非安全值 | property 測試 | S | L0 |
| FW-021 | 微碼載入鏈對照 | BIOS 版↔registry patch↔生效版→`fw.ucode.winner` | 缺一源即明說 | 判定表 | S | L0 |
| FW-022 | Boot Guard ACM 版本 | 可讀範圍內→`fw.bg.acm.{n}` | 不可讀=檢索級聲明 | — | M | L0 |
| FW-023 | ACPI 表存在性對照 | 現有集合↔平台應有表→`fw.acpi.missing` | 「應有」需 SpecRef 級源 | 知識包框架 | S | L0 |
| FW-024 | FPDT 解碼 | 韌體 POST 時間→`fw.fpdt.post` | 表缺席=N/A | 金標 | S | L0 |
| FW-025 | UEFI 變數屬性審計 | NV+BS+RT 組合分類→`fw.var.attr.*` | 與 FW-017 互補不重複 | 組合測試 | S | L0 |

### CM CPU 與記憶體（20）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| CM-001 | Turbo 比率/AVX offset | MSR 0x1AD→`cpu.turbo.ratio.*` | 讀不到=Priv | 金標 | S | L0 |
| CM-002 | SMI 計數觀測 | MSR SMI_COUNT 取樣→`cpu.smi.count` | 取樣有開銷標註 | 取樣器單測 | S | L0 |
| CM-003 | 溫度節流事件史 | IA32_THERM_STATUS＋熱事件→`cpu.thermal.events` | log 缺=時間窗聲明 | 事件解析 | S | L0 |
| CM-004 | 能效曲線測繪 | freq vs package power 散點→`cpu.eff.curve` | 樣本量聲明 | 統計測試 | M | L0 |
| CM-005 | 快取共享拓撲圖 | CPUID 0x04 共享遮罩→`cpu.cache.share.{n}` | 格數由拓撲決定（熱區教訓） | 遮罩金標 | S | L0 |
| CM-006 | C-state 駐留趨勢 | 駐留讀數入歷史倉→`hist.cstate.*` | 僅接線 | FormatVersion 測 | S | L0 |
| CM-007 | 混插一致性審計 | SPD 週次/批次/原廠差分→`mem.mixmatch.{n}` | 不一致≠故障 | 假件差分 | S | L0 |
| CM-008 | MRC 訓練快照盤點 | 快取檔存在＋雜湊→`mem.mrc.{n}` | 只盤點不解碼（繞開裁決） | 檔案枚舉 | S | L1 |
| CM-009 | SMBIOS RAS 全欄位 | Type16/17 patrol scrub 等→`mem.ras.*` | 兩欄不一致並列 | 解碼測試 | S | L0 |
| CM-010 | WHEA 錯誤時間線 | WHEA-Logger 聚合→`cm.whea.timeline` | 未施測≠沒問題 | 聚合測試 | S | L1 |
| CM-011 | Uncore 頻率歷史 | UncorePmu 讀數入倉→`hist.uncore.*` | 白名單外=N/A | FormatVersion 測 | S | L0 |
| CM-012 | 記憶體延遲階梯 | L1/L2/L3/RAM 指標游標→`ms.memlat.*` | 量測期間標註 | 方 法 學 測試 | M | L0 |
| CM-013 | TLB 階層探測 | page size 斷點→`ms.tlb.*` | 量測≠事實分開陳述 | 斷點測試 | M | L0 |
| CM-014 | 逐核對稱性檢查 | 同 workload 逐核分布→`ms.coresym.*` | 溫度干擾聲明 | 哨兵復用 | M | L0 |
| CM-015 | 功率牆四件套 | MSR PKG_POWER_LIMIT→`cpu.pl1/pl2/window` | 位元布局金標 | 金標 | S | L0 |
| CM-016 | 電壓事實 | VCCSA/VCCIO/VDDQ→`cpu.volt.*` | 途徑不足不猜 | — | S | L0 |
| CM-017 | 訓練時長趨勢 | boot timing train 階段→`hist.memtrain.*` | Event 家族擴充 | 時間線測試 | S | L0 |
| CM-018 | 步進缺陷集知識表 | stepping↔已知問題→`cpu.stepping.known` | 快照日期必印 | 知識包框架 | S | L0 |
| CM-019 | 親和性亂象審計 | 綁死單核的行程→`cm.affinity.*` | 取樣瞬間 | 枚舉測試 | S | L1 |
| CM-020 | 行程 NUMA 分布 | node 歸屬→`cm.numa.proc.*` | 單插槽 N/A | N/A 測試 | S | L0 |

### ST 儲存（20）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| ST-001 | NVMe 特性頁全解 | Identify：PLP/原子寫/LBA/sanitize→`st.nvme.*` | 絕不發 sanitize 命令 | Identify 金標 | M | L0 |
| ST-002 | SMART 屬性歷史 | FormatVersion 3 逐日入倉＋哨兵→`hist.smart.*` | 錯位檔硬性拒收（v2 教訓） | 遷移＋拒收測 | M | L0 |
| ST-003 | TBW/磨損觀測 | host vs NAND writes→`st.wear.*` | 明標估計 | 計算測試 | S | L0 |
| ST-004 | 掉電事件計數 | unexpected power loss→`st.powerloss` | 廠商語意差異聲明 | 解碼金標 | S | L0 |
| ST-005 | Sanitize/加密能力 | capability＋status log→`st.sanitize.cap` | 唯讀命令白名單守門 | 白名單掃描 | S | L0 |
| ST-006 | 檔案系統/VSS 盤點 | ReFS integrity/VSS 空間→`st.fs.*` | 系統差異聲明 | 枚舉測試 | S | L1 |
| ST-007 | 控制器拓撲樹 | VMD/RAID 模式/直通→`st.ctrl.tree` | 模式誤判聲明 | 拓撲判定 | M | L0 |
| ST-008 | 表面掃描熱圖 | 既有結果熱圖渲染 | RenderSnapshot 復用 | 算繪基線 | S | L0 |
| ST-009 | 外接盒橋接晶片 | UASP/SCSI＋知識表→`st.bridge.*` | 未收錄如實 | 知識包框架 | S | L0 |
| ST-010 | metadata 微延遲 | create/stat/delete p99→`ms.fslat.*` | 寫入需同意閘門 | 方 法 學 測試 | M | L0 |
| ST-011 | NVMe 錯誤日誌頁 | Error Log entries→`st.nvme.err.{n}` | 條目語意差異聲明 | 金標 | S | L0 |
| ST-012 | NVMe 0x02 差集 | 與既有 19 欄對照補缺→`st.nvme.extra.*` | 重複欄位不重列 | 差集測試 | S | L0 |
| ST-013 | Storage Spaces 拓撲 | 實體↔虛擬盤→`st.ss.*` | 無 SS=N/A | WMI 假件 | S | L0 |
| ST-014 | SSD 韌體已知問題表 | 韌體↔bug 離線表→`st.fw.known` | 快照日期必印 | 知識包框架 | S | L0 |
| ST-015 | 磁碟電源轉換計數 | idle↔active transitions→`st.pstrans.*` | 語意聲明 | 解碼測試 | S | L0 |
| ST-016 | Get-PhysicalDisk 交叉 | WMI↔本工具 diff→`st.xcheck.*` | 不一致=矛盾卡非錯誤 | 交叉測試 | S | L0 |
| ST-017 | 對齊 vs erase block | 分區 4K 對齊→`st.align.*` | 與 FW-010 交叉不重複 | 計算測試 | S | L0 |
| ST-018 | DWPD 計算 | 寫入量歷史→`st.dwpd` | 明標估計 | 計算測試 | S | L0 |
| ST-019 | 長尾儲存盤點 | 光碟機/讀卡機→`st.legacy.*` | N/A 展示 | 枚舉測試 | S | L0 |
| ST-020 | iSCSI session 深化 | session/multipath 狀態→`st.iscsi.*` | 未連線如實 | 假件測試 | S | L1 |

### NW 網路與周邊（22）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| NW-001 | NIC 錯誤計數趨勢 | 健康讀數入倉＋哨兵→`hist.nicerr.*` | 僅接線 | FormatVersion 測 | S | L0 |
| NW-002 | DNS 設定＋hosts 異常 | 解析器/DoH/hosts→`nw.dns.*` | 不查 DNS 伺服器（不出網） | 解析測試 | S | L1 |
| NW-003 | SMB/NetBIOS 硬化 | 簽名/SMB1/匿名列舉→`nw.smb.*` | 設定關≠攻擊面關 | 組合判定 | S | L2 |
| NW-004 | 防火牆概覽 | 狀態/政策/最近規則→`nw.fw.*` | 只讀不建議 | 枚舉測試 | S | L2 |
| NW-005 | 憑證信任店時間線 | 非 MS 根＋到期/重複→`nw.cert.*` | 存在性非信任判決 | 時間線測試 | S | L2 |
| NW-006 | 虛擬網卡盤點 | TAP/WireGuard/vSwitch→`nw.vnic.*` | 分類錯誤聲明 | 假件測試 | S | L0 |
| NW-007 | Wi-Fi 設定檔清單 | profiles＋認證類型→`nw.wifi.*` | 不含密碼、不掃周圍（紅線） | 掃描禁令守門 | S | L2 |
| NW-008 | 藍牙配對歷史 | 快取裝置＋服務→`nw.bt.*` | 快取殘留=Unknown | 枚舉測試 | S | L1 |
| NW-009 | USB 描述符深解 | BOS/MS OS desc＋速度落差→`nw.usb.desc.*` | 協商速度未施測聲明 | 描述符金標 | M | L0 |
| NW-010 | EDID 全解 | checksum/CTA/HDR/DTD/週次→`nw.edid.*` | ushort[] 坑復用 | 金標＋真 fixture | M | L0 |
| NW-011 | ICC 設定檔綁定 | per-display profile→`nw.icc.*` | 綁定缺失如實 | 枚舉測試 | S | L0 |
| NW-012 | 列印佇列健康 | 驅動隔離/失敗數→`nw.print.*` | 池機制聲明 | 枚舉測試 | S | L1 |
| NW-013 | 卸載全表 | RSC/USO/QoS→`nw.offload2.*` | 未提供≠停用（舊教訓） | 假件測試 | S | L0 |
| NW-014 | WOL/EEE 事實化 | EEE 能力/WOL 三層/模式位元→`nw.wol.*`/`nw.eee.*` | 設定關≠硬體不支援 | 位元金標 | S | L0 |
| NW-015 | 魔術封包計數 | 接收計數＋模式位元→`nw.wol.packets` | 計數器存在性三態 | 金標 | S | L0 |
| NW-016 | 路由歸屬呈現 | metric/預設路由→`nw.route.*` | 先紅線討論（路由掃描曾裁定不做） | — | S | L1 |
| NW-017 | 藍牙韌體版本 | radio firmware→`nw.bt.fw` | 查無=未收錄 | 枚舉測試 | S | L0 |
| NW-018 | 感應器清單 | 光線/加速度→`nw.sensor.*` | 桌上型 N/A | 枚舉測試 | S | L0 |
| NW-019 | 無線周邊電量 | HID battery→`nw.hidbat.*` | 查不到=N/A | 枚舉測試 | S | L0 |
| NW-020 | 印表機韌體聚合 | 版本聚合→`nw.printer.*` | 缺源列缺源 | 聚合測試 | S | L0 |
| NW-021 | USB 埠映射持久化 | location paths＋使用者標籤→`nw.usb.port.*` | 寫入經 WriteGate | 寫入測試 | M | L1 |
| NW-022 | LPT/COM 盤點 | 殘留埠→`nw.legacy.*` | N/A 展示 | 枚舉測試 | S | L0 |

### AU 音訊與周邊電源（4）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| AU-001 | APO 處理鏈列舉 | 增強 DLL 清單→`au.apo.*` | 載入≠啟用聲明 | 枚舉測試 | S | L1 |
| AU-002 | 隱私開關事實 | 麥克風/攝影機權限→`au.privacy.*` | 軟體開關≠硬體開關 | 枚舉測試 | S | L2 |
| AU-003 | 時鐘漂移量測 | 錄放同時取樣→`au.drift` | 量測期間標註 | 方 法 學 測試 | M | L0 |
| AU-004 | 電池健康 | 設計 vs 滿充/循環→`pw.battery.*` | 無電池 N/A | 假件測試 | S | L0 |

### WS Windows 系統（13）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| WS-001 | 供電事件史 | KP 41/1074/6008 聚合→`ws.power.events` | 沒事件≠沒問題 | 事件解析 | S | L1 |
| WS-002 | 服務依賴圖 | 依賴鏈/失敗動作→`ws.svc.graph` | 圖建構測試 | 圖測試 | M | L1 |
| WS-003 | 設備驅動清單 | 年代/簽章/問題特徵→`ws.driver.*` | 簽章核心復用 | 簽章測試 | S | L1 |
| WS-004 | 自啟動聚合 | Run/資料夾/任務＋簽章→`ws.autostart.*` | 聚合視圖非新掃描 | 聚合測試 | M | L2 |
| WS-005 | 事件通道健全性 | 停用/清空 vs 最後事件→`ws.channel.*` | 1102 擴充全通道 | 矛盾判定 | S | L2 |
| WS-006 | LSA/CredGuard 卡 | 保護狀態聚合→`ws.lsa.*` | 缺源列缺源 | 聚合測試 | S | L2 |
| WS-007 | 傾印設定審計 | dump 類型/pagefile→`ws.dumpcfg.*` | 只審計不調整 | 解析測試 | S | L1 |
| WS-008 | AppX 簽章一致性 | publisher↔簽章→`ws.appx.*` | 誤報率聲明 | 假件測試 | M | L1 |
| WS-009 | 虛擬化矛盾卡 | vmms vs hypervisorlaunchtype→`ws.virt.contra` | 本機真實實例 | 判定表 | S | L0 |
| WS-010 | KB 已知問題表 | WU 歷史↔離線表→`ws.kb.known` | 快照日期必印 | 知識包框架 | S | L0 |
| WS-011 | 排程任務全解 | 觸發器/動作/帳號/孤兒二進位→`ws.task.*` | 孤兒=風險面非判決 | 解析測試 | M | L2 |
| WS-012 | Minifilter 拓撲 | FltMgr 附加高度全列→`ws.minifilter.*` | 「誰疊在檔案系統上」 | 枚舉測試 | M | L1 |
| WS-013 | 開機驅動載入順序 | group order＋失敗時間線→`ws.bootdrv.*` | 順序事實非因果 | 解析測試 | S | L1 |

### MS 量測與深測（18）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| MS-001 | DPC/ISR 延遲 | ETW 30s 逐裝置 p99→`ms.dpc.*` | session 開銷標註；降級 .etl | 方 法 學 測試 | M | L0 |
| MS-002 | 跨 NUMA 頻寬 | 三角量測→`ms.xnuma.*` | 單插槽 N/A | N/A 測試 | M | L0 |
| MS-003 | 開機全階段計時 | Diag-Perf 家族→`ms.boot.*` | Event 100 之外 | 解析測試 | S | L0 |
| MS-004 | 熱時間常數 | 階躍衰減擬合→`ms.tau.*` | 擬合=模型非事實 | 擬合測試 | M | L0 |
| MS-005 | 風扇曲線擬合 | RPM/溫度散點＋滯後→`ms.fancurve.*` | 閾值聲明 | 哨兵復用 | S | L0 |
| MS-006 | PSU 效率估計 | 型號表＋package power→`ms.psu.eff` | 明標估計 | 知識包框架 | S | L0 |
| MS-007 | 網路目標延遲 UI | opt-in 白名單模式→`ms.ping.*` | 使用者指定才出網 | 白名單守門 | S | L1 |
| MS-008 | Benchmark 漂移偵測 | 哨兵接 DeepBench→`ms.benchdrift.*` | 漂移=環境問題 | 統計復用 | S | L0 |
| MS-009 | 核心間延遲矩陣 | →`ms.corelat.*` | 同 socket N/A | N/A 測試 | M | L0 |
| MS-010 | 內容切換基準 | →`ms.ctxsw.*` | 量測期間標註 | 方 法 學 測試 | M | L0 |
| MS-011 | 中斷親和性歸屬 | 裝置↔核心 ETW→`ms.irqaff.*` | 取樣瞬間 | 解析測試 | M | L0 |
| MS-012 | NTFS 特性審計 | journal/$MFT/8.3→`st.ntfs.*` | 特性非效能判決 | 解析測試 | S | L0 |
| MS-013 | GPU FP16/int8 冒煙 | D3D 擴充→`ms.gpu.fp16` | 無卡雙軌契約 | GpuUnsupported 契約 | M | L0 |
| MS-014 | 多風扇分區階躍 | 同步記錄→`ms.fanzones.*` | 分區對應未知聲明 | 記錄測試 | S | L0 |
| MS-015 | 睡眠進出計時 | S0ix/S3 實測→`ms.sleep.*` | Modern Standby 未施測 | 計時測試 | M | L0 |
| MS-016 | USB 吞吐量測 | 複製測→`ms.usbps.*` | 同意閘門（FakeCapacity 前例） | 閘門測試 | S | L0 |
| MS-017 | localhost 環回吞吐 | →`ms.lo.throughput` | 不出網原則內 | 方 法 學 測試 | S | L0 |
| MS-018 | 結果哈希鏈 | DeepBench 結果入審計→`ms.bench.chain` | 哈希蓋結果檔 | 審計復用 | S | L0 |

### EV 證據、報告與工作流（20）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| EV-001 | PDF 報告 | 原生 PDF→`ev.pdf` | 中文字型嵌入工程量 | 版面快照 | L | L0 |
| EV-002 | 證據包匯出 | zip：JSON+HTML+審計+manifest→`ev.pack` | manifest 可獨立驗證 | 往返測試 | M | L1 |
| EV-003 | 報告差分頁 | 兩份快照並排→`ev.diff` | 時間膠囊之上的呈現層 | 差分測試 | M | L0 |
| EV-004 | 匿名化選擇器 | 逐類遮蔽開關→`ev.anon` | L2/L3 不可解除（機器釘死） | 分級守門 | M | L2 |
| EV-005 | 查詢聚合擴充 | count/avg/sort/top | 不破壞既有解析 | 解析回歸 | M | L0 |
| EV-006 | CLI watch | 週期收集→`cli watch` | 資源上限聲明 | CLI 測試 | S | L0 |
| EV-007 | 快照排程器 | 工作排程整合 | 排程建立經 WriteGate | 寫入測試 | M | L1 |
| EV-008 | 機隊並排 UI | RunOnSnapshots 視覺化 | 後端已有 | UI 測試 | M | L0 |
| EV-009 | 規則包版本對帳 | builtin.json 版本＋變更 | 變更守門 | 版本測試 | S | L0 |
| EV-010 | CSV/MD 匯出 | 任意表格右鍵 | 格式往返 | 往返測試 | S | L0 |
| EV-011 | 自訂儀表板 | 挑鍵組頁 | 鍵引用有效性 | 配置校驗 | M | L0 |
| EV-012 | 匯出驗證 CLI | schema 檢查子命令 | — | — | S | L0 |
| EV-013 | 審計鏈時間軸 UI | AuditLog 時間線 | 與 EvidenceTimeline 並存聲明 | UI 測試 | S | L1 |
| EV-014 | schema 遷移器 | v1→v2 升級工具 | 升級不可逆聲明 | 遷移往返 | M | L0 |
| EV-015 | 命名查詢視圖 | query profile 持久化 | 寫入經 WriteGate | 存取測試 | S | L0 |
| EV-016 | 報告浮水印 | 頁首頁尾/浮水印 | 送審正式化 | 渲染測試 | S | L0 |
| EV-017 | 多語系報告 | 報告語言選擇 | 翻譯覆蓋率守門 | — | M | L0 |
| EV-018 | .xinsig 密封格式 | zip＋簽章＋內建驗證 | 格式 spec 公開 | 驗證往返 | M | L0 |
| EV-019 | 規則命中歷史 | 何時紅過入倉→`hist.rules.*` | 規則結果≠事實 | FormatVersion 測 | S | L0 |
| EV-020 | 統一匯入框架 | HWiNFO/AIDA64 CSV→事實鍵 | 匯入值 Unknown 起步；不覆蓋本機值 | 樣本金標＋掃描守門 | M | L0 |

### UX UX 與工程品質（18）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| UX-001 | 逐值溯源 UI | FactProvenance 卡 | 溯源≠正確性背書 | 溯源錨點掃描守門 | M | L1 |
| UX-002 | 資料年齡徽章 | 每頁取得時間 | — | 守門 | S | L0 |
| UX-003 | 命令面板 | Ctrl+K 跳轉 | — | — | M | L0 |
| UX-004 | Help 檢索 | 全文離線搜尋 | — | — | S | L0 |
| UX-005 | UIA 守門 | 樹名稱完整性（朗讀者） | — | 自動化測試 | M | L0 |
| UX-006 | 雙解析度截圖守門 | DPI 基線擴充 | — | 基線擴充 | S | L0 |
| UX-007 | 高對比/自動主題 | 跟隨系統 | — | — | S | L0 |
| UX-008 | 節能取樣 | 背景降頻率 | 效能預算復用 | 預算測試 | S | L0 |
| UX-009 | 崩潰報告頁 | 下次啟動呈現異常 | 如實不粉飾 | — | S | L1 |
| UX-010 | 錯誤聚合頁 | ReadError 集中＋重試 | 重試=唯讀重掃 | 聚合＋禁寫掃描 | S | L0 |
| UX-011 | 事實鍵瀏覽器 | 鍵樹＋搜尋 | 鍵 semver 顯示 | — | M | L0 |
| UX-012 | 鍵盤導航守門 | 焦點視覺審計 | — | 自動化測試 | M | L0 |
| UX-013 | 全域右鍵複製 | 含鍵名＋時間戳 | — | — | S | L0 |
| UX-014 | 頁級截圖匯出 | 分享場景 | — | — | S | L0 |
| UX-015 | 設定搜尋 | — | — | — | S | L0 |
| UX-016 | 深測歷史並排 | 執行對比視圖 | — | — | S | L0 |
| UX-017 | 首次執行導覽 | 五頁「不會做什麼」 | 信任建設 | — | S | L0 |
| UX-018 | 語言資源外部化 | 自訂語言 JSON | 外部檔案=攻擊面必校驗 | 校驗測試 | M | L0 |

### EG 策略與工程（30）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| EG-001 | 第二平台驗證計畫 | UNTESTED-CLAIMS 產生器＋CLI diff | 清單非承諾 | 產生器測試 | M | L0 |
| EG-002 | 知識表離線包 | knowledge-pack-vN＋SHA | 沒有更新通道就明說 | 完整性往返 | M | L0 |
| EG-003 | 鍵 semver | 鍵命名穩定性＋棄用流程 | — | 棄用測試 | M | L0 |
| EG-004 | corpus 貢獻包 v2 | 匿名化抽樣器工具化 | — | 遮蔽驗證 | M | L2 |
| EG-005 | 效能 p95 儀表 | SelfTelemetry 報表化 | — | — | S | L0 |
| EG-006 | 版號語意凍結 | major/minor 政策 | — | 政策測試 | S | L0 |
| EG-007 | 倉庫衛生守門 | 未追蹤清單腳本 | — | — | S | L0 |
| EG-008 | 文件單一來源 | HANDOFF 併 docs＋過期偵測 | — | 偵測測試 | S | L0 |
| EG-009 | 已知不可為頁 | 刻意不做入產品頁 | — | — | S | L0 |
| EG-010 | 混合架構路徑 | P/E-core 拓撲判讀 | 全標未施測 | — | M | L0 |
| EG-011 | GitHub Actions CI | 建置＋單元子集 | 硬體測試除外；runner 差異聲明 | CI 配置測試 | M | L0 |
| EG-012 | PR/issue 模板 | 貢獻指南 | — | — | S | L0 |
| EG-013 | minidump 符號化腳本 | 本地崩潰分析 | — | — | S | L1 |
| EG-014 | 效能回歸守門 | 啟動/記憶體基線 | 環境抖動容差聲明 | 基線測試 | M | L0 |
| EG-015 | JSON schema 快照 | 輸出漂移守門 | 下游契約 | 快照測試 | S | L0 |
| EG-016 | release.ps1 守門化 | 管線測試覆蓋 | — | — | S | L0 |
| EG-017 | 全 docs 連結驗證 | 擴全目錄 | — | — | S | L0 |
| EG-018 | 第三方授權清單 | 元件↔授權↔NOTICES | 授權未確認不得發佈（硬性） | 對帳測試 | S | L0 |
| EG-019 | SECURITY.md | 披露流程頁 | — | — | S | L0 |
| EG-020 | 逐頁導覽文件 | 截圖文件擴充 | — | — | S | L0 |
| EG-021 | ARM64 路徑 | 能力矩陣 N/A 平台 | 未施測聲明 | — | L | L0 |
| EG-022 | Win10/11 分支守門 | 版本 API 降級測試 | — | — | M | L0 |
| EG-023 | 結構化 fuzz | 檔案格式 harness | — | — | M | L0 |
| EG-024 | 真實樣本語料 | 每解碼器 fixture | 敏感資料遮蔽 | — | M | L0 |
| EG-025 | 覆蓋率自動化 | coverlet＋基線 | — | — | M | L0 |
| EG-026 | 分析器基線 | 警告清零擴靜態分析 | — | — | S | L0 |
| EG-027 | 鍵 API 文件產生 | 170+ 鍵→文件頁 | — | 產生器測試 | S | L0 |
| EG-028 | 新舊快照互讀矩陣 | 相容性矩陣 | — | — | S | L0 |
| EG-029 | 發佈三方對帳收口 | byte↔README↔Release | — | — | S | L0 |
| EG-030 | SelfTelemetry 可視化 | 自己看自己 | — | — | S | L0 |

### TM 時間與時鐘（5）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| TM-001 | 時鐘源事實 | source/TSC invariant→`tm.source` | 時間互校深化 | — | S | L0 |
| TM-002 | RTC 關機漂移 | →`tm.rtc.drift` | 需重啟配合標限制 | — | S | L0 |
| TM-003 | NTP 同步狀態 | w32time 層級/偏移→`tm.ntp.*` | 只讀狀態不出網查詢 | — | S | L0 |
| TM-004 | 時區/DST 版本 | tzdb 版本→`tm.tzdb` | — | — | S | L0 |
| TM-005 | QPC 成本基準 | →`tm.qpc.cost` | — | — | S | L0 |

### PV 隱私與遙測（5）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| PV-001 | 遙測層級事實 | AllowTelemetry→`pv.telemetry` | — | — | S | L2 |
| PV-002 | 廣告 ID/活動歷史設定 | →`pv.adid.*` | — | — | S | L2 |
| PV-003 | 診斷檢視器狀態 | →`pv.ddv` | — | — | S | L0 |
| PV-004 | UWP 權限概覽 | broker 權限→`pv.uwp.*` | — | — | M | L2 |
| PV-005 | 語音/輸入遙測設定 | →`pv.speech.*` | — | — | S | L2 |

### GP 遊戲與呈現（4）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| GP-001 | HAGS/VRR/遊戲模式事實 | GPU 排程狀態→`gp.hags` | — | — | S | L0 |
| GP-002 | Game DVR 清單 | 全域優化狀態→`gp.dvr.*` | 只讀 | — | S | L0 |
| GP-003 | 幀呈現觀測 | PresentMon 式 ETW→`gp.present.*` | live session 需評估；.etl 後備 | — | M | L0 |
| GP-004 | 滑鼠輪詢率實測 | HID 速率→`gp.pollrate` | — | — | S | L0 |

### SA 安全審計（20）——共用 SA 登錄檔掃描骨架（P0-03 為代表）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| SA-001 | IFEO 劫持審計 | Debugger/GfiSrcDll→`sa.ifeo.{img}` | 命中=風險面非判決 | 掃描 fixture | S | L2 |
| SA-002 | Winlogon 審計 | Shell/Userinit/Notify→`sa.winlogon.*` | 同上 | 同上 | S | L2 |
| SA-003 | AppInit_DLLs | 值＋載入開關→`sa.appinit.*` | — | 同上 | S | L2 |
| SA-004 | COM 劫持偵測 | HKCU 覆蓋 HKLM→`sa.comhij.*` | 覆蓋≠惡意（合法軟體常用） | 比對測試 | M | L2 |
| SA-005 | 輔助功能劫持 | sethc/utilman Debugger→`sa.a11y.*` | 同 IFEO | 同上 | S | L2 |
| SA-006 | Winsock LSP 審計 | 分層提供者→`sa.lsp.{n}` | 合法 LSP 存在聲明 | 枚舉測試 | S | L1 |
| SA-007 | 代理設定審計 | WinINET/WinHTTP→`sa.proxy.*` | 代理≠惡意 | 解析測試 | S | L2 |
| SA-008 | 監聽埠三方對照 | socket↔行程↔簽章→`net.listen.*` | 取樣瞬間；不阻擋 | fixture 注入 | M | L1 |
| SA-009 | 暴露面總覽 | RDP/WinRM/遠端登錄/管理共用→`sa.exposure.*` | 狀態≠可達 | 聚合測試 | S | L2 |
| SA-010 | 檔案關聯劫持 | exe/lnk 異動→`sa.assoc.*` | 無基線聲明 | 比對測試 | M | L2 |
| SA-011 | ADS 掃描 | 系統目錄可執行檔流→`sa.ads.{n}` | 掃描範圍聲明 | 掃描測試 | M | L1 |
| SA-012 | 已安裝程式簽章審計 | Uninstall 鍵孤兒/未簽章→`sa.uninst.*` | — | — | M | L1 |
| SA-013 | 帳戶安全審計 | admin 群組/RID 500/孤兒 SID→`sa.acct.*` | 存在性非判定 | — | S | L2 |
| SA-014 | 自動登入風險 | DefaultUserName/Password **存在性**→`sa.autologon` | **值永不輸出**（L3 禁令） | L3 禁令守門 | S | L3禁 |
| SA-015 | 登入歷史聚合 | 4624/4625 尖峰→`sa.logon.*` | 聚合層級聲明 | — | S | L2 |
| SA-016 | BitLocker 修復金鑰輪廓 | 存在性/備份狀態→`sa.bl.recovery` | 金鑰值 L3 禁 | L3 禁令 | S | L2 |
| SA-017 | 服務二進位 ACL 審計 | image path 可寫偵測→`sa.svcbin.*` | 可寫=劫持面非已劫持 | ACL 檢查測試 | M | L2 |
| SA-018 | PATH 可寫性偵測 | PATH 目錄 ACL→`sa.path.*` | 防禦視角唯讀 | ACL 檢查測試 | S | L2 |
| SA-019 | WMI 永久訂閱審計 | __EventFilter/__EventConsumer→`sa.wmisub.*` | 存在≠惡意 | 枚舉 fixture | M | L2 |
| SA-020 | 服務 SDDL 權限審計 | 非 admin 可重配置服務→`sa.sddl.*` | 權限事實非漏洞判決 | 解析測試 | S | L2 |

### RC 可靠性考古（16）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| RC-001 | Minidump bugcheck 解碼 | PAGEDU64＋code/參數＋知識表→`rc.dump.*` | 解碼非診斷；L3 禁 | 金標＋真 fixture | M | L0 |
| RC-002 | LiveKernelReports | 目錄＋分類→`rc.lkr.*` | — | — | S | L0 |
| RC-003 | WER 崩潰史聚合 | 哪程式崩幾次→`rc.wer.*` | — | 聚合測試 | S | L1 |
| RC-004 | 懸掛歷史 | Event 1002→`rc.hang.*` | — | — | S | L1 |
| RC-005 | PendingFileRename 審計 | →`rc.pending` | 「卡在重啟」現場 | 解析測試 | S | L1 |
| RC-006 | WinSxS 健康 | CBS pending→`rc.winsxs.*` | — | — | M | L1 |
| RC-007 | 還原點清單 | →`rc.rp.*` | — | 枚舉測試 | S | L0 |
| RC-008 | 備份狀態事實 | File History 開關→`rc.backup.*` | — | — | S | L0 |
| RC-009 | 資源耗盡事件 | Resource-Exhaustion→`rc.exhaust.*` | — | — | S | L0 |
| RC-010 | 逐行程 I/O 排行 | ETW 取樣→`rc.io.*` | 量測中標註 | 方 法 學 | M | L1 |
| RC-011 | 連結審計 | junction/symlink 孤兒→`rc.link.*` | — | — | S | L0 |
| RC-012 | 系統目錄 ACL 抽查 | Program Files 弱權限→`rc.acl.*` | — | — | M | L2 |
| RC-013 | 休眠檔案審計 | hiberfil 大小/類型→`rc.hiber.*` | — | — | S | L0 |
| RC-014 | 快速啟動影響 | hiberboot 事件→`rc.fastboot.*` | — | — | S | L0 |
| RC-015 | VSS 供應商審計 | 供應商＋影子副本→`rc.vss.*` | 與 ST-006 交叉不重複 | 枚舉測試 | S | L0 |
| RC-016 | Installer 快取孤兒 | 孤兒 MSI/MSP→`rc.msi.orphan.*` | 刪除判斷留給人 | 枚舉測試 | S | L1 |

### PW 電源與喚醒（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| PW-001 | 喚醒來源審計 | last wake＋計時器→`pw.wake.*` | WOL 者剛需 | — | S | L1 |
| PW-002 | USB 選擇性暫停 | 設定事實→`pw.usbsel` | — | — | S | L0 |
| PW-003 | 睡眠能力矩陣 | FADT S1/S3/S4→`pw.sleep.cap` | — | 金標 | S | L0 |
| PW-004 | 睡眠研究解碼 | SleepStudy 報告→`pw.sleepstudy.*` | Modern Standby 未施測 | — | M | L0 |
| PW-005 | 電源計畫漂移 | active vs 覆寫→`pw.plan.drift` | — | — | S | L0 |
| PW-006 | 現代待機相容性 | 檢查清單→`pw.ms.compat` | 未施測聲明 | — | M | L0 |
| PW-007 | 關機時長史 | 事件側測量→`pw.shutdown.*` | — | — | S | L0 |
| PW-008 | 排程喚醒對照 | 勾喚醒的任務→`pw.waketask.*` | — | — | S | L1 |

### GD 圖形與顯示（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| GD-001 | TDR 事件史 | GPU 重置時間線→`gd.tdr.*` | — | — | S | L0 |
| GD-002 | DirectX 矩陣 | feature level 逐卡→`gd.dx.*` | — | — | S | L0 |
| GD-003 | 熱插拔事件史 | 顯示器事件→`gd.plug.*` | — | — | S | L0 |
| GD-004 | 多螢幕拓撲快照 | 排列/DPI 入倉→`hist.mon.*` | — | FormatVersion | S | L0 |
| GD-005 | 著色器快取 | 狀態/大小→`gd.shcache.*` | — | — | S | L0 |
| GD-006 | HDR 能力鏈 | EDID→OS→驅動→`gd.hdr.*` | — | — | S | L0 |
| GD-007 | VRR 交叉 | 能力 vs 啟用→`gd.vrr.*` | — | — | S | L0 |
| GD-008 | 繪圖板盤點 | →`gd.tablet.*` | N/A 展示 | — | S | L0 |

### DV 開發與虛擬化（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| DV-001 | WSL 盤點 | 發行版＋核心→`dv.wsl.*` | — | — | S | L0 |
| DV-002 | 巢狀虛擬化能力 | →`dv.nested` | — | — | S | L0 |
| DV-003 | .NET/VC++ 執行階段 | 版本＋EOL 離線表→`dv.runtime.*` | 快照日期必印 | 知識包框架 | S | L0 |
| DV-004 | 環境變數審計 | PATH 重複/衝突→`dv.env.*` | — | — | S | L1 |
| DV-005 | SDK 盤點 | JDK/Python/Node→`dv.sdk.*` | — | — | M | L0 |
| DV-006 | Hyper-V VM 配置 | 唯讀盤點→`dv.vm.*` | 零 VM=誠實空清單 | — | S | L0 |
| DV-007 | 容器 runtime 狀態 | →`dv.container.*` | — | — | S | L0 |
| DV-008 | 工具鏈版本 | git/建置→`dv.toolchain.*` | — | — | S | L0 |

### OF 離線鑑識（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| OF-001 | 離線 hive 讀取 | SOFTWARE/SYSTEM 掛載審計→`of.hive.*` | 掛載=讀取不寫入；離線明示 | — | L | L2 |
| OF-002 | .evtx 讀回 | 事件姊妹件→`of.evtx.*` | — | — | M | L1 |
| OF-003 | 離線 minidump 批次 | →`of.dump.*` | — | — | S | L0 |
| OF-004 | 離線 BCD 審計 | →`of.bcd.*` | — | — | S | L0 |
| OF-005 | 離線裸碟讀取 | 分割區結構→`of.raw.*` | — | — | M | L0 |
| OF-006 | WinPE 模式 | 無 OS 收集 | 重大工程標長期 | — | XL | L0 |
| OF-007 | 離線快照差異 | 同碟兩次掛載 diff→`of.diff.*` | 取證核心 | — | M | L0 |
| OF-008 | 寫入封鎖聲明 | 離線唯讀保證 | 機制化非口號 | 寫入禁令掃描 | S | L0 |

### IE 整合與生態（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| IE-001 | OpenMetrics 端點 | 127.0.0.1＋opt-in→`/metrics` | 不出本機；預設關 | 白名單守門 | M | L1 |
| IE-002 | Grafana 匯出 | 儀表板 JSON | — | — | S | L0 |
| IE-003 | 統一匯入框架 | **併入 EV-020**（路線圖計一次） | — | — | — | — |
| IE-004 | .etl 關聯包 | ETW 分析者配套 | — | — | S | L0 |
| IE-005 | PowerShell 範本庫 | 查詢範例集 | — | — | S | L0 |
| IE-006 | deep-link scheme | xinspect://頁/鍵 | 報告連回工具 | — | S | L0 |
| IE-007 | 參數自動完成 | arg completer | — | — | S | L0 |
| IE-008 | 桌面小工具 | 常駐指標 | 常駐改變產品性格（需討論） | — | M | L0 |

### DK 資料與知識工程（10）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| DK-001 | 知識表編輯器 | GUI 編輯＋校驗 | 寫入經 WriteGate | 校驗測試 | M | L0 |
| DK-002 | 規則候選輔助 | 矛盾記錄→候選 | 人核可才入；不自動 | — | M | L0 |
| DK-003 | 鍵別名字典 | 俗名→正式鍵 | — | — | S | L0 |
| DK-004 | 報告範本引擎 | 章節組合 | — | — | M | L0 |
| DK-005 | 匿名 ID 穩定性 | 跨快照不漂移審計 | — | — | S | L0 |
| DK-006 | 查詢索引 | corpus 效能 | — | — | M | L0 |
| DK-007 | 資料保留政策 | 歷史自動裁剪 | 裁剪不可逆聲明；審計鏈例外 | — | S | L0 |
| DK-008 | 過期提示 | 快照生命週期標籤 | — | — | S | L0 |
| DK-009 | 匯出匯入往返 | 設定/歷史備份 | — | 往返測試 | S | L0 |
| DK-010 | 文件-程式對帳 | docs 功能 vs 實作 | 防文件說謊 | 對帳測試 | M | L0 |

### SC 使用者情境包（10）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| SC-001 | 二手電腦驗機精靈 | 聚合既有檢查一鍵跑→`sc.verifycheck.*` | 不修復不「優化」；缺源列灰 | 清單↔實作對帳 | L | L1 |
| SC-002 | 該不該修摘要 | Warning/Critical 聚合→`sc.verdict.*` | 唯讀建議不執行 | — | M | L0 |
| SC-003 | 效能為什麼慢 | 頻率→DPC→磁碟→熱證據鏈→`sc.slow.*` | — | — | M | L0 |
| SC-004 | 誰在動我的硬碟 | I/O 來源聚合→`sc.io.*` | — | — | S | L1 |
| SC-005 | 風扇軸承哨兵 | RPM-溫度關係漂移→`sc.fanbearing` | 退化非判決 | 哨兵復用 | S | L0 |
| SC-006 | 換零件對照精靈 | 時間膠囊產品化→`sc.swap.*` | — | — | S | L0 |
| SC-007 | 多久沒重啟摘要 | uptime＋pending＋更新→`sc.uptime.*` | — | — | S | L0 |
| SC-008 | 溫度分位數基線 | 季節基線 vs 現值→`sc.tbaseline.*` | 基線樣本量聲明 | — | M | L0 |
| SC-009 | 網路為什麼斷 | up/down＋錯誤聚合→`sc.netdrop.*` | — | — | S | L0 |
| SC-010 | 供電穩嗎 | 電壓/功率長期曲線→`sc.pcurve.*` | — | — | M | L0 |

---

## 六、四季路線圖（建議，圈了才算數）

| 批次 | 時窗 | 主題 | 內容（ID） | 進入準則 | 退出準則 |
|---|---|---|---|---|---|
| 一 | 2026 Q4 | 本地安全與可靠性考古 | SA-008 骨架＋SA-001..020、RC-001..005、UX-001、UX-010 | 本文件圈選確認 | 守門全綠＋測數基線更新＋發佈一致性綠 |
| 二 | 2027 Q1 | 韌體主線補全 | FW-002、FW-001、FW-005、FW-004、FW-007、FW-008、EG-002、EG-018 | FW-001 接縫補到 API 級 | 同上＋知識包框架上線 |
| 三 | 2027 Q2 | 量測與電源 | MS-001、MS-008、CM-002、CM-012、CM-015、PW-001、NW-014、NW-015、SC-005 | 批次二退出 | 同上 |
| 四 | 2027 Q3 | 產品化與鑑識 | SC-001、SC-003、EV-020、EV-002、OF-001..003、OF-008 | 被聚合項 ≥60% 已落地 | 同上＋OF-008 唯讀保證測試綠 |

其餘為 P2/P3 候補；每季結束用 EG-001 產生器重產 UNTESTED-CLAIMS，讓「未施測」清單成為路線圖的輸入。

---

## 尾註

- 本文件規格與實作的對帳走 §1.6：規格聲稱的鍵必須在 FactKeyCatalog 有帳。
- 300 項的價值在**取捨**：建議任何時窗在工的項目 ≤8，其餘明確停在本表。
- 版本：v1.0（2026-10-10）。圈選後由開工輪回填 DESIGN/TEST/VERIFIED 三級 Trace。
