# XinSpect 功能目錄・工業級規格【第二組】（2026-10-11）

**獨立一組 300 項**，與第一組（`docs/INDUSTRIAL-CATALOG-2026-10-10.md`，300 項）**並列、不混編**：

- 兩組可各自獨立圈選、獨立開工；域碼與編號互不重疊（全域唯一）。
- 規格欄位定義、成本單位（S/M/L/XL）、敏感度分級（L0–L3）、Trace 四級、橫切框架七件——**一律沿用第一組 §一、§二**（單一標準來源；若兩份分歧，以第一組為準並回填此處）。
- 本組取向：兩路擴張——(a) 第一組既有域的**第二層深化**（ACPI/PCIe/USB 表層、GPU/NVML、BitLocker 卷級、Windows 內部資料）；(b) 第一組**完全沒有的新維度**（伺服器角色、檔案鑑識、自基線與預測、自我完整性、EOL 庫、情境包第二波、平台包裝與自動化）。
- 使用規則同第一組：**圈了 ID 才開工，沒圈的只是候選。**

---

## 一、域碼與項數（27 個新域）

| 域碼 | 域 | 項數 | 域碼 | 域 | 項數 |
|---|---|---|---|---|---|
| AC | ACPI 與 AML 深化 | 18 | EL | EOL 與壽命資料庫 | 8 |
| SB | SMBIOS/PCIe 表深化 | 15 | IN | 自我完整性（工業級） | 10 |
| GX | GPU 深化（NVML/ADLX/驅動） | 14 | QS | 查詢與智慧層 | 12 |
| UV | 匯流排深化（TB/USB4/CXL） | 12 | UF | 更新深化 | 10 |
| SV | 伺服器角色（本機 Server 2025） | 14 | DM | 排程與維護 | 8 |
| FS | 檔案鑑識（L2 opt-in） | 16 | PL | 平台與包裝 | 10 |
| DS | 資料安全深化 | 12 | TU | 深測第二波 | 12 |
| PO | 效能觀測（核心/記憶體內部） | 14 | ZR | 零信任/合規 | 8 |
| BL | 基線學習與預測 | 12 | MU | 多媒體編解碼 | 8 |
| SG | 情境包第二波（「為什麼」族） | 14 | DL | 開發者診斷（L2） | 10 |
| RS | 報告輸出第二波 | 12 | HM | 硬體事件史 | 8 |
| SE | 進階安全 | 14 | NG | 診斷導航 | 6 |
| — | — | — | XT | 擴展介面 | 8 |
| — | — | — | VR | 視覺化深化 | 8 |
| — | — | — | AG | 代理與自動化 | 7 |

合計 **300**。全域累計：第一組 300 ＋ 第二組 300 ＝ **600**。

---

## 二、矩陣（300 項）

欄位同第一組：ID｜名稱｜接縫→輸出鍵｜誠實界線｜守門｜成本｜敏感度。「深化」＝與第一組某項互補但內容不同（非重複）。

### AC ACPI 與 AML 深化（18）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| AC-001 | _PSS 頻率表解碼 | ACPI _PSS 唯讀→`ac.pss.{core}.{n}` | 需 AML 評估層；未提供=N/A | 金標 | M | L0 |
| AC-002 | _CPC CPPC 能力 | _CPC 物件→`ac.cpc.*` | 未提供=N/A | 金標 | M | L0 |
| AC-003 | 熱區門檻全解 | _PSV/_CRT/_HOT→`ac.thermal.{zone}` | 門檻事實非設定建議 | 金標 | M | L0 |
| AC-004 | 電源資源 _PRx | PowerResource 狀態→`ac.pwres.*` | 唯讀 | — | S | L0 |
| AC-005 | _DSM GUID 清單 | 裝置功能集→`ac.dsm.{dev}` | 查詢失敗如實 | 清單測試 | M | L0 |
| AC-006 | DSDT/SSDT 字串掃描 | 版本/OEM 字串→`ac.aml.str` | 不解碼 AML 本體 | 掃描測試 | S | L0 |
| AC-007 | AML 反編譯骨架 | 字節碼 offset 統計先行→`ac.aml.stat` | **XL 標長期**；不解碼即不宣稱 | 統計測試 | XL | L0 |
| AC-008 | 第三方 SSDT 注入偵測 | OEM ID/表來源對照→`ac.ssdt.third` | 注入≠惡意（OEM 常態） | 對照測試 | S | L0 |
| AC-009 | NFIT 解碼 | NVDIMM 表→`ac.nfit.*` | 無 NVDIMM=N/A | 金標 | M | L0 |
| AC-010 | MPAM/新表存在性 | 表頭存在性→`ac.mpam` | N/A 展示 | 表頭解析 | S | L0 |
| AC-011 | BGRT 開機 logo 表 | 存在性＋版本→`ac.bgrt` | — | 表頭解析 | S | L0 |
| AC-012 | SPCR 序列主控台 | 配置事實→`ac.spcr` | — | 表頭解析 | S | L0 |
| AC-013 | DBG2 除錯埠 | 埠存在性→`ac.dbg2` | — | 表頭解析 | S | L0 |
| AC-014 | HPET 結構全欄位 | 既有指紋深化→`ac.hpet.full` | 與第一組 WP25 交叉 | 金標 | S | L0 |
| AC-015 | MCFG 全欄位對照 | ECAM 配置→`ac.mcfg.full` | 與既有讀取交叉 | 金標 | S | L0 |
| AC-016 | SLIT 矩陣熱圖 | 已解碼→UI 渲染 | RenderSnapshot 復用 | 算繪基線 | S | L0 |
| AC-017 | PPTT 拓撲表 | ACPI 拓撲↔CPUID 交叉→`ac.pptt.*` | 兩源不一致=矛盾卡 | 交叉測試 | M | L0 |
| AC-018 | ACPI 表匯總頁 | 全表存在性/版本/校驗→`ac.tables.summary` | 與第一組 FW-023 交叉 | 匯總測試 | S | L0 |

### SB SMBIOS/PCIe 表深化（15）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| SB-001 | Type 0–3 全欄位 | BIOS/board/chassis→`sb.type03.*` | 與既有 Type3 交叉 | 解碼測試 | S | L0 |
| SB-002 | Type 9 插槽完整 | 類型/用法/狀態→`sb.slot.{n}` | — | 解碼測試 | S | L0 |
| SB-003 | Type 17 全欄位 | 製造週/部件號（序號 L1）→`sb.dim.{n}.*` | 序號遮蔽 | 解碼測試 | S | L1 |
| SB-004 | Type 42 管理控制器 | →`sb.mctl` | N/A 展示 | — | S | L0 |
| SB-005 | Type 11 OEM strings | →`sb.oem.{n}` | OEM 字串可能含身份=L1 | 枚舉測試 | S | L1 |
| SB-006 | Type 39 電源供應 | PSU 型號若有→`sb.psu.*` | 多半 N/A 如實 | 解碼測試 | S | L0 |
| SB-007 | PCI capability 全解 | MSI/MSI-X/ATS/PASID/PRI→`sb.pci.cap.*` | 逐 cap 解碼 | 金標 | L | L0 |
| SB-008 | SR-IOV VF 列舉 | →`sb.pci.vf.{n}` | 不支援=N/A | 枚舉測試 | M | L0 |
| SB-009 | ACS 能力 | 根埠/交換器→`sb.pci.acs.*` | — | 金標 | M | L0 |
| SB-010 | L1 子狀態深化 | L1.1/L1.2 位→`sb.pci.l1sub.*` | 與 ASPM 交叉 | 位元金標 | S | L0 |
| SB-011 | DPC capability | 下游埠遏制→`sb.pci.dpc.*` | — | 金標 | S | L0 |
| SB-012 | VC/Arbitration 表 | →`sb.pci.vc.*` | — | 金標 | M | L0 |
| SB-013 | AER 完整欄位 | 既有掃描深化→`sb.pcieaer.full.*` | 掃描範圍聲明 | 金標 | M | L0 |
| SB-014 | 裝置 revision 知識表 | rev↔已知修正→`sb.pci.rev.known` | 快照日期必印 | 知識包框架 | M | L0 |
| SB-015 | M.2 槽位對照 | 物理槽↔location→`sb.m2slot.*` | 對照錯誤率聲明 | 對照測試 | M | L0 |

### GX GPU 深化（14）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| GX-001 | NVML 全狀態 | P0–P15/時脈/節流原因→`gx.nv.state` | 無卡 N/A | 枚舉測試 | M | L0 |
| GX-002 | NVIDIA 風扇曲線 | 若可讀→`gx.nv.fan` | 不可讀如實 | — | S | L0 |
| GX-003 | NVIDIA 功率限制 | power limit states→`gx.nv.pl` | — | — | S | L0 |
| GX-004 | NVIDIA ECC 能力 | →`gx.nv.ecc` | N/A 展示 | — | S | L0 |
| GX-005 | ADLX 感測器全表 | →`gx.amd.*` | 無卡 N/A | 枚舉測試 | M | L0 |
| GX-006 | Intel Xe 感測器 | iGPU 若有→`gx.intel.*` | N/A | — | M | L0 |
| GX-007 | VBIOS 版本 | 唯讀字串→`gx.vbios` | 途徑不足如實 | — | M | L0 |
| GX-008 | VRAM 使用歷史 | 入倉→`hist.vram.*` | — | FormatVersion | S | L0 |
| GX-009 | 驅動版本鏈 | 驅動↔WDDM↔DX→`gx.drvchain` | — | 對照測試 | S | L0 |
| GX-010 | 多 GPU 模式 | 主/輔/複製→`gx.multi.*` | — | — | S | L0 |
| GX-011 | 虛擬顯示器偵測 | 假轉接器判定→`gx.vdisp.*` | 本機 3 虛擬轉接器真實場景；判定≠惡意 | 判定測試 | S | L0 |
| GX-012 | 色深事實 | bit depth→`gx.depth` | — | — | S | L0 |
| GX-013 | RT/AI 能力旗標 | →`gx.cap.rt` | — | — | S | L0 |
| GX-014 | GPU 驅動版本史 | 變更時間線→`hist.gpudriver` | — | FormatVersion | S | L0 |

### UV 匯流排深化（12）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| UV-001 | TB 安全等級 | SL0–SL1→`uv.tb.sec` | — | — | S | L0 |
| UV-002 | TB 路由路徑 | drom 拓撲→`uv.tb.route` | 可讀範圍聲明 | — | M | L0 |
| UV-003 | USB4 埠能力 | 速度/隧道→`uv.usb4.*` | 與 TbUsb4Service 交叉 | — | S | L0 |
| UV-004 | CXL DVSEC 解碼 | →`uv.cxl.*` | 無硬體 N/A、代碼先行 | 金標 | M | L0 |
| UV-005 | CDAT 表解碼 | →`uv.cdat.*` | 同上 | 金標 | M | L0 |
| UV-006 | USB descriptors 全解 | device/config/interface/endpoint→`uv.usb.desc.*` | 第一組 NW-009 深化 | 金標 | M | L0 |
| UV-007 | USB 速度歷史 | 插拔速度變更→`hist.usb.speed` | — | FormatVersion | S | L0 |
| UV-008 | USB 電源角色 | Source/Sink/DrP→`uv.usb.pwr` | — | — | S | L0 |
| UV-009 | 隨身碟 TRIM 支援 | →`uv.usb.trim` | — | — | S | L0 |
| UV-010 | Hub 電源管理 | 每 hub 掛裝置→`uv.hub.*` | 與第一組 NW-021 交叉 | 枚舉測試 | M | L0 |
| UV-011 | 斷連/重置計數 | →`uv.usb.err.*` | — | — | S | L0 |
| UV-012 | 外接 GPU/HBA 電源 | →`uv.ext.*` | N/A 展示 | — | S | L0 |

### SV 伺服器角色（14）——本機 Server 2025 真實可測

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| SV-001 | 伺服器角色盤點 | AD/DNS/DHCP/File/IIS/Hyper-V→`sv.role.*` | 裝了≠配置好 | 枚舉測試 | M | L0 |
| SV-002 | IIS 應用池 | 若裝→`sv.iis.*` | — | — | S | L1 |
| SV-003 | AD DS 狀態 | →`sv.adds.*` | 非 DC=N/A | — | S | L2 |
| SV-004 | DNS 區域清單 | →`sv.dns.*` | 若裝 | — | S | L1 |
| SV-005 | DHCP 伺服器 | →`sv.dhcp` | — | — | S | L0 |
| SV-006 | 檔案伺服器角色 | 共用視角→`sv.file.*` | 與第一組 SA-009 交叉 | — | S | L2 |
| SV-007 | 印表伺服器 | →`sv.print` | — | — | S | L0 |
| SV-008 | RDS 角色 | →`sv.rds` | — | — | S | L0 |
| SV-009 | Hyper-V 交換器清單 | 虛擬交換器/埠→`sv.vswitch.*` | 零 VM 如實空清單 | 枚舉測試 | S | L0 |
| SV-010 | Failover Cluster | →`sv.cluster` | N/A | — | S | L0 |
| SV-011 | Server Core 判定 | →`sv.core` | — | — | S | L0 |
| SV-012 | 授權/啟用狀態 | 版本/通道（不含金鑰）→`sv.license` | **金鑰 L3 禁** | L3 禁令 | S | L2 |
| SV-013 | Server 特有服務 | SMSvcHost/W32Time 等→`sv.svc.*` | — | 枚舉測試 | S | L0 |
| SV-014 | 伺服器合規摘要 | 角色紅黃聚合→`sv.summary` | — | 聚合測試 | S | L0 |

### FS 檔案鑑識（16）——L2 鑑識域，預設 opt-in

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| FS-001 | Shimcache 解碼 | AppCompatCache→`fs.shim.*` | 執行痕跡≠執行成功 | 金標＋真 fixture | M | L2 |
| FS-002 | Amcache 解碼 | 已裝/執行痕跡→`fs.amcache.*` | 同上 | 金標 | M | L2 |
| FS-003 | Prefetch 解碼 | 執行歷史→`fs.prefetch.*` | 同上 | 金標 | M | L2 |
| FS-004 | SRUM 讀回 | 資源/網路使用史→`fs.srum.*` | 隱私聚合聲明；opt-in | 解析測試 | L | L2 |
| FS-005 | USN Journal 讀回 | 檔案變更時間線→`fs.usn.*` | 大量資料分頁化 | 解析測試 | M | L2 |
| FS-006 | $MFT 基本統計 | 記錄數/大小→`fs.mft.stat` | 不解全文 | 統計測試 | S | L1 |
| FS-007 | 回收桶審計 | $Recycle.Bin 條目→`fs.bin.*` | — | 枚舉測試 | S | L1 |
| FS-008 | BITS 佇列審計 | 下載工作→`fs.bits.*` | — | — | S | L1 |
| FS-009 | MountedDevices 歷史 | →`fs.mount.*` | — | 解析測試 | S | L0 |
| FS-010 | 任務歷史痕跡 | 排程操作事件→`fs.taskhist.*` | — | — | S | L1 |
| FS-011 | 安裝痕跡時間線 | MSI 事件→`fs.insthist.*` | — | — | S | L1 |
| FS-012 | 鑑識匯總時間線 | FS-001..011 合併時間軸 | 原始資料不入報告（只聚類） | 聚合測試 | M | L2 |
| FS-013 | 隱私禁項機制化 | Jump lists/MRU/縮圖 **明文不讀**→禁令測試 | L3 來源禁令機器釘死 | 禁令掃描守門 | S | L3禁 |
| FS-014 | USN 篩選器狀態 | 是否啟用→`fs.usn.state` | — | — | S | L0 |
| FS-015 | VSS 差異鑑識 | 還原點內 hive 歷史 | opt-in | — | L | L2 |
| FS-016 | 開機扇區對照 | MBR/GPT 開機碼↔乾淨基線 | 對照≠中毒判決 | 基線測試 | M | L0 |

### DS 資料安全深化（12）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| DS-001 | BitLocker 卷級細節 | 演算法/進度/鎖定→`ds.bl.*` | 與第一組 FW-003 交叉（保護器 vs 卷） | — | S | L2 |
| DS-002 | DHA 裝置健康證明 | TPM 2.0→`ds.dha` | 未施測聲明 | — | M | L2 |
| DS-003 | EFS 憑證盤點 | 存在性→`ds.efs.*` | — | — | S | L2 |
| DS-004 | Hello 註冊狀態 | 臉/指紋/PIN 存在性→`ds.hello.*` | **生物特徵永不讀** | 枚舉測試 | S | L2 |
| DS-005 | 憑證店細分時間線 | root/CA/disallowed→`ds.cert.*` | 與第一組 NW-005 交叉 | 時間線測試 | M | L2 |
| DS-006 | 憑證自動更新 | 狀態→`ds.cert.auto` | — | — | S | L2 |
| DS-007 | 自簽憑證清單 | →`ds.selfsigned.*` | — | — | S | L2 |
| DS-008 | DRM/PlayReady | →`ds.drm` | — | — | S | L0 |
| DS-009 | TLS 版本停用 | 系統層事實→`ds.tls.*` | — | — | S | L2 |
| DS-010 | Smart App Control | →`ds.sac` | — | — | S | L0 |
| DS-011 | 裝置安裝限制政策 | →`ds.devpol.*` | — | — | M | L2 |
| DS-012 | 資料安全摘要卡 | 紅黃聚合→`ds.summary` | — | 聚合測試 | S | L0 |

### PO 效能觀測（14）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| PO-001 | 分頁池趨勢 | →`po.pool.*` | 入歷史倉 | FormatVersion | S | L0 |
| PO-002 | 記憶體壓縮比率 | MemCompression→`po.compression` | — | — | S | L0 |
| PO-003 | 頁面錯誤率趨勢 | →`po.pgflt.*` | — | FormatVersion | S | L0 |
| PO-004 | ReadyBoost 狀態 | →`po.rboost` | N/A 展示 | — | S | L0 |
| PO-005 | 服務就緒時間線 | 開機後 ready 時間→`po.svcboot.*` | — | 解析測試 | M | L0 |
| PO-006 | 服務崩潰重啟史 | SCM 7031/7034→`po.svccrash.*` | — | 事件解析 | S | L1 |
| PO-007 | 行程樹快照簽章 | 異常父行程審計→`po.proctree.*` | opt-in L2；瞬時快照聲明 | 簽章復用 | M | L2 |
| PO-008 | 系統 handle 統計 | →`po.handles` | — | — | S | L0 |
| PO-009 | GDI 物件統計 | →`po.gdi` | — | — | S | L0 |
| PO-010 | 工作集排行 | opt-in→`po.ws.*` | L2 | — | S | L2 |
| PO-011 | 頻率波動譜 | 變異係數→`po.freqvar` | — | 統計復用 | S | L0 |
| PO-012 | 狀態遷移頻率 | C/P-state 切換率→`po.trans.*` | 推測級聲明 | — | M | L0 |
| PO-013 | 計時器解析度事實 | timeBeginPeriod 殘留→`po.timerres` | 遊戲/音訊相關 | — | S | L0 |
| PO-014 | 系統呼叫延遲樣本 | NtQSI 微測→`po.syscall.lat` | 量測期間標註 | 方 法 學 | M | L0 |

### BL 基線學習與預測（12）——質變域

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| BL-001 | 自基線引擎 | 本機「正常」分位數模型→`bl.baseline.*` | 樣本量聲明 | 統計復用 | L | L0 |
| BL-002 | 溫度趨勢外推 | 明標估計→`bl.temp.forecast` | 估計非預言 | CI 視覺化復用 | M | L0 |
| BL-003 | 壽命預估整合 | TBW+DWPD→`bl.life` | 估計聲明 | 計算測試 | M | L0 |
| BL-004 | 異常評分卡 | 全域 z-score→`bl.score` | 分數≠診斷 | 統計復用 | M | L0 |
| BL-005 | 季節模式偵測 | →`bl.season.*` | — | 哨兵復用 | M | L0 |
| BL-006 | 相關性探索 UI | 任兩指標散點 | CorrelationShift 教訓復用 | — | M | L0 |
| BL-007 | 事件標註層 | WHEA/TDR 標在溫度線上 | — | — | M | L0 |
| BL-008 | 開機效能基線 | 每次開機 vs 自基線→`bl.boot.*` | — | 統計復用 | S | L0 |
| BL-009 | 負載模式分類 | 統計聚類→`bl.pattern` | 推測級聲明 | — | L | L0 |
| BL-010 | 異常通知 | 本機規則命中通知 | 常駐改變性格（需討論） | — | M | L0 |
| BL-011 | 預測不確定性視覺化 | 信賴區間顯示 | Sen CI 教訓復用 | — | S | L0 |
| BL-012 | 資料品質聲明 | 每預測標樣本量/缺口 | — | 守門 | S | L0 |

### SG 情境包第二波（14）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| SG-001 | 開機為什麼慢 | 服務就緒＋啟動項→`sg.boot.*` | 引導非診斷 | 清單對帳 | M | L0 |
| SG-002 | 磁碟為什麼滿 | 大檔/孤兒/dump→`sg.diskfull.*` | **只讀排行不刪** | 枚舉測試 | M | L1 |
| SG-003 | 誰在佔記憶體 | 聚合頁→`sg.mem.*` | opt-in | — | S | L2 |
| SG-004 | CPU 為什麼熱 | 頻率/電壓/負載/節流→`sg.hot.*` | — | — | M | L0 |
| SG-005 | 網卡為什麼慢 | 速度/雙工/offload/EEE→`sg.nicslow.*` | — | — | M | L0 |
| SG-006 | 藍牙為什麼斷 | →`sg.bt.*` | — | — | S | L0 |
| SG-007 | Wi-Fi 為什麼慢 | →`sg.wifi.*` | — | — | S | L0 |
| SG-008 | USB 為什麼慢 | 速度落差＋協定→`sg.usbslow.*` | — | — | S | L0 |
| SG-009 | 印表機為什麼不印 | →`sg.print.*` | — | — | S | L0 |
| SG-010 | 聲音為什麼斷 | APO/時鐘/緩衝→`sg.audio.*` | — | — | M | L0 |
| SG-011 | 電腦為什麼當 | WHEA/斷電/TDR→`sg.freeze.*` | — | — | M | L0 |
| SG-012 | 更新為什麼卡 | pending＋更新史→`sg.updcap.*` | — | — | S | L0 |
| SG-013 | 游標為什麼卡 | 輸入延遲鏈→`sg.input.*` | — | — | M | L0 |
| SG-014 | 視窗為什麼白屏 | 顯示鏈→`sg.display.*` | — | — | M | L0 |

### RS 報告輸出第二波（12）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| RS-001 | 匿名求助範本 | 論壇貼文格式→`rs.helpfmt` | 匿名層復用 | 遮蔽驗證 | S | L0 |
| RS-002 | SBOM 產生 | 軟體物料清單（CycloneDX）→`rs.sbom` | 工業級必備 | 格式測試 | M | L0 |
| RS-003 | SVG 圖表匯出 | →`rs.svg` | — | — | S | L0 |
| RS-004 | xlsx 匯出 | →`rs.xlsx` | — | — | M | L0 |
| RS-005 | 列印版面 | →`rs.print` | — | — | S | L0 |
| RS-006 | JSONL 串流 | →`rs.jsonl` | — | — | S | L0 |
| RS-007 | 後處理鉤子 | opt-in 腳本 | 執行外部腳本=攻擊面，需簽章 | 簽章守門 | M | L2 |
| RS-008 | 機隊批次 CSV | →`rs.fleet` | — | — | S | L0 |
| RS-009 | 機器可讀 diff | →`rs.diff.json` | — | — | S | L0 |
| RS-010 | 電子簽章報告 | 報告雜湊入審計→`rs.sig` | — | 審計復用 | S | L0 |
| RS-011 | 報告版本比較 | 兩份報告間→`rs.rdiff` | — | — | S | L0 |
| RS-012 | 離線手冊匯出 | Help→PDF/HTML | — | — | M | L0 |

### SE 進階安全（14）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| SE-001 | 攻擊面匯總頁 | SA/RC/FW 紅黃聚合→`se.surface` | — | 聚合測試 | M | L0 |
| SE-002 | OS CVE 離線對照 | 版本↔CVE 表→`se.cve.*` | 快照日期必印；對照≠已中招 | 知識包框架 | L | L0 |
| SE-003 | SMB 共用權限審計 | 共用↔ACL→`se.share.*` | 第一組 SA-009 深化 | — | M | L2 |
| SE-004 | Named pipe 列舉 | 開放 pipe→`se.pipe.*` | — | — | S | L1 |
| SE-005 | 進階稽核政策 | 九類之外細項→`se.audit2.*` | — | — | S | L2 |
| SE-006 | RDP 憑證狀態 | 自簽/CA→`se.rdp.cert` | — | — | S | L2 |
| SE-007 | NTLM 使用狀態 | 停用進度→`se.ntlm` | — | — | S | L2 |
| SE-008 | Kerberos 設定 | →`se.kerb.*` | — | — | S | L2 |
| SE-009 | Credential roaming | →`se.credroam` | — | — | S | L2 |
| SE-010 | WPBT 表存在性 | 平台自訂二進位→`se.wpbt` | 存在性聲明 | 表頭解析 | S | L0 |
| SE-011 | ESP 簽章基線 | ESP 檔案↔基線 diff | 與既有 ESP 掃描交叉 | 基線測試 | M | L0 |
| SE-012 | 安全基線報告 | 對照 MS Baseline 離線表→`se.baseline.*` | 快照日期必印 | 知識包框架 | M | L0 |
| SE-013 | WinRE 環境審計 | 恢復環境狀態/映像→`se.winre.*` | — | 枚舉測試 | S | L0 |
| SE-014 | 安全摘要分級 | 全域紅黃灰→`se.grade` | 分級≠安全背書 | 聚合測試 | S | L0 |

### EL EOL 與壽命資料庫（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| EL-001 | OS EOL 對照 | 離線表→`el.os` | 快照日期必印 | 知識包框架 | S | L0 |
| EL-002 | 生產力套件版本 | Office 等版本→`el.office.*` | — | 枚舉測試 | S | L0 |
| EL-003 | 瀏覽器版本事實 | →`el.browser.*` | — | — | S | L0 |
| EL-004 | 驅動 EOL | 支援狀態離線表→`el.driver.*` | 覆蓋有限聲明 | 知識包框架 | M | L0 |
| EL-005 | BIOS EOL | →`el.bios` | 同上 | 同上 | M | L0 |
| EL-006 | 韌體公告對照 | 廠商公告離線表→`el.advisory.*` | 同上 | 同上 | M | L0 |
| EL-007 | EOL 儀表頁 | →`el.dash` | — | — | S | L0 |
| EL-008 | EOL 包版本化 | F4 框架 | — | 包完整性 | S | L0 |

### IN 自我完整性（10）——工業級必備

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| IN-001 | 自身二進位自檢 | 啟動雜湊對帳＋審計→`in.self` | 雜湊蓋自身，非防篡改保證 | 審計復用 | M | L0 |
| IN-002 | 設定檔反篡改 | 雜湊基線→`in.cfg.integrity` | 損毀→重建提示非靜默 | 基線測試 | S | L0 |
| IN-003 | 執行環境自我聲明 | VM/沙箱偵測→`in.env` | 誠實聲明非反 VM | 判定測試 | M | L0 |
| IN-004 | token 特權報告 | 自身特權列舉→`in.privs` | 誠實權限報告 | 枚舉測試 | S | L0 |
| IN-005 | 提權最小化審計 | 何時要 admin 的記錄→`in.elevation.*` | — | — | M | L0 |
| IN-006 | 自我更新完整性 | 更新包簽章→`in.update.sig` | — | 簽章測試 | M | L0 |
| IN-007 | 依賴完整性 | 載入 DLL 簽章→`in.deps.*` | DLL 側載偵測 | 簽章復用 | M | L1 |
| IN-008 | 設定損毀自我修復 | 偵測與重建（先備份）→`in.cfg.repair` | 重建前備份 | 往返測試 | S | L0 |
| IN-009 | 除錯偵測聲明 | 被除錯時如實記錄→`in.debug` | **非反除錯**（反逆向是敵意行為） | — | S | L0 |
| IN-010 | 自我完整性報告頁 | →`in.report` | — | — | S | L0 |

### QS 查詢與智慧層（12）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| QS-001 | 鍵範圍檢查 | 物理合理域→`qs.range` | 超域標 Unknown 非錯誤 | 範圍測試 | M | L0 |
| QS-002 | 來源信任評分 | 來源信譽聚合→`qs.trust` | 分數≠正確性 | — | M | L0 |
| QS-003 | 進階 DSL | 時間窗/趨勢述詞→查詢語言 v2 | 文法 semver | 解析回歸 | L | L0 |
| QS-004 | 單位轉換層 | 統一單位→`qs.unit` | — | 轉換測試 | M | L0 |
| QS-005 | 懸浮說明 | 鍵→文件自動嵌入 | — | — | S | L0 |
| QS-006 | 查詢效能索引 | →`qs.index` | corpus 效能 | 基準測試 | M | L0 |
| QS-007 | 查詢結果快取 | 失效機制 | 快取殘留=Unknown 聲明 | — | M | L0 |
| QS-008 | 查詢歷史 | →`qs.history` | L1 隱私 | — | S | L1 |
| QS-009 | 鍵集 diff 工具 | 兩快照鍵對照→`qs.keydiff` | — | — | S | L0 |
| QS-010 | 文法版本化 | 查詢語言 semver | — | 測試 | S | L0 |
| QS-011 | 防呆建議 | ParseException 修正建議 | — | — | S | L0 |
| QS-012 | 機器可讀查詢 API | 本地 API 擴充 | 與第一組 IE-001 交叉 | — | S | L0 |

### UF 更新深化（10）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| UF-001 | 安裝時長歷史 | →`uf.duration.*` | — | — | S | L0 |
| UF-002 | 失敗重試鏈 | →`uf.retry.*` | — | — | S | L0 |
| UF-003 | 驅動更新分類 | →`uf.driver.*` | — | — | S | L0 |
| UF-004 | 功能更新就緒度 | 硬體/空間審計→`uf.ready` | 唯讀評估 | — | M | L0 |
| UF-005 | 更新時間軸 UI | 更新歷史時間線→`uf.timeline` | — | — | S | L0 |
| UF-006 | 更新來源事實 | WU/WSUS/商店→`uf.source` | — | — | S | L0 |
| UF-007 | 更新通道事實 | Insider 狀態→`uf.channel` | — | — | S | L0 |
| UF-008 | 累積更新大小史 | →`uf.size.*` | — | FormatVersion | S | L0 |
| UF-009 | 服務堆疊前置條件 | servicing stack 版本→`uf.servicing` | — | 解析測試 | S | L0 |
| UF-010 | 更新健康儀表 | →`uf.dash` | — | — | S | L0 |

### DM 排程與維護（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| DM-001 | 自動維護狀態史 | →`dm.maint.*` | — | — | S | L0 |
| DM-002 | Storage Sense 狀態 | →`dm.sense` | — | — | S | L0 |
| DM-003 | 任務結果碼全列 | 0x41301 等→`dm.taskres.*` | 結果碼≠故障判決 | 對照測試 | M | L0 |
| DM-004 | 維護視窗衝突 | →`dm.conflict` | — | — | S | L0 |
| DM-005 | 磁碟清理狀態 | →`dm.clean` | — | — | S | L0 |
| DM-006 | 任務運行時間線 | →`dm.timeline.*` | — | — | S | L0 |
| DM-007 | 隱藏任務偵測 | →`dm.hidden.*` | 隱藏≠惡意 | — | S | L1 |
| DM-008 | 維護摘要頁 | →`dm.dash` | — | — | S | L0 |

### PL 平台與包裝（10）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| PL-001 | 便攜/安裝雙包裝 | →`pl.package` | 產品方向需討論 | 性格改變聲明 | M | L0 |
| PL-002 | 安裝器升級測試 | 舊→新遷移 | — | 遷移測試 | M | L0 |
| PL-003 | 設定遷移測試 | schema 版本 | — | 往返測試 | S | L0 |
| PL-004 | 多語安裝器 | — | — | — | M | L0 |
| PL-005 | 綠色零寫入模式 | 唯讀保證極致→`pl.portable` | 寫入禁令掃描擴大 | 禁令守門 | M | L0 |
| PL-006 | 閃爍/字型回退測試 | UI 品質守門 | — | 自動化測試 | S | L0 |
| PL-007 | 多語系截圖基線 | 每語言版面基線 | — | 算繪基線 | M | L0 |
| PL-008 | 每頁首載預算 | 效能 | — | 基準測試 | M | L0 |
| PL-009 | CLI 轉發單一實例 | →`pl.forward` | — | — | S | L0 |
| PL-010 | 版本公告自動草稿 | changelog→release body 草稿→`pl.releasenote` | 草稿非發佈 | 產生器測試 | S | L0 |

### TU 深測第二波（12）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| TU-001 | 風扇噪音頻譜 | 音訊輸入需同意→`tu.spectrum.*` | 同意閘門復用 | 閘門測試 | M | L0 |
| TU-002 | 溫度響應矩陣 | 負載×感測器相關→`tu.thermresp.*` | CorrelationShift 教訓 | 統計復用 | M | L0 |
| TU-003 | 功率-頻率擬合 | →`tu.pfreq.*` | 擬合=模型聲明 | 統計復用 | M | L0 |
| TU-004 | Vdroop 觀測 | →`tu.vdroop` | 推測級聲明 | — | S | L0 |
| TU-005 | 記憶體保持測試 | suspend/resume→`tu.memhold` | 同意閘門 | 閘門測試 | M | L0 |
| TU-006 | 電池放電曲線 | →`tu.battery.*` | 無電池 N/A | — | M | L0 |
| TU-007 | UPS 轉換測試 | 需同意→`tu.ups` | 閘門復用 | 閘門測試 | S | L0 |
| TU-008 | 網路抖動量測 | opt-in→`tu.jitter` | 白名單復用 | 白名單守門 | S | L1 |
| TU-009 | 封包損失趨勢 | 介面計數→`hist.pktloss.*` | — | FormatVersion | S | L0 |
| TU-010 | 深測夜間自跑 | →`tu.nightly` | 產品方向需討論 | 常駐聲明 | M | L0 |
| TU-011 | 深測原始資料導出 | 樣本資料包→`tu.rawpack` | 匿名化復用 | 遮蔽驗證 | S | L1 |
| TU-012 | 深測多機對照矩陣 | 結果並排→`tu.matrix` | — | — | M | L0 |

### ZR 零信任/合規（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| ZR-001 | MDM 註冊狀態 | →`zr.mdm` | — | — | S | L2 |
| ZR-002 | Entra 加入狀態 | 工作/個人→`zr.aadj` | — | — | S | L2 |
| ZR-003 | 憑證型登入 | →`zr.cba` | — | — | S | L2 |
| ZR-004 | 裝置合規基線 | 對照清單→`zr.compliance.*` | 快照日期必印 | 知識包框架 | M | L2 |
| ZR-005 | Passkey 支援 | →`zr.passkey` | — | — | S | L0 |
| ZR-006 | 動態鎖定設定 | →`zr.dynlock` | — | — | S | L0 |
| ZR-007 | 遠端抹除能力 | →`zr.wipe` | — | — | S | L2 |
| ZR-008 | 合規報告 | →`zr.report` | — | — | S | L0 |

### MU 多媒體編解碼（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| MU-001 | 編解碼器清單 | →`mu.codec.*` | — | 枚舉測試 | S | L0 |
| MU-002 | HEVC/AV1 硬解 | →`mu.hwdec.*` | — | — | S | L0 |
| MU-003 | DRM 就緒 | →`mu.drm` | — | — | S | L0 |
| MU-004 | 相機能力 | 解析度/幀率→`mu.cam.*` | — | — | S | L0 |
| MU-005 | 麥克風陣列 | →`mu.mic.*` | — | — | S | L0 |
| MU-006 | 空間音訊 | →`mu.spatial` | — | — | S | L0 |
| MU-007 | 媒體基礎診斷 | →`mu.mf.*` | — | — | M | L0 |
| MU-008 | HDR 影片就緒 | →`mu.hdr` | — | — | S | L0 |

### DL 開發者診斷（10）——L2

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| DL-001 | ETW 篩選視圖 | 條件式事件→`dl.etw.*` | 量測期間標註 | — | M | L2 |
| DL-002 | 行程傾摘 | 需同意→`dl.dump` | 同意閘門 | 閘門測試 | S | L2 |
| DL-003 | 符號路徑審計 | →`dl.sympath` | — | — | S | L1 |
| DL-004 | 崩潰行程堆疊快照 | 需同意→`dl.stack` | 同意閘門 | 閘門測試 | M | L2 |
| DL-005 | 行程命令列全列 | L2 opt-in→`dl.cmdline.*` | 隱私聚合聲明 | — | S | L2 |
| DL-006 | 逐行程 DLL 清單 | L2→`dl.dll.*` | 簽章交叉 | 簽章復用 | M | L2 |
| DL-007 | 句柄洩漏偵測 | 長期取樣→`dl.leak.*` | — | 統計復用 | M | L0 |
| DL-008 | 線程池統計 | →`dl.threadpool.*` | — | — | S | L0 |
| DL-009 | GC 模式事實 | .NET 應用→`dl.gc.*` | — | — | S | L0 |
| DL-010 | 模組衝突偵測 | →`dl.modconflict.*` | — | — | M | L1 |

### HM 硬體事件史（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| HM-001 | 未知裝置清單 | 缺驅動/未知裝置→`hm.unknown.*` | 缺驅動≠故障 | 枚舉測試 | S | L0 |
| HM-002 | 裝置錯誤碼史 | CM_PROB 歷史→`hm.prob.*` | — | — | S | L0 |
| HM-003 | 即插即用資源衝突 | →`hm.conflict.*` | — | — | M | L0 |
| HM-004 | 裝置喚醒記錄 | →`hm.wake.*` | 與第一組 PW-001 交叉 | — | S | L0 |
| HM-005 | 感測器校準漂移 | 多溫度感測器一致性→`hm.sensdrift` | 交叉≠校準判決 | 交叉測試 | M | L0 |
| HM-006 | 驅動版本變更史 | →`hm.drivhist.*` | — | — | S | L0 |
| HM-007 | 硬體變更時間軸 | 快照 diff→`hm.hwdiff` | 時間膠囊交叉 | — | S | L0 |
| HM-008 | 裝置樹健康總覽 | →`hm.tree` | — | — | S | L0 |

### NG 診斷導航（6）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| NG-001 | 診斷樹導航 | 症狀→檢查→證據→`ng.tree` | 樹是導航非診斷背書 | 清單對帳 | L | L0 |
| NG-002 | 自訂檢查清單 | YAML 清單→`ng.checklist` | 寫入經 WriteGate | 校驗測試 | M | L0 |
| NG-003 | 檢查進度記憶 | →`ng.progress` | — | — | S | L0 |
| NG-004 | 診斷報告整合 | →`ng.report` | — | — | S | L0 |
| NG-005 | 導覽歷史 | →`ng.history` | L1 | — | S | L1 |
| NG-006 | 檢查覆蓋統計 | 哪些跑過→`ng.coverage` | — | — | S | L0 |

### XT 擴展介面（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| XT-001 | 鍵匯出 C# 常數 | 開發者集成→`xt.csharp` | — | 產生器測試 | S | L0 |
| XT-002 | Python 讀取器範例 | 離線 JSON→`xt.py` | — | — | S | L0 |
| XT-003 | REST 範例集 | →`xt.rest` | — | — | S | L0 |
| XT-004 | 本地檔案觸發 | **webhook 出網明文不做**，改檔案觸發→`xt.trigger` | 零網路不破 | — | S | L0 |
| XT-005 | 快照檔案整合 | 檔案系統級→`xt.snapshot.fs` | — | — | S | L0 |
| XT-006 | schema registry | 事實鍵標準化匯出→`xt.registry` | — | — | M | L0 |
| XT-007 | Sysmon 格式對照 | 事件→`xt.sysmon.*` | — | — | M | L0 |
| XT-008 | 第三方解碼器載入 | 簽章驗證外掛 | 攻擊面大——需討論**或明文不做** | 簽章守門 | L | L2 |

### VR 視覺化深化（8）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| VR-001 | 桑基資料流圖 | →`vr.sankey` | — | 算繪基線 | M | L0 |
| VR-002 | 時間×感測器熱力圖 | →`vr.heatmap2d` | RenderSnapshot 復用 | 算繪基線 | M | L0 |
| VR-003 | 拓撲樹狀圖 UI | （3D **明文不做**）→`vr.tree` | — | — | M | L0 |
| VR-004 | 開機甘特圖 | →`vr.gantt` | — | — | S | L0 |
| VR-005 | 能力雷達圖 | →`vr.radar` | — | — | S | L0 |
| VR-006 | 歷史圖縮放交互 | →`vr.zoom` | — | — | S | L0 |
| VR-007 | 多軸對照圖 | →`vr.multiaxis` | — | — | M | L0 |
| VR-008 | 圖表主題匯出 | →`vr.theme` | — | — | S | L0 |

### AG 代理與自動化（7）

| ID | 名稱 | 接縫→鍵 | 界線 | 守門 | 成本 | 敏感 |
|---|---|---|---|---|---|---|
| AG-001 | headless 強化 | CI 用全 CLI→`ag.headless` | — | CLI 測試 | M | L0 |
| AG-002 | YAML 檢查組合 | →`ag.compose` | 校驗復用 | 校驗測試 | M | L0 |
| AG-003 | 退出碼語意化 | 對照表→`ag.exitcode` | — | 對照測試 | S | L0 |
| AG-004 | stdin 批次模式 | 管線驅動查詢→`ag.stdin` | — | CLI 測試 | S | L0 |
| AG-005 | 服務模式 | 背景常駐→`ag.service` | 產品方向需討論 | — | L | L0 |
| AG-006 | 排程輸出輪轉 | →`ag.rotate` | — | — | S | L0 |
| AG-007 | 遠端查詢 | →`ag.remote` | **明文不做**（零網路） | 禁令展示 | — | — |

---

## 三、第二組建議批次（圈了才算數）

| 批次 | 主題 | 內容 | 理由 |
|---|---|---|---|
| A | 自我完整性（工業級門面） | IN-001、IN-002、IN-004、IN-007、IN-010 | 成本低、故事強——「工具證明自己沒被動過」 |
| B | 檔案鑑識主線 | FS-001、FS-002、FS-003、FS-012、FS-013 | 鑑識經典＋隱私禁項機制化（負責任的鑑識） |
| C | 新維度質變 | BL-001、BL-004、BL-007、QS-001、SE-002 | 自基線／異常評分／CVE 對照——從「讀值」到「判斷」 |
| D | 伺服器與情境 | SV-001、SV-014、SG-002、SG-011、RS-002 | 本機是 Server 2025，現成可測；SG-002/RS-002 大眾價值 |

其餘為 P2/P3 候補。**兩組合計 600 項 —— 這已遠超一年產能，價值在取捨不在數量。**

---

## 尾註

- 本組與第一組**並列不混編**；規格標準以第一組 §一/§二 為唯一來源。
- 規格與實作對帳同第一組 §1.6：規格聲稱的鍵必須在 FactKeyCatalog 有帳。
- 版本：第二組 v1.0（2026-10-11）。全域累計 600 項（第一組 22 域＋第二組 27 域＝49 域）。
