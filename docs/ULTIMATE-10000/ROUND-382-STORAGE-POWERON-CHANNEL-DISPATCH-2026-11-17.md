# Round382：累計通電時間依 ChannelOf 拒絕不支援通路（2026-11-17）

**正式來源與新測試已套用；第二次 rebuilt 聯合 298/298 通過，本 class 14/14。新 full／freeze 與正式文件守門待驗。** 歷史候選 `obj/audit/post379-storage-high/manifest.json` 的 Finding 為 POST379-STORAGE-POWERON-DISPATCH、Round為null；正式輪次現在是R382，不改寫原候選歷史欄位。

原 TryReadPowerOnHours 只問 ChannelOf(bus)是否NVMe，所有非NVMe值皆走 ATA SMART，包括未知bus0、SAS、RAID、Spaces、virtual、USB等 ChannelOf 已拒絕的通路。MachineAgeService上游只排除第一次bus結果0；第二次查詢失敗或非ATA非NVMe仍可進錯分支。修正先遵循共同 ChannelOf policy：只有 Ata 可派發 ATA，其餘直接回既有讀不到sentinel0。新增 internal ReadPowerOnHours 接縫抽出同一正式派發／解碼，公開入口仍供應既有bus查詢與native delegate。NVMe log偏移0x80的低64位、ATA屬性9低3bytes、null／短buffer／超long範圍與sentinel語義均保留，沒有新增native存取、retry、watchdog、背景thread或更改breaker。

PowerOnHoursDispatchTests 的9個不支援bus（0／1／7／8／10／14／15／16／UInt32.MaxValue）逐項驗回0且NVMe／ATA delegate都不呼叫；ATA3／11與NVMe17驗原解碼及唯一正確delegate，null NVMe不fallback、null ATA保留0。實跑14案例完全使用managed byte arrays；沒有真的開磁碟、WMI、constructor、Thread.Sleep或metadata存取。此輪修的是dispatch policy，不宣稱完成其他NVMe parser、非同步native生命週期、裝置健康或硬體安全資格。

負對照 `r380-382-negative.trx` 聯合 **30通過／27失敗，共57，TestExit1**；本 class實際 **5通過／9失敗，共14**。對照只將原派發／解碼抽到相同helper API，候選與該控制的行為差異為拒絕非ATA的check；9個不支援bus確實紅、5個正常／null保留控制綠。它明確是 instrumentation-only original dispatch，並非 byte-original 來源執行，也不把舊來源缺helper的編譯錯誤當行為負對照。byte-original preimage及控制來源hash分開保留。

第一次 fresh rebuilt `r380-382-target.trx` **294通過／4失敗，共298，0略過，TestExit1，runner4秒**；本 class已14/14。四紅均在共同整合守門：memory serializer測試錯取省略的Present、兩個既有memory鍵未列catalog、coverage479與實值481差異、公開coverage表舊值。root保持本輪StorageSmartService候選，修正測試序列化預期、登記真既有鍵與未覆蓋理由並更新公開表。第二次 fresh rebuilt `r380-382-target-2.trx` **298/298通過，0失敗／略過，TestExit0，runner4秒**，PowerOnHoursDispatch14/14；原失敗與負對照保留。

合格target2的時間 **2026-11-17T21:47:40.8187558+08:00–21:47:48.7203368+08:00**，TRX SHA-256 `cebd49e9a7932e5355192217858a47b8dcd895918881cfe045ef8a6775bfa408`，exit收據SHA-256 `ce0ba663888174ed186adf7f7ac6a025c0a3c7c5df2e7a1d99b4d1fa6fbc1151`。相鄰target2-observation.json綁定真TRX／exit／class counters與正式source hashes。R383 SPD匹配、R384 WMI匹配、R385 AI quantity說明、R386 CLI collector橋接仍在另行staged／規劃，不屬R372–382新full；CLI XPF只有靜態真入口接線與注入service測試，memory.current／rate尚不在CLI AllFacts。

新full／freeze及正式文件守門仍待root；baseline維持上一次合格完整實跑 **9,257（R371）**，本輪298 target不是全套結果。沒有commit、push或發版。

新全套前置第一次 `obj/audit/r382/joint-final01/r382-prefull.trx` 實跑 140 通過／1 失敗，共141，0略過；source seal 比較零變更，未進入 full。唯一失敗是簡體 README 摘要表仍寫161個前綴，實際162；兩官方generator checks皆0未涵蓋這個摘要 literal，既有 ReadmeStatsTests 正確攔截。root只修該欄為162，不改斷言；原失敗、seal與收據完整保留，另建 fresh final02 前置／full，不重用輸出。


## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
