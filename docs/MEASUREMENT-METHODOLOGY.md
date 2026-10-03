# WP33 量測方法學（Deep Bench／訊號／延遲量測的通用規則）

- **範圍**：本文件約束所有「量測型」輸出（Deep Bench 測項、loopback 訊號統計、延遲取樣）。
  與 `docs/spec/METHODOLOGY.md`（事實來源契約）互補：那份講「事實從哪來」，這份講「量測怎麼做、數字怎麼算」。

## 1. 通用規則

1. **量不到是三態不是零**：裝置缺席、量測不可用、樣本為空——各以三態呈現，
   絕不輸出 0 冒充量測結果（例：loopback 全 0 樣本＝「靜音」是有效資料；
   無音訊裝置＝InsufficientPrivilege/ReadError）。
2. **統計有前提**：RMS／峰值只對同一量測配置的樣本有意義——混池規則
   （`DeepBenchMeasurementStatistics.PoolsDistinctConfigurations`）同樣適用於任何
   「不同配置的點不可平均」的場合（梯子型量測：stream×threads、QD ladder、快取工作集）。
3. **時間源**：單調鐘（Stopwatch/QPC）量經過時間；牆鐘只做時間戳標示。
   兩者差異本身就是量測對象（WP25 漂移 ppm）——不可混用。
4. **臨界值要成文**：削波判定（連續 ≥3 個滿格樣本）、突變測試門檻（low 70）、
   效能預算（冷啟動 3 秒）等所有門檻都寫在程式或規格文件，不留口頭約定。

## 2. 訊號量測（WP38）

- 樣本為 IEEE float（−1..1 滿格比例）。
- 峰值＝樣本絕對值最大值；RMS＝√(Σx²/N)；dBFS＝20·log10(滿格比例)，
  0 樣本以 **−120 dBFS 底線**呈現（−∞ 是「無意義值」，不是可溝通的數字）。
- 削波＝連續 ≥3 個 |x|≥1.0 樣本——單點滿格可能是合法峰值，不冒充削波。

## 3. 延遲與頻寬（Deep Bench）

- 每個測項的樣本保留原始分佈（可逐點呈現），摘要統計（中位數／百分位）不掩蓋離群值。
- 取樣窗、暖機策略、取消語意以各服務的 doc comment 為準；統計分類
  （High/Medium/Low/Insufficient）由 confidence engine 以已知答案合成樣本集釘死。
- flaky 量測（計時敏感測試）一旦確認非回歸，記錄於 `docs/ITERATIONS.md` 觀察清單，
  不靜默重試到綠。
