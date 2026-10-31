# 曦覽 XinSpect 3.09「Ultima XII」

應用程式圖示換成品牌插畫：藍色 X ＋ 兩張視窗卡。exe、工作列、視窗、捷徑一起換。

## 這一版做了什麼

圖示原本是 `make_icon.ps1` 用 System.Drawing 現畫的「曉光儀表」向量標記。現在改用
`Assets/brand-source.jpg` 這張品牌插畫，產生流程也跟著換：

- 裁切以插畫外框（藍色 X 的 bbox）為基準正方形化，留 6% 邊距、四角 20% 圓角；
  下界停在 wordmark 之前，所以 16×16 時不會把「XinSpect」字樣糊成一團。
- 六個尺寸（256／128／64／48／32／16）**都從 1024 母圖重取樣**，不是拿 256 再放大；
  `.ico` 以 PNG payload 打包。
- `make_icon.ps1` 保留成入口，內部呼叫 `Tools/make-icon.py`。換圖是一行的事：
  覆蓋 `Assets/brand-source.jpg` → 跑 `make_icon.ps1` → 提交 `app.ico`。

`MainWindow.xaml` 的視窗圖示與 csproj 的 `ApplicationIcon` 本來就共用同一個 `app.ico`，
所以工作列、視窗左上角、檔案總管與捷徑是同一張，不會出現「執行檔是新圖、視窗是舊圖」。

## 授權

| 情況 | 適用 |
|---|---|
| 一般使用、研究、修改、散布、商業使用 | [AGPL-3.0-or-later](https://github.com/Xinglanclever/XinSpect/blob/main/LICENSE) |
| 閉源整合、隨硬體出貨、組織禁用 copyleft | [商業授權](https://github.com/Xinglanclever/XinSpect/blob/main/COMMERCIAL-LICENSE.md)（可涵蓋任何版本） |
| 隨程式分發的第三方元件 | [THIRD-PARTY-NOTICES.md](https://github.com/Xinglanclever/XinSpect/blob/main/THIRD-PARTY-NOTICES.md) |

雙授權適用於所有版本：兩條路二選一、功能相同，不疊加。

## 如實申報

- 這一版只換圖示與它的產生工具；**沒有任何判定邏輯或讀取通路被改動**，事實鍵 1,166 不動。
- 驗證方式是把發佈出來的 exe 挖開比對：256×256 那張 PNG 的位元組原樣命中，確認圖示
  真的編進執行檔，而不只是換了專案裡的檔案。
- 92 條發佈守門全綠（版號八處、沿革逐字對帳、文件數字、百科 sha256、安裝器、XAML、
  帳本證據）。全套這次沒有重跑——這一版只動了圖檔與產生腳本，沒有任何 C# 邏輯變更。

## 下載

| 檔案 | 用途 |
|---|---|
| `XinSpect.exe` | 主程式（便攜單一執行檔，含藍色中隊守護進程） |
| `BlueSquadronBridge.exe` | 守護進程，要脫離主程式單獨跑防護時使用 |
| `XinSpectDeploy.exe` | 一鍵安裝器（GitHub／Google Drive／中國直連線路；下載時顯示進度條、速度與剩餘時間） |
| `SHA256SUMS.txt` | 逐檔雜湊，發佈後由 `Tools/verify-release.ps1` 對帳 |
