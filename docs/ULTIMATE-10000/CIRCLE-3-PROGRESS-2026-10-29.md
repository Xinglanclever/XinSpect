# U10 第三圈進度報告（第 309–317 輪，2026-10-29）

> 一份給「離開九天之後回來的自己」看的報告：這一圈做了什麼、每一件事的證據在哪、
> 抓到過哪些真問題、還剩什麼。所有數字都能用第八節的指令當場複算。

## 一、這一圈在做什麼

第三圈的主題是**可信度**：第一圈讓程式說得出話（硬體事實），第二圈讓它看得見（呈現與操作），
第三圈決定那句話**能不能被信**——「為什麼讀不到」、「量測本身有多準」、「這份報告是什麼時候量的」。

九輪全是同一個形狀：**純投影／純解析的服務＋逐條守門測試＋機器複算的文件數字**，不改任何讀取路徑、
不寫任何硬體狀態。

## 二、實測帳面（每一個數字都有守門測試從這棵樹重算）

| 項目 | 現值 | 備註 |
|---|---:|---|
| 編譯期事實鍵 | **1,145** | v3.02 時 1,131 |
| 事實域 | **151** | 新增 `mt`（量測嚴謹度）與 `secboot`（安全開機）兩個域 |
| 動態鍵家族 | **59** | v3.02 時 49 |
| 專案測試基線 | **7,467** | v3.03 出貨時 7,361；312 輪起每輪都是提交後全套**實跑**綠 |
| 未覆蓋基線 | **419** | 每條 +1 都在 `FactCoverageReportTests` 寫明「為什麼沒有第二條通路」 |
| 服務檔 | **639** | `find Services -name '*.cs' \| wc -l` |

## 三、逐輪一覽

| 輪 | 提交 | 項目 | 兌現的鍵 | 全套 |
|---|---|---|---|---|
| 309 | `a76afc4` | 裝置診斷進事實層（OS-0002～0004） | `os.dev.issue.*`／`status.*`／`driver.*`＋`os.dev.summary` | 7,373（7,372 綠＋TrackedSources 提交即解） |
| 310 | `6823c57` | 量測時間戳（MT-0003） | `mt.stamp.range`＋`mt.stamp.lag.*` | 7,383（同上） |
| 311 | `45755a2` | 收集步驟讀取代價（MT-0004） | `mt.cost.summary`＋`mt.cost.step.*` | 7,391（同上） |
| 312 | `cb48fd3` | 逐鍵重複性（MT-0001） | `mt.repeat.summary`＋`mt.repeat.key.*` | **7,406 全綠**（提交後覆核） |
| 313 | `6d2700a` | 逐域量測預算（MT-0002） | `mt.budget.summary`＋`mt.budget.*` | **7,416 全綠** |
| 314 | `514ab75` | 安全開機四環（SE-0001／0002） | `secboot.chain`＋`secboot.empty.*` | **7,429 全綠** |
| 315 | `5f93153` | BCD 開機順序與選單等待（BD-0001／0003） | `boot.order.*`＋`boot.order.item.*` | **7,442 全綠** |
| 316 | `987b358` | 電池健康分列（BT-0002～0004） | `pw.battery.design_mwh`／`full_mwh`／`wear_pct`／`cycle_count` | **7,455 全綠** |
| 317 | `055f4d1` | 類別碼三層解碼（PC-0004） | `pci.class.summary`＋`pci.class.dev.*` | **7,467 全綠** |

第 312 輪同時**接手了另一個代理（Freebuff）留在同一棵工作樹的未提交工作**：程式內百科／手冊
（`百科.html`／`手冊.html`／`Dialogs/EncyclopediaWindow`）、把 `Assets/ceremony.mp3`（5.29 MB）
移出倉庫。接手時修掉它一個真 bug：`EncyclopediaWindow.xaml` 引用了 `BrowserView` 的**本檔**資源鍵
`AddressBox`，使用者一開百科視窗就會 `XamlParseException`——已改指全域 `SetTextBox`。

## 四、這九輪抓到的真問題（比「做了什麼」更值錢）

1. **編出來的「循環 0」**（第 316 輪）：`pw.battery.summary` 會印「循環 0」，但程式從來沒查過
   循環次數——`Win32_Battery` 沒有這個欄位，固定變數 0 被直接印出來。**每一台有電池的機器
   都被斷言了一次從未發生的讀取。**現在真的查 `root\WMI BatteryCycleCount`，查不到寫「未回報」，
   兩條測試釘住「沒揭露 ≠ 0 次」。
2. **「空著」與「讀不到」的界線**（第 314、315、316 輪反覆演練）：零條簽章是已知的空（Present＋說明）；
   讀不到轉 Unknown 並指名是哪一環不知道；找不到 `{bootmgr}` 區塊不得寫成「清單是空的」。
   這條界線現在是四個服務的共同形狀，而且各自有測試。
3. **本檔資源鍵當全域用**（第 314 輪接手發現）：`{StaticResource AddressBox}` 在別的 XAML 裡查不到，
   編譯不報錯、執行期才炸。守門 `XamlResourceKeyTests` 一直在，防的是未來。
4. **事實鍵掃描器的形狀依賴**（第 312 輪）：掃描器把「名字含 `Key` 的字串常數」當成事實鍵，
   於是取樣鍵常數與家族前綴常數都得避開這個命名（`CpuUtilId`／`FamilyPrefix`），
   否則未覆蓋數虛增、「目錄≡掃描」紅燈。
5. **SpecRef 是入場券**（第 317 輪）：在 `XinSpect.Decoders` 加公開方法不帶 `[SpecRef]`，
   三條守門一起紅（V7 §12.11「沒有引用＝未驗證」）。
6. **對照表方法別插進別人的掃描區間**（第 317 輪）：`KnowledgeCoverageTests` 用
   「`SubClassName` 到 `VendorName` 之間數 switch case」對帳子類別數；把 `ProgIfName` 插在中間
   就讓申報 19 對到實際 30。加表之前先看它落在哪個掃描區間。
7. **文件錨點會跟著標題裡的數字漂移**（第 313、314 輪各踩一次）：標題寫「N 把鍵」，
   數字一改目錄就指到上一版。修法有二：`gen-readme-domains.py` 現在會同步這些錨點（第 315 輪），
   另有「三份 README 站內錨點都要指得到標題」的守門（第 314 輪）。
8. **`gen-readme-domains.py` 的縮排 bug**（第 315 輪修）：`patch_count_lines` 用
   `startswith("- [第 6 部 …")` 判斷，但目錄行有兩格縮排，那條永遠不匹配——錨點因此一直停在舊數字。

## 五、固定的方法（每一輪都是這四件事）

1. **一個純函式服務**：投影既有事實或解析既有文字，不做 I/O、不開行程——測試於是可以餵任意輸入重跑。
2. **誠實的態度**：沒收錄說沒收錄、沒回報說沒回報、讀不到說讀不到；「沒有」與「讀不到」
   永遠是兩個斷言；公式與界線寫在事實值裡，不是只寫在註解裡。
3. **逐條守門**：六態契約（Present 不帶理由、非 Present 必帶理由且不帶數字）、家族已登記、
   鍵只含合法字元、摘要數字與明細對帳。
4. **數字機器複算**：README 的每個統計都有守門測試從這棵樹重算；提交前全套實跑綠、
   提交後再覆核一次才推。

## 六、還剩什麼（29 項詳列，已結案 15，剩 14）

| ID | 項目 | 批次 |
|---|---|---|
| U10-PC-0001 | 存取方法宣告（設定空間經哪個 API 讀） | D |
| U10-AC-0001 | RSDP 簽章與傳統／延伸總和雙驗證 | D |
| U10-AC-0002 | RSDP 版本與 XSDT 取捨邏輯成文 | D |
| U10-FS-0001 | NTFS 開機磁區 BPB 逐欄 | D |
| U10-FS-0002 | MFT 邏輯叢集號與 MFTMirr 位置 | D |
| U10-WH-0003 | WHEA 平台記憶體錯誤節區（型別 2）解碼 | F |
| U10-WH-0004 | 記憶體節區錯誤類型碼表（CRC／單位／多位） | F |
| U10-NP-0002 | xHCI 版本與支援速度位 | F |
| U10-NP-0003 | 根集線器埠數（逐主控） | F |
| U10-SE-0004 | VBS 服務執行清單 | B |
| U10-SE-0005 | Credential Guard 狀態鏈 | B |
| U10-OS-0001 | 裝置樹全圖（devnode 階層） | A |
| U10-EN-0001 | 同型號族群基線（本地彙總） | G |
| U10-NV-0029 | Identify 保留區非零掃描 | G |

## 七、已知未完成的事

- **README 附錄 H 的服務檔數停在 618**（守門只盯第 0 部的統計表＝639）。下一輪順手把產生器
  對附錄 H 的計數也接上。
- **候補清單的 317 列還寫「本輪」**——提交號要等提交後才知道，下一次動那份文件時補 `055f4d1`。
- **介面外觀沒有人眼驗證過**：第 305–308 輪的新 UI（懸浮小窗 15 項、感測器儀表板）與這九輪的
  事實頁面，建構測試全綠但沒有截圖逐頁看過。
- 「音樂限制 1 分 22 秒」的原始訴求由另一個代理處理成「mp3 移出倉庫、放回檔案就會播」；
  如果要的是「播完整首」，那是另一件事。

## 八、怎麼複算

```bash
# 鍵數與家族數
grep -c '^        "' Services/FactKeyCatalog.cs
grep -c '^            \["' Services/FactKeyDynamicCatalog.cs
# 測試基線
grep -n 'ProjectTests' Tests/TestSuiteBaseline.cs
# 逐域展開與文件同步（--check 回零才算同步）
python Tools/gen-readme-domains.py
python Tools/gen-docs-html.py
# 全套（單節點建置；每次換新的輸出目錄，絕不 dotnet test | tail）
DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 \
  dotnet test Tests/XinSpect.Tests.csproj -m:1 -p:UseSharedCompilation=false \
  -p:BaseOutputPath="obj/_verify_$(date +%H%M%S)/"
```
