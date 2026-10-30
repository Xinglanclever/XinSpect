using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>單一顆根集線器的查詢結果。<c>Ports</c> 為 null 時 <c>Error</c> 說明原因。</summary>
public sealed record UsbHubRead(string InstanceId, ushort? Ports, uint Error, string? Why);

/// <summary>
/// 根集線器埠數的注入式接縫：生產走「以裝置介面路徑開檔 ＋ <c>IOCTL_USB_GET_NODE_INFORMATION</c>」，
/// 測試餵固定結果，不開裝置。
/// </summary>
public interface IUsbHubProbe
{
    UsbHubRead Read(string instanceId);
}

/// <summary>
/// 生產通路：把裝置 instance ID 換成 hub 的裝置介面路徑，唯讀查一個<b>不含資料傳輸</b>的 IOCTL，用完即關。
/// </summary>
/// <remarks>
/// 開檔需要 <c>GENERIC_READ|GENERIC_WRITE</c>（usbhub 驅動的介面要求），所以非管理員會拿到
/// <c>ERROR_ACCESS_DENIED</c>——那要如實標 <see cref="FactAvailability.InsufficientPrivilege"/>，
/// 不是「沒有集線器」。
/// </remarks>
public sealed class SystemUsbHubProbe : IUsbHubProbe
{
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    /// <summary><c>IOCTL_USB_GET_NODE_INFORMATION</c>＝CTL_CODE(FILE_DEVICE_USB, 258, METHOD_BUFFERED, FILE_ANY_ACCESS)。</summary>
    public const uint IoctlGetNodeInformation = 0x00220408;

    /// <summary>hub 的裝置介面類別 GUID：{F18A0E88-C30C-11D0-8815-00A0C906BED8}。</summary>
    public const string HubInterfaceGuid = "{f18a0e88-c30c-11d0-8815-00a0c906bed8}";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[]? inBuffer, uint inSize,
        byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>instance ID → 裝置介面路徑。範例：<c>USB\ROOT_HUB30\5&amp;7D03CA&amp;0&amp;0</c>。</summary>
    public static string InterfacePath(string instanceId) =>
        @"\\?\" + instanceId.ToLowerInvariant().Replace('\\', '#') + "#" + HubInterfaceGuid;

    /// <summary>
    /// 單顆根的查詢上限。健康的 hub 對這個 IOCTL 是毫秒級；到了秒級就已經不對了
    /// （休眠喚醒中的 hub、韌體卡住的 xHCI 控制器）。
    /// </summary>
    public const int TimeoutMs = 2000;

    public UsbHubRead Read(string instanceId)
    {
        // 第 324 輪（D8）：與 StorageSmartService 同一條規則——**會卡在核心裡的同步 IOCTL 要圈起來**。
        // 這個 IOCTL 沒有 overlapped 版本可用，所以用 DiskIo 的「逾時就不等它」策略：
        // 洩掉一條執行緒，換整條收集不被一顆壞 hub 凍住。
        //
        // 逾時與「讀不到」必須分開講：逾時是「這顆 hub 沒回應」，
        // 而 ReadError 是「它回了，但回的東西不能用」。兩者混在一起，
        // 讀者就分不出「該重開機」還是「該查驅動」。
        var (result, timedOut) = DiskIo.GuardedProbe(() => ReadCore(instanceId), TimeoutMs);
        if (timedOut)
            return new UsbHubRead(instanceId, null, 0,
                $"查詢逾時（{TimeoutMs} ms 內沒回來）——這顆 hub 沒回應，不是它說「沒有埠」");
        return result ?? new UsbHubRead(instanceId, null, 0, "查詢沒有回傳結果——不猜");
    }

    private static UsbHubRead ReadCore(string instanceId)
    {
        IntPtr handle = CreateFileW(InterfacePath(instanceId), GenericReadWrite, ShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            uint err = (uint)Marshal.GetLastWin32Error();
            return new UsbHubRead(instanceId, null, err, "開檔失敗（" + err + "）");
        }
        try
        {
            // 這個 IOCTL 的 input 就是 USB_NODE_INFORMATION 本身（NodeType 要填 UsbHub＝0，
            // 零初始化等同填好）；in/out 用同一個緩衝區，與 UsbLinkService.WalkHub 同一個形狀。
            // 傳 null input 會拿到 ERROR_INVALID_PARAMETER（87），看起來像「這台機器讀不到」。
            var buffer = new byte[256];
            if (!DeviceIoControl(handle, IoctlGetNodeInformation, buffer, (uint)buffer.Length,
                    buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero))
            {
                uint err = (uint)Marshal.GetLastWin32Error();
                return new UsbHubRead(instanceId, null, err, "IOCTL 失敗（" + err + "）");
            }
            var node = UsbHubNodeDecoder.ParseNodeInformation(buffer[..(int)returned]);
            if (node is null)
                return new UsbHubRead(instanceId, null, 0,
                    $"輸出 {returned} 位元組但解不成 hub 節點——不猜");
            if (!node.IsHubDescriptor)
                return new UsbHubRead(instanceId, null, 0,
                    $"描述符型別 0x{node.DescriptorType:X2}／長度 {node.DescriptorLength} 不是 hub descriptor——不採信埠數");
            return new UsbHubRead(instanceId, node.NumberOfPorts, 0, null);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>
/// USB 根集線器的埠數事實（U10 第三圈 NP-0003）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>「接了幾個、還能接幾個」。既有的三把 USB 事實只做計數與「誰掛得最多」，
/// 沒有任何一把回答「還有沒有空埠」。
/// </para>
/// <para>
/// <b>兩個來源、各答一半：</b>埠數只有硬體知道（hub descriptor），在場子裝置數只有 PnP 樹知道。
/// 這一輪把兩邊並排——這也是「空埠」唯一誠實的算法：<b>埠數 − 在場子裝置數</b>。
/// 而當子裝置數<b>比埠數多</b>時，那個減法就沒有意義（複合裝置、內建裝置不佔獨立埠），
/// 此時如實說「這欄在這裡不成立」，而不是印一個負數。
/// </para>
/// <para>
/// <b>沒有新增列舉：</b>根集線器清單與子裝置數都來自 <see cref="DeviceDiagnosticReport"/>，
/// 與 <c>os.dev.*</c>、<c>os.devtree.*</c> 同一次列舉。新增的只有每顆根一個唯讀 IOCTL。
/// </para>
/// </remarks>
public static class UsbRootHubFactsService
{
    public const string Category = "週邊匯流排";
    public const string SummaryKey = "usb.hub.summary";
    public const string BoundaryKey = "usb.hub.boundary";
    public const string PortsPrefix = "usb.hub.ports.";

    private const string Source = "SetupAPI 裝置清單 ＋ hub descriptor（IOCTL_USB_GET_NODE_INFORMATION，唯讀）";

    public static IReadOnlyList<HardwareFact> CollectForThisMachine(
        DeviceDiagnosticReport report, DateTimeOffset at, IUsbHubProbe? probe = null) =>
        Collect(report, at, probe ?? new SystemUsbHubProbe());

    public static IReadOnlyList<HardwareFact> Collect(
        DeviceDiagnosticReport report, DateTimeOffset at, IUsbHubProbe probe)
    {
        // 只數**在場**的根。ghost（換過主板、拆掉擴充卡之後留下的殘影）也在 Devices 裡，
        // 但它沒有埠可數：把它算進「幾顆根」會讓總覽多一顆，而那一顆永遠回「開檔失敗」——
        // 於是「這台機器有 4 顆根、其中 1 顆讀不到」會被誤讀成硬體問題，實際上是歷史殘影。
        var allHubs = report.Devices
            .Where(d => d.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.InstanceId, StringComparer.Ordinal)
            .ToList();
        var hubs = allHubs.Where(d => d.IsPresent).ToList();
        int ghosts = allHubs.Count - hubs.Count;

        if (hubs.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "USB 根集線器埠數彙總", "", "埠", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    ghosts > 0
                        ? $"這份清單裡有 {ghosts} 顆根集線器但**都不在場**（歷史殘影）——沒有可數的埠；"
                          + "不是「讀不到埠數」"
                        : "這份裝置清單裡沒有任何 USB 根集線器——不是「讀不到埠數」，是這台機器沒有根集線器可數"),
                BoundaryFact(at),
            ];

        var facts = new List<HardwareFact>();
        int index = 0;
        int totalPorts = 0, totalChildren = 0, unreadable = 0, unknownPorts = 0;
        foreach (var hub in hubs)
        {
            index++;
            var read = probe.Read(hub.InstanceId);
            int children = hub.Children.Count(c => c.IsPresent);
            totalChildren += children;

            if (read.Ports is not { } ports)
            {
                unreadable++;
                facts.Add(new HardwareFact(PortsPrefix + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
                    Category, $"根集線器 {index}（{HubKind(hub.InstanceId)}）埠數",
                    $"埠數讀不到：{read.Why}・在場子裝置 {children} 個（PnP 樹這一邊讀得到）", "埠", Source,
                    FactTrustLevel.Unknown, false, at, null,
                    read.Error == 5 ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError,
                    "埠數需要對 hub 介面的讀寫開檔權（管理員）；讀不到不等於「沒有埠」"));
                continue;
            }

            totalPorts += ports;
            if (children > ports) unknownPorts++;
            facts.Add(PortsFact(index, hub.InstanceId, ports, children, at));
        }

        facts.Add(SummaryFact(hubs.Count, ghosts, totalPorts, totalChildren, unreadable, unknownPorts, at));
        facts.Add(BoundaryFact(at));
        return facts;
    }

    private static HardwareFact PortsFact(int index, string instanceId, ushort ports, int children, DateTimeOffset at)
    {
        string free = children > ports
            ? $"**在場子裝置 {children} 個比埠數多**——空埠數在這裡不成立（複合裝置／內建裝置可不佔獨立埠），**不給數字**"
            : $"空埠 {ports - children} 個（埠數 − 在場子裝置數）";
        string value = $"{HubKind(instanceId)}：{ports} 個埠・在場子裝置 {children} 個・{free}"
            + $"・控制器 {ParentOf(instanceId)}"
            + "・界線：埠數是硬體描述，不是「這台機器可用幾個」——每個埠的實際協商速度不在本鍵裡";
        return new HardwareFact(PortsPrefix + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            Category, $"根集線器 {index}（{HubKind(instanceId)}）埠數", value, "埠", Source,
            FactTrustLevel.Measured, false, at, ports);
    }

    private static HardwareFact SummaryFact(int hubs, int ghosts, int ports, int children, int unreadable,
        int unknownPorts, DateTimeOffset at)
    {
        string portsFace = unreadable == 0
            ? $"共 {ports} 個埠"
            : $"共 {ports} 個埠（另有 {unreadable} 顆根集線器的埠數讀不到，**不計入這個數**）";
        string freeFace = unreadable > 0 || unknownPorts > 0
            ? "空埠總數**不給**："
              + (unreadable > 0 ? $"有 {unreadable} 顆根集線器的埠數讀不到；" : "")
              + (unknownPorts > 0 ? $"{unknownPorts} 顆根的在場子裝置比埠數多（複合／內建裝置不佔獨立埠）；" : "")
              + "任何一種情形下「埠數 − 子裝置數」都不是可靠的空埠數"
            : $"空埠 {ports - children} 個（埠數 − 在場子裝置數）";
        string value = $"{hubs} 顆 USB 根集線器"
            + (ghosts > 0 ? $"（另有 {ghosts} 顆不在場的殘影未計入）" : "")
            + $"・{portsFace}・在場子裝置 {children} 個・{freeFace}"
            + "・用途：這一欄回答「還能不能再接」的量級，不是頻寬或供電餘裕——那些要看控制器與裝置本身";
        return new HardwareFact(SummaryKey, Category, "USB 根集線器埠數彙總", value, "埠", Source,
            FactTrustLevel.Derived, false, at, unreadable == 0 ? ports : null,
            unreadable == 0 ? FactAvailability.Present : FactAvailability.Unknown,
            unreadable == 0 ? null
                : $"有 {unreadable} 顆根集線器的埠數讀不到，總數不完整（已知的 {ports} 個只是下限）");
    }

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "USB 埠事實的界線宣告",
            "① 控制器**版本**（xHCI 的 CAPLENGTH 之後那個版本暫存器）與**支援速度位**在控制器 MMIO（PCI BAR），"
            + "usermode 讀不到——本版不猜，也不從驅動版本反推；"
            + "② 本版只數**根集線器**的埠，不遞迴外接集線器（那要逐層開檔，屬明示觸發）；"
            + "③ 每個埠的**協商速度**（USB_NODE_CONNECTION_INFORMATION_EX 的 Speed）本版未讀；"
            + "④ 埠數是**硬體描述**，不是「可用頻寬」——20 個空埠不等於能插 20 顆大功率裝置",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);

    private static string HubKind(string instanceId)
    {
        // 先比長的前綴：ROOT_HUB20 會命中 ROOT_HUB。
        if (instanceId.Contains("ROOT_HUB30", StringComparison.OrdinalIgnoreCase)) return "USB 3.x（xHCI）";
        if (instanceId.Contains("ROOT_HUB20", StringComparison.OrdinalIgnoreCase)) return "USB 2.0（EHCI）";
        return "USB 1.1（UHCI／OHCI）";
    }

    /// <summary>
    /// 控制器那一格。instance ID 形如 <c>USB\ROOT_HUB30\5&amp;7D03CA&amp;0&amp;0</c>——
    /// 尾巴是匯流排相對位址，不含序號，所以可以列；真正的控制器 ID 在 PnP 樹的父節點上，
    /// 這裡只取得到尾巴，就只說尾巴。
    /// </summary>
    private static string ParentOf(string instanceId)
    {
        int last = instanceId.LastIndexOf('\\');
        return last > 0 ? $"實例 {instanceId[(last + 1)..]}（父節點請看 os.devtree.*）" : "未知";
    }
}
