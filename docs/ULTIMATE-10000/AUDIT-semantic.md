# AUDIT-semantic — U10 萃取列語意正確性抽審（30 列）

- 稽核對象：`ULTIMATE-10000-EXPANSION-2026-10-24.md`（主目錄，唯讀；共 10,022 列 `U10-*` 資料列）
- 稽核日期：2026-10-25
- 稽核代理：曦覽 XinSpect 語意稽核（抽審版，30-agent 傳統）

## 1. 方法

- **抽樣**：`sample_check.py`（本目錄），`random.Random(20261025)`、每域 `rng.sample(pop, 6)`，域序固定 SB→PM→PH→GD→AC；重跑兩次抽得列 ID 序列完全一致（md5 `4e0eadc3…`），可重現。
- **母體界定**（帶「具體值宣稱」的資料列；純功能描述列與「(枚舉)」佔位列不列入，因其無名稱＝值宣稱可驗）：
  - U10-SB：923 列（母群 1108；排除功能描述與枚舉佔位列）
  - U10-PM：665 列（母群 798；排除 CPUID→事件表 對應列，任務指明 PM 驗 EventName＋Code/UMask）
  - U10-PH：554 列（全部）
  - U10-GD：541 列（母群 542；僅 `U10-GD-0175` 因 payload 含大寫 `0XAF` 未入母體——該寫法係忠實照抄 MdePkg.dec:821 原文，附帶人工驗證亦為「對」）
  - U10-AC：718 列（母群 798；排除功能描述／NotApplicable 列）
- **來源**（Git Bash /tmp，即 `C:\Users\ADMINI~1\AppData\Local\Temp\1\`）：
  - SB→`edk2_SmBios.h`（enum 成員與 #define，數值逐一比對）
  - PM→`pmu_<model>_<category>.json`（EventName 存在＋EventCode/UMask 數值相等；本機有 alderlake/amdzen4/haswell 三模型，本次抽中列均屬之）
  - PH→`cb/*.h` 全目錄搜尋（#define 名稱＋值）
  - GD→`MdePkg.dec`（GUID 名稱＋4+8 段十六進位逐段比對）
  - AC→Linux `actbl*.h` 優先，次及本機 EDK2 ACPI 標頭（`edk2_Acpi62.h`/`Acpi64.h`/`AcpiAml.h`/`WatchdogActionTable.h` 等）；少數 CPPC 常數（如 `CMD_READ`）出自 Linux `include/acpi/cppc_acpi.h`，經上游 master 核對（WebFetch/curl，GitHub raw 當時限流，改由 raw 內容核對：`#define CMD_READ 0`、`#define CMD_WRITE 1`）。
- 判定分三級：**對**（名稱與值均相符）／**錯**（附兩邊實際值）／**無法驗證**（來源缺失，附原因）。

## 2. 逐列判定（30 列）

以下引用列一律行內 `U10-XX-NNNN`，不產生 `| U10-` 開頭的表格資料列，以免污染主文件計數。

### U10-SB（SMBIOS 值域 ← edk2_SmBios.h）— 6/6 對

1. `U10-SB-0616` **對** — 宣稱 `PortConnectorTypeSsaScsi ＝ 0x13`；來源 edk2_SmBios.h:1343 `PortConnectorTypeSsaScsi = 0x13,`。
2. `U10-SB-0570` **對** — 宣稱 `MemoryInterleaveUnknown ＝ 0x02`；來源 :1108 `MemoryInterleaveUnknown = 0x02,`。
3. `U10-SB-0981` **對** — 宣稱 `ManagementDeviceAddressTypeUnknown ＝ 0x02`；來源 :2589 `ManagementDeviceAddressTypeUnknown = 0x02,`。
4. `U10-SB-0412` **對** — 宣稱 `ProcessorFamilyViaC7D ＝ 0xD3`；來源 :740 `ProcessorFamilyViaC7D = 0xD3,`。
5. `U10-SB-0382` **對** — 宣稱 `ProcessorFamilyAS400 ＝ 0xB4`；來源 :710 `ProcessorFamilyAS400 = 0xB4,`。
6. `U10-SB-0874` **對** — 宣稱 `MemoryFormFactorCamm ＝ 0x11`；來源 :1958 `MemoryFormFactorCamm = 0x11,`。

### U10-PM（PMU 事件 ← pmu_*.json）— 6/6 對

7. `U10-PM-0698` **對** — `FP_ARITH_INST_RETIRED.SCALAR`（alderlake）宣稱 Code 0xc7 Um 0x3；pmu_alderlake_floating-point.json：EventCode=0xc7 UMask=0x3。
8. `U10-PM-0527` **對** — `UOPS_DECODED.DEC0_UOPS`（alderlake）宣稱 Code 0x76 Um 0x1；pmu_alderlake_pipeline.json：EventCode=0x76 UMask=0x1。
9. `U10-PM-0121` **對** — `sse_avx_ops_retired.sse_avx_pack`（amdzen4）宣稱 Code 0xb Um 0xc0；pmu_amdzen4_floating-point.json：EventCode=0x0b UMask=0xc0（數值等值）。
10. `U10-PM-0490` **對** — `TX_EXEC.MISC4`（haswell）宣稱 Code 0x5d Um 0x8；pmu_haswell_memory.json：EventCode=0x5d UMask=0x8。
11. `U10-PM-0435` **對** — `SQ_MISC.BUS_LOCK`（alderlake）宣稱 Code 0x2c Um 0x10；pmu_alderlake_cache.json：EventCode=0x2c UMask=0x10。
12. `U10-PM-0195` **對** — `ls_mab_alloc.all_allocations`（amdzen4）宣稱 Code 0x41 Um 0x7f；pmu_amdzen4_cache.json：EventCode=0x41 UMask=0x7f。

### U10-PH（coreboot 暫存器 ← /tmp/cb/*.h）— 6/6 對

13. `U10-PH-0139` **對** — 宣稱 `GPP_D0_IRQ ＝ 0x40`；adl_gpio_defs.h:131 `#define GPP_D0_IRQ 0x40`。
14. `U10-PH-0053` **對** — 宣稱 `GPP_T2_IRQ ＝ 0x32`；adl_gpio_defs.h:36 `#define GPP_T2_IRQ 0x32`。
15. `U10-PH-0348` **對** — 宣稱 `ACPI_BASE_SIZE ＝ 0x100`；adl_iomap.h:120 `#define ACPI_BASE_SIZE 0x100`。
16. `U10-PH-0452` **對** — 宣稱 `V_P2SB_CFG_IBDF_DEV ＝ 30`；adl_systemagent.h:52 `#define V_P2SB_CFG_IBDF_DEV 30`。
17. `U10-PH-0399` **對** — 宣稱 `DSX_CFG ＝ 0x1834`；adl_pmc.h:95 `#define DSX_CFG 0x1834`。
18. `U10-PH-0078` **對** — 宣稱 `GPP_A11_IRQ ＝ 0x4B`；adl_gpio_defs.h:63 `#define GPP_A11_IRQ 0x4B`。

### U10-GD（GUID 表 ← MdePkg.dec）— 6/6 對（4+8 段逐段相等）

19. `U10-GD-0368` **對** — `gEfiSpiSmmHcProtocolGuid`；MdePkg.dec:1503 `{ 0xe9f02217, 0x2093, 0x4470, { 0x8a, 0x54, 0x5c, 0x2c, 0xff, 0xe7, 0x3e, 0xcb }}`，12 段全符。
20. `U10-GD-0085` **對** — `gEfiCertSha1Guid`；dec:639 `{ 0x826ca512, 0xcf10, 0x4ac9, {0xb1, 0x87, 0xbe, 0x1, 0x49, 0x66, 0x31, 0xbd }}`，全符（來源 `0x1` 非補零寫法，數值等值）。
21. `U10-GD-0497` **對** — `gEfiBluetoothIoProtocolGuid`；dec:1907 `{ 0x467313de, 0x4e30, 0x43f1, { 0x94, 0x3e, 0x32, 0x3f, 0x89, 0x84, 0x5d, 0xb5 }}`，全符。
22. `U10-GD-0045` **對** — `gEfiMemoryOverwriteRequestControlLockGuid`；dec:523 `{ 0xBB983CCF, 0x151D, 0x40E1, {0xA0, 0x7B, 0x4A, 0x17, 0xBE, 0x16, 0x82, 0x92}}`，全符。
23. `U10-GD-0063` **對** — `gEfiFirmwareErrorSectionGuid`；dec:577 `{ 0x81212a96, 0x09ed, 0x4996, { 0x94, 0x71, 0x8d, 0x72, 0x9c, 0x8e, 0x69, 0xed }}`，全符。
24. `U10-GD-0103` **對** — `gEfiKmsFormatSha512512Guid`；dec:662 `{ 0x2f240e12, 0xe14d, 0x475c, {0x83, 0xb0, 0xef, 0xff, 0x22, 0xd7, 0x7b, 0xe7 }}`，全符。

### U10-AC（ACPI 常數 ← actbl*.h／EDK2 ACPI 標頭）— 6/6 對

25. `U10-AC-0107` **對** — 宣稱 `ACPI_SIG_MSCT ＝ "MSCT"`；actbl1.h:50 `#define ACPI_SIG_MSCT "MSCT"`。
26. `U10-AC-0338` **對** — 宣稱 `EFI_ACPI_6_2_FPDT_RECORD_TYPE_FIRMWARE_BASIC_BOOT_POINTER ＝ 0x0000`；edk2_Acpi62.h:1212 同值。
27. `U10-AC-0503` **對** — 宣稱 `AML_BYTE_PREFIX ＝ 0x0a`；edk2_AcpiAml.h:24 同值。
28. `U10-AC-0557` **對** — 宣稱 `AML_ARG4 ＝ 0x6c`；edk2_AcpiAml.h:78 同值。
29. `U10-AC-0298` **對** — 宣稱 `EFI_ACPI_6_2_GIC_V4 ＝ 0x04`；edk2_Acpi62.h:658 同值。
30. `U10-AC-0579` **對** — 宣稱 `AML_DEREF_OF_OP ＝ 0x83`；edk2_AcpiAml.h:100 同值。

> 附註（初輪追查，非 30 列之內）：初版僅查 actbl*.h+edk2_Acpi62.h 時曾判 3 列「無法驗證」，擴及本機其他 EDK2 ACPI 標頭與 Linux cppc_acpi.h 後均轉「對」——`EFI_ACPI_WDAT_1_0_WATCHDOG_INSTRUCTION_READ_COUNTDOWN ＝ 0x1`（edk2_WatchdogActionTable.h:83）、`EFI_ACPI_6_4_HMAT_SSLBI_DATA_TYPE_ACCESS_BANDWIDTH ＝ 3`（edk2_Acpi64.h:2175）、`CMD_READ ＝ 0`（Linux master include/acpi/cppc_acpi.h）。此三列屬前一次抽樣組合（GD 母體放寬前），不計入本次 30 列，僅作旁證。

## 3. 結論

- **正確率：對 30／錯 0／無法驗證 0（30/30 = 100%）**；分域均 6/6。
- 抽中列之常數名、值、GUID 十六進位序列、PMU EventCode/UMask 全數與正典來源相符；未發現虛構常數、值漂移或 GUID 段序錯置。
- 錯列修正建議：無（0 錯）。
- 觀察（非錯誤）：
  - `U10-AC-0690` 一類 CPPC 常數（`CMD_READ`/`MAX_CPC_REG_ENT` 等）來源為 Linux `include/acpi/cppc_acpi.h`，不在 ACPI 表頭；主文件來源欄若未標明，讀者難以溯源，建議在該小節來源註記補此檔。
  - `U10-GD-0175` 的 `0XAF`（大寫 X）係照抄 MdePkg.dec 原文，語意無誤，但與全表 `0x` 小寫慣例不一致。
- 抽樣為seed 固定之隨機抽審，30 列約當五域宣稱母體 3,401 列之 0.88%；100% 通過為強訊號但非全批保證，建議下輪以不同 seed 續抽以累積信心。

## 4. 重現

```bash
cd U10-parts && python sample_check.py   # seed=20261025 內建；輸出 30 列判定與彙總
```

來源路徑可由環境變數 `XINSPECT_TMP` 覆寫（預設 `C:\Users\ADMINI~1\AppData\Local\Temp\1`，即 Git Bash `/tmp`）。完整原始輸出見同目錄 `sample_check_output.txt`。
