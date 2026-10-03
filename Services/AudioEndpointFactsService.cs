using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// WAVEFORMATEX／WAVEFORMATEXTENSIBLE 的純解碼器（V7 WP23／A15）：
/// 混合格式（PKEY_AudioEngine_DeviceFormat）的聲道數、取樣率、位元深度。
/// 垃圾（聲道 0、取樣率 0）如實回 null——不解碼垃圾。
/// </summary>
public static class AudioFormatDecoder
{
    /// <summary>解 WAVEFORMATEX（至少 16 位元組；EXTENSIBLE 須 ≥ 40）。格式異常回 null。</summary>
    [SpecRef("Microsoft WAVEFORMATEX 佈局：wFormatTag u16、nChannels u16、nSamplesPerSec u32、nAvgBytesPerSec u32、nBlockAlign u16、wBitsPerSample u16、cbSize u16；EXTENSIBLE（0xFFFE）接 validBits u16、channelMask u32、SubFormat GUID")]
    public static (ushort Channels, uint SamplesPerSec, ushort Bits, string TagText)? Parse(byte[] data)
    {
        if (data.Length < 16) return null;
        ushort tag = GetU16(data, 0);
        ushort channels = GetU16(data, 2);
        uint rate = GetU32(data, 4);
        ushort bits = GetU16(data, 14);
        if (channels == 0 || channels > 64 || rate == 0 || rate > 2_000_000) return null;

        string tagText = tag switch
        {
            1 => "PCM",
            3 => "IEEE 浮點",
            0xFFFE => data.Length >= 40 ? "可延伸（EXTENSIBLE）" : "可延伸（宣告不足 40 位元組，擴充欄位缺失）",
            _ => $"formatTag 0x{tag:X4}",
        };
        return (channels, rate, bits, tagText);
    }

    /// <summary>端點一行描述。</summary>
    public static string Describe((ushort Channels, uint SamplesPerSec, ushort Bits, string TagText) f)
        => $"{f.Channels} 聲道、{f.SamplesPerSec} Hz、{f.Bits}-bit（{f.TagText}）";

    private static ushort GetU16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
    private static uint GetU32(byte[] b, int off) => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}

/// <summary>
/// 音訊端點混合格式事實（V7 WP23／A15）：列舉作用中端點（渲染＋擷取），報裝置名與混合格式。
/// 通路是 Windows MMDevice API（COM，usermode）——「裝置宣稱的能力」與「引擎實際混合格式」是兩件事，
/// 本層報後者。解析失敗如實帶原始位元組數。
/// </summary>
public static class AudioEndpointFactsService
{
    private const string Category = "音訊";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<(string Name, byte[]? Format)>>? endpoints = null)
    {
        var list = (endpoints ?? RealEndpoints)();
        if (list.Count == 0)
            return [Unavailable(at, "MMDevice API 無作用中音訊端點（或列舉失敗）——如實標")];

        var facts = new List<HardwareFact>();
        int i = 0;
        foreach (var (name, format) in list)
        {
            string value = format is null || format.Length < 16
                ? $"混合格式未提供或過短（{format?.Length ?? 0} 位元組）——不解碼"
                : AudioFormatDecoder.Parse(format) is { } f ? AudioFormatDecoder.Describe(f)
                : $"格式無法解析（原始 {format.Length} 位元組，聲道或取樣率越界）";
            facts.Add(new HardwareFact($"audio.endpoint.{i}", Category, $"音訊端點 {name}", value,
                "", "Windows MMDevice（PKEY_AudioEngine_DeviceFormat）", FactTrustLevel.Reported, false, at));
            i++;
        }
        return facts;
    }

    // ── 真實 MMDevice 通路（薄；測試以注入委派取代）──

    private static IReadOnlyList<(string, byte[]?)> RealEndpoints()
    {
        var result = new List<(string, byte[]?)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            foreach (var flow in new uint[] { 0, 1 }) // eRender, eCapture
            {
                if (enumerator.EnumAudioEndpoints(flow, 1 /*DEVICE_STATE_ACTIVE*/, out var collection) != 0) continue;
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var device);
                    device.OpenPropertyStore(0 /*STGM_READ*/, out var store);
                    string name = ReadString(store, DeviceFriendlyName) ?? $"端點 {result.Count}";
                    result.Add((name, ReadBlob(store, AudioEngineDeviceFormat)));
                }
            }
        }
        catch { /* 音訊列舉為附加功能，失敗由上層標三態 */ }
        return result;
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out var pv);
        return pv.VarType == 31 /*VT_LPWSTR*/ && pv.PointerValue != nint.Zero
            ? System.Runtime.InteropServices.Marshal.PtrToStringUni(pv.PointerValue)
            : null;
    }

    private static byte[]? ReadBlob(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out var pv);
        if (pv.VarType != 65 /*VT_BLOB*/ || pv.Blob.Size <= 0 || pv.Blob.Data == nint.Zero) return null;
        var bytes = new byte[pv.Blob.Size];
        System.Runtime.InteropServices.Marshal.Copy(pv.Blob.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    // .NET ComTypes 只有 VARIANT 沒有 PROPVARIANT——自訂最小版（x64：vt 在 0、聯合在 8）。
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public nint PointerValue;
        [FieldOffset(8)] public NativeBlob Blob;

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeBlob { public int Size; public nint Data; }
    }

    private static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly PropertyKey AudioEngineDeviceFormat = new(new Guid("F19F064D-082C-4E27-BC73-6882A1BB8E4C"), 0);

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

    private static HardwareFact Unavailable(DateTimeOffset at, string reason) =>
        new("audio.endpoints", Category, "音訊端點", "", "", "Windows MMDevice API", FactTrustLevel.Unknown, false, at,
            null, FactAvailability.NotSupported, reason);
}
