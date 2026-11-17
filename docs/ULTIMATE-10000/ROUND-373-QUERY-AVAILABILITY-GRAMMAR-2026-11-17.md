# Round373：查詢可用狀態的單一名稱與真實行號（2026-11-17）

**已套來源、聯合定向131/131；新full／freeze待驗。**

Services/QueryParser.cs 原 Enum.TryParse 接受數字及逗號組合，即使 FactAvailability 不是Flags也可接受，會把非法輸入偽裝成無匹配。現在fallback同時要求解析成功、IsDefined及單一正式名稱精確相等（不分大小寫）；原別名及前後空白相容。docs/spec/query-language.v1.md 成文六態、全別名及拒絕規則；GUI／CLI共用parser，沒有重複新語言。

先只套新測試，在原生產碼取得9個非法值全部實紅；六態名稱與10個別名對照仍綠，證據為聯合23項負控制中的Query20項。修正版第一次聯合定向另外揭露真缺陷：Parse傳入已加1的行號，ValidateField再加1，第二行錯報第三行；保留原「第2行」精確斷言，修呼叫端傳零起算index，另加6個欄位／日期／運算子／缺值分支反例。沒有改測試去迎合錯行號。原125項結果116通過／9失敗完整保留。

這是純parser行為及錯誤文字，不驗證任意查詢預算、快照來源真實性或外部資料信任。缺運算子與缺值仍在Parse中直接報行號。

## 真實套用及驗證

三輪七個targets已按candidate／preimage SHA-256套用；obj/audit/r374/integration-root/applied.json、各輪applied及preimage保留。唯讀代理複核在 obj/audit/r374/review-next-high/REVIEW-R372-R373-R374-2026-11-17.md，不是編譯或硬體證據。

r373-374-original-red.trx為原生產碼＋兩份新fixture：12通過／11失敗／0略過，共23，runner110ms；9個Query非法值及2個RAPL回繞，TestExit1，original-negative-root/verified-result.json核hash與結果。R372不在此負控制。修正source首次r372-374-target.trx為116通過／9失敗／125，runner762ms，9項揭露上述行號缺陷。修正行號並保留斷言後r372-374-target-2.trx實際131/131通過、0失敗／略過，runner950ms，TestExit0；新class確有FirmwareReboot11／QueryAvailability26／RaplCounterWidth3個案例，不以廣filter既有綠燈掩蓋未發現。

target-2-verified-root.json保留counters／時間／TRX與exit hash；obj/audit/r373/line-number-root/correction.json保存額外行號修復前後hash。新source尚無獨立聯合full／freeze，baseline保持上一合格359–371實跑9,257，不能由新增方法推算。沒有commit、push或發版，沒有native重啟、MSR實測或CPU壓力測試。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
