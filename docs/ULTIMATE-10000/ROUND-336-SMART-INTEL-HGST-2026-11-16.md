# 第 336 輪：SMART 型號限定與單位校正

> 日期 2026-11-16 · 範圍：U10-KP-0004～0006、0011～0012 · 無新硬體讀取

在同一張 `SmartAttributeKnowledge` 加 Intel/Solidigm 與 HGST 兩個品牌群。原六組共 140 列，新增 Intel/Solidigm 21 列、HGST 1 列，共 162 列。初稿曾報 180 列，其中 18 列只是 DEFAULT HDD 通用定義，沒有 HGST 具名型號證據，已撤回；不以數量門檻強迫補足。

## 真正生效的型號邊界

- 新增 `LookupForModel`，桌面／CLI 共用消費端經它取得名稱與 raw 語意。`Lookup(vendor, id)` 僅供參考表查詢，不是裝置適用證明。
- Intel 只接受上游完整型號規則的嚴格子集：730/DC S35x0/3610/3700、Intel/Solidigm S46x0/S45x0、Pro 5400s。NVMe、行銷名稱、311/320、未知尾碼與未驗證 OEM 均不套表；Windows `ATA Device`／`SCSI Disk Device`／`SATA Disk Device` 類型尾綴可剝除，真正 OEM 型號尾碼保留。
- Pro 5400s 的 249 才是 `NAND_Writes_1GiB`；234 熱節流格式與 243 不套到它。其他已收系列 241/242 為 32 MiB/count，不能先被舊通稱「LBA」截走。190 也依系列分外殼／磁碟溫度。
- Intel 170 不再混入 311/320 的 `Reserve_Block_Count`。226 明列 timed workload percent × 1024；234 保留 `raw24/raw32:04321`，不把打包欄位冒充次數。
- E8（十六進位）＝232（十進位），已核定為 `Available_Reservd_Space`；E7＝231 對本次已收系列保留 Unknown。Lenovo OEM 條目對 231 另有 SATA PHY 語意，不能泛稱為所有 Intel 的壽命指標。
- HGST 僅收具名 He6/He8/He10 完整型號條目明示的 22 `Helium_Level`。不推算液位、壓力或濃度；沒有實際屬性就不顯示。220/240 仍是上游 DEFAULT HDD 名稱，沒有型號佐證就不作 HGST 專屬命名。194/196/198 保留既有通稱，未新增廠商換算。
- 原始六位元組與 LE 整數照列，語意只附註，不自動換算成總寫入量或壽命。未核型號的衝突 ID 保留「廠商自訂」，不套錯單位。

## 證據與限制

上游來源固定於 smartmontools `drivedb.h` commit `a3aee76ed7f342efebd5f01631be1d79f7fdb780`，2026-11-16 取得。型號範例與 raw 格式按該提交核對，尚無這些盤的實機驗證；型號命中也不證明不同韌體永遠相同。未收 OEM、He12/HC 系列如實保留缺口。

Antigravity Gemini 3.8 Flash High 完成一次設計獨立審查。其 OEM／型號邊界建議已對照來源並加回歸；其餘未提供證據的韌體單位猜測不當成新缺陷或支援承諾。

## 驗證

- SMART 知識表、顯示接線、既有解碼器、知識清冊與嵌入 HTML 定向：70 項通過（型號尾碼補強前）；最終版本重新跑全套。
- 已在既有回歸方法補 NVMe、OEM、型號完整邊界、Pro5400s、氦氣／非氦氣、E7/E8、LBA／32 MiB 與 raw 數值保真斷言，未增加測試案例數。
- Round 336 二進位全套已取得 TRX：8,137 通過、2 失敗、0 略過。兩項均為 ReadmeGeneratorTests，原因是測試進行時 Round 337 修改了服務行數而附錄尚未重生；已重生附錄與嵌入 HTML。這次不列為全套通過，最新程式另跑最終全套。
- Round 339 合併驗證 SMART 新表／顯示接線、共用探針、韌體宣告、時鐘及磁碟回歸，107 項通過。最新全套結果見交接文檔。
