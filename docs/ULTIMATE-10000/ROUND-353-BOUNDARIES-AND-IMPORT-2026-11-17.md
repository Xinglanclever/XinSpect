# Round353：量測邊界、失敗證據與離線匯入（2026-11-17）

五組候選已核hash套用為 **17個獨立targets**（`obj/audit/r353/applied.json`）；原manifest18筆包含EvidenceLabService重疊，以獨立merged353版去重，不算18個獨立變檔。**定向84/84通過、0失敗／略過**（`obj/audit/r353/r353-target.trx`），runner15秒；TRX2026-11-17 05:24:28.0568178–05:24:47.5220829（UTC+08:00，19.465秒），計時範圍分列。包含WPF實際建置及UiSmoke，但沒有實際對話框／visual／accessibility操作證據，不擴大為全部GUI端到端驗收或350–354全套。

r351歷史全套 **8,731/8,731通過** 與 **2020檔／Drift2** 必須並列；不宣稱凍結0漂移，不更新baseline。最新完成且完整凍結合格基線仍 **8,602**，待350–354真實聯合full收口，不由新增測試推估總數。

## 已套用實作與限制

| 組別 | 最終候選行為 | 保留限制 |
|---|---|---|
| 02 Baseline／Fleet來源失敗，3筆 | GUI把History.Query作為Func交給service worker，讓query／projection失敗有正式邊界；舊BaselineFacts替換為三項ReadError，清空值／numeric，保留安全例外類型。Fleet獨立取得，失敗整份替換為summary ReadError，不留舊peer；成功不可變facts保留時間／unit／敏感資訊。 | 不為兩來源建立原子狀態或generation，並發晚到仍可能覆寫，其他100+來源未修。合作checkpoint不能中斷History.Query／EventLog／Fleet檔案I/O。CLI沒有GUI History階段，進階停用路徑不擴張。 |
| 04 DiskBench完整讀取，2筆 | sequential 1MiB及random4KiB均用正式ReadFullBlock，實際長度必須等於requested；短／零讀在計入byte／operation／latency前拋IOException，整個RunAll結果不發布，不在未核對齊offset重試。初始／執行／結束文字如實揭露固定4096假設。 | 沒有真實短讀／NoBuffering／幾何probe；不驗內容integrity或校準。服務取消／故障後某些numeric label及舊Progress排序仍有限制；helper注入不是RunAll benchmark。 |
| 06 FlushDurability拒絕，2筆 | 最終route C在private default engine正式入口、free-space／plan／filesystem之前回NotRun及明確reopen所有權未核原因、空metrics；移除unsafe native write／flush／reopen實作及service按路徑清理。注入engine仍可測計算契約，await實際完成後處理late cancellation。 | 這是阻止未核能力，不是已完成真實flush durability。未以同handle或anchor偷偷替代完整close/reopen定義；恢復能力仍需所有權、all-handles-closed、分享及timing方法設計，非實體持久性認證。 |
| 07 SLC owned handle，4筆 | Direct UI固定檔名以CreateAtPath／CreateNew安全拒碰撞；DeepBench採GUID leaf。兩者共用RunOwned，pre/post acquisition及post-write cancellation，在同步write／flush真正結束後才dispose，無path delete或reserve-close-reopen。FileStream包同SafeFileHandle；成功完整返回才發布sample。 | FileStream.Dispose自身flush可拋錯覆寫原fault，這輪未做雙故障保存；cap可能小於4MiB尾量而overshoot，space為快照，volume未pin。方法保留不代表歷史time等同或快取大小／持久性校準完成。 |
| 09 Evidence頁儲存快照signed policy匯入，7筆 | 新增獨立trusted PEM／signed policy／saved snapshot依序選檔卡片，共用既有簽章／條件核心；新選擇立即清舊成功，single-flight忙碌至實際Task.Run完成，policy先驗再讀snapshot，讀後重核UTC期限。只顯示aggregate、實際schema／semantics／signature及公鑰fingerprint；snapshot讀取以單handle實際bytes強制16MiB上限。 | 最小saved-snapshot入口，不是live／自動合規或完整企業GUI流程，不新增zr.compliance facts／export。公鑰仍需組織信任通道，snapshot／on-screen report未簽署；remote filesystem可能卡住、沒有硬截止／取消。XAML well-formed不是WPF實機binding／visual／accessibility驗證。 |

## 重疊合併及來源界線

EvidenceLabService正式合併候選在`obj/audit/r353/integration/capacity03/`：以02來源失敗修正為base，只插入09的198 bytes importer comment／property／blank；merged353 hash **94e871aa9e72d03516f47f65de9bcdfc296fe7054dbeb7815b698f658254cebd**。獨立byte check確認移除插入即還原02 base，不含r354 WHEA gate。必須按manifest真前置核對，不直接覆蓋舊全檔。

02的成功／失敗來源與r351已凍結輸入分開：新失敗清當前來源，不能重標舊fact時間，也不修改先前Immutable snapshot。passed caller-token OCE重拋；未請求或不同token OCE視為來源故障。這是針對History／Fleet窄修，不是消除所有舊來源或timeout晚到。

09審查原先找到snapshot檔案成長時可能越過cap，最終候選已在實際read bound修正；null unit沿shared evaluator保留，不轉空字串。實際fixture若執行會產生小型檔案，不是沒有I/O；不觸發live hardware採集。正式企業說明的GUI gap須精確描述為此saved-snapshot最小入口，不能宣稱全企業驗收完成。

## 待核驗證帳

- 各manifest／review：`obj/audit/r353/staged/capacity02|04|06|07|09`及`reviews/capacity03|capacity05`，06以final route C review為準，舊preflight-only review不當最終結論。
- DiskBench API依本機.NET10 System.Runtime.xml的RandomAccess.Read實際bytes契約，未以實機短讀重現背書。SLC dependency路徑已修為`Services/OwnedTemporaryFile.cs`，測試Messages名稱修正以最終hash為準。
- 套用17 targets及84項定向已核；套用後補修、CLI／文件／生成器／HTML／實際UI操作及350–354全套各待root新證據，不沿用本次定向當作全套。各組候選中九／十等案例計畫不直接換算成分組通過數。
- 後續與350–354聯合驗證，原r351 full8731及Drift2不覆寫、不背書新改動；baseline保持8,602。
- 本輪未提交、上傳或發版，不新增候選完成數或硬體支援宣稱；r352定向42/42僅背書其自身7映射範圍，不覆蓋本輪。
