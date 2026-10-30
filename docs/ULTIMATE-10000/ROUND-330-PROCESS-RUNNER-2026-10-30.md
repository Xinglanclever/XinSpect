# 第 330 輪（修 D4／D9／D10／D11）：四個「平時看不出來」的缺陷

> 這一輪不動任何事實鍵，修的是四個**平時完全看不出來**的缺陷。
> 它們的共同點是：壞掉的時候不會拋例外、不會紅燈，只會「卡住」、「少一筆」或
> 「設定檔壞掉」。而四個裡面最貴的一句是：**逾時不是保護，它只是一行程式碼**——
> 走不到的那一行保護不了任何東西。

## 一、缺陷長什麼樣

修之前有兩處、兩種程度上不同的寫法。

**`BootOrderReader.TryReadEnum`** — 兩條串流都讀了，但都是**同步**的 `ReadToEnd()`：

```csharp
string output = process.StandardOutput.ReadToEnd();
string error  = process.StandardError.ReadToEnd();   // ← 兩條都在 WaitForExit 之前
if (!process.WaitForExit(TimeoutMs)) { ... }
```

**`AdvancedSupplementFactsService.RunProcess`** — 更嚴重：`RedirectStandardError = true`，
但 stderr **一次都沒讀**。

兩者的死鎖機制相同：

1. 子行程把 stderr 寫滿 pipe 緩衝區（Windows 約 4 KB）就卡在 write 上；
2. 父行程卡在 stdout 的同步 read 上等 EOF；
3. `WaitForExit(timeoutMs)` 那一行**永遠執行不到**。

第三點是這一輪最值得記下來的一句話：**`timeoutMs` 不是超時保護，它只是一行程式碼**。
它保護不了自己走不到的路徑。第二種寫法連 `catch` 都救不了——因為根本沒有例外會被拋出，
只有兩個行程互相等待。

## 二、為什麼這條值得用一整輪

`bcdedit /enum` 正常只有數百位元組、幾乎不寫 stderr，所以實務上罕見。
`RunProcess` 的呼叫端（電源／工具查詢）風險高一些。這是典型的**潛伏型**缺陷：
不會每天炸，但一旦炸就是「App 卡在啟動收集，使用者只看到『正在收集證據…』」。

而它的**槓桿比行號看起來大**：`AdvancedSupplementFactsService.RunProcess` 是全檔案共用的私有工具，
改它等於一次修掉所有呼叫端。所以做法不是各修各的，而是收成**一支**。

## 三、做成什麼

新增 `Services/ProcessRunner.cs`：

```csharp
// **關鍵順序**：兩個串流都在 WaitForExit 之前開始讀。
// 少讀任何一個，只要子行程往那裡寫滿緩衝區就會死鎖。
Task<string> outTask = process.StandardOutput.ReadToEndAsync();
Task<string> errTask = process.StandardError.ReadToEndAsync();

if (!process.WaitForExit(timeoutMs))
{
    try { process.Kill(entireProcessTree: true); } catch { }
    Observe(outTask);
    Observe(errTask);
    return new ProcessRunResult(null, null, null, true,
        $"{fileName} 在 {timeoutMs} 毫秒內沒有回應，已中止（整個子行程樹）");
}
```

三個設計決定：

1. **`ReadToEndAsync` 在 `WaitForExit` 之前。** 兩條串流都先掛上讀取，之後才去等。
   這樣不論子行程往哪一條灌水，都有人在收。
2. **逾時要殺整棵樹**（`entireProcessTree: true`）。只殺父行程會留下孤兒孫行程，
   而那正是「使用者按了關閉，程式還在背景跑」的來源。
3. **遲到的例外要吃掉**（`Observe`）。逾時後那兩個讀取工作還在飛，它們會因為串流被關而拋
   `ObjectDisposedException`；不觀察的話會在 GC 時變成未觀察例外，把整個行程帶走——
   那等於用更糟的方式重現我們正要防的問題。

回傳 `ProcessRunResult(Stdout, Stderr, ExitCode, TimedOut, Reason)`：
`Stdout` 為 `null` 時 `Reason` 說明原因。**逾時與「結束碼非零」是兩種不同的失敗**，
所以 `ExitCode` 逾時為 `null`，不是 0。

界線上，這支**不接收命令字串**——一律走 `ArgumentList`，避免空白與引號的組字問題。

## 四、測試怎麼證明它修好了

死鎖的測試有個麻煩：**舊實作下測試不會失敗，它會卡住**。所以每一條都帶相對短的
timeout，並斷言「有回來」。

| 測試 | 造的子行程 | 舊實作下的結果 |
|---|---|---|
| `大量stderr不會死鎖` | 往 stderr 灌 400 行 | 卡住（緩衝區滿） |
| `兩個串流都大量輸出也不會死鎖` | 兩條同時各灌 300 行 | 卡住 |
| `逾時_要殺掉並回報逾時` | `ping -n 30`，timeout 800 ms | 回不來 |
| `程式不存在_回報原因而不是拋例外` | 不存在的檔名 | 例外／無訊息 |

另外兩條是**接線守門**，直接讀原始碼斷言：

- 兩個既有呼叫端都不再出現 `StandardOutput.ReadToEnd(`／`StandardError.ReadToEnd(`；
- `BootOrderReader` 確實呼叫 `ProcessRunner.Run`。

第二類守門在這裡是必要的：這個專案的歷史裡，「修了 A 卻留下 B 的同一個寫法」
已經發生過不止一次（第 329 輪的 WCTEMP 就是程式與測試共用同一個錯誤假設）。
**斷言「不再有那個字串」比斷言「行為正確」更難繞過。**

## 五、第二個：`AtomicWrite` 的暫存檔名（D9）

原本是固定的 `path + ".tmp"`。單行程單執行緒下完全正確，但本專案**支援多實例**
（`SettingsService` 的 `AllowMultiInstance`）：

```
A 寫完 tmp → B 覆寫 tmp → A Move → A 把 B 的半成品搬成正式檔
```

原本的註解說「殘留無害：下次寫入會原路覆寫它」——那句話對**殘留**成立，對**併發**不成立。

改成 `{path}.{ProcessId}.{序號}.tmp`，並補上「換名失敗就刪掉自己的暫存檔」，
免得失敗路徑留下沒人認領的垃圾。測試是兩條執行緒各寫 300 次、每次 20,000 字元：

- 落地內容必須**恰好等於其中一次的完整版**（撕裂寫入會產生不在集合裡的混血字串）；
- 資料夾裡不能留下任何 `.tmp`；
- 目標路徑被佔住時（拿資料夾當檔名），必須拋例外**且**把暫存檔收乾淨。

## 六、第三個：`HistoryStore` 的尾端垃圾（D10）

崩潰會在檔尾留下半截紀錄。`Load` 原本只忽略它——但 `Append` 走 `FileMode.Append`，
新紀錄接在**真正的檔尾**，也就是垃圾之後：

```
表頭 ＋ n 筆 ＋ k 位元組垃圾 ＋ 1 筆新紀錄
```

下一次 `Load` 用 `(len - 16) / 每筆長度` 算 `n`，於是把那一筆讀在錯的位移上，
時間戳變成隨機值而被丟棄。原本靠 `_minTicks.Count != n` 觸發的 `Compact` 自癒，
**代價是「崩潰後的第一筆會掉」**。

修法一行：`(raw.Length - HeaderBytes) % RecordBytes != 0` 也觸發 `Compact`。
載入當場就把垃圾清掉，那一筆新紀錄還沒寫進去，所以不會掉。

測試三條，其中一條是**反向**的：乾淨的檔案載入後 mtime 不變——不能為了保險而
每次啟動都重寫一遍 4 MB 的歷史檔。

## 七、第四個：外部報告沒有大小上限（D11）

`ExternalReportService.ParseFile` 對使用者選的任意檔案無條件 `ReadAllBytes`。
選到 3 GB 的 HWiNFO 全紀錄或誤選 `.iso`，就是一次大配置＋編碼偵測＋整份 `Split`，
最壞 OOM 或長時間無回應，而畫面上不會說為什麼。

加上 `MaxBytes = 64 MiB` 與**明白的 Status**：

> 檔案過大（65.0 MB），未解析：本頁上限 64 MB，整檔讀進記憶體會讓程式長時間無回應或失敗

三個細節：

1. **先看 `FileInfo.Length` 再讀。** 不是讀進來才檢查——那樣上限就沒意義了。
2. **界線寫進 Status，不靜默失敗。** 回空 Rows 但不解釋，等於換一種方式騙人。
3. **測試用 `SetLength` 造 64 MB + 1 位元組的檔案**，不必真的寫 64 MB；
   斷言 5 秒內回來，也就順便證明它沒有去讀那一坨。

## 八、這一輪順帶修掉的帳面漂移

新增 `Services/ProcessRunner.cs` 讓服務檔從 654 變 655、測試檔從 614 變 615，
於是 README 的規模表、§0.8 的複算算式、§6 的服務層敘述、附錄 J 的第 17 條、
附錄 H 的標題**全部**要跟著動。這件事每一輪都在發生，而它每次都會被
`ReadmeStatsTests` 抓出來——這正是那組守門存在的理由。

| 項目 | 前 | 後 |
|---|---:|---:|
| 事實鍵 | 1,166 | 1,166（本輪不動） |
| 服務檔 | 654 | **655** |
| 全域 C# 檔 | 1,549 | **1,552** |
| 測試檔 | 614 | **616** |
| `[Fact]` 方法 | 6,333 | **6,352** |
| 專案測試基線 | 7,660 | **7,679** |

## 九、界線宣告

- **不改行為語意**：`RunProcess` 原本就回「成功才給 stdout，否則 null」，改完仍一樣。
  這一輪換的是**取得方式**，不是判定規則。
- **不新增裝置查詢**：`bcdedit /enum` 與電源／工具查詢都是原本就有的唯讀呼叫。
- **不管子行程寫不寫得出來**：這支只保證「父行程不會被子行程卡住」。子行程自己
  寫不完是它的事，逾時會處置。

## 十、怎麼複算

```bash
# 1. 兩個呼叫端都不再自己同步讀串流
grep -n "ReadToEnd(" Services/BootOrderReader.cs Services/AdvancedSupplementFactsService.cs

# 2. 兩個暫存檔名都不再是固定字串
grep -n 'FilePath + ".tmp"\|path + ".tmp"' Services/*.cs

# 3. 外部報告有上限
grep -n "MaxBytes" Services/ExternalReportService.cs

# 4. 跑這一輪的兩組測試（含會灌爆 pipe 與造 64 MB 檔案的情境）
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~ProcessRunnerTests|FullyQualifiedName~StorageRobustnessTests"
```

第一條應該**沒有任何輸出**。它比跑測試快，而且在 CI 之外也驗得動。
