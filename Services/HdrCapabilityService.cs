using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>HDR 顯示能力：EDID CTA-861 HDR Static Metadata + Windows HDR 開關狀態。</summary>
public sealed record HdrCapabilityRow(
    string Label, string Value, string Status);

/// <summary>
/// HDR 顯示能力解析：
/// 1) 從登錄檔原始 EDID 找 CTA-861 擴充塊（EDID tag 0x02）內的
///    HDR Static Metadata Data Block（<b>延伸標籤 6</b>，不是資料塊標籤 6——後者是保留值），
///    解出 EOTF 位元、靜態中繼資料類型與亮度碼值；
/// 2) 用 DisplayConfig 的 Advanced Color Info 查 Windows HDR 是否啟用。
/// 全部零特權、純 API 直讀；讀不到就標示未知，不猜。
/// <para>
/// <b>2026-10-27 修掉兩個實質缺陷（第 276 輪）：</b>
/// ①標籤判錯——舊碼在資料塊集合裡找「標籤 6」，而 HDR 靜態中繼資料是
/// <b>標籤 7（延伸標籤）＋延伸標籤 6</b>；CTA-861-H Table 60 的標籤 6 是保留值，
/// 所以真機 EDID 的 <c>E6 06 0D 01 A2 A2 10</c>（標頭 0xE6＝標籤 7）從來沒被匹配到，
/// 畫面只能一直顯示「未找到」。②位移與換算式錯——EOTF 在 <c>payload[1]</c>、
/// 期望最大亮度在 <c>payload[3]</c>、最小亮度在 <c>payload[5]</c>（舊碼用了 +2／+3／+4），
/// 而亮度換算是 <c>50×2^(CV/32)</c>（舊碼寫成 <c>(CV+1)/2</c>）。
/// 兩者都不拋例外，只是把「有宣告 HDR」講成「未宣告」——<b>錯的標籤只會安靜地講錯話</b>。
/// 現在整段走 <see cref="Cta861"/>，與 CTA 事實（<c>edid.cta.*</c>）共用同一份解碼器。
/// </para>
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

        var extensions = Cta861.ParseExtensions(edid);
        if (extensions.Count == 0)
        {
            rows.Add(new HdrCapabilityRow("EDID HDR 區塊", "未找到", "EDID 無 CTA-861 擴充塊"));
            return rows;
        }

        // HDR 靜態中繼資料塊可能出現在任何一個 CTA 擴充塊裡（多數面板只有第一個）。
        CtaHdrStatic? hdr = null;
        foreach (var extension in extensions)
        {
            hdr = Cta861.HdrStatic(extension);
            if (hdr is not null) break;
        }

        if (hdr is null)
        {
            rows.Add(new HdrCapabilityRow("EDID HDR 區塊", "未找到",
                "CTA-861 擴充塊裡沒有 HDR 靜態中繼資料塊（延伸標籤 6）"));
            return rows;
        }

        // EOTF 四位元逐一陳述：bit1（傳統 gamma HDR 範圍）不是「支援 HDR」，
        // 真正的 HDR 傳輸函數是 bit2（PQ／ST 2084）與 bit3（HLG）。
        var eotfNames = new List<string>();
        if (hdr.TraditionalSdr) eotfNames.Add("傳統 gamma（SDR 範圍）");
        if (hdr.TraditionalHdr) eotfNames.Add("傳統 gamma（HDR 範圍）");
        if (hdr.SmpteSt2084) eotfNames.Add("SMPTE ST 2084（PQ）");
        if (hdr.Hlg) eotfNames.Add("BT.2100 HLG");

        rows.Add(new HdrCapabilityRow("HDR 支援", hdr.AnyHdrEotf ? "是" : "否",
            hdr.AnyHdrEotf ? "正常" : "面板只宣告傳統 gamma"));
        rows.Add(new HdrCapabilityRow("EOTF",
            eotfNames.Count == 0 ? "（一位都沒設）" : string.Join("、", eotfNames),
            $"EOTF 位元 {hdr.EotfBits:X2}"));
        rows.Add(new HdrCapabilityRow("靜態中繼資料",
            hdr.StaticMetadataType1 ? "已宣告類型 1（SMPTE ST 2086 色度＋亮度）" : "未宣告",
            "—"));

        if (hdr.MaxCll is int maxCode)
        {
            // CTA-861-G：期望最大亮度的碼值 CV 換算為 50×2^(CV/32) cd/m²。
            double maxNits = Cta861.MaxLuminanceNits(maxCode);
            rows.Add(new HdrCapabilityRow("最大亮度", $"{maxNits:F0} nits",
                maxNits >= 600 ? "HDR 級" : "SDR 級"));
            if (hdr.MinCll is int minCode)
            {
                double minNits = Cta861.MinLuminanceNits(maxNits, minCode);
                rows.Add(new HdrCapabilityRow("最小亮度", $"{minNits:F4} nits",
                    $"碼值 {minCode}"));
            }
        }
        rows.Add(new HdrCapabilityRow("SDR 相容", hdr.TraditionalSdr ? "是" : "否", "—"));
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

    /// <summary>
    /// 取第一台顯示器的原始 EDID。<b>不自己走登錄檔</b>——EDID 的登錄檔讀取只有一份實作
    /// （<see cref="EdidService.ReadRawAll"/>），這裡只是取第一筆。
    /// </summary>
    private static byte[] ReadFirstEdid()
    {
        var all = EdidService.ReadRawAll();
        return all.Count > 0 ? all[0].Edid : [];
    }
}
