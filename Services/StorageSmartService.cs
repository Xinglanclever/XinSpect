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
}

/// <summary>一列 S.M.A.R.T. 資料：NVMe 時填 Name／ValueText，SATA 時四欄都填。</summary>
public sealed class SmartRow
{
    public SmartRow(string name, string valueText, string worstText, string rawText)
    { Name = name; ValueText = valueText; WorstText = worstText; RawText = rawText; }
    public string Name { get; }
    public string ValueText { get; }
    public string WorstText { get; }
    public string RawText { get; }
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

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlSmartRcvDriveData = 0x7C82C;   // CTL_CODE(IOCTL_DISK_BASE, 0x220B, METHOD_BUFFERED, FILE_READ_ACCESS)
    private const uint PropertyIdDeviceProperty = 0;
    private const uint PropertyIdProtocolSpecificDevice = 51;
    private const uint ProtocolTypeNvme = 3;
    private const uint NvmeDataTypeLogPage = 2;
    private const uint NvmeLogPageHealth = 0x02;

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

    public StorageSmartService() => _ = RefreshDrivesAsync();

    /// <summary>重新列舉實體磁碟（Win32_DiskDrive：Index／型號／容量）。</summary>
    public async Task RefreshDrivesAsync()
    {
        try
        {
            var list = await Task.Run(() =>
            {
                var result = new List<SmartDriveRow>();
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Index, Model, Size FROM Win32_DiskDrive");
                foreach (var d in searcher.Get())
                {
                    var idx = Convert.ToInt32(d["Index"]);
                    var model = d["Model"]?.ToString() ?? "—";
                    var size = d["Size"] is null ? 0 : Convert.ToInt64(d["Size"]);
                    result.Add(new SmartDriveRow(idx, $"磁碟 {idx}：{model}（{size / 1024.0 / 1024 / 1024:0.#} GB）"));
                }
                return result.OrderBy(r => r.Index).ToList();
            });
            Drives.Clear();
            foreach (var r in list) Drives.Add(r);
        }
        catch (Exception ex)
        {
            Status = "無法列舉實體磁碟：" + ex.Message;
        }
    }

    /// <summary>查詢未回應（逾時未完成）的磁碟：本次執行不再嘗試，避免每次都留下一個卡死的執行緒。</summary>
    private readonly HashSet<int> _noResponse = new();

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
                return bus switch
                {
                    16 or 17 => TryReadNvme(index) is { } nvme
                        ? ("NVMe 健康紀錄（log page 0x02，DeviceIoControl 直讀；匯流排 " + busName + "）", DecodeNvmeHealth(nvme)
                           .Concat(new[] { new SmartRow("── 硬體識別 ──", "", "", "") })
                           .Concat(TryReadNvmeIdentify(index) is { } nvi ? DecodeNvmeIdentify(nvi) : new[] { new SmartRow("NVMe Identify Controller", "不支援（儲存堆疊未回應協定查詢）", "", "") })
                           .ToList())
                        : throw new InvalidOperationException("NVMe 裝置的健康紀錄讀取失敗（儲存堆疊未回應協定查詢）。"),
                    3 or 10 or 11 => TryReadAtaSmartClassic(index) is { } ata
                        ? ("ATA S.M.A.R.T. 屬性（SMART_RCV_DRIVE_DATA，原始欄位照列；匯流排 " + busName + "）", DecodeAtaAttributes(ata)
                           .Concat(new[] { new SmartRow("── 硬體識別 ──", "", "", "") })
                           .Concat(TryReadAtaIdentify(index) is { } ati ? DecodeAtaIdentify(ati) : new[] { new SmartRow("ATA IDENTIFY DEVICE", "不支援（驅動拒絕命令）", "", "") })
                           .ToList())
                        : throw new InvalidOperationException("此磁碟不支援 SMART 讀取（驅動拒絕 SMART_RCV_DRIVE_DATA）。"),
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

            var (kind, rows) = task.Result;   // 已完成，Result 不會阻塞
            KindText = kind;
            foreach (var r in rows) Rows.Add(r);
            Status = "讀取完成。";
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

    /// <summary>StorageDeviceProperty 的 BusType（STORAGE_DEVICE_DESCRIPTOR 的位元組 @28）：3=ATA、10=SATA、16/17=NVMe 類。失敗回 0。</summary>
    public static uint TryGetBusType(int index, out string busName)
    {
        busName = "未知";
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var buf = new byte[512];
            BitConverter.GetBytes(PropertyIdDeviceProperty).CopyTo(buf, 0);   // StorageDeviceProperty
            BitConverter.GetBytes(0u).CopyTo(buf, 4);                         // PropertyStandardQuery
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, IntPtr.Zero)
                || ret < 32)
                return 0;
            byte bus = buf[28];   // BusType 是一個位元組（offset 24 是 SerialNumberOffset，別讀錯）
            busName = bus switch
            {
                1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE1394", 6 => "USB", 7 => "RAID",
                9 => "SAS", 10 => "SATA", 15 => "Storage Spaces", 16 => "NVMe", 17 => "SCM/NVMe 類",
                _ => $"BusType {bus}",
            };
            return bus;
        }
        finally { CloseHandle(handle); }
    }

    // ── NVMe ───────────────────────────────────────────────────────────────

    /// <summary>以 StorageDeviceProtocolSpecificProperty 取 NVMe 健康紀錄 512 位元組；失敗回 null。</summary>
    private static byte[]? TryReadNvme(int index)
    {
        var handle = OpenDrive(index);
        if (handle == IntPtr.Zero) return null;
        try
        {
            const int header = 48;   // STORAGE_PROPERTY_QUERY(8) + STORAGE_PROTOCOL_SPECIFIC_DATA(40)
            var buf = new byte[header + 512];
            BitConverter.GetBytes(PropertyIdProtocolSpecificDevice).CopyTo(buf, 0);   // PropertyId
            BitConverter.GetBytes(0u).CopyTo(buf, 4);                                 // QueryType = PropertyStandardQuery
            BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buf, 8);                   // ProtocolType
            BitConverter.GetBytes(NvmeDataTypeLogPage).CopyTo(buf, 12);               // DataRequested
            BitConverter.GetBytes(NvmeLogPageHealth).CopyTo(buf, 16);                 // RequestValue = 0x02
            BitConverter.GetBytes(0u).CopyTo(buf, 20);                                // RequestSubValue
            BitConverter.GetBytes((uint)header).CopyTo(buf, 24);                      // ProtocolDataOffset（自緩衝區起算）
            BitConverter.GetBytes(512u).CopyTo(buf, 28);                              // ProtocolDataLength

            // METHOD_BUFFERED：輸入與輸出共用同一緩衝區，命令頭必須以輸入傳入才會到達驅動
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, IntPtr.Zero))
                return null;
            if (ret < header + 512) return null;
            var log = new byte[512];
            Array.Copy(buf, header, log, 0, 512);
            return log;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>NVMe 健康紀錄（512B）→ 資料列。位移依 NVMe 1.3+ 規格的 SMART / Health Information log。</summary>
    public static List<SmartRow> DecodeNvmeHealth(byte[] log)
    {
        if (log.Length < 512) throw new InvalidOperationException("NVMe 健康紀錄長度不足 512 位元組。");
        ushort Le16(int o) => (ushort)(log[o] | (log[o + 1] << 8));
        ulong Le64(int o)
        {
            ulong v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | log[o + i];
            return v;
        }
        string DuText(int o)
        {
            ulong units = Le64(o);
            if (units == 0) return "0";
            double bytes = units * 512000.0;   // 1 單位 = 1000 × 512 位元組
            return bytes >= 1e12 ? $"{bytes / 1e12:0.000} TB（{units:N0} 單位）"
                 : bytes >= 1e9 ? $"{bytes / 1e9:0.0} GB（{units:N0} 單位）"
                 : $"{bytes / 1e6:0} MB（{units:N0} 單位）";
        }

        var rows = new List<SmartRow>
        {
            new("關鍵警告", Le16(0) == 0 ? "無" : $"0x{Le16(0):X4}", "", ""),
            new("溫度（綜合）", Le16(2) > 0 ? $"{Le16(2) - 273} °C" : "—", "", ""),
            new("可用備用空間", $"{log[4]}%（門檻 {log[5]}%）", "", ""),
            new("已使用壽命（Percentage Used）", $"{log[6]}%", "", ""),
            new("累計讀取（Data Units Read）", DuText(0x20), "", ""),
            new("累計寫入（Data Units Written）", DuText(0x28), "", ""),
            new("通電時間", Le64(0x50) == 0 ? "—" : $"{Le64(0x50):N0} 小時", "", ""),
            new("電源循環", $"{Le64(0x48):N0} 次", "", ""),
            new("不安全關機", $"{Le64(0x58):N0} 次", "", ""),
            new("媒體與資料完整性錯誤", $"{Le64(0x60):N0}", "", ""),
            new("錯誤資訊紀錄項目", $"{Le64(0x68):N0}", "", ""),
        };
        return rows;
    }

    // ── SATA／ATA ──────────────────────────────────────────────────────────

    /// <summary>以 SMART_RCV_DRIVE_DATA 取 512 位元組屬性表；失敗回 null（不使用會卡死的 ATA_PASS-THROUGH）。</summary>
    private static byte[]? TryReadAtaSmartClassic(int index)
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
    public static List<SmartRow> DecodeAtaAttributes(byte[] sector)
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
            rows.Add(new SmartRow(
                $"{id} {AttributeName(id)}",
                value.ToString(),
                worst.ToString(),
                $"{hex}（LE: {le:N0}）"));
        }
        return rows;
    }

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

    // ── 硬體識別：NVMe Identify Controller + SATA IDENTIFY DEVICE ─────────

    private const uint NvmeDataTypeIdentify = 0;     // CNS=Identify
    private const uint NvmeCnsController = 1;       // CNS=1 為 Controller

    /// <summary>
    /// 取得 NVMe Identify Controller 資料區（4096B；回傳前 4096 位元組，後段是命名空間列表，現用不到）。
    /// 失敗回 null。
    /// </summary>
    public static byte[]? TryReadNvmeIdentify(int index)
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

        // 字串欄位是 ASCII（大端：字元在低位元組、高位元組為 0）
        string AsciiString(int offset, int byteLen)
        {
            var chars = new char[byteLen / 2];
            int w = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                byte lo = data[offset + i * 2];
                if (lo == 0) break;
                chars[w++] = (char)lo;
            }
            return new string(chars, 0, w).Trim();
        }

        static string Status(ushort s) =>
            (s & 1) != 0 ? "未就緒" : (s & 0) != 0 ? "就緒" : "就緒（RDY=0）";

        var rows = new List<SmartRow>
        {
            new("廠商（Vendor ID）", AsciiString(0, 4), "", ""),
            new("型號（Model）", AsciiString(24, 40), "", ""),
            new("序號（Serial）", AsciiString(4, 20), "", ""),
            new("韌體版本（Firmware）", AsciiString(64, 8), "", ""),
            new("總命名空間數（NN）", $"{data[513] + (data[514] << 8)}", "", ""),
            new("容量（NCAP）", $"{(ulong)BitConverter.ToUInt32(data, 0x38) * 512:N0} 位元組", "", ""),
            new("最大資料傳輸大小（MDTS）", $"{1UL << ((data[77] >> 0) & 0xF):N0} 頁（4 KiB 倍數）", "", ""),
            new("Controller 狀態（CC.EN）", Status((ushort)(data[0x4C] | (data[0x4D] << 8))), "", ""),
        };
        return rows;
    }

    /// <summary>ATA IDENTIFY DEVICE（512B）→ 識別資訊列。位移依 ATA/ATAPI-8 規格，word 為 16 位元小端。</summary>
    public static List<SmartRow> DecodeAtaIdentify(byte[] data)
    {
        if (data.Length < 512) throw new InvalidOperationException("ATA IDENTIFY DEVICE 長度不足 512 位元組。");

        ushort Le16(int o) => (ushort)(data[o] | (data[o + 1] << 8));
        ulong Le32(int o) => (ulong)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));
        static string AsciiString(byte[] d, int offset, int wordCount)
        {
            int len = wordCount * 2;
            var chars = new char[len];
            int w = 0;
            for (int i = 0; i < len; i += 2)
            {
                byte lo = d[offset + i];
                byte hi = d[offset + i + 1];
                if (lo == 0 && hi == 0) break;                  // 全 0 結尾
                if (lo == 0x20 && hi == 0) continue;            // 跳過 0x20 補位空格
                chars[w++] = (char)lo;
            }
            return new string(chars, 0, w).Trim();
        }

        // Word 0：General configuration (bit 15 = ATA device)
        bool isAta = (Le16(0) & 0x8000) != 0;
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
