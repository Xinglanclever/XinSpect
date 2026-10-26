using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>裝置下拉選單的一列。</summary>
public sealed class SmartDriveRow
{
    public SmartDriveRow(int index, string label) { Index = index; Label = label; }
    public int Index { get; }
    public string Label { get; }

    /// <summary>下拉項目的自動化名稱取自 ToString（DisplayMemberPath 只影響畫面），沒有這行會唸成型別名稱。</summary>
    public override string ToString() => Label;
}

/// <summary>一列 S.M.A.R.T. 資料：NVMe 時填 Name／ValueText，SATA 時四欄都填。</summary>
public sealed class SmartRow
{
    public SmartRow(string name, string valueText, string worstText, string rawText,
                    byte id = 0, ulong? rawValue = null)
    { Name = name; ValueText = valueText; WorstText = worstText; RawText = rawText; Id = id; RawValue = rawValue; }
    public string Name { get; }
    public string ValueText { get; }
    public string WorstText { get; }
    public string RawText { get; }

    /// <summary>SATA 屬性編號（9＝通電時間、12＝電源循環…）；NVMe 與分隔列為 0。</summary>
    public byte Id { get; }

    /// <summary>原始值的小端整數；沒有數值意義時為 <c>null</c>——**不要用 0 代替**。</summary>
    public ulong? RawValue { get; }
}

/// <summary>
/// S.M.A.R.T. 原始資料直讀：NVMe 走 <c>IOCTL_STORAGE_QUERY_PROPERTY</c>（StorageDeviceProtocolSpecificProperty，
/// log page 0x02 健康紀錄）；SATA 走經典的 <c>SMART_RCV_DRIVE_DATA</c>（disk.sys 代理，久經驗證的路線）。
/// </summary>
/// <remarks>
/// 誠實界線：SATA 屬性的「原始值」一律以六位元組原樣列出（十六進位＋小端整數），<b>不套任何廠商公式</b>
/// ——不同廠商對同一 ID 的 raw 定義不同，換算就是猜。
/// 安全界線：<b>刻意不使用原始 ATA_PASS-THROUGH</b>——實機測試發現特定驅動會無視逾時直接卡死 IRP，
/// 監控工具把使用者磁碟弄掛是不可接受的；SMART_RCV_DRIVE_DATA 由磁碟類別驅動代理，無此風險。
/// 匯流排預檢（StorageDeviceProperty 的 BusType）不通過者直接顯示不支援，不送可能卡住的命令。
/// </remarks>
public sealed class StorageSmartService : ObservableObject
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode, byte[]? inBuffer, uint inSize,
        byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr device, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr device, IntPtr overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    private static extern void ZeroMemory(IntPtr destination, UIntPtr length);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint ErrorIOPending = 997;
    private const uint Infinite = 0xFFFFFFFF;

    /// <summary>OVERLAPPED 結構在 64 位元的大小；拿來配置與歸零，不靠 <c>Marshal.SizeOf</c> 猜。</summary>
    private const int OverlappedSize = 32;

    /// <summary>NVMe 紀錄頁查詢的等待上限；逾時就真的取消 I/O。</summary>
    private const uint NvmeLogTimeoutMs = 3000;

    /// <summary>
    /// NVMe 紀錄頁查詢<b>外層</b>上限：內層 <see cref="NvmeLogTimeoutMs"/> 只有在呼叫真的走到那次等待時
    /// 才擋得住；實測（2026-10-18）本機 <c>PhysicalDrive 2／4</c> 卡在<b>開檔或 IOCTL 本身</b>，
    /// 呼叫執行緒永遠拿不回控制權。預算＝內層等待 3.0 秒＋取消後 2.5 秒＋餘裕。
    /// </summary>
    private const int NvmeOuterBudgetMs = 6000;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlSmartRcvDriveData = 0x7C82C;   // CTL_CODE(IOCTL_DISK_BASE, 0x220B, METHOD_BUFFERED, FILE_READ_ACCESS)
    private const uint PropertyIdDeviceProperty = 0;
    private const uint PropertyIdProtocolSpecificDevice = 51;
    private const uint ProtocolTypeNvme = 3;
    private const uint NvmeDataTypeLogPage = 2;
    public const uint NvmeLogPageHealth = 0x02;
    public const uint NvmeLogPageError = 0x01;   // 錯誤資訊紀錄
    /// <summary>
    /// 韌體槽資訊紀錄（Firmware Slot Information）。出處：Linux <c>include/linux/nvme.h</c> 的
    /// <c>NVME_LOG_FW_SLOT = 0x03</c> 與 <c>struct nvme_fw_slot_info_log</c>（<c>afi</c> ＋ <c>rsvd1[7]</c>
    /// ＋ <c>frs[7]</c>）；libnvme <c>src/nvme/types.h</c> 的 <c>struct nvme_firmware_slot</c> 寫成
    /// <c>char frs[7][8]</c>，兩份都指向 512 位元組、七個槽、每槽八個 ASCII 字元。
    /// <b>唯一使用它的地方是 P2-098 <c>pdev.fw.updatable</c></b>（見 <see cref="NvmeFwSlot"/>）。
    /// </summary>
    public const uint NvmeLogPageFirmwareSlot = 0x03;

    private bool _busy;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRead)); } }
    public bool CanRead => !_busy;

    private string _status = "選擇實體磁碟後按「讀取」。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _kindText = "—";
    /// <summary>資料來源類別（NVMe 健康紀錄／ATA S.M.A.R.T.），誠實標明量的路徑。</summary>
    public string KindText { get => _kindText; private set => SetProperty(ref _kindText, value); }

    public ObservableCollection<SmartDriveRow> Drives { get; } = [];

    public ObservableCollection<SmartRow> Rows { get; } = [];

    /// <summary>
    /// 直讀到裝置自己回報的序號時發出（磁碟編號, 序號）。
    /// WMI 給的序號是 Windows 重組過的十六進位，與裝置自己說的常常不一樣；有真值就該用真值。
    /// </summary>
    public event Action<int, string>? SerialResolved;

    public StorageSmartService() => _ = RefreshDrivesAsync();

    /// <summary>重新列舉實體磁碟（Win32_DiskDrive：Index／型號／容量）。</summary>
    public async Task RefreshDrivesAsync()
    {
        try
        {
            var (list, models) = await Task.Run(() =>
            {
                var result = new List<SmartDriveRow>();
                var driveModels = new Dictionary<int, string>();
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Index, Model, Size FROM Win32_DiskDrive");
                foreach (var d in searcher.Get())
                {
                    var idx = Convert.ToInt32(d["Index"]);
                    var model = d["Model"]?.ToString() ?? "—";
                    var size = d["Size"] is null ? 0 : Convert.ToInt64(d["Size"]);
                    result.Add(new SmartDriveRow(idx, $"磁碟 {idx}：{model}（{size / 1024.0 / 1024 / 1024:0.#} GB）"));
                    driveModels[idx] = model;
                }
                return (result.OrderBy(r => r.Index).ToList(), (IReadOnlyDictionary<int, string>)driveModels);
            });
            _driveModels = models;
            Drives.Clear();
            foreach (var r in list) Drives.Add(r);
        }
        catch (Exception ex)
        {
            Status = "無法列舉實體磁碟：" + ex.Message;
        }
    }

    /// <summary>磁碟編號→型號（Win32_DiskDrive.Model，列舉時一併記下；只讀快照，列舉後整體換替）。</summary>
    private IReadOnlyDictionary<int, string> _driveModels = new Dictionary<int, string>();

    /// <summary>查詢未回應（逾時未完成）的磁碟：本次執行不再嘗試，避免每次都留下一個卡死的執行緒。</summary>
    private readonly HashSet<int> _noResponse = new();

    /// <summary>
    /// 依下拉選單目前的索引讀取。選單以 XAML 字面值指定 SelectedIndex 時，會在 ItemsSource
    /// （繫結、且 DataContext 由外殼延遲繼承）還空著時被強制成 -1 且不會自己回到 0——
    /// 舊版就是因此按下「讀取」完全沒反應。索引無效時退回第一顆，清單為空時如實回報並重新列舉。
    /// </summary>
    /// <returns>實際使用的選單索引；清單為空時回傳 -1。</returns>
    public int ReadSelected(int uiIndex)
    {
        if (Drives.Count == 0)
        {
            Status = "尚未列舉到實體磁碟（Win32_DiskDrive 無結果）；正在重新列舉，稍候再按一次。";
            _ = RefreshDrivesAsync();
            return -1;
        }
        int i = uiIndex >= 0 && uiIndex < Drives.Count ? uiIndex : 0;
        Read(Drives[i].Index);
        return i;
    }

    /// <summary>讀取指定實體磁碟的 SMART：先判匯流排，NVMe 走協定查詢、SATA/ATA 走 SMART_RCV_DRIVE_DATA。</summary>
    public void Read(int physicalDriveIndex)
    {
        if (IsBusy) return;
        _ = ReadAsync(physicalDriveIndex);
    }

    private async Task ReadAsync(int index)
    {
        IsBusy = true;
        Rows.Clear();
        if (_noResponse.Contains(index))
        {
            KindText = "—";
            Status = "此磁碟先前查詢未回應（已放棄）。直讀在此儲存堆疊上不支援，不影響磁碟本身；這不是磁碟沒有 SMART。";
            IsBusy = false;
            return;
        }
        Status = $"讀取 \\\\.\\PHYSICALDRIVE{index} …";
        try
        {
            var task = Task.Run(() =>
            {
                var bus = TryGetBusType(index, out string busName);
                // 通道判定問 ChannelOf（唯一一份）；分群與原本寫死的 16/17、3/10/11 逐字相同
                return ChannelOf(bus) switch
                {
                    StorageBusChannel.Nvme => TryReadNvmeLog(index) is { } nvme
                        ? ("NVMe 健康紀錄（log page 0x02，DeviceIoControl 直讀；匯流排 " + busName + "）", DecodeNvmeHealth(nvme)
                           .Concat(new[] { new SmartRow("── 硬體識別 ──", "", "", "") })
                           .Concat(TryReadNvmeIdentify(index) is { } nvi ? DecodeNvmeIdentify(nvi) : new[] { new SmartRow("NVMe Identify Controller", "不支援（儲存堆疊未回應協定查詢）", "", "") })
                           .Concat(new[] { new SmartRow("── 錯誤紀錄 ──", "", "", "") })
                           // 「這顆碟開始要壞了」最早的證據在 log page 0x01；讀不到就說讀不到，不猜
                           .Concat(TryReadNvmeLog(index, NvmeLogPageError) is { } errs
                               ? DecodeNvmeErrorLog(errs)
                               : new[] { new SmartRow("錯誤資訊紀錄（log page 0x01）", "讀不到（儲存堆疊未回應這一頁的協定查詢；USB 外接盒多半不轉發 NVMe 命令）", "", "") })
                           .ToList())
                        : throw new InvalidOperationException("NVMe 裝置的健康紀錄讀取失敗（儲存堆疊未回應協定查詢）。"),
                    StorageBusChannel.Ata => TryReadAtaSmartClassic(index) is { } ata
                        ? ("ATA S.M.A.R.T. 屬性（SMART_RCV_DRIVE_DATA，原始欄位照列；匯流排 " + busName + "）",
                           DecodeAtaAttributes(ata, _driveModels.TryGetValue(index, out var driveModel) ? driveModel : null)
                           .Concat(new[] { new SmartRow("── 硬體識別 ──", "", "", "") })
                           .Concat(TryReadAtaIdentify(index) is { } ati ? DecodeAtaIdentify(ati) : new[] { new SmartRow("ATA IDENTIFY DEVICE", "不支援（驅動拒絕命令）", "", "") })
                           .ToList())
                        : throw new InvalidOperationException(
                            "此磁碟不支援直讀（驅動拒絕 SMART_RCV_DRIVE_DATA）——" +
                            "這是此儲存控制器不開放此命令，不代表磁碟本身有問題。" +
                            "需要時可用磁碟原廠工具（如 Kingston SSD Manager）查看 SMART。"),
                    _ => throw new InvalidOperationException($"此匯流排（{busName}）不支援 SMART 直讀；不送可能卡住的原始命令。"),
                };
            }, CancellationToken.None);

            // 斷路器：部分儲存堆疊（實測：本機 Server 2025 + Intel NVMe）對協定查詢不回應且無視逾時。
            // 15 秒未完成就放棄並標記——UI 不能陪它卡死；卡住的執行緒由系統收留（每次每磁碟至多一條）。
            var done = await Task.WhenAny(task, Task.Delay(15000));
            if (done != task)
            {
                _noResponse.Add(index);
                KindText = "—";
                Status = "直讀查詢未回應（15 秒），已放棄並標記此磁碟。此儲存堆疊不支援直讀；這不是磁碟沒有 SMART，需要時請用磁碟原廠工具。";
                return;
            }

            // 已完成，await 不會等待；用 await 而非 Result 是為了讓失敗訊息保持原句——
            // Result 會包成 AggregateException，畫面上就會多出一句「One or more errors occurred.」
            var (kind, rows) = await task;
            KindText = kind;
            foreach (var r in rows) Rows.Add(r);
            Status = "讀取完成。";
            // 識別資料裡有裝置自己回報的序號就往外送，讓儲存卡片改用真值（沒讀就不主動發 IOCTL）
            if (SerialFromRows(rows) is { } serial) SerialResolved?.Invoke(index, serial);
        }
        catch (Exception ex)
        {
            KindText = "—";
            Status = "讀取失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 共用：開啟實體磁碟與匯流排偵測 ─────────────────────────────────────

    private static IntPtr OpenDrive(int index)
    {
        var h = CreateFile($@"\\.\PHYSICALDRIVE{index}", GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return h == new IntPtr(-1) ? IntPtr.Zero : h;
    }

    // ── 共用：受看門狗保護的協定查詢（外加斷路器） ────────────────────────

    /// <summary>
    /// 受看門狗保護的協定查詢，外加「問過不回來就別再問」的斷路器。
    /// </summary>
    /// <remarks>
    /// 回傳值與直接 <see cref="DiskIo.Guarded{T}"/> 完全相同——跳過時回 <c>null</c>，而逾時本來也是回
    /// <c>null</c>，呼叫端拿到的判定逐字不變。省下的是<b>重複等待</b>與<b>每輪都要再洩一條卡死執行緒</b>。
    /// <paramref name="op"/> 是鍵的一半，必須分辨「哪一條查詢」：同一顆碟的匯流排型別讀得到
    /// （毫秒級）而 NVMe 健康紀錄逾時，是兩件不同的事，混用同一個鍵會把好的那條也一起放棄。
    /// 背景與取捨寫在 <see cref="StorageProbeBreaker"/>。
    /// </remarks>
    private static T? GuardedRead<T>(int index, string op, Func<T?> read) where T : class
    {
        if (StorageProbeBreaker.ShouldSkip(index, op)) { StorageProbeBreaker.CountSkip(); return null; }
        var (value, timedOut) = DiskIo.GuardedProbe(read);
        if (timedOut) StorageProbeBreaker.Trip(index, op);
        return value;
    }

    /// <summary><see cref="GuardedRead{T}"/> 的實值型別版本；語意相同。</summary>
    private static T? GuardedReadValue<T>(int index, string op, Func<T?> read) where T : struct
    {
        if (StorageProbeBreaker.ShouldSkip(index, op)) { StorageProbeBreaker.CountSkip(); return null; }
        var (value, timedOut) = DiskIo.GuardedProbeValue(read);
        if (timedOut) StorageProbeBreaker.Trip(index, op);
        return value;
    }

    /// <summary>
    /// 匯流排分出來的<b>通道</b>：決定「用哪一套命令集問這顆裝置」。
    /// 本專案只有兩條可引用的直讀路徑（NVMe 協定查詢、ATA 命令集），其餘一律 <see cref="None"/>。
    /// </summary>
    public enum StorageBusChannel
    {
        /// <summary>沒有對應的唯讀查詢路徑（USB 外接盒、RAID 虛擬碟、虛擬磁碟、未知）。</summary>
        None,
        /// <summary>NVMe 協定查詢（IOCTL_STORAGE_QUERY_PROPERTY 加上 ProtocolType = 3）。</summary>
        Nvme,
        /// <summary>ATA 命令集（SMART_RCV_DRIVE_DATA／IDENTIFY DEVICE）；SAS／SATA 也走這條。</summary>
        Ata,
    }

    /// <summary>
    /// STORAGE_BUS_TYPE 的數值→通道，<b>全專案唯一一份</b>（SMART 直讀與 P2-098 可更新性都問這裡）。
    ///
    /// <para>
    /// 分群沿用本專案一向的 16／17→NVMe、3／10／11→ATA，理由逐項寫清楚：
    /// 17＝BusTypeNvme 走 NVMe 協定；16＝BusTypeSpaces（Storage Spaces 的虛擬層）在 Windows 的
    /// 實作上後端仍是 NVMe，所以照原本一樣先試協定查詢——試不到就是讀不到，不會憑空生成結論。
    /// 3＝BusTypeAta、10＝BusTypeSas、11＝BusTypeSata 三者都用 ATA 命令集（SAS 只是把 ATA 封裝進去），
    /// 走同一條直讀。
    /// </para>
    ///
    /// <para>
    /// <b>出處：</b>本機 Windows SDK 的 <c>um/winioctl.h</c>（10.0.28000.0）
    /// 的 <c>typedef enum _STORAGE_BUS_TYPE</c>，值由 0 起逐項加一。
    /// 2026-10-19 以本機實測對照過：CIM 的 <c>MSFT_PhysicalDisk.BusType</c> 對同一批碟給出
    /// NVMe＝17、SATA＝11，與該列舉一致（見 <c>GATE-LOG-266</c>）。
    /// </para>
    /// </summary>
    public static StorageBusChannel ChannelOf(uint bus) => bus switch
    {
        16 or 17 => StorageBusChannel.Nvme,
        3 or 10 or 11 => StorageBusChannel.Ata,
        _ => StorageBusChannel.None,
    };

    /// <summary>
    /// STORAGE_BUS_TYPE 的數值→名稱，<b>全專案唯一一份</b>。
    ///
    /// <para>
    /// <b>這一輪修掉的缺陷（2026-10-19）：</b>原本專案裡有<b>兩份</b>這種對照表
    /// （這裡的 ReadBusTypeCore 與 <see cref="DiskInfoService"/> 的 BusZh），
    /// 兩份都從列舉中段開始<b>位移一格</b>，而且彼此不一致：同一顆碟在兩個畫面可以標成不同匯流排
    /// （例：17 在這裡被寫成「SCM/NVMe 類」；DiskInfoService 把 12 寫成 SAS、13 寫成 SD）。
    /// 逐項核對 SDK 列舉後更正，並讓 <see cref="DiskInfoService"/> 改呼叫這裡——
    /// <b>兩份表遲早會再漂開一次，收斂成一份才是修法</b>（論述見 <c>GATE-LOG-266</c>）。
    /// </para>
    /// </summary>
    public static string BusTypeName(uint bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        14 => "Virtual（虛擬磁碟）", 15 => "FileBackedVirtual（檔案後端虛擬磁碟）",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", 20 => "NVMe over Fabrics",
        _ => bus > 0 ? $"BusType {bus}（不在已知列舉內）" : "未知",
    };

    /// <summary>StorageDeviceProperty 的 BusType（STORAGE_DEVICE_DESCRIPTOR 的位元組 @28）：
    /// 通道判定見 <see cref="ChannelOf"/>、名稱見 <see cref="BusTypeName"/>，兩者都不在這裡寫第二份。失敗回 0。</summary>
    /// <remarks>走 <see cref="DiskIo.GuardedValue"/>：某些碟的 IOCTL 會卡在核心裡不回來，卡住就當讀不到。</remarks>
    public static uint TryGetBusType(int index, out string busName)
    {
        var got = GuardedReadValue(index, nameof(TryGetBusType), () => ReadBusTypeCore(index));
        busName = got?.Name ?? "未知";
        return got?.Bus ?? 0;
    }

    private static (uint Bus, string Name)? ReadBusTypeCore(int index)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buf = new byte[512];
            BitConverter.GetBytes(PropertyIdDeviceProperty).CopyTo(buf, 0);   // StorageDeviceProperty
            BitConverter.GetBytes(0u).CopyTo(buf, 4);                         // PropertyStandardQuery
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, IntPtr.Zero)
                || ret < 32)
                return null;
            byte bus = buf[28];   // BusType 是一個位元組（offset 24 是 SerialNumberOffset，別讀錯）
            // 名稱一律問 BusTypeName（唯一一份，逐項對準 SDK 的 STORAGE_BUS_TYPE）
            return (bus, BusTypeName(bus));
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// 以 <b>overlapped</b> 模式開啟 <c>\\.\PHYSICALDRIVE{index}</c>：這是磁碟 I/O 唯一能真的取消的路。
    /// </summary>
    /// <remarks>
    /// 原本用 <c>OpenExisting</c> 同步開啟，配合 <see cref="DiskIo.Guarded{T}"/> 的「逾時就不等它」策略。
    /// 2026-10-14 傾印（<c>work/hangdiag.ps1</c>）顯示那個策略有洞：<c>Task.Wait(timeout)</c> 逾時後，
    /// 執行緒其實卡在 <c>DeviceIoControl</c> 的核心不可中斷等待，<b>永遠不釋放</b>——每讀一顆逾時的碟就洩一條，
    /// 一路把執行緒集區吃光，最後整個行程沒有一條執行緒能繼續，全套測試連結果都印不出來。
    /// 逾時放棄只是「呼叫端不等」，不是「I/O 結束」。
    /// </remarks>
    private static IntPtr OpenDriveOverlapped(int index)
    {
        var h = CreateFile($@"\\.\PHYSICALDRIVE{index}", GenericRead, ShareReadWrite, IntPtr.Zero,
            OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        return h == new IntPtr(-1) ? IntPtr.Zero : h;
    }

    /// <summary>同步的 <c>DeviceIoControl</c>；呼叫端負責確保 <paramref name="overlapped"/> 不為 0。</summary>
    [DllImport("kernel32.dll", EntryPoint = nameof(DeviceIoControl), SetLastError = true)]
    private static extern bool DeviceIoControlSync(IntPtr device, uint ioControlCode, byte[]? inBuffer, uint inSize,
        byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    // ── NVMe ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 以 StorageDeviceProtocolSpecificProperty 取指定 NVMe 紀錄頁 512 位元組；失敗或逾時回 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>兩層防護，缺一不可</b>：內層是 <see cref="ReadNvmeLogCore"/> 的 overlapped I/O——
    /// <c>WaitForSingleObject</c> 逾時後真的 <c>CancelIoEx</c>；沒讀到再由
    /// <see cref="StorageProbeBreaker.Report"/> 記帳。
    /// </para>
    /// <para>
    /// <b>外層上限是 2026-10-18 補上的，因為內層的前提被實測推翻。</b>內層能取消的前提是
    /// 「呼叫會走到那次等待」；本機實測（逐顆磁碟計時，見 <c>Tests/NvmeOverlappedIoTests</c>）
    /// <c>PhysicalDrive 2／4</c> 的呼叫做不到那一步——它卡在<b>開檔或 IOCTL 本身</b>，
    /// 於是 <c>CancelIoEx</c> 根本沒機會送出，呼叫執行緒永遠不回來。後果不是「這一項讀不到」，
    /// 而是整條收集鏈卡死：headless <c>--json</c> 跑 35 分鐘沒有輸出、行程連
    /// <c>taskkill /F</c> 都殺不掉（停留在核心不可中斷等待）。
    /// </para>
    /// <para>
    /// <b>為什麼可以直接洩執行緒（2026-10-14 的禁令在這裡有條件解禁）</b>：那條禁令的成因是
    /// 「同一條查詢被反覆呼叫，每輪多洩一條，把執行緒集區吃光」。現在兩個條件都不成立——
    /// ① 逾時會 <see cref="StorageProbeBreaker.Trip"/>，冷卻期間整條查詢直接跳過，
    /// 一顆碟一條查詢一個冷卻期內最多洩一條；② 這裡用<b>專屬執行緒</b>而不是執行緒集區，
    /// 就算真的洩掉，也不會餓死集區（那正是 2026-10-14 傾印裡整份行程連結果都印不出來的原因）。
    /// 交換的是「一條背景執行緒永遠不回來」換「呼叫端一定拿得回控制權」——後者才是
    /// 「讀不到」能被誠實回報的前提。
    /// </para>
    /// <para>
    /// <b>取消是「請求」不是「保證」</b>：<c>CancelIoEx</c> 會立即讓呼叫端拿回控制權，但底層 IRP
    /// 仍可能被卡在驅動的取消常式裡。
    /// </para>
    /// </remarks>
    public static byte[]? TryReadNvmeLog(int index, uint logPage = NvmeLogPageHealth)
    {
        string op = $"{nameof(TryReadNvmeLog)}.{logPage}";
        if (StorageProbeBreaker.ShouldSkip(index, op)) { StorageProbeBreaker.CountSkip(); return null; }

        var probe = BoundedProbeValue(() => ReadNvmeLogCore(index, logPage), NvmeOuterBudgetMs);
        byte[]? log = null;
        bool timedOut = true;   // 外層逾時時 probe.Value 為 null，這裡的初值就是那個情況的答案
        if (probe.Value is { } inner) { log = inner.Log; timedOut = inner.TimedOut || probe.TimedOut; }
        StorageProbeBreaker.Report(index, op, log is not null && !timedOut);
        return log;
    }

    /// <summary>
    /// 在<b>專屬</b>執行緒上跑 <paramref name="read"/>，最多等 <paramref name="budgetMs"/>；
    /// 逾時就把那條執行緒留在原地（中止不了，理由見 <see cref="NvmeOuterBudgetMs"/>）。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>Task.Run</c>／<see cref="DiskIo.GuardedProbe{T}"/>：那裡借的是執行緒集區，
    /// 而集區被卡死的查詢吃光時，整個行程連一行輸出都印不出來（2026-10-14 傾印）。
    /// 逾時回 <c>TimedOut=true</c>；讀取自己拋例外回讀不到但不標逾時——
    /// 「裝置回一句不支援」與「裝置不回來」是兩件事，把它們混在一起會讓斷路器放棄本來讀得到的地方。
    /// </remarks>
    private static (T? Value, bool TimedOut) BoundedProbeValue<T>(Func<T> read, int budgetMs) where T : struct
    {
        T result = default;
        var t = new System.Threading.Thread(() =>
        {
            try { result = read(); }
            catch { /* 讀取自己拋例外：不是逾時，由呼叫端記成讀不到 */ }
        })
        { IsBackground = true, Name = "XinSpect-nvme-probe" };
        t.Start();
        return t.Join(budgetMs) ? (result, false) : (null, true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    private static (byte[]? Log, bool TimedOut) ReadNvmeLogCore(int index, uint logPage)
    {
        var handle = OpenDriveOverlapped(index);
        if (handle == IntPtr.Zero) return (null, false);

        // 事件與重疊結構手動配置：這條路徑沒有 SafeHandle，出事時靠 finally 逐一釋放。
        IntPtr evt = IntPtr.Zero, ov = IntPtr.Zero;
        try
        {
            evt = CreateEvent(IntPtr.Zero, true, false, null);
            if (evt == IntPtr.Zero) return (null, false);
            ov = Marshal.AllocHGlobal(OverlappedSize);
            ZeroMemory(ov, OverlappedSize);
            Marshal.WriteIntPtr(ov, IntPtr.Size, evt);   // OVERLAPPED.hEvent 在 64 位元是 offset 8

            const int header = 48;   // STORAGE_PROPERTY_QUERY(8) + STORAGE_PROTOCOL_SPECIFIC_DATA(40)
            var buf = new byte[header + 512];
            BitConverter.GetBytes(PropertyIdProtocolSpecificDevice).CopyTo(buf, 0);   // PropertyId
            BitConverter.GetBytes(0u).CopyTo(buf, 4);                                 // QueryType = PropertyStandardQuery
            BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buf, 8);                   // ProtocolType
            BitConverter.GetBytes(NvmeDataTypeLogPage).CopyTo(buf, 12);               // DataRequested
            BitConverter.GetBytes(logPage).CopyTo(buf, 16);                            // RequestValue（0x02 健康／0x01 錯誤）
            BitConverter.GetBytes(0u).CopyTo(buf, 20);                                // RequestSubValue
            BitConverter.GetBytes((uint)header).CopyTo(buf, 24);                      // ProtocolDataOffset（自緩衝區起算）
            BitConverter.GetBytes(512u).CopyTo(buf, 28);                              // ProtocolDataLength

            // METHOD_BUFFERED：輸入與輸出共用同一緩衝區，命令頭必須以輸入傳入才會到達驅動
            bool ok = DeviceIoControlSync(handle, IoctlStorageQueryProperty, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, ov);
            if (!ok)
            {
                uint err = (uint)Marshal.GetLastWin32Error();
                if (err != ErrorIOPending) return (null, false);

                // 還在跑：等它，最多 NvmeLogTimeoutMs。逾時就 CancelIoEx 請求取消，並在
                // 取消請求送達後再等一下，讓驅動有機會回應——本機實測：取消請求本身可能被卡在
                // 裝置的逾時佇列裡（SRB_STATUS_SELECTION_TIMEOUT），所以底層 I/O 有可能仍繼續；
                // 這裡只確保「送出取消」並回報逾時，剩下交給斷路器不再重問。
                uint wait = WaitForSingleObject(evt, NvmeLogTimeoutMs);
                if (wait != 0 /* WAIT_OBJECT_0 */)
                {
                    CancelIoEx(handle, ov);
                    WaitForSingleObject(evt, 2500);   // 給驅動一點時間回應取消，順便確保緩衝區不再被寫
                    return (null, true);
                }
                if (!GetOverlappedResult(handle, ov, out ret, false)) return (null, false);
            }

            if (ret < header + 512) return (null, false);
            var log = new byte[512];
            Array.Copy(buf, header, log, 0, 512);
            return (log, false);
        }
        finally
        {
            if (ov != IntPtr.Zero) Marshal.FreeHGlobal(ov);
            if (evt != IntPtr.Zero) CloseHandle(evt);
            CloseHandle(handle);
        }
    }

    /// <summary>NVMe 健康紀錄（512B）→ 資料列。位移依 NVMe 1.3+ 規格的 SMART / Health Information log。</summary>
    public static List<SmartRow> DecodeNvmeHealth(byte[] log)
    {
        // 位移只在 NvmeHealth／NvmeLogDecoder 出現一次。1.9.1 之前這裡自己算位移，而且錯了兩處：
        // 關鍵警告被當成 2 位元組（規格是 1），把溫度／備用空間／已使用壽命整批推後一格；
        // 128 位元計數器被當成 8 位元組，把寫入量之後的每一個計數器都推錯位置。
        // 那個 bug 帶了三個發佈版，正因為「顯示」與「取值」各有一套位移，一套對一套錯。
        var h = NvmeHealth.Decode(log)
            ?? throw new InvalidOperationException("NVMe 健康紀錄長度不足 512 位元組。");

        string DuText(ulong units)
        {
            if (units == 0) return "0";
            double bytes = units * 512000.0;   // 1 單位 = 1000 × 512 位元組
            return bytes >= 1e12 ? $"{bytes / 1e12:0.000} TB（{units:N0} 單位）"
                 : bytes >= 1e9 ? $"{bytes / 1e9:0.0} GB（{units:N0} 單位）"
                 : $"{bytes / 1e6:0} MB（{units:N0} 單位）";
        }

        var warns = NvmeLogDecoder.CriticalWarnings(h.CriticalWarning);

        var rows = new List<SmartRow>
        {
            // 原始位元組照留（0x00 就是「沒有任何警告」），但底下逐條說出到底哪一位元亮了
            new("關鍵警告", warns.Count == 0 ? $"無（0x{h.CriticalWarning:X2}）"
                                            : $"{warns.Count} 項（0x{h.CriticalWarning:X2}）", "", ""),
        };
        foreach (var w in warns)
            rows.Add(new SmartRow($"　└ 位元 {w.Bit}：{w.Name}", w.Meaning, "", ""));

        rows.Add(new("溫度（綜合）", h.CompositeTempCelsius is { } c ? $"{c} °C" : "—", "", ""));
        rows.Add(new("可用備用空間",
            $"{h.AvailableSparePercent}%（門檻 {h.SpareThresholdPercent}%）", "", ""));
        rows.Add(new("已使用壽命（Percentage Used）", $"{h.PercentageUsed}%", "", ""));
        rows.Add(new("累計讀取（Data Units Read）", DuText(h.DataUnitsRead), "", ""));
        rows.Add(new("累計寫入（Data Units Written）", DuText(h.DataUnitsWritten), "", ""));
        rows.Add(new("通電時間", h.PowerOnHours == 0 ? "—" : $"{h.PowerOnHours:N0} 小時", "", ""));
        rows.Add(new("電源循環", $"{h.PowerCycles:N0} 次", "", ""));
        rows.Add(new("不安全關機", $"{h.UnsafeShutdowns:N0} 次", "", ""));
        rows.Add(new("媒體與資料完整性錯誤", $"{h.MediaErrors:N0}", "", ""));
        rows.Add(new("錯誤資訊紀錄項目", $"{h.ErrorLogEntries:N0}", "", ""));
        return rows;
    }

    /// <summary>
    /// 錯誤資訊紀錄（log page 0x01）→ 資料列。空槽位（錯誤計數 0）不列。
    /// </summary>
    /// <remarks>
    /// 這是「這顆碟開始要壞了」最早的證據所在：每一筆記著出錯的命令、狀態碼與 LBA，
    /// 而 Windows 完全不看它。讀不到就要說讀不到——USB 外接盒多半不轉發 NVMe 命令。
    /// </remarks>
    public static List<SmartRow> DecodeNvmeErrorLog(byte[] log)
    {
        var entries = NvmeLogDecoder.ErrorEntries(log);
        if (entries.Count == 0)
            return [new SmartRow("錯誤資訊紀錄（log page 0x01）", "沒有任何一筆——這顆碟從未回報過命令層級的錯誤。", "", "")];

        var rows = new List<SmartRow>
        {
            new("錯誤資訊紀錄（log page 0x01）", $"{entries.Count} 筆（最新的在最前面）", "", ""),
        };
        foreach (var e in entries)
            rows.Add(new SmartRow($"　└ {e.CountText}", $"{e.StatusText}（{e.RawStatusText}）"
                + $" ・ LBA {e.LbaText} ・ 命名空間 {e.NamespaceText} ・ {e.QueueText}", "", ""));
        return rows;
    }

    // ── SATA／ATA ──────────────────────────────────────────────────────────

    /// <summary>以 SMART_RCV_DRIVE_DATA 取 512 位元組屬性表；失敗回 null（不使用會卡死的 ATA_PASS-THROUGH）。</summary>
    /// <summary>
    /// 斷路器的查詢鍵（磁碟, 查詢）中的「查詢」那一半，<b>與 <see cref="TryReadAtaSmartClassic"/> 的
    /// <c>GuardedRead</c> 逐字相同</b>（同一個 <c>nameof</c>）。給外層自己先問「這一輪該不該直接跳過」的
    /// 呼叫端用（<see cref="SmartLookupProbe"/>，第 274 輪）——字串若在兩處各寫一份，遲早會漂開，
    /// 那時斷路器就會變成永遠不跳過、或永遠跳過。
    /// </summary>
    public const string AtaAttributesOp = nameof(TryReadAtaSmartClassic);

    /// <summary>ATA SMART READ DATA（512B）；失敗或逾時回 null。走看門狗，見 Services/DiskIo.cs。</summary>
    private static byte[]? TryReadAtaSmartClassic(int index)
        => GuardedRead(index, AtaAttributesOp, () => ReadAtaSmartClassicCore(index));

    private static byte[]? ReadAtaSmartClassicCore(int index)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // SENDCMDINPARAMS：cBufferSize(4)＋暫存器區(8)＋磁碟號(1)＋保留(3)＝16，bBuffer 另計
            var input = new byte[16 + 512];
            BitConverter.GetBytes(512u).CopyTo(input, 0);   // cBufferSize
            input[4] = 0xD0;    // bFeaturesReg = SMART
            input[5] = 0x01;    // bSectorCountReg = 1
            input[6] = 0x01;    // bSectorNumberReg = 1
            input[7] = 0x4F;    // bCylLowReg（SMART 簽章）
            input[8] = 0xC2;    // bCylHighReg
            input[9] = 0xA0;    // bDriveHeadReg
            input[10] = 0xB0;   // bCommandReg = SMART
            input[12] = (byte)index;   // bDriveNumber

            var output = new byte[16 + 512];   // SENDCMDOUTPARAMS：cBufferSize(4)＋DriverStatus(12)＋資料
            if (!DeviceIoControl(handle, IoctlSmartRcvDriveData, input, (uint)input.Length, output, (uint)output.Length, out uint ret, IntPtr.Zero))
                return null;
            if (output[4] != 0)   // bDriverError 非零＝驅動回報失敗
                return null;
            if (ret < 16 + 512) return null;
            var sector = new byte[512];
            Array.Copy(output, 16, sector, 0, 512);
            return sector;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>SATA SMART READ DATA（512B）→ 30 筆屬性：ID、現值、最差、原始六位元組（十六進位原樣＋小端整數）。</summary>
    /// <remarks>
    /// <paramref name="driveModel"/> 是 Win32_DiskDrive 的 Model（呼叫端從列舉快照帶入）；
    /// 只影響「廠商自訂」那條路徑——既有對照表認得的 ID，名稱與 raw 欄位逐字不變。
    /// </remarks>
    public static List<SmartRow> DecodeAtaAttributes(byte[] sector, string? driveModel = null)
    {
        if (sector.Length < 512) throw new InvalidOperationException("SMART 屬性表長度不足 512 位元組。");
        var rows = new List<SmartRow>();
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            byte id = sector[o];
            if (id == 0) continue;   // 未使用的槽位
            byte value = sector[o + 3];
            byte worst = sector[o + 4];
            var raw = new byte[6];
            Array.Copy(sector, o + 5, raw, 0, 6);
            ulong le = 0;
            for (int k = 5; k >= 0; k--) le = (le << 8) | raw[k];
            string hex = Convert.ToHexString(raw);
            string attrName = AttributeName(id, driveModel);
            string rawText = $"{hex}（LE: {le:N0}）";
            // 知識表補強路徑（只有既有表回「廠商自訂」、而型號認得出廠商且知識表收錄時才走到）：
            // raw 六位元組若有語意就按語意附註；原樣十六進位照列，不受附註影響。
            if (attrName != AttributeName(id)
                && TryVendorFromModel(driveModel, out SmartVendor vendor)
                && SmartAttributeKnowledge.Lookup(vendor, id) is { } info)
                rawText += $"；raw：{info.RawSemantics}";
            rows.Add(new SmartRow(
                $"{id} {attrName}",
                value.ToString(),
                worst.ToString(),
                rawText,
                id, le));           // 編號與數值一併帶出,供驗機規則對帳
        }
        return rows;
    }

    /// <summary>
    /// SATA SMART 屬性(帶編號與原始值);讀不到或逾時回 <c>null</c>。
    /// **不回空清單**——空清單會被誤讀成「讀到了,但這顆碟沒有任何屬性」,那是另一回事。
    /// </summary>
    public static IReadOnlyList<SmartRow>? TryReadAtaAttributes(int index)
        => TryReadAtaAttributes(index, null);

    /// <summary>
    /// 與單參版同一條讀取，但把 <paramref name="driveModel"/> 帶進解碼（第 274 輪）。
    /// </summary>
    /// <remarks>
    /// <b>為什麼需要這個重載：</b><see cref="DecodeAtaAttributes"/> 的「廠商自訂」補強路徑要看型號
    /// （<see cref="TryVendorFromModel"/> → <c>SmartAttributeKnowledge.Lookup</c>），而單參版永遠用
    /// <c>null</c> 解碼——headless 的 <c>ata.smart.attr.lookup</c> 若走單參版，
    /// 統計到的「命中」會永遠是 0（每一筆廠商自訂都只會是「未收錄」），那是一個<b>看起來正常、
    /// 實際上錯的數字</b>，比不接線更糟。
    /// <b>不新增第二條讀取路徑</b>：這裡與單參版呼叫同一個 <see cref="TryReadAtaSmartClassic"/>。
    /// </remarks>
    public static IReadOnlyList<SmartRow>? TryReadAtaAttributes(int index, string? driveModel)
    {
        byte[]? sector = TryReadAtaSmartClassic(index);
        return sector is null || sector.Length < 512 ? null : DecodeAtaAttributes(sector, driveModel);
    }

    /// <summary>ATA SMART READ THRESHOLDS（0xD1 簽章，512B 門檻表）；失敗回 null。與 READ DATA 同一 ioctl 通路。</summary>
    public static byte[]? TryReadAtaThresholds(int index)
        => GuardedRead(index, nameof(TryReadAtaThresholds), () => ReadAtaThresholdsCore(index));

    private static byte[]? ReadAtaThresholdsCore(int index)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var input = new byte[16 + 512];
            BitConverter.GetBytes(512u).CopyTo(input, 0);
            input[4] = 0xD1;    // bFeaturesReg = SMART READ THRESHOLDS
            input[5] = 0x01;    // bSectorCountReg = 1
            input[6] = 0x01;    // bSectorNumberReg = 1
            input[7] = 0x4F;    // bCylLowReg（SMART 簽章）
            input[8] = 0xC2;    // bCylHighReg
            input[9] = 0xA0;    // bDriveHeadReg
            input[10] = 0xB0;   // bCommandReg = SMART
            input[12] = (byte)index;

            var output = new byte[16 + 512];
            if (!DeviceIoControl(handle, IoctlSmartRcvDriveData, input, (uint)input.Length, output, (uint)output.Length, out uint ret, IntPtr.Zero))
                return null;
            if (output[4] != 0) return null;
            if (ret < 16 + 512) return null;
            var sector = new byte[512];
            Array.Copy(output, 16, sector, 0, 512);
            return sector;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>SMART READ THRESHOLDS 表：每筆 12 bytes（ID@0、門檻@1），version 2 bytes 起。回 ID→門檻。</summary>
    public static Dictionary<byte, byte> DecodeAtaThresholds(byte[] sector)
    {
        var map = new Dictionary<byte, byte>();
        if (sector is null || sector.Length < 512) return map;
        for (int off = 2; off + 12 <= 512 && off <= 2 + 29 * 12; off += 12)
        {
            byte id = sector[off];
            if (id == 0) continue;
            map[id] = sector[off + 1];
        }
        return map;
    }

    /// <summary>門檻評比結果：屬性 ID、現值、門檻。<b>現值 ≤ 門檻（門檻非 0）＝現正低於門檻（failing now）</b>。</summary>
    public sealed record FailingNowRow(byte Id, string Name, long Value, long Threshold);

    /// <summary>
    /// 現值對門檻評比。門檻 0＝SMART 規範「無門檻」，不參與評比（不猜）；
    /// 現值以 SmartRow.RawValue 優先、退回解析 ValueText。
    /// </summary>
    public static IReadOnlyList<FailingNowRow> EvaluateFailingNow(IEnumerable<SmartRow> attrs,
        IReadOnlyDictionary<byte, byte> thresholds)
    {
        var failing = new List<FailingNowRow>();
        foreach (var a in attrs)
        {
            if (a.Id == 0) continue;
            if (!thresholds.TryGetValue(a.Id, out byte th) || th == 0) continue;
            long value;
            if (a.RawValue is { } raw) value = (long)Math.Min(raw, long.MaxValue);
            else if (!long.TryParse(a.ValueText, out value)) continue;
            if (value <= th) failing.Add(new FailingNowRow(a.Id, a.Name, value, th));
        }
        return failing;
    }

    /// <summary>NVMe WCTEMP 評比狀態。</summary>
    public enum WctempState { Normal, Warning, NotProvided }

    /// <summary>WCTEMP 對照結果。</summary>
    public sealed record WctempVerdict(WctempState State, int ThresholdC, int CompositeC);

    /// <summary>
    /// NVMe 溫度門檻對照：WCTEMP 在 Identify Controller 偏移 0x14A（u16 LE，°C；0＝未提供）。
    /// 合成溫度 ≥ WCTEMP＝警告。
    /// </summary>
    public static WctempVerdict EvaluateWctemp(int compositeTempC, byte[] identifyController)
    {
        if (identifyController is null || identifyController.Length < 0x14C)
            return new WctempVerdict(WctempState.NotProvided, 0, compositeTempC);
        ushort wctemp = BitConverter.ToUInt16(identifyController, 0x14A);
        if (wctemp == 0) return new WctempVerdict(WctempState.NotProvided, 0, compositeTempC);
        return new WctempVerdict(compositeTempC >= wctemp ? WctempState.Warning : WctempState.Normal, wctemp, compositeTempC);
    }

    /// <summary>NVMe 健康紀錄的型別化快照;讀不到、逾時或長度不足回 <c>null</c>。</summary>
    public static NvmeHealthSnapshot? TryReadNvmeHealth(int index)
        => TryReadNvmeLog(index) is { } log ? NvmeHealth.Decode(log) : null;

    /// <summary>常見屬性 ID 的通稱；不認得的 ID 顯示「廠商自訂」——不猜定義。</summary>
    public static string AttributeName(byte id) => id switch
    {
        1 => "讀取錯誤率", 5 => "重配置磁區數", 9 => "通電時間", 12 => "電源循環",
        184 => "端對端錯誤", 187 => "回報的不可修正錯誤", 188 => "命令逾時", 190 => "氣流溫度",
        194 => "溫度", 195 => "硬體 ECC 修正", 196 => "重配置事件數", 197 => "待定磁區數",
        198 => "離線不可修正", 199 => "UDMA CRC 錯誤", 233 => "媒體損耗指標",
        241 => "累計寫入 LBA", 242 => "累計讀取 LBA",
        _ => "廠商自訂",
    };

    /// <summary>
    /// 帶裝置型號的屬性名（第 270 輪 SMART 知識包的顯示接線）。
    /// 既有對照表先查——認得的 ID <b>行為逐字不變</b>；只有既有表回「廠商自訂」時，
    /// 才以型號映射廠商、問 <see cref="SmartAttributeKnowledge"/> 補名稱；
    /// 廠商或 ID 未收錄就維持「廠商自訂」原樣——未知就是未知，不猜。
    /// </summary>
    public static string AttributeName(byte id, string? driveModel)
    {
        string known = AttributeName(id);
        if (known != "廠商自訂") return known;
        if (TryVendorFromModel(driveModel, out SmartVendor vendor)
            && SmartAttributeKnowledge.Lookup(vendor, id) is { } info)
            return $"廠商自訂（{info.Name}）";
        return known;
    }

    /// <summary>裝置型號 → SMART 知識表的六家廠商。認不得的回 false（未知廠商不是錯誤）。</summary>
    public static bool TryVendorFromModel(string? model, out SmartVendor vendor)
    {
        string m = (model ?? "").Trim();
        if (m.Length == 0) { vendor = default; return false; }
        if (m.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) { vendor = SmartVendor.Samsung; return true; }
        if (m.Contains("Seagate", StringComparison.OrdinalIgnoreCase)) { vendor = SmartVendor.Seagate; return true; }
        if (m.Contains("SanDisk", StringComparison.OrdinalIgnoreCase)) { vendor = SmartVendor.SanDisk; return true; }
        if (m.Contains("Kingston", StringComparison.OrdinalIgnoreCase)) { vendor = SmartVendor.Kingston; return true; }
        if (m.Contains("Crucial", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Micron", StringComparison.OrdinalIgnoreCase)) { vendor = SmartVendor.Micron; return true; }
        if (m.Contains("Western Digital", StringComparison.OrdinalIgnoreCase)
            || m.Contains("WDC", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("WD", StringComparison.OrdinalIgnoreCase)) { vendor = SmartVendor.WesternDigital; return true; }
        vendor = default;
        return false;
    }

    // ── 硬體識別：NVMe Identify Controller + SATA IDENTIFY DEVICE ─────────

    /// <summary>
    /// 從識別列中取出裝置自己回報的序號（NVMe Identify 與 ATA IDENTIFY 的序號列都以「序號」起頭）。
    /// 找不到或韌體回空字串時回傳 null——沒讀到就別假裝讀到了。
    /// </summary>
    public static string? SerialFromRows(IEnumerable<SmartRow> rows)
    {
        foreach (var r in rows)
        {
            if (!r.Name.StartsWith("序號", StringComparison.Ordinal)) continue;
            string v = r.ValueText.Trim();
            // 只在字串全為可列印 ASCII（0x20–0x7E）時才回填——解碼失敗留下的亂碼絕不拿去蓋掉 WMI 的真值。
            if (v.Length > 0 && v.All(c => c is >= ' ' and <= '~')) return v;
        }
        return null;
    }

    /// <summary>
    /// 讀一顆磁碟的累計通電小時；讀不到回 0。
    /// </summary>
    /// <remarks>
    /// NVMe 取健康紀錄（log 0x02）偏移 0x80 的 128 位元計數器的低 64 位元；
    /// SATA 取屬性 9（通電時間）的原始值低三個位元組——那是業界通用的小時數，
    /// 不套任何廠商公式。讀不到就回 0，由呼叫端顯示「讀不到」，不猜。
    /// </remarks>
    public static long TryReadPowerOnHours(int index)
    {
        uint bus = TryGetBusType(index, out _);

        if (bus is 16 or 17)
        {
            byte[]? log = TryReadNvmeLog(index);
            if (log is null || log.Length < 0x88) return 0;
            ulong v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | log[0x80 + i];
            return v > long.MaxValue ? 0 : (long)v;
        }

        byte[]? sector = TryReadAtaSmartClassic(index);
        if (sector is null || sector.Length < 362) return 0;
        // SMART 資料區：偏移 2 起，每 12 位元組一筆屬性；[0]=ID、[5..7]=原始值低三位元組
        for (int o = 2; o + 12 <= 362; o += 12)
        {
            if (sector[o] != 9) continue;
            return sector[o + 5] | (sector[o + 6] << 8) | (sector[o + 7] << 16);
        }
        return 0;
    }

    private const uint NvmeDataTypeIdentify = 0;     // CNS=Identify
    private const uint NvmeCnsController = 1;       // CNS=1 為 Controller
    private const uint NvmeDataTypeFeature = 3;     // STORAGE_PROTOCOL_NVME_DATA_TYPE::NVMeDataTypeFeature

    /// <summary>
    /// 嘗試取 NVMe Get Features 的資料區（例如 FID 0x0C＝APST 的 256 位元組表）。
    /// </summary>
    /// <remarks>
    /// Windows 只對一部分 Feature 開放這條唯讀查詢，多數機器會直接失敗——那是常態，不是缺陷。
    /// 失敗回 null，由呼叫端如實說明「讀不到」，不要拿規格書的預設值頂替。
    /// </remarks>
    public static byte[]? TryReadNvmeFeature(int index, uint featureId, int length)
        => GuardedRead(index, $"{nameof(TryReadNvmeFeature)}.{featureId}.{length}", () => ReadNvmeFeatureCore(index, featureId, length));

    private static byte[]? ReadNvmeFeatureCore(int index, uint featureId, int length)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            const int header = 48;
            var buf = new byte[header + length];
            BitConverter.GetBytes(PropertyIdProtocolSpecificDevice).CopyTo(buf, 0);
            BitConverter.GetBytes(0u).CopyTo(buf, 4);
            BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buf, 8);
            BitConverter.GetBytes(NvmeDataTypeFeature).CopyTo(buf, 12);
            BitConverter.GetBytes(featureId).CopyTo(buf, 16);      // RequestValue = FID
            BitConverter.GetBytes(0u).CopyTo(buf, 20);
            BitConverter.GetBytes((uint)header).CopyTo(buf, 24);
            BitConverter.GetBytes((uint)length).CopyTo(buf, 28);

            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, IntPtr.Zero))
                return null;
            if (ret < header + length) return null;
            var data = new byte[length];
            Array.Copy(buf, header, data, 0, length);
            return data;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// 取得 NVMe Identify Controller 資料區（4096B；回傳前 4096 位元組，後段是命名空間列表，現用不到）。
    /// 失敗回 null。
    /// </summary>
    public static byte[]? TryReadNvmeIdentify(int index)
        => GuardedRead(index, nameof(TryReadNvmeIdentify), () => ReadNvmeIdentifyCore(index));

    private static byte[]? ReadNvmeIdentifyCore(int index)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            const int header = 48;
            var buf = new byte[header + 4096];
            BitConverter.GetBytes(PropertyIdProtocolSpecificDevice).CopyTo(buf, 0);
            BitConverter.GetBytes(0u).CopyTo(buf, 4);                              // PropertyStandardQuery
            BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buf, 8);
            BitConverter.GetBytes(NvmeDataTypeIdentify).CopyTo(buf, 12);
            BitConverter.GetBytes(NvmeCnsController).CopyTo(buf, 16);              // RequestValue = CNS=1
            BitConverter.GetBytes(0u).CopyTo(buf, 20);
            BitConverter.GetBytes((uint)header).CopyTo(buf, 24);
            BitConverter.GetBytes(4096u).CopyTo(buf, 28);

            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, IntPtr.Zero))
                return null;
            if (ret < header + 4096) return null;
            var data = new byte[4096];
            Array.Copy(buf, header, data, 0, 4096);
            return data;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// 取得 ATA IDENTIFY DEVICE 結果（512B）。用 SMART 0xEC 簽章走 SMART_RCV_DRIVE_DATA 同一路徑。
    /// 失敗回 null。
    /// </summary>
    public static byte[]? TryReadAtaIdentify(int index)
        => GuardedRead(index, nameof(TryReadAtaIdentify), () => ReadAtaIdentifyCore(index));

    private static byte[]? ReadAtaIdentifyCore(int index)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var input = new byte[16 + 512];
            BitConverter.GetBytes(512u).CopyTo(input, 0);
            input[4] = 0x00;   // bFeaturesReg
            input[5] = 0x01;   // bSectorCountReg
            input[6] = 0x01;   // bSectorNumberReg
            input[7] = 0x4F;   // bCylLowReg
            input[8] = 0xC2;   // bCylHighReg
            input[9] = 0xA0;   // bDriveHeadReg
            input[10] = 0xEC;  // bCommandReg = IDENTIFY DEVICE
            input[12] = (byte)index;

            var output = new byte[16 + 512];
            if (!DeviceIoControl(handle, IoctlSmartRcvDriveData, input, (uint)input.Length, output, (uint)output.Length, out uint ret, IntPtr.Zero))
                return null;
            if (output[4] != 0) return null;
            if (ret < 16 + 512) return null;
            var data = new byte[512];
            Array.Copy(output, 16, data, 0, 512);
            return data;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>NVMe Identify Controller（4096B）→ 識別資訊列。位元組位移依 NVMe 1.3+ 規格。</summary>
    public static List<SmartRow> DecodeNvmeIdentify(byte[] data)
    {
        if (data.Length < 4096) throw new InvalidOperationException("NVMe Identify Controller 長度不足 4096 位元組。");

        // 字串欄位是「一字元一位元組」的連續 ASCII、空格補位（不是每 2 位元組取 1——那是先前的 bug，
        // 只讀到半截字串）。空格由 Trim 收尾；少數韌體以 NUL 補位，故遇 0 也停。
        string AsciiString(int offset, int byteLen)
        {
            var chars = new char[byteLen];
            int w = 0;
            for (int i = 0; i < byteLen; i++)
            {
                byte b = data[offset + i];
                if (b == 0) break;
                chars[w++] = (char)b;
            }
            return new string(chars, 0, w).Trim();
        }

        ulong tnvmcap = BitConverter.ToUInt64(data, 280);   // TNVMCAP 低 64 位（單位：位元組）

        var rows = new List<SmartRow>
        {
            new("廠商（Vendor ID）", $"0x{BitConverter.ToUInt16(data, 0):X4}", "", ""),
            new("型號（Model）", AsciiString(24, 40), "", ""),
            new("序號（Serial）", AsciiString(4, 20), "", ""),
            new("韌體版本（Firmware）", AsciiString(64, 8), "", ""),
            new("總命名空間數（NN）", $"{BitConverter.ToUInt32(data, 516)}", "", ""),
            new("容量（TNVMCAP）", tnvmcap > 0 ? $"{tnvmcap:N0} 位元組（≈{tnvmcap / 1e9:0.0} GB）" : "—", "", ""),
            new("最大資料傳輸大小（MDTS）", $"{1UL << (data[77] & 0xF):N0} 頁（4 KiB 倍數）", "", ""),
        };
        return rows;
    }

    /// <summary>ATA IDENTIFY DEVICE（512B）→ 識別資訊列。位移依 ATA/ATAPI-8 規格，word 為 16 位元小端。</summary>
    public static List<SmartRow> DecodeAtaIdentify(byte[] data)
    {
        if (data.Length < 512) throw new InvalidOperationException("ATA IDENTIFY DEVICE 長度不足 512 位元組。");

        ushort Le16(int o) => (ushort)(data[o] | (data[o + 1] << 8));
        ulong Le32(int o) => (ulong)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));
        // 每個 16 位元 word 內為大端——首字元在高位元組。先前只取偶數（低）位元組且方向反了，
        // 讀出來是半截且錯位的亂碼。正解逐 word 先取高位元組、再取低位元組（同 Linux ata_id_string）。
        static string AsciiString(byte[] d, int offset, int wordCount)
        {
            var chars = new char[wordCount * 2];
            int w = 0;
            for (int i = 0; i < wordCount; i++)
            {
                chars[w++] = (char)d[offset + i * 2 + 1];   // 高位元組 = 該 word 的前一字元
                chars[w++] = (char)d[offset + i * 2];       // 低位元組 = 後一字元
            }
            return new string(chars, 0, w).Replace('\0', ' ').Trim();
        }

        // Word 0：General configuration。bit 15 = 0 才是 ATA 裝置；= 1 是 ATAPI／非 ATA。先前判反了。
        bool isAta = (Le16(0) & 0x8000) == 0;
        // Word 10-19：Model number (20 words)
        // Word 23-26：Firmware revision (4 words)
        // Word 27-46：User addressable sectors (max LBA for 28-bit) 已被 48-bit LBA 取代
        // Word 69 bit 14 = supports Trim
        // Word 78 bit 5 = supports DevSleep
        // Word 82-83：48-bit LBA supported sectors
        // Word 100-103：Total logical sectors (16-byte)
        // Word 106：Physical/Logical sector size (bit 12 = 4K logical, bit 13 = 4K physical)
        // Word 217：Nominal media rotation rate (SSD = 0x0001)
        // Word 128：Security status

        var rows = new List<SmartRow>
        {
            new("型號（Model）", AsciiString(data, 2 * 27, 20), "", ""),
            new("序號（Serial）", AsciiString(data, 2 * 10, 10), "", ""),
            new("韌體（Firmware）", AsciiString(data, 2 * 23, 4), "", ""),
            new("裝置類型", isAta ? "ATA" : "ATAPI／未知", "", ""),
            new("最大 LBA（48-bit）", Le32(2 * 100) != 0 ? $"{Le32(2 * 100):N0} 磁區（≈{Le32(2 * 100) * 512 / 1e12:0.00} TB）" : "—", "", ""),
            new("邏輯磁區大小", (Le16(2 * 106) & 0x1000) != 0 ? "4 KiB" : "512 B", "", ""),
            new("實體磁區大小", (Le16(2 * 106) & 0x2000) != 0 ? "4 KiB" : "512 B", "", ""),
            new("支援 TRIM", (Le16(2 * 69) & 0x4000) != 0 ? "是（Data Set Management）" : "否", "", ""),
            new("支援 DevSleep", (Le16(2 * 78) & 0x20) != 0 ? "是" : "否", "", ""),
            new("媒體旋轉率（Word 217）", Le16(2 * 217) switch { 0x0001 => "SSD（無旋轉）", 0 => "未回報", var w => $"{w} RPM" }, "", ""),
            new("安全狀態（Word 128）", $"0x{Le16(2 * 128):X4}（0x0001 = 支援、0x0002 = 已啟用）", "", ""),
            new("ATA 版本（Word 80）", $"0x{Le16(2 * 80):X4}（位 15:8 = 最高支援版本）", "", ""),
        };
        return rows;
    }
}
