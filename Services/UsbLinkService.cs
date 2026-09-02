using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// USB 鏈路真相：把每個 USB 埠上的<b>埠能力</b>、<b>裝置能力</b>與<b>目前速度</b>擺在一起比。
/// 支援 10 Gb/s 的外接 SSD 只跑在 480 Mb/s、USB 3.x 的隨身碟插在只有 2.0 的埠上——
/// 這兩件事在檔案總管裡看不出來，只有問集線器才知道。
/// </summary>
/// <remarks>
/// <para>
/// 機制：以 <c>SetupDiGetClassDevs</c> 列出 USB 主控制器介面，對每個控制器問
/// <c>IOCTL_USB_GET_ROOT_HUB_NAME</c> 拿到根集線器，再逐層走下去：
/// <c>IOCTL_USB_GET_NODE_INFORMATION</c>（埠數）、
/// <c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX</c>（裝置描述元、目前速度）、
/// <c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2</c>（埠支援的協定、裝置能力旗標）、
/// <c>IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION</c>（字串與設定描述元）。
/// 遇到集線器就用 <c>IOCTL_USB_GET_NODE_CONNECTION_NAME</c> 往下一層遞迴。全程<b>唯讀查詢</b>，
/// 不重設埠、不改電源狀態、不寫任何描述元。
/// </para>
/// <para>
/// 誠實界線：①名稱是<b>裝置自己回報的字串描述元</b>，問不到就說沒回報，不去比對任何型號資料庫；
/// ②舊的 USB 2.0 集線器不支援 _V2 查詢，那些埠只寫得出目前速度，能力欄如實留空，不拿現況冒充「相符」；
/// ③空埠與「列舉失敗」都是回一片零的描述元，這裡分不出來，所以合併算作「未接或未列舉」；
/// ④最大電流是裝置在設定描述元裡<b>宣告</b>的上限，不是實際耗電。
/// </para>
/// </remarks>
public sealed class UsbLinkService : ObservableObject
{
    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。按「重新掃描」向 USB 集線器查詢（唯讀）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private string _topology = "—";
    /// <summary>拓樸一句話：幾個主控制器、幾個集線器、幾個埠是空的。</summary>
    public string TopologyText { get => _topology; private set => SetProperty(ref _topology, value); }

    public ObservableCollection<UsbPortRow> Rows { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "查詢中…";
        Rows.Clear();
        // 查詢期間 Rows 是空的；Summary 若留著上一輪的字樣，看的人會把「查詢中」誤當成「查完了，什麼都沒有」。
        Summary = "—";
        TopologyText = "—";
        try
        {
            var scan = await Task.Run(ScanAll);
            foreach (var r in scan.Rows) Rows.Add(r);
            Summary = UsbLinkDecoder.Summarize(scan.Rows);
            TopologyText = scan.Controllers == 0
                ? "沒有列出任何 USB 主控制器。"
                : $"{scan.Controllers} 個主控制器 ・ {scan.Hubs} 個集線器（含根集線器）・ "
                + $"{scan.Rows.Count} 個裝置 ・ {scan.EmptyPorts} 個埠未接或未列舉";
            Status = $"完成 ・ 共 {scan.Rows.Count} 個裝置。";
        }
        catch (Exception ex)
        {
            Summary = "無法查詢 USB 鏈路：" + ex.Message;
            Status = "查詢失敗。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>一次掃描的結果：裝置列表，加上拓樸的計數。</summary>
    private sealed record ScanResult(List<UsbPortRow> Rows, int Controllers, int Hubs, int EmptyPorts);

    // ── 原生介面 ────────────────────────────────────────────────────────────

    /// <summary>GUID_DEVINTERFACE_USB_HOST_CONTROLLER。</summary>
    private static readonly Guid HostControllerGuid = new("3abf6f2d-71c4-462a-8a92-1e6861e6af27");

    // USB 的 IOCTL 都是 CTL_CODE(FILE_DEVICE_USB=0x22, 功能碼, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlRootHubName = 0x220408;   // 功能碼 258（對主控制器）
    private const uint IoctlNodeInfo = 0x220408;      // 功能碼 258（對集線器）
    private const uint IoctlDescriptor = 0x220410;    // 功能碼 260
    private const uint IoctlConnName = 0x220414;      // 功能碼 261
    private const uint IoctlConnInfoEx = 0x220448;    // 功能碼 274
    private const uint IoctlConnInfoExV2 = 0x22045C;  // 功能碼 279

    private const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;
    private const uint GenericWrite = 0x40000000, ShareReadWrite = 0x03, OpenExisting = 3;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SpDeviceInterfaceData data,
        nint detail, uint detailSize, out uint required, nint devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string path, uint access, uint share, nint sec,
                                          uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint h, uint code, byte[]? inBuf, uint inSize,
                                               byte[]? outBuf, uint outSize, out uint returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint h);

    /// <summary>列出某個裝置介面類別的所有路徑（\\?\usb#…）。</summary>
    private static List<string> InterfacePaths(Guid classGuid)
    {
        var list = new List<string>();
        nint set = SetupDiGetClassDevs(ref classGuid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1 || set == 0) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref classGuid, i, ref data)) break;

                SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out uint needed, 0);
                if (needed == 0) continue;

                nint buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    // cbSize 是「結構本體」大小而非緩衝區大小：64 位元下為 8，32 位元下為 6。
                    Marshal.WriteInt32(buf, 0, nint.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, needed, out _, 0)
                        && Marshal.PtrToStringUni(buf + 4) is { Length: > 0 } path)
                        list.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>
    /// 開啟裝置。集線器與主控制器一般要 GENERIC_WRITE 才問得動；開不起來就退一步用「不要求存取權」
    /// 再試一次——這幾個 IOCTL 是 FILE_ANY_ACCESS，沒有存取權往往也問得到。
    /// </summary>
    private static nint Open(string path)
    {
        nint h = CreateFile(path, GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (h != -1) return h;
        return CreateFile(path, 0, ShareReadWrite, 0, OpenExisting, 0, 0);
    }

    // ── 掃描 ────────────────────────────────────────────────────────────────

    /// <summary>掃描過程中的計數（遞迴時要跨層累加）。</summary>
    private sealed class Counters { public int Hubs, Empty; }

    private static ScanResult ScanAll()
    {
        var rows = new List<UsbPortRow>();
        var counters = new Counters();
        int controllers = 0;

        foreach (string path in InterfacePaths(HostControllerGuid))
        {
            controllers++;
            if (RootHubName(path) is { } hub)
                WalkHub($@"\\.\{hub}", $"控制器 {controllers}", 0, rows, counters);
        }

        return new ScanResult(rows, controllers, counters.Hubs, counters.Empty);
    }

    /// <summary>問主控制器它的根集線器叫什麼。</summary>
    private static string? RootHubName(string controllerPath)
    {
        nint h = Open(controllerPath);
        if (h == -1) return null;
        try
        {
            var buf = new byte[600];
            return DeviceIoControl(h, IoctlRootHubName, null, 0, buf, (uint)buf.Length, out uint ret, 0)
                ? NameAt(buf, 4, ret) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// 走一層集線器：問出埠數，逐埠查詢，遇到集線器就往下一層。
    /// USB 規格最多 7 層，超過就停——資料異常時不讓遞迴無止盡走下去。
    /// </summary>
    private static void WalkHub(string hubPath, string location, int depth, List<UsbPortRow> rows, Counters c)
    {
        if (depth > 6) return;

        nint h = Open(hubPath);
        if (h == -1)
        {
            Diag.Swallow($"UsbLinkService.Open({location})", null,
                         $"Win32 錯誤 {Marshal.GetLastWin32Error()}；這一層集線器以下的裝置不會列出。");
            return;
        }
        try
        {
            c.Hubs++;
            var node = new byte[128];
            if (!DeviceIoControl(h, IoctlNodeInfo, node, (uint)node.Length, node, (uint)node.Length, out _, 0)) return;

            int ports = UsbLinkDecoder.PortCount(node);
            for (int port = 1; port <= ports; port++)
                ReadPort(h, port, location, depth, rows, c);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// 查一個埠。沒有有效的裝置描述元就算「未接或未列舉」——空埠與列舉失敗回的都是一片零，
    /// 這裡分不出來，就不假裝分得出來。
    /// </summary>
    private static void ReadPort(nint hub, int port, string location, int depth, List<UsbPortRow> rows, Counters c)
    {
        var conn = new byte[UsbLinkDecoder.ConnInfoMinBytes + 30 * 16];   // 表頭 ＋ 管線清單的餘裕
        BitConverter.TryWriteBytes(conn.AsSpan(0), port);
        if (!DeviceIoControl(hub, IoctlConnInfoEx, conn, (uint)conn.Length, conn, (uint)conn.Length, out _, 0)
            || UsbLinkDecoder.DecodeConnectionInfo(conn) is not { } info)
        {
            c.Empty++;
            return;
        }

        // _V2：埠支援的協定與裝置能力旗標。USB 2.0 的舊集線器不支援這個查詢，失敗就當沒有，不推測。
        uint flags = 0, protocols = 0;
        var v2 = new byte[16];
        BitConverter.TryWriteBytes(v2.AsSpan(0), port);
        BitConverter.TryWriteBytes(v2.AsSpan(4), v2.Length);             // Length
        bool hasV2 = DeviceIoControl(hub, IoctlConnInfoExV2, v2, (uint)v2.Length, v2, (uint)v2.Length, out uint got, 0)
                     && got >= v2.Length;
        if (hasV2)
        {
            protocols = BitConverter.ToUInt32(v2, 8);
            flags = BitConverter.ToUInt32(v2, 12);
        }

        var (verdict, severity) = UsbLinkDecoder.Judge(info.Speed, flags, protocols, hasV2);
        var cfg = ConfigDescriptor(hub, port);
        string where = $"{location}／埠 {port}";

        rows.Add(new UsbPortRow(
            where, depth,
            DeviceName(hub, port, info) ?? "（裝置沒有回報名稱）",
            $"VID {info.Vid:X4} ・ PID {info.Pid:X4}",
            UsbLinkDecoder.UsbVersionText(info.BcdUsb),
            UsbLinkDecoder.ClassName(info.DeviceClass),
            UsbLinkDecoder.OperatingText(info.Speed, flags, hasV2),
            UsbLinkDecoder.CapableText(flags, hasV2),
            UsbLinkDecoder.PortText(protocols),
            UsbLinkDecoder.PowerText(cfg?.MaxPowerRaw ?? -1, cfg?.Attributes ?? 0, info.Speed == 3),
            verdict, severity));

        if (info.IsHub && ChildHubName(hub, port) is { } child)
            WalkHub($@"\\.\{child}", where, depth + 1, rows, c);
    }

    // ── 描述元與名稱 ────────────────────────────────────────────────────────

    /// <summary>
    /// 向指定埠上的裝置要一份描述元。前 12 個位元組是請求表頭（連線編號 ＋ 8 位元組的 setup 封包），
    /// 描述元本體接在後面；同一個緩衝區既當輸入也當輸出。
    /// </summary>
    private static byte[]? Descriptor(nint hub, int port, byte type, byte index, ushort lang)
    {
        const int Header = 12, Payload = 256;
        var buf = new byte[Header + Payload];

        BitConverter.TryWriteBytes(buf.AsSpan(0), port);
        buf[4] = 0x80;                                                  // bmRequest：裝置→主機、標準、對裝置
        buf[5] = 0x06;                                                  // bRequest：GET_DESCRIPTOR
        buf[6] = index; buf[7] = type;                                  // wValue：型別在高位元組、索引在低位元組
        BitConverter.TryWriteBytes(buf.AsSpan(8), lang);                // wIndex：語言（字串以外填 0）
        BitConverter.TryWriteBytes(buf.AsSpan(10), (ushort)Payload);    // wLength

        if (!DeviceIoControl(hub, IoctlDescriptor, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, 0)
            || ret <= Header)
            return null;

        return buf[Header..(int)Math.Min(ret, (uint)buf.Length)];
    }

    /// <summary>設定描述元（型別 2）：供電屬性與宣告的最大電流就在這裡。</summary>
    private static (int MaxPowerRaw, byte Attributes)? ConfigDescriptor(nint hub, int port)
        => Descriptor(hub, port, 0x02, 0, 0) is { } d ? UsbLinkDecoder.DecodeConfigDescriptor(d) : null;

    /// <summary>裝置自己回報的名稱：先取產品字串，沒有就退回製造商字串。兩個都沒有就是沒有。</summary>
    private static string? DeviceName(nint hub, int port, UsbConnectionInfo info)
    {
        ushort lang = LanguageId(hub, port);
        return StringDescriptor(hub, port, info.ProductIndex, lang)
            ?? StringDescriptor(hub, port, info.ManufacturerIndex, lang);
    }

    /// <summary>字串描述元 0 是這個裝置支援的語言清單，取第一個；問不到就用美式英文試。</summary>
    private static ushort LanguageId(nint hub, int port)
        => Descriptor(hub, port, 0x03, 0, 0) is { Length: >= 4 } d && d[1] == 3
            ? (ushort)(d[2] | d[3] << 8) : (ushort)0x0409;

    private static string? StringDescriptor(nint hub, int port, byte index, ushort lang)
        => index == 0 ? null
            : Descriptor(hub, port, 0x03, index, lang) is { } d ? UsbLinkDecoder.DecodeStringDescriptor(d) : null;

    /// <summary>
    /// 讀出 <c>USB_ROOT_HUB_NAME</c>／<c>USB_NODE_CONNECTION_NAME</c> 裡的字串（UTF-16LE，以 NUL 結尾）。
    /// 找不到結尾就回 null——不把整個緩衝區當成名字。
    /// </summary>
    private static string? NameAt(byte[] buf, int offset, uint returned)
    {
        int end = (int)Math.Min(returned, (uint)buf.Length);
        for (int i = offset; i + 1 < end; i += 2)
            if (buf[i] == 0 && buf[i + 1] == 0)
                return i > offset ? Encoding.Unicode.GetString(buf, offset, i - offset) : null;
        return null;
    }

    /// <summary>問這個埠上的集線器叫什麼，以便往下一層走。</summary>
    private static string? ChildHubName(nint hub, int port)
    {
        var buf = new byte[600];
        BitConverter.TryWriteBytes(buf.AsSpan(0), port);
        return DeviceIoControl(hub, IoctlConnName, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, 0)
            ? NameAt(buf, 8, ret) : null;
    }
}
