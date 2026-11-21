# Round374：RAPL32位元回繞差分（2026-11-17）

**已套來源、聯合定向131/131；新full／freeze待驗。**

Services/RaplEnergyProbe.cs 原對64-bit傳輸值用模2^64差分；能量欄位實際32-bit，FFFFFFF0→10應為32，舊式會得巨大值，基線與負載兩窗都可反向誤判。改共用既有 CeilingDecoder.Delta32，不新增相同數學helper。

來源為已保存Intel SDM253669-087 Volume3B §16.10.3／Figure16-37，印刷頁16-51，能量bits31:0、63:32保留；官方PDF、URL、hash及擷取時間見 obj/audit/r374/staged/rapl-counter-width-v2/manifest.json 與source.json／sources。不宣稱這是最新修訂。

新probe端到端注入測試經真控制流：正常、基線回繞、負載回繞三例；唯一真thread只等取消，不執行算術負載。原碼正常例綠、兩個回繞例實紅，分別錯判為NotResponding及Trusted；修正版三例全部在後續131綠中實跑。R370 gate／溫度／Join、被動不啟負載語意保留；沒有新增GUI互動驗證按鈕或CLI入口。

單次mod差分無法識別reset、多次wrap、跨socket身份或讀取精準度；這些仍須額外證據。stage-v1因CRLF needle不符而assert失敗、未改正式碼；v2另存，原失敗目錄及腳本保留。

## 真實套用及驗證

三輪七個targets已按candidate／preimage SHA-256套用；obj/audit/r374/integration-root/applied.json、各輪applied及preimage保留。唯讀代理複核在 obj/audit/r374/review-next-high/REVIEW-R372-R373-R374-2026-11-17.md，不是編譯或硬體證據。

r373-374-original-red.trx為原生產碼＋兩份新fixture：12通過／11失敗／0略過，共23，runner110ms；9個Query非法值及2個RAPL回繞，TestExit1，original-negative-root/verified-result.json核hash與結果。R372不在此負控制。修正source首次r372-374-target.trx為116通過／9失敗／125，runner762ms，9項揭露上述行號缺陷。修正行號並保留斷言後r372-374-target-2.trx實際131/131通過、0失敗／略過，runner950ms，TestExit0；新class確有FirmwareReboot11／QueryAvailability26／RaplCounterWidth3個案例，不以廣filter既有綠燈掩蓋未發現。

target-2-verified-root.json保留counters／時間／TRX與exit hash；obj/audit/r373/line-number-root/correction.json保存額外行號修復前後hash。新source尚無獨立聯合full／freeze，baseline保持上一合格359–371實跑9,257，不能由新增方法推算。沒有commit、push或發版，沒有native重啟、MSR實測或CPU壓力測試。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
