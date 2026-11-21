# XinSpect Agent Instructions

## R372–382 聯合全套與後驗收口（2026-11-17，最新狀態）

**本次完整回歸已實跑 9,420/9,420 通過，0失敗／略過，runner 8分1秒；2,151 來源／文件＋3份同輪 DLL 共2,154份 SHA-256 零漂移。** 範圍為已套 R372–382，排除 R364 blocked 來源及仍在 obj 的 R383–387。原證據 `obj/audit/r382/joint-final02/r382-full.trx`、`full-command-exit.json`、`freeze-verification.json`、`actual-full-result.json` 綁定真 counters、時間、source／DLL freeze；此 freeze 不在後驗文件更新後重新驗覆寫。baseline 由最近合格 R371 的9,257依本次真 full更新為9,420，8份公開文件／常數的精確差異與套用收據保留於 baseline-plan、baseline-applied.json；百科 Part11 原 bytes SHA78876283… 未改。

前置第一次140/141的CN前綴摘要161錯字已按真實162修正，失敗與原seal保留；fresh final02前置141/141通過且來源零變更，才建立新freeze並跑full。更新baseline後，fresh後驗文件守門 **85/85**、0失敗／略過，runner13秒，兩官方generator checks0。CLI fresh建置0錯誤、13個既有CS0162警告、1分18.12秒；9個離線smoke退出0／1／2／2／0／0／0／0／0且內容／遮蔽／scope鍵集合斷言全部通過，包括canonical asset鍵與省略Present的Fleet讀入、Redfish私密欄遮蔽、雙控制器兩順序保留各值而scalar null及唯一320W scalar。這是合成離線入口驗證，不是BMC／WMI／XPF真producer或硬體矩陣驗收。

R380的目前控制器時序沒有新增實讀；WH-0001仍partial。XPF CLI生產接線是靜態trace＋注入service證據，沒有將本次普通CLI smoke冒稱XPF實際採集。memory.current／rate仍缺CLI即時採集，R386橋接修補另在obj候選；R383正面DIMM身份、R384按來源WMI、R385 AI來源描述已獨審修訂，尚未套用／編譯／C#實跑。新R387 ECAM bus255 wrap／讀取邊界修補正在obj獨審。R364 AuditLog仍blocked，另研究同object journal格式與恢復契約，未套來源。

本次公開來源706服務／1,715 C#／722頂層測試檔／7,712方法及1,216鍵／162域／72家族均不是10000完成率。下一步先驗本段收口文件，再串行套用R383–385負控／正控並推進386／387，保留歷史失敗與各階段證據。本機無commit、push、上傳或發版。以下各段是當時歷史狀態，本段為目前合格full／postfull狀態；收口文字回填後的獨立文件守門另記原TRX，不預填通過。

## R372–382 接續整合（2026-11-17，最新狀態）

R372–382 修補已套用；R364 AuditLog 仍 blocked、未套用。R376–379 最終定向實跑 294/294 通過；R380–382 首次定向為 294 通過／4 失敗，共 298，已保留原 TRX，定位為漏登既有 memory.current/rate 兩鍵、未覆蓋數及 README 對帳、Present 省略契約測試四處。修正後 `obj/audit/r382/r380-382-target-2.trx` 真實 298/298 通過，0 失敗／略過、runner 4 秒、命令退出 0；原始 counters、分帳與 SHA 見 `target2-verified-root.json`。R380–382 診斷負控制共 57 例、27 失敗，含 same-API 舊語意 mutant／instrumented 舊行為及舊 typed-retention 接線，並非全數 literal 原碼，候選已逐檔 hash 還原；各輪文件記錄具體口徑。

本批新增記憶體逐量來源／信任／六態、離線 CPER XPF processor section 解碼及通電時數協定派發；不宣稱新增控制器目前時序實讀或 WH-0001 全部完成。XPF 的 executable CLI 採集鏈已靜態追到共用 WHEA 服務，尚缺該入口實際 XPF 執行證據；memory.current/rate 目前只在桌面 EvidenceLab 路徑，CLI 接線缺口已列 R386 候選。CPU／DRAM 來源優先序及兩級安全閘門保持既有契約。

新 R372–382 聯合 full／同輪 freeze 待驗，**最近合格全套仍是 R371 的 9,257/9,257**，不可由本批定向或方法數預填。公開來源統計已同步：706 服務檔、1,715 C#、722 頂層測試檔、7,712 測試方法；1,216 catalog 鍵／162 域／72 動態家族，reviewed uncovered 481。兩把既有 memory 鍵因改為可掃描 producer 而顯現，不是新增兩能力；來源／鍵／方法數均不是 ULTIMATE-10000 完成率。百科 Part 11 歷史 bytes 保留。

新全套前置第一次 `obj/audit/r382/joint-final01/r382-prefull.trx` 實跑 140 通過／1 失敗，共141，0略過；source seal 比較零變更，未進入 full。唯一失敗是簡體 README 摘要表仍寫161個前綴，實際162；兩官方generator checks皆0未涵蓋這個摘要 literal，既有 ReadmeStatsTests 正確攔截。root只修該欄為162，不改斷言；原失敗、seal與收據完整保留，另建 fresh final02 前置／full，不重用輸出。

下一批 R383 DIMM 正面身份綁定、R384 分來源 WMI 對帳、R385 AI 記憶體來源描述與 R386 CLI 記憶體橋接僅在 obj 候選／提案，未納本輪正式來源或 full。獨審提出的弱身份誤配、placeholder 假矛盾與六態遺失仍須逐項完成候選修訂和實跑。本機尚無 commit、push 或發版。以下歷史段落依其當時階段解讀，以本段為目前整合狀態。

## 359–371聯合驗證收口（2026-11-17，最新狀態）

**聯合full已實跑9,257/9,257通過、0失敗／略過，runner8分7秒；2,079來源／文件＋3DLL共2,082份SHA-256零漂移。** 範圍359–371排除未套用的R364 AuditLog來源。`obj/audit/r371/r371-full.trx`、`full-command-exit.json`（TestExit0／FreezeExit0）、`freeze-verification.json`（Checked2082／Drift0）及`actual-full-result-root.json`保留原始counters、時間及hash；原freeze不重驗覆寫。TRX 2026-11-17T17:34:06.1163433+08:00–17:42:16.8309114+08:00。

baseline依此次合格實跑由9,029更新9,257（`baseline-update.json`）；三語README、百科來源／HTML及守門基線已同步。兩官方generator checks均0，更新後`r371-final-docs.trx`實際78/78通過、0失敗／略過（runner14秒）。CLI建置0錯誤／13個既有CS0162警告（1分15.70秒）；六個離線smoke退出0／1／2／2／0／0且內容斷言通過，三個Redfish電力scope補充退出0／0／0，20／200兩順序保留各筆而scalar null、單一320保scalar320，逐筆遮蔽及key集合斷言通過。它們是離線合成案例，不是BMC實機／硬體精準度驗收。

postfull第一次在電力scope前置檢查中停止：R358複製模板誤留Checked2056，與R371實際2082不符；已有78項文件／CLI建置／六項smoke結果保留。root另版只修此前置常數，再跑原三項scope，沒有重跑full或改freeze。原失敗與修正版hash見`postfull-original-failure-root.json`，真實收口見`postfull-root-result.json`。本段回填後的文件守門另存新TRX，不預填成功。

下面的「新full待驗／baseline9,029」屬先前階段紀錄，以本段為最新驗證狀態；歷史351 Drift2、357三紅、359–363定向失敗、361原碼負控制與371編譯／prefull失敗均保留。來源統計704服務／1,701 C#／711頂層測試檔／7,644方法不是10000完成率。R364仍blocked；下一批R372重啟路徑、R373查詢enum及R374 RAPL回繞候選已備，尚未套用／編譯／執行，AI序號與Ceiling worker後續推進。本機無commit、push或發版。

Before working on this repository, read `docs/ENGINEERING-CONSTITUTION.md` and the applicable source specifications. They define the required depth, reliability, evidence, definitions, provenance, safety defaults, and verification discipline.

For ULTIMATE-10000 work, also read `docs/ULTIMATE-10000/HANDOFF-2026-11-16.md` and the relevant round/candidate record. Historical metrics are not current evidence. Follow the user's latest instructions when they update an older local rule.

## Delegation policy (2026-11-17)

- The user requires `high` reasoning for newly dispatched subagents. Pass the task context explicitly when the tool requires a fresh history to set this option.
- A failed agent request may be retried up to 100 times after the initial request for transient transport, rate-limit, or service-unavailable errors. Use bounded backoff, retain the attempt record, and resume existing artifacts rather than duplicating completed work.
- Preserve failure evidence. A compiler error, failing assertion, permanent authentication error, or invalid input requires diagnosis or correction; it is not a transient success-seeking retry.
- This policy governs development-agent dispatch. Application request retries and hardware-operation budgets retain their separately documented contracts.
