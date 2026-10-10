# Vol2 批次 B（FS 檔案鑑識主線）第一～三波 — 2026-10-11 v2.57／v2.58／v2.59

> **圈選範圍**：`docs/INDUSTRIAL-CATALOG-VOL2-2026-10-11.md` 建議批次 **B（檔案鑑識主線）** 的第一波：**FS-001（Shimcache）＋FS-009（MountedDevices）＋FS-013（隱私禁項機制化）**。
> **第二波（v2.58）**：**FS-003（Prefetch 檔名層）**——MAM 壓縮容器不解壓，檔名＋寫入時間誠實上線。
> **第三波（v2.59）**：**FS-006（$MFT 統計）＋FS-014（USN 日誌狀態）**——卷冊層兩個唯讀 FSCTL，常數與結構對準 winioctl.h。
> **一句話**：執行痕跡與掛載歷史以金標解碼上線，禁項（Jump lists／MRU／縮圖）以機器釘死「不讀」。
> **本輪沒有紅燈**；實機（Server 2025）交叉兩個新來源全部通過。

---

## 1. 交付一覽

| 項 | 交付 | 事實鍵 |
|---|---|---|
| FS-001 | Shimcache（AppCompatCache）登錄值解碼：Win7 x64／Win10 家族兩版面，金標 fixtures 逐欄比對 | `fs.shim.count`、`fs.shim.latest`、`fs.shim.sample` |
| FS-009 | MountedDevices 掛載對照：碟號指派（簽章＋分割區位移）與磁碟區 GUID 路徑 | `fs.mount.count`、`fs.mount.letters`、`fs.mount.volumes` |
| FS-013 | 隱私禁項機制化：禁項表＋掃描器（正對照咬人）＋正式碼全庫零命中（負對照） | 守門測試，無事實鍵 |
| FS-003 | Prefetch 檔名層：目錄列舉＋80 位元組檔頭判格式（MAM 不解壓／SCCA 五版本） | `fs.pf.count`、`fs.pf.formats`、`fs.pf.latest`、`fs.pf.sample` |
| FS-006 | NTFS 卷冊統計：MFT 有效長度／記錄大小／記錄數估算（長÷大小）／叢使用量（FSCTL_GET_NTFS_VOLUME_DATA） | `fs.mft.stat` |
| FS-014 | USN 日誌狀態：啟用與否／跨度／上限／配置增量（FSCTL_QUERY_USN_JOURNAL；1179 未啟用如實標不適用） | `fs.usn.state` |

## 2. 版面出處（這是本輪最重要的部分）

- **Win7 x64**：`EricZimmerman/AppCompatCacheParser` 的 `AppCompatCache/Windows7.cs` x64 分支逐欄——PathSize(2)+MaxPathSize(2)+pad(4)+PathOffset(8，值內絕對位移)+FILETIME(8)+InsertFlags(4，bit1=Executed)+ShimFlags(4)+CacheEntryDataSize(8)+DataOffset(8)；路徑 UTF-16 於 PathOffset。2026-10-11 抓取原檔核對。
- **Win10 家族**：同 repo `Windows10.cs`——值首 4 位元組＝記錄區位移（0x34）、申報數在 0x28；條目＝Sig"10ts"(4)+unknown(4)+CacheEntryDataSize(4)+PathSize(2)+Path+FILETIME(8，1601 視為無時間)+DataSize(4)+Data（尾端 4 位元組==1 即留有執行旗標）。
- **Win8/8.1（00ts／10ts 含 package 段）刻意不實作**：版面未逐欄核對前不出值——格式辨認得出（條目簽名在 0x80 表頭處），回 NotSupported 並寫明「未對準出處，不猜欄位」。這是本專案「未對準出處就不出值」鐵律的應用，不是待辦遺漏。

## 3. 設計取捨

1. **條目明細串在彙總值裡，零動態家族**（沿用 v2.56 §2.1 的先例）：`fs.shim.sample` 收最新 10 條（時間｜路徑｜旗標），其餘以「等 N 條」收束。`fs.shim.sample` 標**敏感**——路徑可能含使用者名，匯出預設遮蔽。
2. **申報與解析數不一致如實並列**：本機實測表頭申報 1734、實際解析 1024——值在系統端被截斷或尾段未寫；落差寫進值裡，不冒充完整。
3. **執行痕跡≠執行成功**：條目由系統維護可能殘留；時間是**檔案**最後修改時間不是執行時間；只讀 CurrentControlSet（歷史 ControlSet002 不在自動收集內）——三句界線分別寫進 count／latest／sample 的值裡。
4. **MountedDevices 是歷史對照不是在線清單**：拔掉的裝置條目會留下；簽章與位移是分割區層事實，不解讀成裝置型號或健康狀態；磁碟區目標原樣解碼 UTF-16，Windows 的跳脫名（底線）不代譯。
5. **L2 讀檔來源不進啟動路徑**：Prefetch／Amcache 要開檔解析（Amcache 要 NT hive 解析器），屬明示觸發範圍；啟動路徑只收登錄值級（毫秒級）的 Shimcache 與 MountedDevices。

## 4. 逐檔改動

| 檔案 | 內容 |
|---|---|
| `XinSpect.Decoders/AppCompatCache.cs` | 純解碼器＋SpecRef（出處逐欄）；已註冊 SpecRefRegistry 並同步 DecoderHardeningTests 期望陣列 |
| `Services/ShimcacheFactsService.cs` | 登錄讀取（probe 注入）＋三態（值缺席＝ReadError 帶「不冒充沒有執行痕跡」） |
| `Services/MountedDevicesFactsService.cs` | 機碼缺席＝NotApplicable、讀取失敗＝ReadError、碟號／磁碟區解碼 |
| `Services/EvidenceLabService.cs`、`Services/EvidenceCollection.cs` | `LoadFsForensics` 共用入口（UI／CLI 同源）＋AllFacts |
| `Services/FactKeyCatalog.cs` | ＋6 把固定鍵（221 → 227） |
| `Tests/AppCompatCacheDecoderTests.cs` 等 4 檔 | 金標逐欄＋三態契約＋禁項守門，＋29 條 |

## 5. 驗證（可重跑）

```bash
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~AppCompatCacheDecoderTests|FullyQualifiedName~ShimcacheFactsTests|FullyQualifiedName~MountedDevicesFactsTests|FullyQualifiedName~ForensicBanGateTests"
#  → 已通過! - 失敗: 0，通過: 29，總計: 29
./bin/Debug/net10.0-windows/win-x64/XinSpect.exe --json evidence --query fs.
#  → fs.shim：Win10/Server2016+・1024 條（申報 1734）；fs.mount：7 碟號＋3 磁碟區（含 USBSTOR）——allPresent
```

## 6. 界線（本輪沒有做的事）

1. **FS-002 Amcache**：需要 NT registry hive 離線解析器（檔案被系統鎖定、RegLoadKey 屬載入動作不做）——延後到獨立版本。
3. **FS-012 鑑識匯總時間線**：等 FS 來源 ≥3 個再合併（本輪只有 2 個）。
4. **韌體安全頁的新分類「檔案鑑識」沒有目視核對**（與 v2.56 同一條界線）：分組排序由既有 CollectionViewSource 機制處理，但人眼確認仍欠。
5. **FS-005（USN 內容讀回）未做**：本輪只報日誌的存在性與規模；`FSCTL_ENUM_USN_DATA` 大量資料分頁化與篩選屬獨立版本。
6. **只掃系統碟**：多卷冊（D:／E:…）屬明示觸發；卷序號刻意不列（機器識別資訊）。
7. **`fs.mft.stat` 的 96 位元組輸出未在 x86 或舊版驗證**：本機（Server 2025 x64）實測通過；其他平台需第二台機器交叉（EG-001 精神）。
