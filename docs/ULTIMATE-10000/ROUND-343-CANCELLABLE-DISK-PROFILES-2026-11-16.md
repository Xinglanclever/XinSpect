# Round 343：可取消唯讀磁碟分尺

日期：2026-11-16。接續 U10-TS-0185／0187，將既有表面掃描接成桌面與 CLI 共用的可取消緩衝唯讀通道。

## 變更

- `DiskSurfaceScanService.ScanProfilesAsync` 只接受 `\\.\X:` 本機固定卷，取得卷長度與可讀取的邏輯磁區尺寸；無法查到磁區尺寸時明示未知。每輪同一卷、同一個起始範圍，預設最多 64 MiB。
- 512 B、8 KB、64 KB、128 KB 四種塊尺可分開或全選；重讀為明確選項且預設關閉。重讀只報第二次實測，不推算快取容量。
- `RandomAccess.ReadAsync` 使用 buffered I/O 與取消 token。取消等待當前核心 I/O 取消／完成後才釋放 handle 與 buffer；不把同步 `ReadFile` 偽稱可中斷。短讀會補讀，I/O 失敗採每輪最多 10 次額外重試預算，耗盡後如實計錯。
- HealthView 顯示掃描範圍、磁區尺寸、I/O 類型與重試數，提供取消控制；CLI 新增 `surface C: [512|8192|65536|131072|all] [--repeat]`，Ctrl+C 取消。
- 不讀 PhysicalDrive，不作未限定全碟壓力測試；沒有新增事實鍵或宣稱 TS 候選收口。

## 驗證與限制

- 定向 `DiskSurfaceIntegrityTests`：19/19 通過，含重試恢復、重試耗盡、短讀、預先／途中取消、範圍完整性。
- `XinSpectCli.csproj` 建置成功，0 錯誤；13 個既有 unreachable-code 警告。
- 沒有在真實磁碟上跑讀取負載，尚缺 Win32 固定卷幾何與裝置行為的實機驗證；因此 TS-0185／0187 保持進行中，不能計為完成。
