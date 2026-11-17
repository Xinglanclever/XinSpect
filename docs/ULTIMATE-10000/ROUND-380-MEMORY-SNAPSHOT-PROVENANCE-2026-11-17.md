# Round380：記憶體快照按數量保留來源與可得狀態（2026-11-17）

**正式來源與新測試已套用；第二次 rebuilt 聯合 298/298 通過，本 class 15/15。新 full／freeze 與正式文件守門待驗。** 本文件是在 obj 對帳後供 root 套用的候選，沒有宣稱全套完成。

原 EvidenceLab 兩個記憶體快照欄位固定標示 CPU-Z／Derived／Present，但實際啟動可能選到 WMI 設定速率或 SPD JEDEC 參考值。MemoryTimings 現在分開攜帶 timing／rate 的 source、trust、六態 availability、reason 與 rate scope；缺乏 metadata 的舊物件安全預設 Unknown。HardwareReadingPriority 保留 kernel→WMI→CPU-Z 的既有選擇，混合 timing 依實際使用欄位保留來源與最弱狀態。WMI 是 configured／Reported，CPU-Z 為 Reported；SPD JEDEC 是 Derived 參考，不冒充目前控制器量測，Unknown 且 numericValue 為 null，名義值以明示參考的文字保留。沒有新增硬體讀取、服務、動態家族或事實能力。

正式路徑為 StartupSequence／CpuzReportService→HardwareReadingPriority→vm.Timings→EvidenceLab.Capture 的 Collect→CollectMemoryTimingFacts→HardwareSnapshotService。新測試注入模型／CPU-Z 文字，走正式 resolver、producer、serializer 與 EvidenceFactRow；沒有建構 MainViewModel、執行 native／WMI，也不證明 kernel current timing 支援或來源更新時間。CLI `--enterprise-baseline --snapshot` 可讀既有桌面快照；真實 CLI collector 的 AllFacts 沒有 memory.current／memory.rate，所以 `--json evidence`、bridge collect_snapshot 與 evidence-pack 並未取得這兩個數量。Fleet 比對前綴也不包含 memory.*。詳實靜態追蹤見 `obj/audit/r381/cli-trace-high/REPORT.md`；R386 的另行橋接提案不屬本輪。

負對照共用 `obj/audit/r382/r380-382-negative.trx`：聯合 **30 通過／27 失敗，共57，TestExit1**。MemorySnapshotProvenanceTests 實際 **1 通過／14 失敗，共15**；這是保留同一 helper API、恢復原硬編碼投影語義的 diagnostic mutant，不是整份 byte-original 產品執行。候選恢復 hash 收據獨立保留；不得把 staged 預期當成實跑。

第一次 rebuilt `r380-382-target.trx` 實際 **294 通過／4 失敗，共298，0略過，TestExit1，runner4秒**。本 class 為14通過／1失敗：RealSnapshotSerializationPreservesUnknownJedecAndWmiProvenance 對 serializer 省略的預設 Present 呼叫 GetProperty，拋 KeyNotFoundException。root 修正測試為驗證 availability 不落盤，並透過正式 LoadFromStream 確認 WMI rate 還原 Present、JEDEC timing 維持 Unknown；沒有放寬 serializer／snapshot validator。其餘三紅是兩個既有 memory 鍵由 Add wrapper 移到可掃描 producer 後，catalog 漏列、未覆蓋實值481高於479，以及公開覆蓋表仍舊。root 登記原已存在的 memory.current／memory.rate，保留未覆蓋並逐項說明 WMI configured／SPD nominal 不是獨立同範圍 current 觀察；未新增豁免、假交叉規則或假能力。XPF +2 對應477→479，既有 memory 可見性校正再479→481。四個失敗及原TRX完整保留。

第二次 fresh output rebuilt `r380-382-target-2.trx` 實際 **298/298 通過，0失敗／略過，TestExit0，runner4秒**；MemorySnapshotProvenance15/15、HardwareReadingPriority15/15、HardwareSnapshot14/14、SnapshotSchemaCompat4/4。TRX時間 **2026-11-17T21:47:40.8187558+08:00–21:47:48.7203368+08:00**；SHA-256 `cebd49e9a7932e5355192217858a47b8dcd895918881cfe045ef8a6775bfa408`，exit 收據 SHA-256 `ce0ba663888174ed186adf7f7ac6a025c0a3c7c5df2e7a1d99b4d1fa6fbc1151`。相鄰 target2-observation.json 綁定原始收據、實際 class counters 與正式來源 hash。

R383 SPD 身分匹配、R384 WMI cross-source 匹配、R385 AI quantity 說明及R386 CLI collector 橋接仍為另行 staged／規劃範圍，均不併入 R372–382 驗證。新 full／freeze 與文件守門待 root 執行；最新合格完整實跑 baseline 仍 **9,257（R371）**。本輪 target 綠不代表全套、實體硬體或 executable integration 驗證；沒有 commit、push 或發版。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
