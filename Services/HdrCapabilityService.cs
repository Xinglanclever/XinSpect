using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>HDR 顯示能力：EDID CTA-861 HDR Static Metadata + Windows HDR 開關狀態。</summary>
public sealed record HdrCapabilityRow(
    string Label, string Value, string Status);

/// <summary>
/// HDR 顯示能力解析：
/// 1) 從登錄檔原始 EDID 找 CTA-861 擴充塊（tag 0x02）內的
///    HDR Static Metadata Data Block（tag 0x6），解出亮度與支援旗標；
/// 2) 用 DisplayConfig 的 Advanced Color Info 查 Windows HDR 是否啟用。
/// 全部零特權、純 API 直讀；讀不到就標示未知，不猜。
/// </summary>
public static class HdrCapabilityService
{
    public static List<HdrCapabilityRow> Read(byte[]? edidRaw = null)
    {
        var rows = new List<HdrCapabilityRow>();

        // Windows HDR 是目前 active path 的系統狀態；EDID 只是面板能力的來源之一。
        var os = QueryHdrState();
        rows.Add(os.Ok
            ? new HdrCapabilityRow("Windows HDR", os.Enabled ? "已啟用" : "未啟用",
                os.Supported ? (os.Enabled ? "正常" : "—") : "面板未宣告支援")
            : new HdrCapabilityRow("Windows HDR", "未知", "DisplayConfig 查詢失敗"));

        byte[] edid = edidRaw ?? ReadFirstEdid();
        if (edid.Length < 128)
        {
            rows.Add(new HdrCapabilityRow("EDID HDR 區塊", "未知", "未取得可用 EDID"));
            return rows;
        }
        if (edid.Length < 256)
        {
            rows.Add(new HdrCapabilityRow("EDID HDR 區塊", "未找到", "EDID 無擴充塊"));
            return rows;
        }

        int extCount = edid[0x7E];
        for (int ext = 0; ext < extCount; ext++)
        {
            int extBase = 128 * (ext + 1);
            if (extBase + 128 > edid.Length) break;
            if (edid[extBase] != 0x02) continue; // 只看 CTA-861

            // CTA-861 data block 區段從 offset 4 開始到 DTD start。
            int dtdStart = edid[extBase + 2] & 0xFF;
            if (dtdStart < 4 || dtdStart > 127) continue;
            int pos = 4;
            while (pos < dtdStart)
            {
                byte header = edid[extBase + pos];
                byte tag = (byte)((header >> 5) & 0x07);
                int len = header & 0x1F;
                if (len == 0 || pos + 1 + len > dtdStart) break;

                if (tag == 0x06 && len >= 3) // HDR Static Metadata Data Block
                {
                    byte sm = edid[extBase + pos + 2];
                    bool sdrSupported = (sm & 0x01) != 0;
                    bool hdrSupported = (sm & 0x02) != 0;
                    rows.Add(new HdrCapabilityRow("HDR 支援", hdrSupported ? "是" : "否",
                        hdrSupported ? "正常" : "—"));
                    if (len >= 4)
                    {
                        // CTA-861-H：1..255 → 1..4999 nits（(value + 1) / 2）。
                        double maxNits = (edid[extBase + pos + 3] + 1) / 2.0;
                        rows.Add(new HdrCapabilityRow("最大亮度", $"{maxNits:F0} nits",
                            maxNits >= 600 ? "HDR 級" : "SDR 級"));
                    }
                    if (len >= 5)
                    {
                        // 1..255 → 0.0001..0.0099 cd/m²（2^(value/32) × 0.0001）。
                        double minNits = Math.Pow(2, edid[extBase + pos + 4] / 32.0) * 0.0001;
                        rows.Add(new HdrCapabilityRow("最小亮度", $"{minNits:F4} nits", "—"));
                    }
                    rows.Add(new HdrCapabilityRow("SDR 相容", sdrSupported ? "是" : "否", "—"));
                    return rows;
                }
                pos += 1 + len;
            }
        }
        rows.Add(new HdrCapabilityRow("EDID HDR 區塊", "未找到", "此面板可能不支援 HDR 或 EDID 未宣告"));
        return rows;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PathInfo[] paths,
        ref uint numMode, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

    private const uint QdcOnlyActivePaths = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational { public uint Num; public uint Den; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Region2D { public uint Cx; public uint Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx;
        public uint OutputTechnology; public uint Rotation; public uint Scaling;
        public Rational RefreshRate; public uint ScanLineOrdering;
        public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFreq; public Rational VSyncFreq;
        public Region2D ActiveSize; public Region2D TotalSize;
        public uint AdditionalSignalInfo; public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceMode { public uint Width; public uint Height; public uint PixelFormat; public int X; public int Y; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public VideoSignalInfo TargetMode;
        [FieldOffset(16)] public SourceMode SourceMode;
    }

    private readonly record struct HdrState(bool Ok, bool Supported, bool Enabled);

    private static HdrState QueryHdrState()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0 ||
                pathCount == 0 || modeCount == 0)
                return new(false, false, false);
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return new(false, false, false);

            bool supported = false, enabled = false;
            for (int i = 0; i < pathCount; i++)
            {
                var (ok, pathSupported, pathEnabled) = AdvancedColor(paths[i].Target.AdapterId, paths[i].Target.Id);
                if (!ok) continue;
                supported |= pathSupported;
                enabled |= pathEnabled;
            }
            return new(true, supported, enabled);
        }
        catch { return new(false, false, false); }
    }

    private static (bool Ok, bool Supported, bool Enabled) AdvancedColor(Luid adapter, uint id)
    {
        const int size = 32; // header(20) + value(4) + colorEncoding(4) + bitsPerColorChannel(4)
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, 9); // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
            Marshal.WriteInt32(p, 4, size);
            Marshal.WriteInt32(p, 8, unchecked((int)adapter.Low));
            Marshal.WriteInt32(p, 12, adapter.High);
            Marshal.WriteInt32(p, 16, unchecked((int)id));

            if (DisplayConfigGetDeviceInfo(p) != 0)
                return (false, false, false);
            uint value = (uint)Marshal.ReadInt32(p, 20);
            return (true, (value & 0x1) != 0, (value & 0x2) != 0);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>從 registry 讀取第一個顯示器的原始 EDID（與其他 EDID 頁同一來源）。</summary>
    private static byte[] ReadFirstEdid()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (root is null) return [];
            foreach (var modelName in root.GetSubKeyNames())
            {
                using var model = root.OpenSubKey(modelName);
                if (model is null) continue;
                foreach (var instName in model.GetSubKeyNames())
                {
                    using var inst = model.OpenSubKey(instName);
                    using var dp = inst?.OpenSubKey("Device Parameters");
                    if (dp?.GetValue("EDID") is byte[] e && e.Length >= 128)
                        return e;
                }
            }
        }
        catch { }
        return [];
    }
}
