# Round381：保存的 XPF 處理器節區進入既有 WHEA 投影（2026-11-17）

**正式來源與新測試已套用；第二次 rebuilt 聯合 298/298 通過，XPF class 28/28。U10-WH-0001 仍為 partial；新 full／freeze 與正式文件守門待驗。** 早期 R383 編號只是歷史暫編，最終套用依 R381 manifest，不與後續真正 R383 SPD 匹配混用。

純 XPF decoder 解碼已保存 CPER 處理器節區，envelope 保留驗證後 typed payload，WheaErrorFactsService 將 safe facts 併入原有一次 WHEA read。既有 publication lease／timeout revoke 與 AllFacts 消費路徑保留，沒有新增 EventLog enumeration、native reader 或硬體探測。界線為64B prefix、最多63筆64B ProcInfo、最多63筆 context；context header16B、u16長度與16B零 padding，要求精確消耗。未知 check GUID、保留 validity 位與不支援延伸不被猜成已知資料。

公開投影只留已知 check kind、validity 允許的安全 scalar 與 context 類型／長度。APIC／CpuId 原值、target/requester/responder ID、instruction pointer、context address／register bytes、任意 GUID 不出現在模型／facts／JSON。export key 使用批次內 ordinal；EventRecordID 僅在內部複合身分去重，不對外暴露。不做 CPU／socket 歸因、native MCA bank 解碼或真實故障判定。新增2 static keys 與1 dynamic family；covered 申報仍保留單一 saved-source 的未覆蓋理由，477→479。R380既有memory鍵顯形後再校正為481；沒有新豁免或湊數比對規則。family cap71→72，公開文件跟隨實際 catalog／AST，方法數與runtime案例分開。

`r380-382-negative.trx` 聯合 **30通過／27失敗，共57，TestExit1**；WheaXpfProcessorSyntheticTests **24通過／4失敗，共28**。負對照保留新 parser／facts／tests，回復舊 envelope retention 與舊 aggregator，讓同一 regression 能偵測 production typed retention／聚合漏接；它是明示注入控制，並非 byte-original 整產品證明。初次 staging 的 anchor 不唯一與 catalog ordinal 錯位均保留修正前紀錄，不能算實跑。

第一次 rebuilt target **294通過／4失敗，共298，0略過，TestExit1，runner4秒**，XPF28/28已綠。四紅為 R380 serializer fixture 對省略 Present 的錯誤 GetProperty、catalog 未列原有 memory.current／rate、未覆蓋481高於479，以及公開覆蓋表舊值。root 修正 fixture 以正式還原驗證、登記既有鍵與單一來源理由、對齊公開表；沒有減弱 parser、catalog逐鍵或coverage斷言。第二次 fresh output rebuilt **298/298通過，0失敗／略過，TestExit0，runner4秒**：XPF28、PCIe23、ProcessorGeneric19、WheaEnvelope14、MemoryFacts56、MemoryPublication9、ExportPrivacy4、SectionKnowledge12、DecoderHardening24、SpecRef3、FactKeyCatalog13、KeyPolicy19、FactCoverage12全部通過。TRX **2026-11-17T21:47:40.8187558+08:00–21:47:48.7203368+08:00**，SHA-256 `cebd49e9a7932e5355192217858a47b8dcd895918881cfe045ef8a6775bfa408`；原先 target 與負對照不覆寫。

新測試以 synthetic saved XML／SDK-layout bytes 注入 reader，走真 envelope、decoder、aggregator、publication、AllFacts 及 CliService filtering／JSON；沒有執行 XinSpectConsole Main／Program.Collect、真 EventLog producer 或真機。靜態 trace 證實真 executable 原有 **Program.Collect→EvidenceCollection.LoadUsermodeFacts→MeasureWheaMemory→CollectWheaMemory→WheaErrorFactsService→AllFacts→CLI evidence** 路徑，但前段 ReloadInto 失敗或逾時仍可能阻止到達。這不是實際 executable runtime 證據，也未驗 XPF-specific bridge snapshot／evidence-pack。桌面 Capture 使用明列 subset，未併 WheaMemoryFacts；不能由 AllFacts 有XPF就推論桌面時間膠囊也有。CLI bridge 以AllFacts建snapshot則僅有上述靜態路徑。詳見 `obj/audit/r381/cli-trace-high/REPORT.md`。

真 producer fixture provenance、manifest/provider版本、sanitized CPER及實機相容仍缺；synthetic bytes不構成物理支援資格，U10-WH-0001保持partial。R383–386另行staged／規劃均排除本輪full。相鄰 target2-observation.json 保存原TRX／exit／source hashes；新full／freeze與文件守門待驗，最近合格完整baseline仍 **9,257（R371）**。沒有commit、push或發版。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
