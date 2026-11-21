# Round372：韌體重啟的系統工具路徑（2026-11-17）

**已套來源、聯合定向131/131；新full／freeze待驗。**

Services/BiosMeService.cs 原 shutdown.exe 裸檔名交給提權 shell 搜尋，可能解析到非系統路徑。本輪固定採 Environment.SystemDirectory/shutdown.exe 完整路徑，路徑無效／檔案缺失直接拒絕；固定 /r /fw /t 0、runas 語意保留，不尋找 CWD／PATH 備援。

public RebootToFirmwareSetup 經注入目錄／檔案存在／啟動回應測試；風險勾選在讀路徑前及dispatch前重核，回應false不冒稱成功、例外保留而不重試。預設constructor不Refresh，不新增韌體／WMI工作。此改動不新增硬體寫入或CLI重啟入口，也未全面收口其他BIOS風險政策。

檔案存在檢查至Process.Start仍有時間差，沒有handle pin或映像簽章驗證。未執行shutdown、UAC、實際重開機或韌體寫入。原碼沒有新注入constructor，不能把直接回退編譯失敗稱為runtime反證；本輪沒有R372舊碼runtime負控制。

## 真實套用及驗證

三輪七個targets已按candidate／preimage SHA-256套用；obj/audit/r374/integration-root/applied.json、各輪applied及preimage保留。唯讀代理複核在 obj/audit/r374/review-next-high/REVIEW-R372-R373-R374-2026-11-17.md，不是編譯或硬體證據。

r373-374-original-red.trx為原生產碼＋兩份新fixture：12通過／11失敗／0略過，共23，runner110ms；9個Query非法值及2個RAPL回繞，TestExit1，original-negative-root/verified-result.json核hash與結果。R372不在此負控制。修正source首次r372-374-target.trx為116通過／9失敗／125，runner762ms，9項揭露上述行號缺陷。修正行號並保留斷言後r372-374-target-2.trx實際131/131通過、0失敗／略過，runner950ms，TestExit0；新class確有FirmwareReboot11／QueryAvailability26／RaplCounterWidth3個案例，不以廣filter既有綠燈掩蓋未發現。

target-2-verified-root.json保留counters／時間／TRX與exit hash；obj/audit/r373/line-number-root/correction.json保存額外行號修復前後hash。新source尚無獨立聯合full／freeze，baseline保持上一合格359–371實跑9,257，不能由新增方法推算。沒有commit、push或發版，沒有native重啟、MSR實測或CPU壓力測試。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
