using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>一台顯示器目前的鏈路狀態。</summary>
public sealed class DisplayLinkRow
{
    public required string Name { get; init; }
    public required string ConnectionText { get; init; }
    public required string ModeText { get; init; }
    public required string PixelClockText { get; init; }
    public required string EncodingText { get; init; }
    public required string DepthText { get; init; }
    public required string HdrText { get; init; }
    public required string RequiredText { get; init; }
    public required string Verdict { get; init; }
    public required Severity Severity { get; init; }
}

/// <summary>
/// 顯示鏈路真相：這個模式需要多少頻寬，畫面有沒有為了塞進線裡而被犧牲。
/// </summary>
/// <remarks>
/// 全部走 Windows 自己的 <c>QueryDisplayConfig</c>／<c>DisplayConfigGetDeviceInfo</c>（唯讀查詢），
/// 因此在 NVIDIA、AMD、Intel 上一樣有效，不依賴任何廠商 SDK。
/// <para>
/// 讀得到的是：連接技術（DisplayPort／HDMI／內建）、實際像素時鐘、目前的色彩編碼與每通道位元數、
/// HDR 是否啟用。由前兩者算得「這個模式的影像資料率」。
/// </para>
/// <para>
/// <b>讀不到的是協商後的鏈路速率與通道數</b>——Windows 不提供，要靠廠商私有 API。本頁因此不印那個數字，
/// 而是把「需要多少」與「有沒有被壓」講清楚；後者才是使用者眼睛看得出來的那件事。
/// </para>
/// </remarks>
public sealed class DisplayLinkService : ObservableObject
{
    // ── 唯讀查詢的原生介面 ────────────────────────────────────────────────

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

    /// <summary>視訊訊號資訊：<see cref="PixelRate"/> 就是實際像素時鐘，計算頻寬的分母不能用「解析度×刷新率」湊。</summary>
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

    /// <summary>模式項目。聯集以顯式配置手動疊在同一位移上，大小必須維持 64 位元組。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;      // 1＝來源、2＝目標、3＝桌面影像
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public VideoSignalInfo TargetMode;
        [FieldOffset(16)] public SourceMode SourceMode;
    }

    private const uint InfoTypeTarget = 2;

    // ── 對外狀態 ──────────────────────────────────────────────────────────

    public ObservableCollection<DisplayLinkRow> Rows { get; } = [];

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }

    public bool CanRefresh => !_busy;

    private string _status = "按「重新讀取」查詢目前的顯示鏈路。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _loaded;

    /// <summary>第一次進頁時自動讀一次（唯讀查詢，成本很低）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_busy) return;
        IsBusy = true;
        _ = Task.Run(Collect).ContinueWith(t =>
        {
            Rows.Clear();
            var (rows, status) = t.Result;
            foreach (var r in rows) Rows.Add(r);
            Status = status;
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── 收集 ──────────────────────────────────────────────────────────────

    /// <summary>結構大小的自我檢查值：算錯會讓 API 寫壞記憶體，寧可什麼都不查。</summary>
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    /// <summary>供測試直接呼叫（不需要 UI 執行緒）：回傳目前這台機器的實際查詢結果。</summary>
    internal static (List<DisplayLinkRow> Rows, string Status) Collect()
    {
        var rows = new List<DisplayLinkRow>();

        if (Marshal.SizeOf<PathInfo>() != PathInfoSize || Marshal.SizeOf<ModeInfo>() != ModeInfoSize)
            return (rows, $"不查：結構大小與規格不符（PATH_INFO {Marshal.SizeOf<PathInfo>()}、"
                        + $"MODE_INFO {Marshal.SizeOf<ModeInfo>()}），繼續下去會讓系統呼叫寫壞記憶體。");

        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint numPath, out uint numMode) != 0)
                return (rows, "讀不到顯示設定（GetDisplayConfigBufferSizes 失敗）。");
            if (numPath == 0)
                return (rows, "沒有作用中的顯示路徑。");

            var paths = new PathInfo[numPath];
            var modes = new ModeInfo[numMode];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref numPath, paths, ref numMode, modes, IntPtr.Zero) != 0)
                return (rows, "讀不到顯示設定（QueryDisplayConfig 失敗）。");

            for (int i = 0; i < numPath; i++)
            {
                var p = paths[i];
                var (encoding, bits, hdrSupported, hdrEnabled, colorOk) = AdvancedColor(p.Target.AdapterId, p.Target.Id);
                string name = TargetName(p.Target.AdapterId, p.Target.Id, i);

                ulong pixelRate = 0;
                uint activeW = 0, activeH = 0;
                if (p.Target.ModeInfoIdx < numMode)
                {
                    var m = modes[p.Target.ModeInfoIdx];
                    if (m.InfoType == InfoTypeTarget)
                    {
                        pixelRate = m.TargetMode.PixelRate;
                        activeW = m.TargetMode.ActiveSize.Cx;
                        activeH = m.TargetMode.ActiveSize.Cy;
                    }
                }

                double? gbps = DisplayLinkDecoder.VideoGbps(pixelRate, encoding, bits);
                var (verdict, severity) = DisplayLinkDecoder.Judge(encoding, bits, hdrEnabled);

                rows.Add(new DisplayLinkRow
                {
                    Name = name,
                    ConnectionText = DisplayLinkDecoder.OutputTechText(p.Target.OutputTechnology),
                    ModeText = activeW > 0
                        ? $"{activeW} × {activeH} ・ {DisplayLinkDecoder.RefreshText(p.Target.RefreshRate.Num, p.Target.RefreshRate.Den)}"
                        : "讀不到目前模式",
                    PixelClockText = pixelRate > 0 ? $"{pixelRate / 1e6:0.00} MHz" : "未回報",
                    EncodingText = colorOk ? DisplayLinkDecoder.EncodingText(encoding) : "讀不到（驅動未提供）",
                    DepthText = bits > 0 ? $"每通道 {bits} 位元" : "讀不到",
                    HdrText = !colorOk ? "讀不到"
                            : hdrEnabled ? "已啟用"
                            : hdrSupported ? "支援但未啟用" : "不支援",
                    RequiredText = DisplayLinkDecoder.GbpsText(gbps),
                    Verdict = verdict,
                    Severity = severity,
                });
            }

            int reduced = rows.Count(r => r.Severity is Severity.Warning or Severity.Serious);
            return (rows, reduced == 0
                ? $"共 {rows.Count} 條作用中的顯示路徑，色彩都沒有被壓。全程唯讀查詢。"
                : $"共 {rows.Count} 條作用中的顯示路徑，其中 {reduced} 條的色彩被降級了（見下方判決）。");
        }
        catch (Exception ex)
        {
            Diag.Swallow("DisplayLinkService.Collect", ex, "顯示鏈路讀不到，該頁顯示為空。");
            return (rows, "讀取顯示設定時發生例外，已記入診斷紀錄；本頁不顯示猜測值。");
        }
    }

    /// <summary>
    /// 取進階色彩資訊（DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO，type 9）。
    /// 這是唯一能問出「現在是 RGB 還是被降成 YCbCr 4:2:2」的官方途徑。
    /// </summary>
    private static (DisplayColorEncoding Encoding, int Bits, bool HdrSupported, bool HdrEnabled, bool Ok)
        AdvancedColor(Luid adapter, uint id)
    {
        const int size = 32;   // header(20) + value(4) + colorEncoding(4) + bitsPerColorChannel(4)
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, 9);                        // type
            Marshal.WriteInt32(p, 4, size);                     // size
            Marshal.WriteInt32(p, 8, (int)adapter.Low);         // adapterId.LowPart
            Marshal.WriteInt32(p, 12, adapter.High);            // adapterId.HighPart
            Marshal.WriteInt32(p, 16, (int)id);                 // id

            if (DisplayConfigGetDeviceInfo(p) != 0)
                return (DisplayColorEncoding.Unknown, 0, false, false, false);

            uint value = (uint)Marshal.ReadInt32(p, 20);
            uint enc = (uint)Marshal.ReadInt32(p, 24);
            int bits = Marshal.ReadInt32(p, 28);

            var encoding = enc <= 4 ? (DisplayColorEncoding)enc : DisplayColorEncoding.Unknown;
            return (encoding, bits, (value & 0x1) != 0, (value & 0x2) != 0, true);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>取顯示器的友善名稱（type 2）；問不到就用路徑序號，不編一個型號出來。</summary>
    private static string TargetName(Luid adapter, uint id, int pathIndex)
    {
        const int size = 420;   // header(20) + flags(4) + tech(4) + edid(4) + connector(4) + name(128) + path(256)
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, 2);                 // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
            Marshal.WriteInt32(p, 4, size);
            Marshal.WriteInt32(p, 8, (int)adapter.Low);
            Marshal.WriteInt32(p, 12, adapter.High);
            Marshal.WriteInt32(p, 16, (int)id);

            if (DisplayConfigGetDeviceInfo(p) != 0) return $"顯示路徑 {pathIndex + 1}";
            string name = Marshal.PtrToStringUni(p + 36, 64)?.TrimEnd('\0').Trim() ?? "";
            return string.IsNullOrWhiteSpace(name) ? $"顯示路徑 {pathIndex + 1}（未回報名稱）" : name;
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}

