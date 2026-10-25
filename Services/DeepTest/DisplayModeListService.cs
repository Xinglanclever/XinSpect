using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// U10-TS-0112「全模式清單逐模式顯示」的資料面：<c>ts.disp.modes.cycle</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：Windows 顯示驅動的模式清單（<c>EnumDisplaySettingsEx</c>，usermode 唯讀、零特權）。
/// 本層只做<b>模式清單列舉</b>（解析度×色深×刷新的離散集合）；「逐模式切換顯示畫面」
/// 是 UI 層的事——切換會改動顯示狀態，不在這個唯讀層做。
/// </para>
/// <para>
/// 誠實界線：模式清單是驅動回報的<b>可設定</b>集合，不是「這些模式都保證正確顯示」；
/// 列舉不到裝置（無桌面會話、RDP 斷線後）如實標 NotSupported。
/// </para>
/// </remarks>
public static class DisplayModeListService
{
    private const string Category = "顯示";
    public const string Key = "ts.disp.modes.cycle";
    /// <summary>逐裝置家族前綴（成員＝裝置索引）——名字刻意不含 Key，是家族前綴不是一把鍵。</summary>
    public const string ModesCyclePrefix = "ts.disp.modes.cycle.";
    public const string Source = "Windows 顯示驅動模式清單 EnumDisplaySettingsEx（usermode 唯讀）";

    /// <summary>單一顯示裝置的模式列舉結果。</summary>
    public sealed record DisplayDeviceModes(string DeviceName, string DeviceString,
        IReadOnlyList<ModeRow> Modes);

    /// <summary>一個離散模式：寬、高、位元深度、刷新率。</summary>
    public sealed record ModeRow(uint Width, uint Height, uint BitsPerPixel, uint RefreshHz);

    /// <summary>收集模式清單。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<DisplayDeviceModes>>? probe = null)
    {
        IReadOnlyList<DisplayDeviceModes> devices;
        try { devices = (probe ?? RealProbe)(); }
        catch (Exception ex)
        {
            return [Fail(at, FactAvailability.ReadError, "顯示模式列舉失敗：" + ex.Message)];
        }
        if (devices.Count == 0)
            return [Fail(at, FactAvailability.NotSupported,
                "列舉不到任何顯示裝置（無作用中桌面或顯示子系統未就緒）")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var (value, availability, reason) = Describe(d);
            facts.Add(new HardwareFact($"{ModesCyclePrefix}{i}", Category, $"顯示模式清單：{d.DeviceName}", value, "",
                Source, FactTrustLevel.Reported, false, at, null, availability, reason));
        }
        return facts;
    }

    /// <summary>純函式：單裝置模式清單 → 判讀。零模式是來源可疑（Unknown），不是 0 個模式。</summary>
    public static (string Value, FactAvailability Availability, string? UnavailableReason) Describe(
        DisplayDeviceModes device)
    {
        var modes = device.Modes
            .Distinct()
            .ToList(); // 注入清單或驅動都可能給重複行——呈現前先去重，與 RealProbe 的 seen 一致
        if (modes.Count == 0)
            return ("", FactAvailability.Unknown,
                $"驅動對 {device.DeviceName} 回報了零個模式——列舉成功但清單空，來源可疑，不以 0 冒充");
        var resolutions = modes.Select(m => (m.Width, m.Height)).Distinct().Count();
        var refreshes = modes.Select(m => m.RefreshHz).Where(hz => hz > 0).Distinct().OrderBy(hz => hz).ToList();
        var depths = modes.Select(m => m.BitsPerPixel).Where(b => b > 0).Distinct().OrderBy(b => b).ToList();
        string value = $"{modes.Count} 個離散模式（{resolutions} 種解析度、{refreshes.Count} 種刷新率"
            + $"、色深 {string.Join("/", depths)} bit）；刷新率集合 {string.Join("/", refreshes)} Hz";
        return (value, FactAvailability.Present, null);
    }

    // ── 真實列舉通路（薄；測試以注入取代）──
    // 來源：Windows SDK wingdi.h／winuser.h——EnumDisplayDevicesW、EnumDisplaySettingsExW、
    // DEVMODEW（dmSize/dmDriverExtra 契約、ENUM_CURRENT_SETTINGS）。

    internal static IReadOnlyList<DisplayDeviceModes> RealProbe()
    {
        var result = new List<DisplayDeviceModes>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = new DISPLAY_DEVICE();
            adapter.cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>();
            if (EnumDisplayDevices(null, adapterIndex, ref adapter, 0) == 0) break;
            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = new DISPLAY_DEVICE();
                monitor.cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>();
                if (EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 0) == 0)
                {
                    result.Add(new DisplayDeviceModes(adapter.DeviceName, adapter.DeviceString, ReadModes(adapter.DeviceName)));
                    break;
                }
                if ((monitor.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 || monitorIndex == 0)
                {
                    result.Add(new DisplayDeviceModes(monitor.DeviceName, monitor.DeviceString, ReadModes(monitor.DeviceName)));
                    break;
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<ModeRow> ReadModes(string deviceName)
    {
        var modes = new List<ModeRow>();
        var seen = new HashSet<(uint, uint, uint, uint)>();
        for (uint modeIndex = 0; ; modeIndex++)
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettingsEx(deviceName, modeIndex, ref dm, 0) == 0) break;
            var row = new ModeRow(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmBitsPerPel, dm.dmDisplayFrequency);
            if (seen.Add((row.Width, row.Height, row.BitsPerPixel, row.RefreshHz))) modes.Add(row);
        }
        return modes;
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_ACTIVE = 0x10;
    private const uint ENUM_CURRENT_SETTINGS = unchecked(0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString = "";
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey = "";
        public DISPLAY_DEVICE() { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName = "";
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName = "";
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
        public DEVMODE() { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplaySettingsEx(string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    /// <summary>ENUM_CURRENT_SETTINGS 查詢（供刷新率核對取現值；唯讀）。</summary>
    internal static int QueryCurrentSettings(string deviceName, ref DEVMODE lpDevMode) =>
        EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref lpDevMode, 0);

    private static HardwareFact Fail(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(Key, Category, "顯示模式清單", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
