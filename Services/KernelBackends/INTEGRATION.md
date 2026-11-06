# 核心後端鏈補丁包（三枚合・可切換）——整合筆記

> 本包回答一個問題：PawnIO（沙箱）、WinRing0（萬能鑰匙）、BlueSquadron（自研原生）三枚
> 如何合併成一條**可切換**的核心存取鏈。設計原則沿用專案既有哲學：三態、誠實申報、
> 寫入收口、能力即邊界。
>
> ⚠ **本包未經建置驗證**（撰寫環境無 .NET 10 SDK）。簽名與既有 API 的出入請在建置時
> 以編譯器為準；風格對齊 `Services/` 現行慣例。

## 檔案

| 檔案 | 內容 |
|---|---|
| `Services/KernelBackends/KernelBackend.cs` | `IKernelBackend` 介面、`KernelCapability`、`KernelTrustTier`、三態寫入結果 |
| `Services/KernelBackends/WinRing0KernelBackend.cs` | 既有 `WinRing0Bridge` 的適配器（行為不變，多記 `WriteGate`） |
| `Services/KernelBackends/PawnIoBackend.cs` | 沙箱後端，**內建自證協定**（對付「成功卻回零」事故） |
| `Services/KernelBackends/KernelBackendSelector.cs` | 按能力逐項選路 + 模式開關（Auto／僅沙箱／允許萬能鑰匙） |

## 核心設計決策

### 1. 按能力選路，不按後端選路
三枚能力圈互不包含。同一台機器「讀 MSR 走 PawnIO、寫 RDT 走 WinRing0」是合法且更優的
組合。`KernelBackendSelector.Route(need)` 的單位是「這一次呼叫、這一項能力」。

### 2. 信任層級是標示不是保證
`KernelTrustTier`（Sandboxed/Universal/Native）進每一項事實的來源標示，
驅動就緒卡照實渲染——沿用四級可信度的哲學。

### 3. PawnIO 事故的直接回應：自證
2026-08-30 實測「PawnIO IntelMsr 對每個 MSR 回成功卻回 0」（McaService.cs 在案），
這是 PawnIO 被退回的真正原因。本包不解釋這個 bug，而是**不信任回傳值**：
- 載入時讀語義上必非零的 MSR（0x8B 微碼、0xCE PLATFORM_INFO）；
- 全零 → 判 `SilentZero`，**整體退出鏈**（「成功卻回零」比「讀不到」更糟，因為它冒充事實）;
- WinRing0 同時可用時做一條交叉抽查；
- 自證結論進 `backend.pawnio.health` 事實。

### 4. 模式開關（settings.json：`KernelBackendMode`）
- `Auto`（預設）：沙箱優先，通不過自證才落萬能鑰匙；
- `SandboxOnly`：萬能鑰匙整條擋掉，讀不到就三態——**給 HVCI 使用者與高安全場景**；
- `UniversalAllowed`：明示同意後全開，仍沙箱優先。

## 接線點（按依賴順序）

1. **`SettingsService`**：加 `KernelBackendMode` 屬性（整數持久化，只追加不調換既有順序——
   與 `AiProvider` 枚舉同一紀律）。
2. **服務層 17 個寫入呼叫點＋全部 `WinRing0Bridge.Create()` 呼叫點**：
   改取 `KernelBackendSelector`（經 DI／工廠），`ReadMsrPair64` → `selector.ReadMsr`。
   橋接的引用計數會話語義由 `WinRing0KernelBackend` 保持，呼叫端不用管。
3. **`BackendEnvironmentService`**：
   - `backend.msr` / `backend.mmio` 的值改為「後端名＋信任層級」（如 `PawnIO（沙箱）· Sandboxed`）；
   - 新增 `backend.pawnio.health`、`backend.chain_decision`（哪枚被選、哪枚被拒、為什麼）。
4. **設定頁**：新增「核心存取後端」三選一；選 `UniversalAllowed` 時展示
   WinRing0 的無呼叫者校驗風險行（風險文案直接取自 `WinRing0Bridge` 頭註）。
5. **超頻頁**：寫入前查 `Route(MsrWrite)`——若只有萬能鑰匙可寫，同意對話框多一行後端標示。

## 待補測試（命名沿用現行慣例）

- `KernelBackendSelectorTests`：模式×能力矩陣；拒絕原因逐條可渲染。
- `PawnIoSelfValidationTests`：注入假 Native 縫——全零→SilentZero 退出鏈；交叉不一致→退出鏈。
- `WinRing0BackendTests`：能力申報隨橋接選用方法組變化；寫入必記 `WriteGate`。
- `PawnIoPinTests`（接入官方庫後）：`ModulePins` 逐列對照上游發佈頁雜湊。

## 誠實界線（本包不解決的）

- **PawnIO 原生庫未接入**：`Native.IsBound` 恆 false，後端整體缺席——這是申報不是佔位。
  接入需引官方 PawnIO 二進位＋逐列填 `ModulePins` 官方雜湊。
- **BlueSquadron 作為第三枚**：介面已預留 `KernelTrustTier.Native`，但接進來的前提是
  WHQL/attestation 簽章落地（錢與流程，不是代碼）。
- **沙箱外的能力**：PCI 寫、I/O 埠、MMIO 目前只有萬能鑰匙能做。`SandboxOnly` 模式下
  這些頁面會整片三態——這是特性不是缺陷。
