using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// U10-TS-0145「觸控筆壓力階數測試」的資料面：<c>ts.pen.pressure</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：Windows Pointer 輸入堆疊（usermode 唯讀、零特權）——
/// <c>GetPointerDevices</c> 列舉指標裝置、<c>GetPointerDeviceProperties</c> 取各裝置的 HID 軸屬性；
/// 壓力軸即 HID Usage Table 的 Digitizers 頁（0x0D）之 Tip Pressure（0x30），
/// 階數＝LogicalMax−LogicalMin+1。
/// </para>
/// <para>
/// 誠實界線：本層回報<b>裝置宣告的壓力軸範圍</b>（能力面）；「實際壓出幾階」需要使用者施力
/// （互動層）。沒有筆裝置＝NotApplicable（環境事實）；筆存在但無壓力軸＝NotSupported；
/// 宣告範圍為 0 的來源可疑，標 Unknown 不拿 0 冒充。
/// </para>
/// </remarks>
public static class PenPressureService
{
    private const string Category = "輸入";
    public const string Key = "ts.pen.pressure";
    /// <summary>逐筆裝置家族前綴（成員＝筆索引）——名字刻意不含 Key，是家族前綴不是一把鍵。</summary>
    public const string PressurePrefix = "ts.pen.pressure.";
    public const string Source = "Windows GetPointerDevices／GetPointerDeviceProperties（唯讀）";

    /// <summary>HID 軸屬性的可讀投影。</summary>
    public sealed record PointerAxis(int UsagePage, int Usage, int LogicalMin, int LogicalMax);

    /// <summary>單一指標裝置的屬性列。</summary>
    public sealed record PointerDeviceRow(string Name, string DeviceType, IReadOnlyList<PointerAxis> Axes);

    public const int DigitizersUsagePage = 0x0D;
    public const int TipPressureUsage = 0x30;

    /// <summary>收集筆壓力能力。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<PointerDeviceRow>>? probe = null)
    {
        IReadOnlyList<PointerDeviceRow> devices;
        try { devices = probe is { } p ? p() : RealProbe(); }
        catch (Exception ex)
        {
            return [new HardwareFact(Key, Category, "觸控筆壓力能力", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "指標裝置列舉失敗：" + ex.Message)];
        }
        return BuildFacts(at, devices);
    }

    /// <summary>純函式：裝置清單 → 事實（六態傳播）。</summary>
    public static IReadOnlyList<HardwareFact> BuildFacts(DateTimeOffset at, IReadOnlyList<PointerDeviceRow> devices)
    {
        var pens = devices.Where(d => d.DeviceType.Contains("筆")
            || d.DeviceType.Contains("Pen", StringComparison.OrdinalIgnoreCase)).ToList();
        if (devices.Count == 0)
            return [new HardwareFact(Key, Category, "觸控筆壓力能力", "", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                "本機未列舉到任何指標數位板裝置（沒有筆/觸控是環境事實）")];
        if (pens.Count == 0)
            return [new HardwareFact(Key, Category, "觸控筆壓力能力", "", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                $"有 {devices.Count} 個指標裝置（{string.Join("、", devices.Select(d => d.DeviceType).Distinct())}），但沒有筆類裝置")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < pens.Count; i++)
        {
            facts.Add(BuildOne($"{PressurePrefix}{i}", at, pens[i]));
        }
        return facts;
    }

    private static HardwareFact BuildOne(string key, DateTimeOffset at, PointerDeviceRow device)
    {
        var pressure = device.Axes.FirstOrDefault(a => a.UsagePage == DigitizersUsagePage && a.Usage == TipPressureUsage);
        if (pressure is null)
            return new HardwareFact(key, Category, $"筆壓力能力：{device.Name}", "", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotSupported,
                $"{device.DeviceType}「{device.Name}」未宣告 Tip Pressure（UsagePage 0x0D／Usage 0x30）軸——此筆不回報壓力");
        int levels = pressure.LogicalMax - pressure.LogicalMin + 1;
        if (levels <= 0)
            return new HardwareFact(key, Category, $"筆壓力能力：{device.Name}",
                $"宣告範圍 [{pressure.LogicalMin}..{pressure.LogicalMax}]", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Unknown,
                "壓力軸範圍非正數——來源可疑，不得當已確認值使用");
        return new HardwareFact(key, Category, $"筆壓力能力：{device.Name}",
            $"{device.DeviceType}「{device.Name}」：壓力軸 [{pressure.LogicalMin}..{pressure.LogicalMax}]，共 {levels} 階",
            "階", Source, FactTrustLevel.Reported, false, at, levels, FactAvailability.Present, null);
    }

    // ── 真實 Pointer 堆疊通路（薄；測試以注入取代）──
    // 來源：Windows SDK um/winuser.h——POINTER_DEVICE_INFO、POINTER_DEVICE_PROPERTY、
    // GetPointerDevices／GetPointerDeviceProperties；壓力軸依 HID Usage Tables
    // （Digitizers 頁 0x0D、Tip Pressure 0x30）。本機筆裝置未驗證時如實落 ReadError。

    private static IReadOnlyList<PointerDeviceRow> RealProbe()
    {
        var result = new List<PointerDeviceRow>();
        if (!GetPointerDevices(out uint count, nint.Zero) || count == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)(count * Marshal.SizeOf<PointerDeviceInfo>()));
        try
        {
            if (!GetPointerDevices(out count, buffer)) return result;
            for (uint i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PointerDeviceInfo>(buffer + (int)(i * Marshal.SizeOf<PointerDeviceInfo>()));
                string type = info.DeviceType switch
                {
                    0 => "內建筆 (Integrated Pen)",
                    1 => "外接筆 (External Pen)",
                    2 => "觸控 (Touch)",
                    3 => "觸控板 (Touchpad)",
                    _ => $"指標裝置類型 {info.DeviceType}",
                };
                string name = string.IsNullOrWhiteSpace(info.ProductString) ? type : info.ProductString;
                var axes = ReadProperties(info.Device);
                result.Add(new PointerDeviceRow(name, type, axes));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private static IReadOnlyList<PointerAxis> ReadProperties(nint device)
    {
        var axes = new List<PointerAxis>();
        if (!GetPointerDeviceProperties(device, out uint count, nint.Zero) || count == 0) return axes;
        var buffer = Marshal.AllocHGlobal((int)(count * Marshal.SizeOf<PointerDeviceProperty>()));
        try
        {
            if (!GetPointerDeviceProperties(device, out count, buffer)) return axes;
            for (uint i = 0; i < count; i++)
            {
                var p = Marshal.PtrToStructure<PointerDeviceProperty>(buffer + (int)(i * Marshal.SizeOf<PointerDeviceProperty>()));
                axes.Add(new PointerAxis(p.UsagePageId, p.UsageId, p.LogicalMin, p.LogicalMax));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return axes;
    }

    private const int ProductStringMax = 520;   // POINTER_DEVICE_PRODUCT_STRING_MAX
    private const int PropertyNameMax = 128;    // POINTER_DEVICE_PROPERTY_NAME_MAX

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PointerDeviceInfo
    {
        public uint Size;
        public uint DeviceType;
        public nint Device;
        public uint PointerDeviceTotalMemory;
        public uint MaxInputReports;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ProductStringMax)] public string ProductString = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ProductStringMax)] public string VendorString = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ProductStringMax)] public string DeviceString = "";
        public PointerDeviceInfo() { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PointerDeviceProperty
    {
        public int LogicalMin;
        public int LogicalMax;
        public uint Unit;
        public uint UnitExponent;
        public ushort UsagePageId;
        public ushort UsageId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PropertyNameMax)] public string Name = "";
        public PointerDeviceProperty() { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerDevices(out uint deviceCount, nint pointerDevices);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerDeviceProperties(nint device, out uint propertyCount, nint pointerProperties);
}
