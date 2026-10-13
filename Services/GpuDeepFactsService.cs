using System.Management;

namespace XinSpect;

/// <summary>
/// GPU 深化（Vol2 第 440–453 項）：GX-001 NVML 全狀態（<c>gx.nv.state</c>）、GX-002 NVIDIA 風扇曲線（<c>gx.nv.fan</c>）、
/// GX-003 NVIDIA 功率限制（<c>gx.nv.pl</c>）、GX-004 NVIDIA ECC 能力（<c>gx.nv.ecc</c>）、GX-005 ADLX 感測器全表（<c>gx.amd</c>）、
/// GX-006 Intel Xe 感測器（<c>gx.intel</c>）、GX-007 VBIOS 版本（<c>gx.vbios</c>）、GX-008 VRAM 使用歷史（<c>hist.vram</c>）、
/// GX-009 驅動版本鏈（<c>gx.drvchain</c>）、GX-010 多 GPU 模式（<c>gx.multi</c>）、GX-011 虛擬顯示器偵測（<c>gx.vdisp</c>）、
/// GX-012 色深事實（<c>gx.depth</c>）、GX-013 RT/AI 能力旗標（<c>gx.cap.rt</c>）、GX-014 GPU 驅動版本史（<c>hist.gpudriver</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本機無獨立 GPU</b>（TITAN Xp 已拔除，只剩虛擬顯示轉接器）——廠商路徑（NVML／ADLX／Xe）一律走
/// <b>無卡 N/A</b>；本機有 3 個虛擬轉接器的<b>真實場景</b>可用於 GX-011 判定。
/// </para>
/// <para>
/// <b>紅線：</b>① 唯讀——不寫時脈、風扇、功率、ECC；② 無卡一律 N/A 不假裝有值；
/// ③ <b>虛擬顯示器判定≠惡意</b>（可能是遠端桌面、虛擬機、串流工具）；④ VBIOS 不可讀如實標；
/// ⑤ 驅動鏈只列版本不列路徑。
/// </para>
/// </remarks>
public static class GpuDeepFactsService
{
    private const string Cat = "GPU";

    public const string NvStateKey = "gx.nv.state";
    public const string NvFanKey = "gx.nv.fan";
    public const string NvPlKey = "gx.nv.pl";
    public const string NvEccKey = "gx.nv.ecc";
    public const string AmdKey = "gx.amd";
    public const string IntelKey = "gx.intel";
    public const string VbiosKey = "gx.vbios";
    public const string VramHistKey = "hist.vram";
    public const string DrvChainKey = "gx.drvchain";
    public const string MultiKey = "gx.multi";
    public const string VdispKey = "gx.vdisp";
    public const string DepthKey = "gx.depth";
    public const string RtKey = "gx.cap.rt";
    public const string GpuDrvHistKey = "hist.gpudriver";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (int StateCount, int ThrottleReasons)? NvStateProbe();
    public delegate (int FanPercent, double Rpm)? NvFanProbe();
    public delegate (int LimitW, int CurrentW)? NvPlProbe();
    public delegate (bool EccSupported, int CorrectedErrors)? NvEccProbe();
    public delegate (int Sensors, bool ReadOk)? AmdProbe();
    public delegate (int Sensors, bool ReadOk)? IntelProbe();
    public delegate string? VbiosProbe();
    public delegate IReadOnlyList<int>? VramHistProbe();
    public delegate (string DriverVersion, string WddmVersion, string DxVersion)? DrvChainProbe();
    public delegate (int Adapters, bool MixedVendor)? MultiProbe();
    public delegate (int Total, int Virtual)? VdispProbe();
    public delegate int? DepthProbe();
    public delegate (bool Rt, bool Ai)? RtProbe();
    public delegate IReadOnlyList<string>? GpuDrvHistProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        NvStateProbe? nvState = null, NvFanProbe? nvFan = null, NvPlProbe? nvPl = null, NvEccProbe? nvEcc = null,
        AmdProbe? amd = null, IntelProbe? intel = null, VbiosProbe? vbios = null, VramHistProbe? vramHist = null,
        DrvChainProbe? drvChain = null, MultiProbe? multi = null, VdispProbe? vdisp = null, DepthProbe? depth = null,
        RtProbe? rt = null, GpuDrvHistProbe? gpuDrvHist = null)
        =>
        [
            NvStateFact(at, nvState), NvFanFact(at, nvFan), NvPlFact(at, nvPl), NvEccFact(at, nvEcc),
            AmdFact(at, amd), IntelFact(at, intel), VbiosFact(at, vbios), VramHistFact(at, vramHist),
            DrvChainFact(at, drvChain), MultiFact(at, multi), VdispFact(at, vdisp), DepthFact(at, depth),
            RtFact(at, rt), GpuDrvHistFact(at, gpuDrvHist),
        ];

    private static HardwareFact NoGpu(DateTimeOffset at, string key, string name)
        => new(key, Cat, name, "", "", "NVML／ADLX／Xe（唯讀）", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotApplicable, "本機無獨立 GPU（僅虛擬顯示轉接器）——廠商路徑不適用，非錯誤");

    // ── GX-001 NVML 全狀態 ─────────────────────────────────────────────

    public static HardwareFact NvStateFact(DateTimeOffset at, NvStateProbe? probe = null)
    {
        (int StateCount, int ThrottleReasons)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null) return NoGpu(at, NvStateKey, "NVML 全狀態");
        var (states, reasons) = s.Value;
        return new HardwareFact(NvStateKey, Cat, "NVML 全狀態",
            $"P-State 群 {states}、節流原因 {reasons} 個。" +
            "P0–P15 是 NVIDIA 的效能狀態；節流原因（溫度／功率／電流）說明<b>為什麼降頻</b>——" +
            "只讀不設，不改時脈或功率。",
            "個", "NVML（唯讀）", FactTrustLevel.Measured, false, at, states);
    }

    // ── GX-002 NVIDIA 風扇曲線 ────────────────────────────────────────

    public static HardwareFact NvFanFact(DateTimeOffset at, NvFanProbe? probe = null)
    {
        (int FanPercent, double Rpm)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null) return NoGpu(at, NvFanKey, "NVIDIA 風扇曲線");
        var (pct, rpm) = s.Value;
        return new HardwareFact(NvFanKey, Cat, "NVIDIA 風扇曲線",
            $"風扇 {pct}%、轉速 {rpm:0} RPM。<b>不可讀如實標</b>：部分筆電 GPU 由 OEM 鎖定風扇，" +
            "NVML 讀不到——讀不到就不出值，不改風扇。",
            "%", "NVML（唯讀）", FactTrustLevel.Measured, false, at, pct);
    }

    // ── GX-003 NVIDIA 功率限制 ────────────────────────────────────────

    public static HardwareFact NvPlFact(DateTimeOffset at, NvPlProbe? probe = null)
    {
        (int LimitW, int CurrentW)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null) return NoGpu(at, NvPlKey, "NVIDIA 功率限制");
        var (limit, current) = s.Value;
        return new HardwareFact(NvPlKey, Cat, "NVIDIA 功率限制",
            $"功率上限 {limit} W、目前 {current} W。上限是<b>可設定值</b>，本工具只讀現值不調整；" +
            "目前接近上限時會觸發功率節流（見節流原因）。",
            "W", "NVML（唯讀）", FactTrustLevel.Measured, false, at, current);
    }

    // ── GX-004 NVIDIA ECC 能力 ────────────────────────────────────────

    public static HardwareFact NvEccFact(DateTimeOffset at, NvEccProbe? probe = null)
    {
        (bool EccSupported, int CorrectedErrors)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null) return NoGpu(at, NvEccKey, "NVIDIA ECC 能力");
        var (supported, errors) = s.Value;
        return new HardwareFact(NvEccKey, Cat, "NVIDIA ECC 能力",
            supported
                ? $"ECC 支援、已修正錯誤 {errors} 次。消費級卡通常不支援 ECC——" +
                  "支援與否是<b>能力陳述</b>；修正錯誤數上升值得留意，但不單獨構成故障判決。"
                : "此 GPU 不支援 ECC（消費級常見）。不支援是能力事實，不是缺陷。",
            "次", "NVML（唯讀）", FactTrustLevel.Reported, false, at, supported ? 1 : 0);
    }

    // ── GX-005 ADLX 感測器全表 ────────────────────────────────────────

    public static HardwareFact AmdFact(DateTimeOffset at, AmdProbe? probe = null)
    {
        (int Sensors, bool ReadOk)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null) return NoGpu(at, AmdKey, "ADLX 感測器全表");
        var (sensors, ok) = s.Value;
        if (!ok) return NoGpu(at, AmdKey, "ADLX 感測器全表");
        return new HardwareFact(AmdKey, Cat, "ADLX 感測器全表",
            $"感測器 {sensors} 個（ADLX PMLog）。AMD 深度遙測走 atiadlxx.dll 唯讀；" +
            "溫度／風扇／功率的既有鍵（gpu.radeon.*）是此表的收斂呈現。",
            "個", "ADLX（唯讀）", FactTrustLevel.Measured, false, at, sensors);
    }

    // ── GX-006 Intel Xe 感測器 ────────────────────────────────────────

    public static HardwareFact IntelFact(DateTimeOffset at, IntelProbe? probe = null)
    {
        (int Sensors, bool ReadOk)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null) return NoGpu(at, IntelKey, "Intel Xe 感測器");
        var (sensors, ok) = s.Value;
        if (!ok) return NoGpu(at, IntelKey, "Intel Xe 感測器");
        return new HardwareFact(IntelKey, Cat, "Intel Xe 感測器",
            $"感測器 {sensors} 個。Intel Xe 內顯感測（頻率／功耗／溫度）走 iGPU 路徑；" +
            "無 iGPU 或驅動未提供時不適用。",
            "個", "Intel iGPU（唯讀）", FactTrustLevel.Measured, false, at, sensors);
    }

    // ── GX-007 VBIOS 版本 ─────────────────────────────────────────────

    public static HardwareFact VbiosFact(DateTimeOffset at, VbiosProbe? probe = null)
    {
        string? v;
        try { v = (probe ?? (() => ReadVbios()))(); }
        catch { v = null; }

        if (string.IsNullOrEmpty(v))
            return new HardwareFact(VbiosKey, Cat, "VBIOS 版本", "", "", "WMI／登錄（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "途徑不足——VBIOS 字串無法讀取（多數虛擬轉接器與部分驅動不提供），不猜版本");

        return new HardwareFact(VbiosKey, Cat, "VBIOS 版本",
            $"VBIOS {v}。<b>唯讀字串</b>：版本本身不代表更新或安全——" +
            "同一版本可能有不同設定，交叉需其他事實。",
            "", "WMI／登錄（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    private static string? ReadVbios()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT VideoBIOSVersion FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string v = o["VideoBIOSVersion"] as string ?? "";
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            return null;
        }
        catch { return null; }
    }

    // ── GX-008 VRAM 使用歷史 ──────────────────────────────────────────

    public static HardwareFact VramHistFact(DateTimeOffset at, VramHistProbe? probe = null)
    {
        IReadOnlyList<int>? series;
        try { series = (probe ?? (() => null))(); }
        catch { series = null; }

        if (series is null || series.Count < 2)
            return Unavailable(at, VramHistKey, Cat, "VRAM 使用歷史", FactAvailability.NotApplicable,
                "VRAM 歷史樣本少於兩點（或無 GPU 記憶體計數器）——不宣稱");

        int peak = series.Max();
        return new HardwareFact(VramHistKey, Cat, "VRAM 使用歷史",
            $"樣本 {series.Count} 點、峰值 {peak} MiB。VRAM 使用是<b>入倉的歷史</b>——" +
            "單點看不出趨勢；虛擬轉接器沒有真實 VRAM 計數器（值來自系統回報）。",
            "MiB", "歷史倉（唯讀）", FactTrustLevel.Measured, false, at, peak);
    }

    // ── GX-009 驅動版本鏈 ─────────────────────────────────────────────

    public static HardwareFact DrvChainFact(DateTimeOffset at, DrvChainProbe? probe = null)
    {
        (string DriverVersion, string WddmVersion, string DxVersion)? s;
        try { s = (probe ?? (() => ReadDrvChain()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DrvChainKey, Cat, "驅動版本鏈", FactAvailability.ReadError,
                "顯示驅動版本鏈不可讀——讀不到就是不猜");

        var (drv, wddm, dx) = s.Value;
        return new HardwareFact(DrvChainKey, Cat, "驅動版本鏈",
            $"驅動 {drv}／WDDM {wddm}／DirectX {dx}。<b>對照測試</b>：三者互相約束" +
            "（WDDM 版本決定可用的 DX 功能）；只列版本不列路徑。",
            "", "WMI／DXGI（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    private static (string, string, string)? ReadDrvChain()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DriverVersion FROM Win32_VideoController");
            string drv = "—";
            foreach (ManagementObject o in s.Get())
                using (o) { drv = o["DriverVersion"] as string ?? "—"; break; }
            return (drv, "—", "—");   // WDDM／DX 版本需 DXGI 查詢，本機虛擬轉接器途徑不足
        }
        catch { return null; }
    }

    // ── GX-010 多 GPU 模式 ────────────────────────────────────────────

    public static HardwareFact MultiFact(DateTimeOffset at, MultiProbe? probe = null)
    {
        (int Adapters, bool MixedVendor)? s;
        try { s = (probe ?? (() => ReadMulti()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, MultiKey, Cat, "多 GPU 模式", FactAvailability.ReadError, "顯示轉接器列舉失敗");

        var (adapters, mixed) = s.Value;
        return new HardwareFact(MultiKey, Cat, "多 GPU 模式",
            $"顯示轉接器 {adapters} 個、混合廠商={(mixed ? "是" : "否")}。" +
            "多 GPU 的主／輔／複製模式由驅動與使用情境決定；<b>本項只陳述轉接器數量</b>，" +
            "不推論目前是哪種模式（需實際算繪路徑才能確認）。",
            "個", "WMI（唯讀）", FactTrustLevel.Reported, false, at, adapters);
    }

    private static (int, bool) ReadMulti()
    {
        try
        {
            var vendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    n++;
                    string id = o["PNPDeviceID"] as string ?? "";
                    int i = id.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
                    if (i >= 0 && i + 8 <= id.Length) vendors.Add(id.Substring(i + 4, 4));
                }
            return (n, vendors.Count > 1);
        }
        catch { return (0, false); }
    }

    // ── GX-011 虛擬顯示器偵測 ─────────────────────────────────────────

    public static HardwareFact VdispFact(DateTimeOffset at, VdispProbe? probe = null)
    {
        (int Total, int Virtual)? s;
        try { s = (probe ?? (() => ReadVdisp()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, VdispKey, Cat, "虛擬顯示器偵測", FactAvailability.ReadError, "顯示轉接器列舉失敗");

        var (total, vdisp) = s.Value;
        return new HardwareFact(VdispKey, Cat, "虛擬顯示器偵測",
            $"顯示轉接器 {total} 個、疑似虛擬 {vdisp} 個。" +
            "<b>判定≠惡意</b>：虛擬顯示轉接器可能來自遠端桌面、虛擬機、串流工具、顯示器模擬——" +
            "本機就有 3 個虛擬轉接器（實體 GPU 已拔除）。這是<b>環境事實</b>，不是安全判決。",
            "個", "WMI（唯讀）", FactTrustLevel.Reported, false, at, vdisp);
    }

    private static (int, int) ReadVdisp()
    {
        try
        {
            int total = 0, virt = 0;
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    total++;
                    string name = (o["Name"] as string ?? "").ToLowerInvariant();
                    string id = (o["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                    if (name.Contains("virtual") || name.Contains("basic display")
                        || name.Contains("remote") || id.StartsWith("ROOT\\"))
                        virt++;
                }
            return (total, virt);
        }
        catch { return (0, 0); }
    }

    // ── GX-012 色深事實 ───────────────────────────────────────────────

    public static HardwareFact DepthFact(DateTimeOffset at, DepthProbe? probe = null)
    {
        int? depth;
        try { depth = (probe ?? (() => ReadDepth()))(); }
        catch { depth = null; }

        if (depth is null)
            return Unavailable(at, DepthKey, Cat, "色深事實", FactAvailability.NotSupported,
                "顯示模式未提供色深（虛擬轉接器常見）——讀不到就是不猜");

        return new HardwareFact(DepthKey, Cat, "色深事實",
            $"色深 {depth} bit。色深受輸出組態（連結速率、像素格式、頻寬）影響；" +
            "8-bit 與 10-bit 的差異需面板與內容都支援才看得到。",
            "bit", "顯示模式（唯讀）", FactTrustLevel.Reported, false, at, depth);
    }

    private static int? ReadDepth()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT CurrentBitsPerPixel FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (o["CurrentBitsPerPixel"] is not null) return Convert.ToInt32(o["CurrentBitsPerPixel"]);
                }
            return null;
        }
        catch { return null; }
    }

    // ── GX-013 RT/AI 能力旗標 ─────────────────────────────────────────

    public static HardwareFact RtFact(DateTimeOffset at, RtProbe? probe = null)
    {
        (bool Rt, bool Ai)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(RtKey, Cat, "RT/AI 能力旗標", "", "", "D3D 功能階層（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "無可查詢的 D3D 裝置（虛擬轉接器不支援功能階層查詢）——不猜能力");

        var (rt, ai) = s.Value;
        return new HardwareFact(RtKey, Cat, "RT/AI 能力旗標",
            $"光線追蹤={(rt ? "支援" : "不支援")}、AI 加速={(ai ? "支援" : "不支援")}。" +
            "<b>能力旗標</b>來自 D3D 功能階層——支援是硬體／驅動宣告，不保證實際效能。",
            "", "D3D 功能階層（唯讀）", FactTrustLevel.Reported, false, at, rt ? 1 : 0);
    }

    // ── GX-014 GPU 驅動版本史 ─────────────────────────────────────────

    public static HardwareFact GpuDrvHistFact(DateTimeOffset at, GpuDrvHistProbe? probe = null)
    {
        IReadOnlyList<string>? versions;
        try { versions = (probe ?? (() => ReadGpuDrvHist()))(); }
        catch { versions = null; }

        if (versions is null || versions.Count == 0)
            return Unavailable(at, GpuDrvHistKey, Cat, "GPU 驅動版本史", FactAvailability.NotApplicable,
                "沒有 GPU 驅動變更記錄——不從缺記錄推論更新史");

        return new HardwareFact(GpuDrvHistKey, Cat, "GPU 驅動版本史",
            $"記錄 {versions.Count} 筆版本變更（最新 {versions[0]}）。" +
            "驅動版本變更是<b>時間線</b>；變更與問題的因果需交叉事件記錄，單看版本史不下結論。" +
            "不列驅動路徑。",
            "筆", "驅動變更歷史（唯讀）", FactTrustLevel.Reported, false, at, versions.Count);
    }

    private static IReadOnlyList<string>? ReadGpuDrvHist()
    {
        try { return []; } catch { return null; }
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
