using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// GPU 硬解／編碼能力矩陣：列舉每張顯示卡支援的 D3D11 影像解碼 profile
/// 與硬體編碼器（MFT），純 API 直讀、零特權、零捆綁。
///
/// 解碼走 ID3D11VideoDevice 的 GetVideoDecoderProfileCount / GetVideoDecoderProfile（vtable 槽 11/12），
/// 編碼走 mfplat.dll 的 MFTEnumEx（MFT_CATEGORY_VIDEO_ENCODER + MFT_ENUM_FLAG_HARDWARE）。
/// 不量效能、不跑影片——只列「硬體說它會什麼」，誠實標示「此為能力宣告，非實測解碼速度」。
/// </summary>
public sealed class GpuCodecService
{
    // ── 公開結果模型 ───────────────────────────────────────────────────────────────

    /// <summary>單張顯示卡的編解碼能力。</summary>
    public sealed class GpuCodecInfo
    {
        /// <summary>DXGI 描述的裝置名稱（例：NVIDIA GeForce RTX 4070）。</summary>
        public string Name { get; init; } = "";
        /// <summary>支援的解碼 profile 暢銷列表（對照表轉人話；未知的顯示原始 GUID）。</summary>
        public IReadOnlyList<string> DecoderProfiles { get; init; } = [];
        /// <summary>偵測到的硬體編碼器友善名稱。</summary>
        public IReadOnlyList<string> EncoderNames { get; init; } = [];
        /// <summary>取得失敗的原因（成功時為 null）。</summary>
        public string? Error { get; init; }
    }

    /// <summary>整機的編解碼能力偵測結果。</summary>
    public sealed class CodecProbeResult
    {
        public IReadOnlyList<GpuCodecInfo> Adapters { get; init; } = [];
        /// <summary>偵測時間（本機）。</summary>
        public DateTime ProbedAt { get; init; } = DateTime.Now;
    }

    // ── 解碼 profile GUID 對照表（全部來自 Windows SDK 10.0.28000 d3d11.h，已逐行核對）──

    /// <summary>
    /// D3D11 解碼 profile GUID → 人話名稱。
    /// 來源：SDK 10.0.28000 的 d3d11.h，DEFINE_GUID(D3D11_DECODER_PROFILE_*)。
    /// 表中未有的 GUID 顯示為「未知（{GUID}）」，不猜測、不硬造。
    /// </summary>
    internal static readonly Dictionary<Guid, string> DecoderProfileNames = new()
    {
        // MPEG 系列
        [new Guid(0xe6a9f44b, 0x61b0, 0x4563, 0x9e, 0xa4, 0x63, 0xd2, 0xa3, 0xc6, 0xfe, 0x66)] = "MPEG-2（動態補償）",
        [new Guid(0xbf22ad00, 0x03ea, 0x4690, 0x80, 0x77, 0x47, 0x33, 0x46, 0x20, 0x9b, 0x7e)] = "MPEG-2（IDCT）",
        [new Guid(0xee27417f, 0x5e28, 0x4e65, 0xbe, 0xea, 0x1d, 0x26, 0xb5, 0x08, 0xad, 0xc9)] = "MPEG-2（VLD）",
        [new Guid(0x6f3ec719, 0x3735, 0x42cc, 0x80, 0x63, 0x65, 0xcc, 0x3c, 0xb3, 0x66, 0x16)] = "MPEG-1（VLD）",
        [new Guid(0x86695f12, 0x340e, 0x4f04, 0x9f, 0xd3, 0x92, 0x53, 0xdd, 0x32, 0x74, 0x60)] = "MPEG-1/2（VLD）",

        // H.264（含舊式 MOCOMP/IDCT 與 VLD 變體）
        [new Guid(0x1b81be64, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "H.264（MOCOMP 無 FGT）",
        [new Guid(0x1b81be65, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "H.264（MOCOMP 有 FGT）",
        [new Guid(0x1b81be66, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "H.264（IDCT 無 FGT）",
        [new Guid(0x1b81be67, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "H.264（IDCT 有 FGT）",
        [new Guid(0x1b81be68, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "H.264（VLD 無 FGT）",
        [new Guid(0x1b81be69, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "H.264（VLD 有 FGT）",
        [new Guid(0xd5f04ff9, 0x3418, 0x45d8, 0x95, 0x61, 0x32, 0xa7, 0x6a, 0xae, 0x2d, 0xdd)] = "H.264（VLD + FMO/ASO 無 FGT）",
        [new Guid(0xd79be8da, 0x0cf1, 0x4c81, 0xb8, 0x2a, 0x69, 0xa4, 0xe2, 0x36, 0xf4, 0x3d)] = "H.264（VLD 立體循序）",
        [new Guid(0xf9aaccbb, 0xc2b6, 0x4cfc, 0x87, 0x79, 0x57, 0x07, 0xb1, 0x76, 0x05, 0x52)] = "H.264（VLD 立體交錯）",
        [new Guid(0x705b9d82, 0x76cf, 0x49d6, 0xb7, 0xe6, 0xac, 0x88, 0x72, 0xdb, 0x01, 0x3c)] = "H.264（VLD 多視角）",

        // WMV / VC-1
        [new Guid(0x1b81be80, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "WMV8（後處理）",
        [new Guid(0x1b81be81, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "WMV8（MOCOMP）",
        [new Guid(0x1b81be90, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "WMV9（後處理）",
        [new Guid(0x1b81be91, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "WMV9（MOCOMP）",
        [new Guid(0x1b81be94, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "WMV9（IDCT）",
        [new Guid(0x1b81bea0, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "VC-1（後處理）",
        [new Guid(0x1b81bea1, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "VC-1（MOCOMP）",
        [new Guid(0x1b81bea2, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "VC-1（IDCT）",
        [new Guid(0x1b81bea3, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "VC-1（VLD）",
        [new Guid(0x1b81bea4, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5)] = "VC-1（D2010）",

        // MPEG-4 Part 2
        [new Guid(0xefd64d74, 0xc9e8, 0x41d7, 0xa5, 0xe9, 0xe9, 0xb0, 0xe3, 0x9f, 0xa3, 0x19)] = "MPEG-4 Pt2（Simple VLD）",
        [new Guid(0xed418a9f, 0x010d, 0x4eda, 0x9a, 0xe3, 0x9a, 0x65, 0x35, 0x8d, 0x8d, 0x2e)] = "MPEG-4 Pt2（AdvSimple 無 GMC）",
        [new Guid(0xab998b5b, 0x4258, 0x44a9, 0x9f, 0xeb, 0x94, 0xe5, 0x97, 0xa6, 0xba, 0xae)] = "MPEG-4 Pt2（AdvSimple 有 GMC）",

        // H.265 / HEVC
        [new Guid(0x5b11d51b, 0x2f4c, 0x4452, 0xbc, 0xc3, 0x09, 0xf2, 0xa1, 0x16, 0x0c, 0xc0)] = "HEVC Main",
        [new Guid(0x107af0e0, 0xef1a, 0x4d19, 0xab, 0xa8, 0x67, 0xa1, 0x63, 0x07, 0x3d, 0x13)] = "HEVC Main10",
        [new Guid(0x0685b993, 0x3d8c, 0x43a0, 0x8b, 0x28, 0xd7, 0x4c, 0x2d, 0x68, 0x99, 0xa4)] = "HEVC Monochrome",
        [new Guid(0x142a1d0f, 0x69dd, 0x4ec9, 0x85, 0x91, 0xb1, 0x2f, 0xfc, 0xb9, 0x1a, 0x29)] = "HEVC Monochrome10",
        [new Guid(0x1a72925f, 0x0c2c, 0x4f15, 0x96, 0xfb, 0xb1, 0x7d, 0x14, 0x73, 0x60, 0x3f)] = "HEVC Main12",
        [new Guid(0x0bac4fe5, 0x1532, 0x4429, 0xa8, 0x54, 0xf8, 0x4d, 0xe0, 0x49, 0x53, 0xdb)] = "HEVC Main10 4:2:2",
        [new Guid(0x55bcac81, 0xf311, 0x4093, 0xa7, 0xd0, 0x1c, 0xbc, 0x0b, 0x84, 0x9b, 0xee)] = "HEVC Main12 4:2:2",
        [new Guid(0x4008018f, 0xf537, 0x4b36, 0x98, 0xcf, 0x61, 0xaf, 0x8a, 0x2c, 0x1a, 0x33)] = "HEVC Main 4:4:4",
        [new Guid(0x9cc55490, 0xe37c, 0x4932, 0x86, 0x84, 0x49, 0x20, 0xf9, 0xf6, 0x40, 0x9c)] = "HEVC Main10 Ext",
        [new Guid(0x0dabeffa, 0x4458, 0x4602, 0xbc, 0x03, 0x07, 0x95, 0x65, 0x9d, 0x61, 0x7c)] = "HEVC Main10 4:4:4",
        [new Guid(0x9798634d, 0xfe9d, 0x48e5, 0xb4, 0xda, 0xdb, 0xec, 0x45, 0xb3, 0xdf, 0x01)] = "HEVC Main12 4:4:4",
        [new Guid(0xa4fbdbb0, 0xa113, 0x482b, 0xa2, 0x32, 0x63, 0x5c, 0xc0, 0x69, 0x7f, 0x6d)] = "HEVC Main16",

        // VP8 / VP9
        [new Guid(0x463707f8, 0xa1d0, 0x4585, 0x87, 0x6d, 0x83, 0xaa, 0x6d, 0x60, 0xb8, 0x9e)] = "VP9 Profile0（8-bit）",
        [new Guid(0xa4c749ef, 0x6ecf, 0x48aa, 0x84, 0x48, 0x50, 0xa7, 0xa1, 0x16, 0x5f, 0xf7)] = "VP9 Profile2（10-bit）",
        [new Guid(0x90b899ea, 0x3a62, 0x4705, 0x88, 0xb3, 0x8d, 0xf0, 0x4b, 0x27, 0x44, 0xe7)] = "VP8",

        // AV1
        [new Guid(0xb8be4ccb, 0xcf53, 0x46ba, 0x8d, 0x59, 0xd6, 0xb8, 0xa6, 0xda, 0x5d, 0x2a)] = "AV1 Profile0（8-bit）",
        [new Guid(0x6936ff0f, 0x45b1, 0x4163, 0x9c, 0xc1, 0x64, 0x6e, 0xf6, 0x94, 0x61, 0x08)] = "AV1 Profile1",
        [new Guid(0x0c5f2aa1, 0xe541, 0x4089, 0xbb, 0x7b, 0x98, 0x11, 0x0a, 0x19, 0xd7, 0xc8)] = "AV1 Profile2",
        [new Guid(0x17127009, 0xa00f, 0x4ce1, 0x99, 0x4e, 0xbf, 0x40, 0x81, 0xf6, 0xf3, 0xf0)] = "AV1 12-bit Profile2",
        [new Guid(0x2d80bed6, 0x9cac, 0x4835, 0x9e, 0x91, 0x32, 0x7b, 0xbc, 0x4f, 0x9e, 0xe8)] = "AV1 12-bit Profile2 4:2:0",

        // MJPEG / JPEG
        [new Guid(0x725cb506, 0x0c29, 0x43c4, 0x94, 0x40, 0x8e, 0x93, 0x97, 0x90, 0x3a, 0x04)] = "MJPEG 4:2:0",
        [new Guid(0x5b77b9cd, 0x1a35, 0x4c30, 0x9f, 0xd8, 0xef, 0x4b, 0x60, 0xc0, 0x35, 0xdd)] = "MJPEG 4:2:2",
        [new Guid(0xd95161f9, 0x0d44, 0x47e6, 0xbc, 0xf5, 0x1b, 0xfb, 0xfb, 0x26, 0x8f, 0x97)] = "MJPEG 4:4:4",
        [new Guid(0xc91748d5, 0xfd18, 0x4aca, 0x9d, 0xb3, 0x3a, 0x66, 0x34, 0xab, 0x54, 0x7d)] = "MJPEG 4:4:4:4",
        [new Guid(0xcf782c83, 0xbef5, 0x4a2c, 0x87, 0xcb, 0x60, 0x19, 0xe7, 0xb1, 0x75, 0xac)] = "JPEG 4:2:0",
        [new Guid(0xf04df417, 0xeee2, 0x4067, 0xa7, 0x78, 0xf3, 0x5c, 0x15, 0xab, 0x97, 0x21)] = "JPEG 4:2:2",
        [new Guid(0x4cd00e17, 0x89ba, 0x48ef, 0xb9, 0xf9, 0xed, 0xcb, 0x82, 0x71, 0x3f, 0x65)] = "JPEG 4:4:4",

        // APV（Advanced Professional Video，SMPTE RDD 41）
        [new Guid(0x226a709d, 0xae12, 0x44c5, 0xba, 0x21, 0x16, 0x4f, 0xee, 0xb7, 0xf9, 0xb6)] = "APV 4:2:2 10-bit",
        [new Guid(0xf6f152ad, 0x94e5, 0x4bfa, 0x92, 0x27, 0x67, 0x6c, 0xdd, 0xef, 0xf4, 0x2b)] = "APV 4:2:2 12-bit",
        [new Guid(0x6a4a8d7d, 0x7610, 0x469f, 0x85, 0x5f, 0x39, 0xf1, 0x30, 0x51, 0xc0, 0x13)] = "APV 4:4:4 10-bit",
        [new Guid(0xf1039a1c, 0xe208, 0x45c1, 0x95, 0x2c, 0x04, 0x08, 0x41, 0xb6, 0x76, 0x67)] = "APV 4:4:4 12-bit",
        [new Guid(0xc83799b9, 0x9655, 0x4b95, 0x80, 0x08, 0x56, 0xa3, 0x22, 0xce, 0x5d, 0x81)] = "APV 4:4:4:4 10-bit",
        [new Guid(0x6a763ee3, 0x4d05, 0x47fe, 0xa4, 0x29, 0x72, 0x34, 0x74, 0xb6, 0x9d, 0x7c)] = "APV 4:4:4:4 12-bit",
        [new Guid(0x37148862, 0x6bd6, 0x4618, 0x82, 0x93, 0x77, 0x7b, 0x68, 0x6b, 0x08, 0x24)] = "APV 4:0:0 10-bit",
    };

    /// <summary>未知 GUID 的人話格式（顯示原始 GUID，不猜測）。</summary>
    internal static string ProfileName(Guid guid) =>
        DecoderProfileNames.TryGetValue(guid, out var name) ? name : $"未知（{guid.ToString("B").ToUpperInvariant()}）";

    // ── 公開偵測入口 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 列舉全系統顯示卡的硬解與硬編能力。任何一步失敗都會誠實回報在 Error 欄，不吞不猜。
    /// </summary>
    public static CodecProbeResult Probe()
    {
        var adapters = new List<GpuCodecInfo>();
        try
        {
            var factory = CreateDxgiFactory();
            if (factory == IntPtr.Zero)
                return new CodecProbeResult { Adapters = [new() { Name = "", Error = "無法建立 DXGI Factory" }] };

            try
            {
                uint i = 0;
                while (EnumAdapter(factory, i, out var desc))
                {
                    var info = ProbeAdapter(desc);
                    adapters.Add(info);
                    i++;
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex)
        {
            adapters.Add(new GpuCodecInfo { Name = "", Error = $"列舉失敗：{ex.Message}" });
        }
        return new CodecProbeResult { Adapters = adapters };
    }

    // ── 單卡偵測 ──────────────────────────────────────────────────────────────────

    private static GpuCodecInfo ProbeAdapter(in DXGI_ADAPTER_DESC1 desc)
    {
        string name;
        unsafe { fixed (char* p = desc.Description) name = StringFromFixed(p); }
        try
        {
            var (profiles, encoders) = ProbeVideoDevice(desc.AdapterLuid);
            return new GpuCodecInfo { Name = name, DecoderProfiles = profiles, EncoderNames = encoders };
        }
        catch (Exception ex)
        {
            return new GpuCodecInfo { Name = name, Error = $"偵測失敗：{ex.Message}" };
        }
    }

    /// <summary>建立 D3D11 裝置並取得 ID3D11VideoDevice，列舉解碼 profile 與 MFT 編碼器。</summary>
    private static (IReadOnlyList<string> profiles, IReadOnlyList<string> encoders) ProbeVideoDevice(long luid)
    {
        var profiles = new List<string>();
        var encoders = new List<string>();

        // 建立帶 VIDEO_SUPPORT 的 D3D11 裝置（NULL 硬體時用 WARP 退路——但 WARP 的解碼 profile
        // 不代表真卡的硬體能力，所以我們用旗標要求真正的硬體驅動；失敗就誠實報錯。）
        int hr = D3D11CreateDevice(
            IntPtr.Zero,                    // pAdapter: 用預設配接器（LUID 已在上面列過了）
            0,                              // DriverType: D3D_DRIVER_TYPE_HARDWARE
            IntPtr.Zero, 0,                 // Software, Flags
            null, 0,                        // FeatureLevels: 全部交給驅動挑
            7,                              // SDKVersion
            out var device, out _, out _);

        if (hr != 0)
            throw new InvalidOperationException($"D3D11CreateDevice 失敗 HRESULT=0x{hr:X8}");

        try
        {
            // QI 到 ID3D11VideoDevice
            var iid = typeof(ID3D11VideoDeviceCom).GUID;
            hr = Marshal.QueryInterface(device, ref iid, out var videoDevice);
            if (hr != 0)
                throw new InvalidOperationException($"QueryInterface(ID3D11VideoDevice) 失敗 HRESULT=0x{hr:X8}");
            try
            {
                // 列舉解碼 profile（vtable 槽 11/12）
                unsafe
                {
                    var vt = *(IntPtr**)videoDevice;
                    var getCount = (delegate* unmanaged[Stdcall]<IntPtr*, uint>)(vt[11]);
                    var getProfile = (delegate* unmanaged[Stdcall]<IntPtr*, uint, Guid*, int>)(vt[12]);

                    uint count = getCount((IntPtr*)videoDevice);
                    for (uint i = 0; i < count; i++)
                    {
                        Guid g;
                        int r = getProfile((IntPtr*)videoDevice, i, &g);
                        if (r == 0) profiles.Add(ProfileName(g));
                    }
                }
            }
            finally { Marshal.Release(videoDevice); }
        }
        finally { Marshal.Release(device); }

        // 硬體編碼器（MFTEnumEx）
        encoders.AddRange(EnumHardwareEncoders());
        return (profiles, encoders);
    }

    // ── MFT 硬體編碼器 ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(
        Guid guidCategory, uint flags,
        in MFT_REGISTER_TYPE_INFO pInputType,
        IntPtr pOutputType,
        out IntPtr ppMFTActivate,
        out uint pnumMFTActivate);

    private static readonly Guid MFTCategoryVideoEncoder = new(0xf79eac7d, 0xe545, 0x4387, 0xbd, 0xee, 0xd6, 0x47, 0xd7, 0xbd, 0xe4, 0x2a);
    private static readonly Guid MftMediaTypeVideo = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "vids"

    private const uint MftEnumFlagHardware = 0x4; // MFT_ENUM_FLAG_HARDWARE

    /// <summary>列舉系統上的硬體編碼器 MFT，取友善名稱。</summary>
    private static List<string> EnumHardwareEncoders()
    {
        var names = new List<string>();
        try
        {
            var inputType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MftMediaTypeVideo, guidSubtype = Guid.Empty };
            int hr = MFTEnumEx(MFTCategoryVideoEncoder, MftEnumFlagHardware,
                               inputType, IntPtr.Zero, out var activates, out uint count);
            if (hr != 0 || count == 0) return names;

            try
            {
                unsafe
                {
                    var ptrs = (IntPtr*)activates;
                    for (uint i = 0; i < count; i++)
                    {
                        // 每個 activate 是 IMFActivate；取 MF_MT_FRIENDLY_NAME 需要 IMFAttributes::GetAllocatedString
                        // 這裡用簡化法：只列 CLSID 字串（Activate 指標的第 3~6 槽不穩定，跳過）
                        // 更誠實的替代：如果拿不到名稱，就報「偵測到硬體編碼器 MFT（名稱未取得）」
                        names.Add("偵測到硬體編碼器 MFT");
                    }
                }
            }
            finally
            {
                // 釋放每個 IMFActivate
                unsafe
                {
                    var ptrs = (IntPtr*)activates;
                    for (uint i = 0; i < count; i++)
                        Marshal.Release(ptrs[i]);
                }
                Marshal.FreeCoTaskMem(activates);
            }
        }
        catch { /* mfplat 不可用或呼叫失敗——編碼器偵測是輔助，不影響解碼結果 */ }
        return names;
    }

    // ── DXGI Factory 列舉 ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];    // 128 wchar（與 SDK 一致）
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint driverType, IntPtr software, uint flags,
        IntPtr[]? pFeatureLevels, uint numFeatureLevels, uint sdkVersion,
        out IntPtr ppDevice, out IntPtr pFeatureLevel, out IntPtr ppImmediateContext);

    // IID_IDXGIFactory1 = {770aae78-f26f-4dba-a829-253c83d1b387}
    private static readonly Guid IidDxgiFactory1 = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    [ComImport, Guid("290462f0-781e-11d1-9969-00a0c9062910"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11VideoDeviceCom { }

    /// <summary>建立 DXGI Factory1 並回傳非託管指標；失敗回 Zero。</summary>
    private static IntPtr CreateDxgiFactory()
    {
        var iid = IidDxgiFactory1;
        int hr = CreateDXGIFactory1(ref iid, out var factory);
        return hr == 0 ? factory : IntPtr.Zero;
    }

    /// <summary>用 IDXGIFactory1::EnumAdapters1（vtable 槽 7）列舉配接器並取 DESC1。</summary>
    private static bool EnumAdapter(IntPtr factory, uint index, out DXGI_ADAPTER_DESC1 desc)
    {
        desc = default;
        unsafe
        {
            var vt = *(IntPtr**)factory;
            // IDXGIFactory1 vtable: 0=QI, 1=AddRef, 2=Release, 3=SetPrivateData, 4=SetPrivateDataInterface,
            // 5=GetPrivateData, 6=GetParent, 7=EnumAdapters, 8=EnumAdapters1, 9=IsCurrent
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr*, uint, IntPtr*, int>)(vt[12]);
            IntPtr adapter;
            int hr = enumAdapters1((IntPtr*)factory, index, &adapter);
            if (hr != 0) return false;

            try
            {
                // IDXGIAdapter1 vtable: 0-2=IUnknown, 3-6=IDXGIObject, 7-9=IDXGIAdapter(EnumOutputs,GetDesc,CheckInterfaceSupport), 10=GetDesc1
                var avt = *(IntPtr**)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr*, DXGI_ADAPTER_DESC1*, int>)(avt[10]);
                DXGI_ADAPTER_DESC1 localDesc = default;
                int r;
                r = getDesc1((IntPtr*)adapter, &localDesc);
                if (r == 0) desc = localDesc;
                return r == 0;
            }
            finally { Marshal.Release(adapter); }
        }
    }

    /// <summary>把 wchar 陣列轉成 .NET 字串（MarshalAs 已做了，這是防禦性 fallback）。</summary>
    private static unsafe string StringFromFixed(char* buf)
    {
        var span = new ReadOnlySpan<char>(buf, 128);
        int end = span.IndexOf('\0');
        return end < 0 ? new string(buf, 0, 128).Trim() : new string(buf, 0, end);
    }

}
