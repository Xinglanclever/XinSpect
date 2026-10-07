# 語言模式修復交接（2026-10-07）

> 背景：使用者回報「英語模式根本沒有好」，並要求續修簡體模式與切換按鈕卡住的問題
> （紀年切換、AI 供應商切換）。本檔記錄本 session 已完成、進行中、卡住的每一件事。
> 基準：main = `bc52323`（v2.25 已發佈）；本輪所有變更**尚未 commit**（工作區內）。

---

## 一、已查實的三個根因

### 1. 英語模式沒生效（屬實，量化如下）

- `LanguageService.SetLanguage(AppLanguage.English)` 呼叫 `ConvertVisualTree(main, _simplified=false)`
  ——**視覺樹轉換只看繁簡旗標，英語完全沒有分支**。XAML 硬編碼文字永遠停在繁中。
- `EnglishStrings.cs` 翻譯表只有 **83 條**；XAML 全量收割（obj/_xaml_strings.txt）有
  **1,338 條**含中文字串（Text/Content/Header/ToolTip/行內文字）。覆蓋率 ~6%。
- `LanguageService.Initialize` 只載入 `SimplifiedChinese`，沒載 `IsEnglish`——
  重啟後英語偏好不還原（MainWindow 首啟對話窗路徑也沒套用英語）。

### 2. 紀年切換卡住（簡體模式）

`MainViewModel.EraIndex` 的 getter/setter 原本直接用**列舉值**當索引。但簡體模式的
`EraNames` 清單**少了民國**（4 項 vs 繁體 5 項），索引與列舉值錯位：
選清單第 2 項「宣統」（索引 2）→ 套到列舉 2＝**黃帝**。看起來就是「切不過去/卡住」。

### 3. AI 供應商切換卡住

`Views/SettingsView.xaml.cs` 的 `AiProvider_Changed` 在 `SelectionChanged` 事件裡讀
`vm.Settings.AiProviderEnum`——但 ComboBox 的 `SelectedIndex` 是 TwoWay 綁定，
**事件觸發時綁定可能還沒把新值推回源**，處理器（與它更新的提示文字）讀到舊供應商。

---

## 二、已完成的修復（工作區內，未 commit）

### 機制（Services/LanguageService.cs）

1. `FromOriginal` 加英語分支：`_isEnglish` 時回 `EnglishStrings.Lookup(原文) ?? 原文`
   （未收錄回繁中原文）。永遠從保存原文出發→繁/簡/英三向可逆、冪等。
2. `SetLanguage(AppLanguage)` 兩種模式都走視覺樹轉換＋`RebuildNavIfNeeded`，
   並把 `settings.IsEnglish` 一併持久化。
3. `Initialize` 載入 `IsEnglish`。
4. 新增 `internal static void SetEnglishForTests(bool)` 測試鉤子
   （不走 SetLanguage 的 Shell/設定檔路徑）。

### 啟動（MainWindow.xaml.cs）

首啟對話窗後：`Settings.IsEnglish` → `SetLanguage(AppLanguage.English)`（優先於簡體判斷）。

### 翻譯表（Services/EnglishStrings.cs）

- 以 `obj/_xaml_strings.txt`（1,338 條收割）為底，人工翻譯 **1,234 條新增**
  （腳本 `obj/_build_trans.py` 合併；XAML 實體 `&gt;` 等已解碼；`{Binding…, StringFormat=中文…}`
  條目如實跳過——WPF 轉換器在 StringFormat 之前執行、只看得到原始值，那批需要
  XAML 改用 ConverterParameter 才能翻，屬後續工作）。
- 現表 **1,317 條**。建置通過。

### 紀年 bug（Models/EraCalendar.cs＋ViewModels/MainViewModel.cs）

- `EraCalendar` 新增純函式 `FromIndex(bool simplified, int)`／`IndexOf(bool simplified, EraMode)`。
- `EraIndex` 改經顯示清單映射；`Settings.DefaultEra` 存**列舉值**（不是顯示索引）。
- 回歸測試 `Tests/EraCalendarTests.cs`（`EraCalendarMappingTests` 類）：簡體下選宣統
  真的套宣統、繁體索引＝列舉值、roundtrip 不變。**已綠（31 通過）**。

### AI 切換 bug（Views/SettingsView.xaml.cs）

`AiProvider_Changed` 開頭改為直接讀 `ComboBox.SelectedIndex` 推回 `vm.Settings.AiProvider`
（事件早於綁定回源的時序問題）。

### 英語視覺樹測試（Tests/EnglishTreeTests.cs，新檔）

三個測試：收錄/未收錄行為、繁英簡三向往返冪等、翻譯表抽樣。
RunSta 已加例外傳遞（原本裸 Thread 會炸 host）。

---

## 三、卡住的點（接手從這裡開始）

**`Tests/EnglishTreeTests.cs` 仍在崩測試 host**（xunit 報 Unhandled exception：
`Assert.Equal` 失敗 "Overview" vs 繁中原文——即視覺樹轉換在測試裡沒走到英語分支，
或靜態旗標在 STA 執行緒上沒被 ConvertVisualTree 讀到）。

排查建議：
1. 確認 build 後的 DLL 是新的（`--no-build` 前先建一次；前一轮曾跑到舊 DLL）。
2. `SetEnglishForTests(true)` 設的是靜態欄位；`ConvertVisualTree`→`FromOriginal` 讀的
   也是同一靜態欄 `_isEnglish`——理論上跨執行緒可見。若仍失敗，在 `FromOriginal` 加
   暫時性輸出確認分支，懷疑點：**測試 filter 只跑 EnglishTree 時崩的是另一支測試**
   （abort 訊息裡的方法名是亂碼，先跑單一測試 `--filter "FullyQualifiedName~英語模式"` 縮小）。
3. 測試通了之後：全套 `dotnet test Tests/XinSpect.Tests.csproj --nologo`（基準 3126＋新增）。

---

## 四、收尾清單（測試綠後）

1. 簡體模式殘留檢查：`LanguageService.T()` 的呼叫點（HelpCatalog、狀態列等程式碼產生文字）
   是否也都過英語表——目前只修了視覺樹層，程式碼層 `T()` 已有英語分支（查表→回退原文），理論上自動跟上。
2. `{Binding…, StringFormat=中文…}` 條目（obj/_xaml_strings.txt 第 136–179 行）：
   需 XAML 端參數化，屬下一輪。
3. 清掉 `obj/_trans_a.py`、`obj/_trans_b.py`、`obj/_build_trans.py`、`obj/_xaml_strings.txt`。
4. commit（繁中訊息）＋push。
5. 更新記憶 `xinspect-project-state.md`：英語模式修復細節與「使用者點名罵過」這件事。

## 五、誠實聲明

- 英語翻譯 1,234 條為 AI 一次寫成，**未經母語者校對**；未收錄字串回退繁中是設計行為。
- AI 切換與紀年切換的修復有回歸測試釘住；英語視覺樹測試本身尚未轉綠，
  **修復未驗證完成前不得宣稱「英語模式已修好」**——上一次就是這樣被罵的。
