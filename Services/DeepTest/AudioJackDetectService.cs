using System.IO;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// U10-TS-0127「插孔偵測測試（插拔事件記錄）」的資料面起點：<c>ts.aud.jackdetect</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：音訊端點的 KS Proxy（<c>IKSControl</c>，由 <c>IMMDevice::Activate</c> 取得）上的
/// <c>KSPROPERTY_JACK_DESCRIPTION</c>（KSPROPSETID_Audio）——驅動回報的插孔描述
/// （每個插孔的連接類型、幾何位置、顏色、連接狀態）。usermode 唯讀、零特權、零網路。
/// </para>
/// <para>
/// 誠實界線：①本層讀的是<b>靜態插孔描述與當下連接位元</b>，不是插拔「事件」流——
/// 事件記錄屬 UI 層（需常駐監聽 KS 事件），不在這個服務；②不少驅動把 <c>IsConnected</c>
/// 寫死或恆回 false，該位元只能照實轉述、不能當權威判決；③端點不提供插孔屬性
/// （如 HDMI、虛擬裝置）如實標 NotSupported，不編「有插孔」。
/// </para>
/// </remarks>
public static class AudioJackDetectService
{
    private const string Category = "音訊";
    public const string Key = "ts.aud.jackdetect";
    /// <summary>逐端點家族前綴（成員＝端點索引）——名字刻意不含 Key，是家族前綴不是一把鍵。</summary>
    public const string JackDetectPrefix = "ts.aud.jackdetect.";
    public const string Source = "WASAPI 端點 KS Proxy KSPROPERTY_JACK_DESCRIPTION（KSPROPSETID_Audio；唯讀）";

    /// <summary>單一插孔的描述（KSJACK_DESCRIPTION 的可讀投影）。</summary>
    public sealed record JackRow(uint ConnectionType, uint GeoLocation, uint Color, bool IsConnected);

    /// <summary>單一端點的插孔資訊。</summary>
    public sealed record EndpointJacks(string Name, IReadOnlyList<JackRow> Jacks);

    /// <summary>收集插孔描述。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<EndpointJacks>>? endpoints = null)
    {
        var list = endpoints is null ? RealEndpoints() : endpoints();
        if (list.Count == 0)
            return [new HardwareFact(Key, Category, "音訊插孔描述", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "無作用中音訊端點或端點皆未提供插孔描述（如 HDMI／虛擬裝置）——如實標")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < list.Count; i++)
        {
            var (value, availability, reason) = Describe(list[i]);
            facts.Add(new HardwareFact($"{JackDetectPrefix}{i}", Category, $"插孔描述：{list[i].Name}", value, "", Source,
                FactTrustLevel.Reported, false, at, null, availability, reason));
        }
        return facts;
    }

    /// <summary>純函式：端點插孔清單 → 判讀。零插孔是 Unknown（來源可疑），不是「沒有插孔」。</summary>
    public static (string Value, FactAvailability Availability, string? UnavailableReason) Describe(
        EndpointJacks endpoint)
    {
        if (endpoint.Jacks.Count == 0)
            return ("", FactAvailability.Unknown,
                $"{endpoint.Name} 查詢成功但插孔數為 0——驅動未提供描述或資料不完整，不以 0 冒充");
        var parts = endpoint.Jacks.Select(j =>
            $"{DescribeConnectionType(j.ConnectionType)}"
            + (j.GeoLocation > 0 ? $"／{DescribeGeoLocation(j.GeoLocation)}" : "")
            + $"（{(j.IsConnected ? "已連接" : "未連接")}）");
        return ($"{endpoint.Jacks.Count} 個插孔：{string.Join("、", parts)}；"
            + "IsConnected 位元照實轉述——部分驅動此位元不反映實體狀態",
            FactAvailability.Present, null);
    }

    /// <summary>純函式：連接類型代碼（ksmedia.h KSJACK_DESCRIPTION_CONNECTION_TYPE）→ 文字。</summary>
    public static string DescribeConnectionType(uint type) => type switch
    {
        0 => "未知",
        1 => "3.5mm 類比",
        2 => "6.3mm 類比",
        3 => "ATAPI 類比",
        4 => "RCA 類比",
        5 => "光纖",
        6 => "其他數位",
        7 => "其他類比",
        8 => "多聲道類比 DIN",
        9 => "XLR 類比",
        10 => "RJ11 數據機",
        11 => "組合插孔",
        _ => $"未知類型 {type}",
    };

    /// <summary>純函式：幾何位置代碼（ksmedia.h KSJACK_GEO_LOCATION）→ 文字（主要者）。</summary>
    public static string DescribeGeoLocation(uint geo) => geo switch
    {
        0x1 => "外部後方",
        0x2 => "外部前方",
        0x3 => "外部左側",
        0x4 => "外部右側",
        0x5 => "外部頂部",
        0x6 => "外部底部",
        0x7 => "內部",
        0x8 => "分離式（Seperate，如耳麥分線）",
        0x9 => "其他",
        _ => $"位置代碼 {geo}",
    };

    /// <summary>
    /// 純函式：KSPROPERTY_JACK_DESCRIPTION 的資料區（KSMULTIPLE_ITEM + KSJACK_DESCRIPTION[]）解碼。
    /// 結構尺寸或計數不符時如實拒絕，不硬切。
    /// </summary>
    [SpecRef("Windows SDK km/ksmedia.h：KSJACK_DESCRIPTION（7×DWORD，28 位元組）與"
        + " KSPROPERTY_JACK_DESCRIPTION（KSPROPSETID_Audio 屬性 2）；Windows SDK mmreg.h／portcls：KSMULTIPLE_ITEM（Size、Count 各 4 位元組）")]
    public static IReadOnlyList<JackRow> ParseJackBuffer(byte[] data)
    {
        if (data.Length < 8)
            throw new ArgumentException($"資料區只有 {data.Length} 位元組，連 KSMULTIPLE_ITEM 標頭都不足。", nameof(data));
        uint declaredSize = BitConverter.ToUInt32(data, 0);
        uint count = BitConverter.ToUInt32(data, 4);
        if (count == 0 || count > 256) throw new InvalidDataException($"插孔計數 {count} 越界（0 或超過 256）——資料不可信。");
        if (declaredSize != 8 + count * JackStructSize)
            throw new InvalidDataException($"KSMULTIPLE_ITEM.Size {declaredSize} 與計數 {count}×{JackStructSize}+8 不符——資料不可信。");
        if (data.Length < (int)declaredSize)
            throw new InvalidDataException($"回報大小 {declaredSize} 位元組，實際只收到 {data.Length} 位元組——資料被截斷。");
        var jacks = new List<JackRow>((int)count);
        for (uint i = 0; i < count; i++)
        {
            int off = 8 + (int)(i * JackStructSize);
            jacks.Add(new JackRow(
                BitConverter.ToUInt32(data, off + 8),  // ConnectionType
                BitConverter.ToUInt32(data, off + 12), // GeoLocation
                BitConverter.ToUInt32(data, off + 4),  // Color
                BitConverter.ToUInt32(data, off + 24) != 0)); // IsConnected
        }
        return jacks;
    }

    private const int JackStructSize = 28; // ChannelMapping, Color, ConnectionType, GeoLocation, GenLocation, PortConnection, IsConnected（BOOL）

    // ── 真實 KS 通路（薄；測試以注入取代）──

    private static IReadOnlyList<EndpointJacks> RealEndpoints()
    {
        var result = new List<EndpointJacks>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            foreach (var flow in new uint[] { 0 /*eRender*/, 1 /*eCapture*/ })
            {
                if (enumerator.EnumAudioEndpoints(flow, 1 /*DEVICE_STATE_ACTIVE*/, out var collection) != 0) continue;
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var device);
                    string name = ReadFriendlyName(device) ?? $"端點 {result.Count}";
                    var jacks = ReadJacks(device);
                    if (jacks is { } list2) result.Add(new EndpointJacks(name, list2));
                }
            }
        }
        catch { /* 音訊列舉為附加功能，失敗由上層標三態 */ }
        return result;
    }

    private static IReadOnlyList<JackRow>? ReadJacks(IMMDevice device)
    {
        var iidKsControl = new Guid("28F54685-06FD-11D2-B27A-00A0C9223196"); // IID_IKSControl
        if (device.Activate(ref iidKsControl, 1 /*CLSCTX_INPROC_SERVER*/, nint.Zero, out nint ksControl) != 0)
            return null;
        try
        {
            var request = new KsPropertyRequest
            {
                Set = new Guid("45FFAAA0-6E1B-11D0-BCF2-444553540000"), // KSPROPSETID_Audio
                Id = 2,                                                 // KSPROPERTY_JACK_DESCRIPTION
                Flags = 1,                                              // KSPROPERTY_TYPE_GET
            };
            var buffer = new byte[8 + 256 * JackStructSize];
            int hr = ((IKSControl)Marshal.GetObjectForIUnknown(ksControl)).KsProperty(
                ref request, Marshal.SizeOf<KsPropertyRequest>(), buffer, buffer.Length, out int returned);
            if (hr != 0 || returned < 8) return null;
            var data = new byte[returned];
            Buffer.BlockCopy(buffer, 0, data, 0, returned);
            return ParseJackBuffer(data);
        }
        catch { return null; }
        finally { Marshal.Release(ksControl); }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0 /*STGM_READ*/, out var store);
            var key = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14); // PKEY_Device_FriendlyName
            store.GetValue(ref key, out var pv);
            return pv.VarType == 31 /*VT_LPWSTR*/ && pv.PointerValue != nint.Zero
                ? Marshal.PtrToStringUni(pv.PointerValue) : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsPropertyRequest { public Guid Set; public uint Id; public uint Flags; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(uint dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(uint dataFlow, uint role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice(string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsContext, nint activationParams, out nint iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKSControl
    {
        [PreserveSig] int KsProperty(ref KsPropertyRequest propertyRequest, int propertyRequestSize,
            [In, Out] byte[] propertyData, int dataSize, out int bytesReturned);
    }

    // .NET ComTypes 只有 VARIANT 沒有 PROPVARIANT——自訂最小版（x64：vt 在 0、聯合在 8）。
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public nint PointerValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId; public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }
}
