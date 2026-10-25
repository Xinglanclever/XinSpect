using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// U10-TS-0124「取樣率支援枚舉（資料面）」：<c>ts.aud.formats</c>。
/// </summary>
/// <remarks>
/// <para>
/// 對每個作用中渲染端點，以 WASAPI <c>IAudioClient::IsFormatSupported</c>（共用模式查詢，
/// <b>不呼叫 Initialize、不 Start、不佔用裝置</b>——這在未初始化的 client 上是合法的純查詢）
/// 逐組探測取樣率×位元深度，回報<b>端點宣稱支援</b>的集合。
/// 「裝置宣稱支援」與「引擎實際混合格式」（<c>audio.endpoint.*</c> 已收口）是兩件事：
/// 前者是能力面，後者是現況面，數字不可互相推論。
/// </para>
/// <para>
/// 誠實界線：探測結果是「共用模式下引擎是否接受」，不是「硬體 D/A 能力上限」；
/// 嚴格以 S_OK 為支援——S_FALSE（引擎回最接近格式）記為「不直接支援」，不擅自替換格式；
/// 全部被拒的端點照實列（可能是獨占鎖定或驅動限制），不猜測原因。
/// </para>
/// </remarks>
public static class AudioFormatProbeService
{
    private const string Category = "音訊";
    public const string Key = "ts.aud.formats";
    /// <summary>逐端點家族前綴（成員＝端點索引）——名字刻意不含 Key，是家族前綴不是一把鍵。</summary>
    public const string FormatsPrefix = "ts.aud.formats.";
    public const string Source = "WASAPI IAudioClient::IsFormatSupported（共用模式純查詢；不初始化、不啟動串流）";

    /// <summary>一組待探測格式。</summary>
    public sealed record ProbeFormat(uint SampleRate, ushort BitsPerSample, bool Float);

    /// <summary>單一端點的探測接縫：回 0＝支援（S_OK）、非 0＝被拒、委派丟例外＝該端點探測不可用。</summary>
    public sealed record EndpointProbe(string Name, Func<ProbeFormat, int> IsFormatSupported);

    /// <summary>預設探測矩陣：涵蓋音樂（44.1k）到高解析（384k）與 16/24/32f。</summary>
    public static readonly IReadOnlyList<ProbeFormat> DefaultMatrix =
    [
        new(8000, 16, false), new(11025, 16, false), new(16000, 16, false), new(22050, 16, false),
        new(32000, 16, false), new(44100, 16, false), new(44100, 24, false),
        new(48000, 16, false), new(48000, 24, false), new(48000, 32, true),
        new(88200, 24, false), new(96000, 24, false), new(96000, 32, true),
        new(176400, 24, false), new(192000, 24, false), new(192000, 32, true),
        new(384000, 24, false), new(384000, 32, true),
    ];

    /// <summary>收集端點格式支援枚舉。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<EndpointProbe>>? endpoints = null) =>
        Collect(at, DefaultMatrix, endpoints);

    /// <summary>帶矩陣的收集（測試可縮小矩陣）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<ProbeFormat> matrix, Func<IReadOnlyList<EndpointProbe>>? endpoints = null)
    {
        var list = endpoints is null ? RealEndpoints() : endpoints();
        if (list.Count == 0)
            return [new HardwareFact(Key, Category, "音訊格式支援枚舉", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "MMDevice API 無作用中音訊端點（或列舉失敗）——如實標")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < list.Count; i++)
        {
            var (value, availability, reason) = ProbeOne(list[i], matrix);
            facts.Add(new HardwareFact($"{FormatsPrefix}{i}", Category, $"格式支援：{list[i].Name}", value, "", Source,
                FactTrustLevel.Reported, false, at, null, availability, reason));
        }
        return facts;
    }

    /// <summary>純函式：單端點探測結果 → 判讀。</summary>
    public static (string Value, FactAvailability Availability, string? UnavailableReason) ProbeOne(
        EndpointProbe endpoint, IReadOnlyList<ProbeFormat> matrix)
    {
        var byDepth = new Dictionary<string, List<uint>>();
        int attempted = 0, failed = 0;
        foreach (var fmt in matrix)
        {
            int result;
            try { result = endpoint.IsFormatSupported(fmt); }
            catch { failed++; continue; }
            attempted++;
            if (result != 0) continue;
            var depthKey = fmt.Float ? "32f" : fmt.BitsPerSample.ToString();
            if (!byDepth.TryGetValue(depthKey, out var rates)) byDepth[depthKey] = rates = [];
            rates.Add(fmt.SampleRate);
        }
        if (attempted == 0 && failed > 0)
            return ("", FactAvailability.ReadError, $"全部 {failed} 組格式探測都丟出例外——端點無法回應格式查詢");
        if (byDepth.Count == 0)
            return ($"共用模式全組合被拒（{attempted} 組，S_FALSE 亦計為不直接支援）——可能端點被獨占鎖定或驅動限制；照實列，不猜原因",
                FactAvailability.Present, null);
        var parts = byDepth.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}-bit：{string.Join("/", kv.Value.OrderBy(r => r))}");
        return ($"支援（共用模式）：{string.Join("；", parts)}", FactAvailability.Present, null);
    }

    // ── 真實 WASAPI 通路（薄；測試以注入取代）──
    // 來源：Windows SDK um/audioclient.h／mmdeviceapi.h——IMMDeviceEnumerator／IMMDevice::Activate(IID_IAudioClient)
    // 與 IAudioClient::IsFormatSupported（AUDCLNT_SHAREMODE_SHARED=0）；WAVEFORMATEX 布局出自 mmreg.h。

    private static IReadOnlyList<EndpointProbe> RealEndpoints()
    {
        var result = new List<EndpointProbe>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out var collection) != 0)
                return result;
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                string name = ReadFriendlyName(device) ?? $"端點 {result.Count}";
                var client = ActivateClient(device);
                if (client is null) continue;
                result.Add(new EndpointProbe(name, fmt => QueryFormat(client, fmt)));
            }
        }
        catch { /* 音訊列舉為附加功能，失敗由上層標三態 */ }
        return result;
    }

    private static int QueryFormat(IAudioClient client, ProbeFormat fmt)
    {
        var wfx = new WaveFormatEx
        {
            FormatTag = (ushort)(fmt.Float ? 3 /*WAVE_FORMAT_IEEE_FLOAT*/ : 1 /*WAVE_FORMAT_PCM*/),
            Channels = 2,
            SamplesPerSec = fmt.SampleRate,
            BitsPerSample = fmt.Float ? (ushort)32 : fmt.BitsPerSample,
        };
        wfx.BlockAlign = (ushort)(wfx.Channels * wfx.BitsPerSample / 8);
        wfx.AvgBytesPerSec = wfx.SamplesPerSec * wfx.BlockAlign;
        wfx.CbSize = 0;
        nint formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(wfx, formatPtr, fDeleteOld: false);
            int hr = client.IsFormatSupported(0 /*AUDCLNT_SHAREMODE_SHARED*/, formatPtr, out nint closest);
            if (closest != nint.Zero) Marshal.FreeCoTaskMem(closest);
            return hr; // 0＝S_OK 支援；其餘（含 S_FALSE=1、0x88890008 不支援）＝不直接支援
        }
        finally { Marshal.FreeHGlobal(formatPtr); }
    }

    private static IAudioClient? ActivateClient(IMMDevice device)
    {
        var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"); // IID_IAudioClient
        if (device.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, nint.Zero, out nint iface) != 0)
            return null;
        try { return (IAudioClient)Marshal.GetObjectForIUnknown(iface); }
        catch { Marshal.Release(iface); return null; }
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
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId; public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    // .NET ComTypes 只有 VARIANT 沒有 PROPVARIANT——自訂最小版（x64：vt 在 0、聯合在 8）。
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public nint PointerValue;
    }

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

    // vtable 順序（audioclient.h）：Initialize、GetBufferSize、GetStreamLatency、GetCurrentPadding、
    // IsFormatSupported……前四個只聲明不呼叫，是為了讓 marshal 對齊 vtable。
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(uint shareMode, nint format, out nint closestMatch);
        // 其餘方法（GetService 等）不需要，不聲明。
    }
}
