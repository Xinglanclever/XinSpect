# 給 GLM 5.3 Flash 的工作指令書（由 Claude Opus 4.8 撰寫，2026-08-30）

> 讀完本檔再動手。本檔規定**你能碰什麼、怎麼算做完、怎麼回報**。
> 專案背景與地雷看 `ROADMAP-給GLM.md`（第四部誠實紅線、第五部執行守則必讀）
> 與 `HANDOFF-1.3.2.md`（第五～八節地雷清單）。目前狀態看 `HANDOFF-2026-08-30.md`。

---

## 第零條 ・ 最重要的一條

**你寫的每一句結論，都必須是命令輸出或 `檔案:行號` 支撐的。做不到就寫「未驗證」。**

「應該可以」「大致完成」「已修正」「理論上」——**這些詞在回報裡出現一次，整份回報作廢。**
沒跑過的東西不准說它會動；沒讀到值的欄位不准說它讀到了。
**如實說「這台機器讀不到」是滿分答案；宣稱讀到了但其實沒跑，是零分。**

---

## 第一條 ・ 界線（越界即停）

**你只做本檔第四條清單裡的項目，一次一項，照編號順序。**

不准做的事：
- 不准碰 MSR／PMU／MMIO **寫入**（`PERFEVTSEL`、`PQR_ASSOC`、`L3_QOS_CFG`、iMC PMON、SMU/SMN）。
  那批由 Claude 負責。你只做唯讀、零特權的項目。
- 不准改 `Services/TopDownService.cs`、`Services/RdtService.cs`、`Services/MemoryTruthService.cs`、
  `Views/BenchView.xaml(.cs)`。這三個服務有 Claude 待修的缺陷，你動了就會撞車。
- 不准新增導覽分頁（`_views[]` ↔ Nav 平行陣列，21 頁，動它必炸）。**新功能一律做成既有頁面上的卡片。**
- 不准 bump 版號。不准 push 到 `main`（在 `feature/glm-interfaces` 分支上做）。
- 不准改 `XinSpect.csproj`、`Themes/Theme.xaml`、`App.xaml(.cs)`、`MainWindow.xaml(.cs)`。
- 不准新增 NuGet 相依。
- 不准動 `Services/CpuzReportService.cs:391-392`（那裡的簡體字是解析第三方報告用的，合法）。

**開工前先宣告：** 在回報的「本項改動檔案」欄位列出你要碰的檔案。**沒宣告的檔案不准改。**

---

## 第二條 ・ 每一項的完成定義（三件套，缺一件等於沒做）

1. **服務**：`Services/XxxService.cs`，照 `Services/CacheBenchService.cs` 的形狀
   （`ObservableObject`、`IsRunning`/`CanStart`、`Status`、`ObservableCollection<Row> Rows`、
   `Start()` → `_ = RunAsync()`、`Cancel()` → `_cts?.Cancel()`）。
   **屬性一律走 `SetProperty`，不准用 `{ get; private set; }` 自動屬性**——那樣 UI 永遠不會更新。
2. **接上 UI**：`ViewModels/MainViewModel.cs` 加一個屬性；在**既有頁面**加一張 `{StaticResource Card}`；
   **並且真的有人呼叫它**（`Loaded` 事件或 `DispatcherTimer`，範本見 `Views/CpuView.xaml.cs:11-17`）。
   ⚠ 只寫服務不接呼叫點＝這張卡永遠顯示「—」。上一輪就是這樣掛掉的。
3. **測試**：`Tests/` 下至少一支純函式測試，方法名用中文。**絕不建構 `MainViewModel`。**

**綠燈才算完成：**

```bash
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo
```

這行會連帶編譯主專案（＝驗證 XAML），且不受使用者執行中的 XinSpect.exe 檔案鎖影響。
**它綠了才 commit，一項一筆提交。**（建置驗證另有 `-p:BaseOutputPath=obj/_verify/`，別 taskkill 使用者的程式。）

---

## 第三條 ・ 誠實規則（本專案的主軸，違反等於整項重做）

1. **讀不到就顯示「—」或「本機不支援」。絕不填 0、絕不用典型值、絕不推估。**
2. **「能力」與「實際」必須並列**（協商速率 vs 埠能力、設定值 vs 生效值）。只給一個都是誤導。
3. **沒有校準係數不准換算**（缺分壓比的電壓只能標「未校準 raw」）。
4. **不准用版本號推論安全結論**（可以顯示微碼版本，不可以說「你有 XX 漏洞」）。
5. **不准替使用者下診斷**（可以呈現趨勢，不可以說「你該清灰塵了」）。
6. **敏感值不外顯**（ACPI `MSDM` 含 OEM 產品金鑰，只顯示「存在」，不顯示值、不寫進報告檔）。
7. **量測方法要寫在 UI 上**（說明文字要講清楚量的到底是什麼，不得誇大工具能力）。

---

## 第四條 ・ 工作清單（照編號做，做完一項回報一次）

全部是**零特權唯讀**項目。每項後面標了資料來源與卡片該掛哪一頁。
括號裡的 API 名稱**你必須自己查證真實簽章**，本清單只給方向，不保證拼字正確。

1. **CPUID 補葉** — `X86Base.CpuId`，擴充既有 `CpuIdService`。
   葉 `0x15`（TSC/核心晶振比 → 真實外頻）、`0x16`（base/max/bus MHz）、`0x0A`（PMU 版本與計數器位寬）、
   `0x0D`（XSAVE 區域）、`0x06`（熱與電源管理能力）、`0x80000007`、`0x80000008`（位址位元數）。
   **先讀葉 0 的 EAX 取最大葉、`0x80000000` 取最大擴充葉，超範圍的葉不准讀。** → 掛 CPU 頁。
2. **拓樸交叉驗證** — `GetLogicalProcessorInformationEx` 與 CPUID `0x1F`/`0x0B` 兩邊都算一次
   SMT/Core/Die/Package 層級，**不一致就明說不一致**（虛擬化或 BIOS 關核會造成）。→ 掛 CPU 頁。
3. **NtQuerySystemInformation 系列** — 逐核 Idle/Kernel/User/DpcTime/InterruptTime、
   `SystemMemoryListInformation`（待命／修改／可用清單分優先級）、
   `SystemCodeIntegrityInformation`（測試簽署與核心除錯位元）、`SystemHypervisorDetailInformation`。
   未文件化但極穩定。→ 逐核時間掛健康頁，記憶體清單掛記憶體頁。
4. **SMBIOS 補 Type** — 既有的 SMBIOS 直讀擴充 Type 7/11/24/26/27/28/29/32/45。**沒填就明說沒填。**
   → 掛主機板頁。
5. **ACPI 四表** — `GetSystemFirmwareTable('ACPI', sig)`：`FPDT`（開機韌體耗時）、`PMTT`、`BERT`、`LPIT`。
   先用 `EnumSystemFirmwareTables` 列舉存在哪些表，**不存在就顯示「本機韌體未提供此表」**。→ 掛主機板頁。
6. **裝置樹 `CM_Get_*`** — `CM_PROBLEM` 精確原因碼（22/28/31/43/45/51）、資源描述（IRQ/IO/MEM 重疊＝衝突）、
   `PowerData`（D-state 支援與現況）、DriverVersion/Date/Provider/**Signer**、`LocationPaths`（實體 slot／USB 埠）。
   ※ 這台機器的網卡是自簽 `oem47.inf`，**Signer 欄位應該會顯示它不是 WHQL——那是真實資料，不要當成錯誤**。
   → 掛裝置相關頁面的新卡片。
7. **電源** — `PowerReadACValueIndex` 全子群組（含核心停放、PCIe ASPM、USB 選擇性暫停、隱藏設定）＋
   `CallNtPowerInformation(ProcessorInformation)` 逐核頻率百分比（停放實況）＋`SYSTEM_POWER_CAPABILITIES`。
   → 掛健康頁或電源相關卡片。
8. **USB** — `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2`：**協商速度 vs 埠能力並列**；
   `bInterval` → HID 回報率。→ 掛裝置頁。
9. **音訊** — `IAudioClient3::GetSharedModeEnginePeriod`（真實可達最低延遲，不是宣稱值）＋
   獨占模式支援格式＋`KSPROPERTY_JACK_DESCRIPTION`（接孔顏色與位置）。→ 掛裝置頁。
10. **Wi-Fi／藍牙** — `wlanapi`：current_connection 的 PHY/速率/RSSI ＋ BSS IE 解析
    （HT/VHT/HE/EHT 通道寬度、空間流、MU-MIMO/OFDMA/BSS Color/TWT）＋介面能力，
    做成「網卡 × AP × 協商」三欄併排；藍牙走 `BLUETOOTH_RADIO_INFO` 的 HCI/LMP 版本。
    → 掛網路頁。**本機若無無線網卡，就顯示「本機無無線介面」並在回報裡如實寫明。**

**做不完不要緊，照順序做到 token 用完為止。** 一項都沒做完也可以，但**不准留半成品**（見第五條）。

---

## 第五條 ・ token 將盡時的收尾程序（照做，不要自由發揮）

**當你估計剩餘 token 不足以把「當前這一項」做到 `dotnet test` 綠燈時，立刻停止寫程式，執行：**

```bash
git status --short
```

1. **把做一半的那一項全部撤掉**：新檔案直接刪除，改過的既有檔案 `git checkout -- <檔案>`。
   **寧可少一項功能，也不要留下編譯不過的工作樹。**
   （上一輪就是留了半接的線：XAML 綁了服務沒有的成員、code-behind 呼叫服務沒有的方法，
   結果接手的人第一件事是修你的殘骸，而不是往前做。）
2. 撤完再跑一次 `dotnet test`，**確認是綠的**。
3. 確認 `git status --short` 只剩你已 commit 的內容與未版控的 `.md` 文件。
4. 然後才寫第六條的回報。

---

## 第六條 ・ 回報文本（寫成 `REPORT-GLM.md`，逐欄填，不准改欄位）

**這份回報是給 Claude 讀的，不是給人看的說明文。要的是可核對的事實，不是敘述。**

````markdown
# GLM 回報 ・ <日期時間>

## A. 機器可核對的狀態（貼命令原始輸出，不准手打、不准節錄）

### A1. `git log --oneline -10`
```
<貼原始輸出>
```

### A2. `git status --short`
```
<貼原始輸出>
```

### A3. `dotnet test Tests/XinSpect.Tests.csproj -v q --nologo`
```
<貼最後 15 行原始輸出，必須含「通過!」或失敗摘要與測試總數>
```
測試總數：<數字>（基準 434 + 我新增 <數字> = <數字>）

## B. 逐項狀態（第四條清單十項全列，一項都不准省略）

| # | 項目 | 狀態 | 提交 | 服務檔案 | UI 卡片位置 | 呼叫點 | 測試檔 |
|---|---|---|---|---|---|---|---|
| 1 | CPUID 補葉 | 完成／未動／已撤銷 | `<sha>` | `檔案:行` | `檔案:行` | `檔案:行` | `檔案` |
| 2 | … | | | | | | |

**狀態只能填這三個詞之一：`完成`（測試綠且已 commit）／`未動`（一行都沒寫）／`已撤銷`（做一半，已按第五條撤掉）。**
**沒有「部分完成」這個選項。**

## C. 本機實測值（每個「完成」的項目都要有，這是防止你自己騙自己的關鍵）

每項回答三題：

1. **在這台機器上真的跑出了什麼值？** 貼三筆真實讀值（例：`葉 0x16: base 2600 MHz / max 4200 MHz / bus 100 MHz`）。
2. **哪些欄位讀不到、顯示成「—」？** 逐一列出，並寫出原因（葉不存在／韌體未填／本機無此裝置）。
3. **你是怎麼確認的？** 是跑起來在畫面上看到的，還是只跑了單元測試？
   **只跑單元測試就寫「僅單元測試，未於畫面上驗證」——這樣寫不會被扣分，謊稱看過會。**

## D. 我沒做到的事 / 我不確定的事

- 逐條列出。**這一節空白的回報視為不可信。** 任何猜的 API 簽章、任何沒查證的假設，都寫在這裡。

## E. 給 Claude 的具體交辦

- 需要 Claude 決定或修補的點，逐條寫清楚，附 `檔案:行號`。
````

---

## 第七條 ・ 三句話總結

1. **一次一項，綠了才 commit，做不完就撤乾淨。**
2. **每句結論都要有命令輸出或 `檔案:行號`；沒有就寫「未驗證」。**
3. **讀不到就誠實說讀不到——那是滿分答案。**

— Claude Opus 4.8，2026-08-30
