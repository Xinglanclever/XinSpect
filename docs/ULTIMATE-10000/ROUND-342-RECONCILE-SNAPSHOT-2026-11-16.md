# 第 342 輪：對帳測試共用唯讀快照

日期：2026-11-16。Round 339 的 TRX 顯示四項 `FactKeyRuntimeReconcileTests` 各自跑整條真機收集，分別約 85、85、86、121 秒，合計約 377 秒。它們檢查的是同一份事實的四個不同條件，沒有必要各讀一次硬體。

新增同檔的 xUnit class fixture `RealHardwareFactSnapshot`：仍實際執行 `ReloadInto`、`LoadUsermodeFacts`、`LoadPlatformFacts` 三個生產入口，之後把 AllFacts 凍結為 Array.AsReadOnly，四項測試只讀這一份快照。

仍保留原有的目錄／動態家族對帳、非 Present 原因、事實基本形狀與核心鍵存在檢查。合成儲存讀取器與禁止平行的集合規則也保留；沒有把真機收集換成全合成資料，沒有刪減案例或斷言。

這是測試效率與同一時點一致性的改進，不代表桌面啟動已減少相同時間。StorageReliability 等其他重複收集測試仍未重構，實際定向／全套耗時另記交接文檔。本輪不新增 10000 候選完成數。

最終整合驗證：Round 342 全套 8,216 通過、0 失敗、0 略過，13 分 31 秒；測試期間驗證輸入散列未變。`obj/audit/r342/r342-full.trx` 為原始證據。
