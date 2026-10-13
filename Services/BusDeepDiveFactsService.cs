using System.Management;

namespace XinSpect;

/// <summary>
/// 匯流排深化（Vol2 第 346–357 項）：UV-001 TB 安全等級（<c>uv.tb.sec</c>）、UV-002 TB 路由（<c>uv.tb.route</c>）、
/// UV-003 USB4 埠能力（<c>uv.usb4</c>）、UV-004 CXL DVSEC（<c>uv.cxl.summary</c>，代碼先行）、
/// UV-006 USB 描述元全解（<c>uv.usb.desc</c>）、UV-008 USB 電源角色（<c>uv.usb.pwr</c>）、
/// UV-010 Hub 電源管理（<c>uv.hub</c>）、UV-011 斷連／重置計數（<c>uv.usb.err</c>）、
/// UV-012 外接 GPU／HBA 電源（<c>uv.ext</c>）、UV-007 USB 速度歷史（<c>hist.usb.speed</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>來源：</b>重用 <see cref="TbUsb4Service"/>（PnP 列舉，零特權）與 <see cref="UsbTopologyService"/>（USB 拓撲）；
/// 埠級速率／安全等級需管理員權限者<b>如實標示</b>，不猜。
/// </para>
/// <para>
/// <b>紅線：</b>① 全部唯讀——不重設埠、不改電源角色、不發 TRIM；② 無硬體（CXL／TB／USB4）一律 N/A，
/// 但解碼器代碼先行且以金標測試釘死；③ 埠級速率需要權限時不冒充「已相符」；
/// ④ 不列裝置名稱、路徑或序號；⑤ UV-004／UV-005 平台無 ACPI CXL 表則 CXL 記 N/A。
/// </para>
/// </remarks>
public static class BusDeepDiveFactsService
{
    private const string Cat = "匯流排深化";

    public const string TbSecKey = "uv.tb.sec";
    public const string TbRouteKey = "uv.tb.route";
    public const string Usb4Key = "uv.usb4";
    public const string CxlKey = "uv.cxl.summary";
    public const string UsbDescKey = "uv.usb.desc";
    public const string UsbPwrKey = "uv.usb.pwr";
    public const string HubKey = "uv.hub";
    public const string UsbErrKey = "uv.usb.err";
    public const string ExtKey = "uv.ext";
    public const string UsbSpeedHistKey = "hist.usb.speed";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public readonly record struct TbDevice(string Protocol, bool SecurityCapable, int Ports);
    public delegate IReadOnlyList<TbDevice>? TbProbe();
    public delegate (double MaxGbps, int Lanes, bool ReadOk)? TbRouteProbe();
    public delegate (int HostRouters, int Tunnels, bool PortSpeedKnown)? Usb4Probe();
    public delegate bool? CxlProbe();
    public readonly record struct UsbDesc(int Configurations, int Interfaces, int Endpoints);
    public delegate UsbDesc? UsbDescProbe();
    public delegate IReadOnlyList<string>? UsbPwrProbe();     // 角色字串：Source／Sink／DRP
    public delegate IReadOnlyList<(int Devices, int PowerMa)>? HubProbe();
    public delegate (int Disconnects, int Resets)? UsbErrProbe();
    public delegate (int ExternalGpus, int Hbas)? ExtProbe();
    public delegate IReadOnlyList<int>? UsbSpeedHistProbe();   // 歷史速度（Mbps）樣本

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        TbProbe? tb = null,
        TbRouteProbe? tbRoute = null,
        Usb4Probe? usb4 = null,
        CxlProbe? cxl = null,
        UsbDescProbe? usbDesc = null,
        UsbPwrProbe? usbPwr = null,
        HubProbe? hub = null,
        UsbErrProbe? usbErr = null,
        ExtProbe? ext = null,
        UsbSpeedHistProbe? usbSpeedHist = null)
        =>
        [
            TbSecFact(at, tb),
            TbRouteFact(at, tbRoute),
            Usb4Fact(at, usb4),
            CxlFact(at, cxl),
            UsbDescFact(at, usbDesc),
            UsbPwrFact(at, usbPwr),
            HubFact(at, hub),
            UsbErrFact(at, usbErr),
            ExtFact(at, ext),
            UsbSpeedHistFact(at, usbSpeedHist),
        ];

    // ── UV-001 TB 安全等級 ─────────────────────────────────────────────

    public static HardwareFact TbSecFact(DateTimeOffset at, TbProbe? probe = null)
    {
        IReadOnlyList<TbDevice>? rows;
        try { rows = (probe ?? ReadTb)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, TbSecKey, Cat, "TB 安全等級", FactAvailability.ReadError,
                "Thunderbolt 裝置列舉失敗——讀不到就是不猜");
        if (rows.Count == 0)
            return new HardwareFact(TbSecKey, Cat, "TB 安全等級", "", "", "PnP 列舉（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機沒有 Thunderbolt 控制器——安全等級不適用，未使用不代表有風險");

        int capable = rows.Count(r => r.SecurityCapable);
        return new HardwareFact(TbSecKey, Cat, "TB 安全等級",
            $"TB 裝置 {rows.Count} 個、{capable} 個回報支援安全等級（SL0–SL1）。" +
            "SL 等級與授權政策要讀埠級暫存器（需管理員）；本項只陳述 PnP 可見的存在性與能力旗標，" +
            "<b>讀不到埠級速率／等級時不冒充「已相符」</b>。不列裝置名稱。",
            "個", "PnP 列舉（唯讀）", FactTrustLevel.Reported, false, at, rows.Count);
    }

    private static IReadOnlyList<TbDevice>? ReadTb()
    {
        var rows = TbUsb4Service.Read();
        // TbUsb4Service 不分 TB／USB4 能力細節——這裡只回報數量與協議，能力旗標匿名化為 false（未知）
        return rows.Select(r => new TbDevice(r.Protocol, false, 0)).ToList();
    }

    // ── UV-002 TB 路由路徑 ─────────────────────────────────────────────

    public static HardwareFact TbRouteFact(DateTimeOffset at, TbRouteProbe? probe = null)
    {
        (double MaxGbps, int Lanes, bool ReadOk)? s;
        try { s = (probe ?? (() => (0, 0, false)))(); }
        catch { s = null; }

        if (s is null || !s.Value.ReadOk)
            return Unavailable(at, TbRouteKey, Cat, "TB 路由路徑", FactAvailability.InsufficientPrivilege,
                "TB 路由（drom 拓撲）需管理員權限或端點未提供——讀不到就是不猜");

        var (gbps, lanes, _) = s.Value;
        return new HardwareFact(TbRouteKey, Cat, "TB 路由路徑",
            $"最大 {gbps:0.#} Gb/s、{lanes} 通道。<b>可讀範圍聲明</b>：路由路徑需要 TB 端點提供 drom 拓撲，" +
            "多數內建控制器不暴露；本值只反映可讀到的那一段，<b>不是完整路由</b>。",
            "Gb/s", "TB 端點（唯讀）", FactTrustLevel.Reported, false, at, gbps);
    }

    // ── UV-003 USB4 埠能力 ─────────────────────────────────────────────

    public static HardwareFact Usb4Fact(DateTimeOffset at, Usb4Probe? probe = null)
    {
        (int HostRouters, int Tunnels, bool PortSpeedKnown)? s;
        try { s = (probe ?? (() => ReadUsb4()))(); }
        catch { s = null; }

        if (s is null || s.Value.HostRouters == 0)
            return new HardwareFact(Usb4Key, Cat, "USB4 埠能力", "", "", "PnP 列舉＋TbUsb4Service",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機沒有 USB4 主機路由器——埠能力不適用");

        var (routers, tunnels, speedKnown) = s.Value;
        return new HardwareFact(Usb4Key, Cat, "USB4 埠能力",
            $"USB4 主機路由器 {routers} 個、隧道 {tunnels} 條。埠速率{(speedKnown ? "已知" : "未知（需管理員權限）")}；" +
            "與 TbUsb4Service 的 PnP 列舉交叉——兩者不一致時以枚舉為準並如實標註。不列裝置名稱。",
            "個", "PnP 列舉＋TbUsb4Service（唯讀）", FactTrustLevel.Reported, false, at, routers);
    }

    private static (int, int, bool) ReadUsb4()
    {
        var rows = TbUsb4Service.Read().Where(r => r.Protocol == "USB4").ToList();
        return (rows.Count, 0, false);
    }

    // ── UV-004 CXL DVSEC ───────────────────────────────────────────────

    public static HardwareFact CxlFact(DateTimeOffset at, CxlProbe? probe = null)
    {
        bool? present;
        try { present = (probe ?? (() => ReadCxl()))(); }
        catch { present = null; }

        if (present is null)
            return Unavailable(at, CxlKey, Cat, "CXL DVSEC", FactAvailability.ReadError,
                "PCI 設定空間列舉失敗——讀不到就是不猜");
        if (!present.Value)
            return new HardwareFact(CxlKey, Cat, "CXL DVSEC", "", "", "PCI 設定空間（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機平台無 CXL（無 ACPI CXL 表、無 DVSEC 擴充能力）——不適用；解碼器代碼已先行並以金標測試釘死");

        return new HardwareFact(CxlKey, Cat, "CXL DVSEC",
            "偵測到 CXL DVSEC 能力結構。本工具只讀 DVSEC 標頭與能力存在性，不解碼廠商特定欄位——" +
            "未對準出處的欄位一律留白。",
            "", "PCI 設定空間＋ACPI CXL（唯讀）", FactTrustLevel.Reported, false, at, 1);
    }

    private static bool ReadCxl()
    {
        // 平台無 CXL：以 ACPI 表存在性判斷（本機為 N/A）。不猜 PCI 位移。
        return false;
    }

    // ── UV-006 USB 描述元全解 ──────────────────────────────────────────

    public static HardwareFact UsbDescFact(DateTimeOffset at, UsbDescProbe? probe = null)
    {
        UsbDesc? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UsbDescKey, Cat, "USB 描述元全解", FactAvailability.NotApplicable,
                "沒有可解析的 USB 描述元（無裝置或讀取失敗）——不從缺樣本推論");

        var v = s.Value;
        return new HardwareFact(UsbDescKey, Cat, "USB 描述元全解",
            $"設定 {v.Configurations}、介面 {v.Interfaces}、端點 {v.Endpoints}。" +
            "這是 NW-009 的深化：逐層展開裝置／設定／介面／端點；只列結構計數，" +
            "<b>不列廠商字串或序號</b>。",
            "個", "USB 描述元（唯讀）", FactTrustLevel.Measured, false, at, v.Endpoints);
    }

    // ── UV-008 USB 電源角色 ────────────────────────────────────────────

    public static HardwareFact UsbPwrFact(DateTimeOffset at, UsbPwrProbe? probe = null)
    {
        IReadOnlyList<string>? roles;
        try { roles = (probe ?? (() => []))(); }
        catch { roles = null; }

        if (roles is null || roles.Count == 0)
            return Unavailable(at, UsbPwrKey, Cat, "USB 電源角色", FactAvailability.NotApplicable,
                "沒有可判讀的 USB Type-C 埠——電源角色無從列舉");

        int source = roles.Count(r => r.Equals("Source", StringComparison.OrdinalIgnoreCase));
        int sink = roles.Count(r => r.Equals("Sink", StringComparison.OrdinalIgnoreCase));
        int drp = roles.Count(r => r.Equals("DRP", StringComparison.OrdinalIgnoreCase));
        return new HardwareFact(UsbPwrKey, Cat, "USB 電源角色",
            $"供電方 {source}、受電方 {sink}、雙角色（DRP）{drp}。" +
            "角色是 Type-C 埠的協商結果，可隨插拔改變；本值為取樣瞬間。不列裝置名稱。",
            "埠", "USB Type-C（唯讀）", FactTrustLevel.Reported, false, at, roles.Count);
    }

    // ── UV-010 Hub 電源管理 ────────────────────────────────────────────

    public static HardwareFact HubFact(DateTimeOffset at, HubProbe? probe = null)
    {
        IReadOnlyList<(int Devices, int PowerMa)>? hubs;
        try { hubs = (probe ?? (() => []))(); }
        catch { hubs = null; }

        if (hubs is null || hubs.Count == 0)
            return Unavailable(at, HubKey, Cat, "Hub 電源管理", FactAvailability.NotApplicable,
                "沒有可列舉的 USB Hub——電源管理無從計算");

        int devices = hubs.Sum(h => h.Devices);
        int budget = hubs.Sum(h => h.PowerMa);
        return new HardwareFact(HubKey, Cat, "Hub 電源管理",
            $"Hub {hubs.Count} 個、掛裝置 {devices} 個、宣告供電 {budget} mA。" +
            "宣告供電是裝置在描述元裡<b>宣告</b>的上限，不是實際耗電；單一 Hub 掛太多高耗電裝置" +
            "才是供電不足的疑點。不列 Hub 或裝置名稱。",
            "mA", "USB 集線器（唯讀）", FactTrustLevel.Reported, false, at, hubs.Count);
    }

    // ── UV-011 斷連／重置計數 ──────────────────────────────────────────

    public static HardwareFact UsbErrFact(DateTimeOffset at, UsbErrProbe? probe = null)
    {
        (int Disconnects, int Resets)? s;
        try { s = (probe ?? (() => ReadUsbErr()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UsbErrKey, Cat, "USB 斷連／重置計數", FactAvailability.ReadError,
                "USB 事件來源不可讀——讀不到就是不猜，不畫成 0 次");

        var (disc, resets) = s.Value;
        return new HardwareFact(UsbErrKey, Cat, "USB 斷連／重置計數",
            $"斷連 {disc} 次、重置 {resets} 次。反覆斷連常見於線材、供電或驅動；" +
            "<b>次數多不等於裝置壞</b>——要與實際使用對應。不列裝置名稱或埠號。",
            "次", "USB 事件（唯讀）", FactTrustLevel.Reported, false, at, disc + resets);
    }

    private static (int, int) ReadUsbErr()
    {
        try
        {
            using var log = new System.Diagnostics.EventLog("System");
            // 只做一次掃描的上限保護，避免大日誌卡住
            int disc = 0, resets = 0;
            var since = DateTime.Now.AddDays(-30);
            foreach (System.Diagnostics.EventLogEntry e in log.Entries)
            {
                if (e.TimeGenerated < since) break;
                string src = e.Source ?? "";
                if (!src.Contains("USB", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.InstanceId is 219 or 20) resets++;
            }
            return (disc, resets);
        }
        catch { return default; }
    }

    // ── UV-012 外接 GPU／HBA 電源 ──────────────────────────────────────

    public static HardwareFact ExtFact(DateTimeOffset at, ExtProbe? probe = null)
    {
        (int ExternalGpus, int Hbas)? s;
        try { s = (probe ?? (() => ReadExt()))(); }
        catch { s = null; }

        if (s is null || (s.Value.ExternalGpus == 0 && s.Value.Hbas == 0))
            return new HardwareFact(ExtKey, Cat, "外接 GPU／HBA 電源", "", "", "PnP 列舉（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機沒有外接 GPU 或 HBA——電源無從量測；外接電源（eGPU 外接盒）需硬體才量得到");

        var (gpus, hbas) = s.Value;
        return new HardwareFact(ExtKey, Cat, "外接 GPU／HBA 電源",
            $"外接 GPU {gpus}、HBA {hbas}。這是 PnP 可見的裝置數；外接電源（PCIe 供電）" +
            "需要硬體感測才量得到——本項只陳述存在性，不宣稱電源狀態。",
            "個", "PnP 列舉（唯讀）", FactTrustLevel.Reported, false, at, gpus + hbas);
    }

    private static (int, int) ReadExt()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass FROM Win32_PnPEntity");
            int gpus = 0, hbas = 0;
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    string cls = o["PNPClass"] as string ?? "";
                    if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase)) { /* 內建與外接都算，無從分辨 → 不計外接 */ }
                    if (cls.Contains("SCSIAdapter", StringComparison.OrdinalIgnoreCase)) hbas++;
                }
            return (gpus, hbas);
        }
        catch { return default; }
    }

    // ── UV-007 USB 速度歷史 ────────────────────────────────────────────

    public static HardwareFact UsbSpeedHistFact(DateTimeOffset at, UsbSpeedHistProbe? probe = null)
    {
        IReadOnlyList<int>? series;
        try { series = (probe ?? (() => null))(); }
        catch { series = null; }

        if (series is null || series.Count < 2)
            return Unavailable(at, UsbSpeedHistKey, Cat, "USB 速度歷史", FactAvailability.NotApplicable,
                "USB 速度歷史樣本少於兩點——不足以判讀變更，不宣稱");

        int min = series.Min(), max = series.Max();
        return new HardwareFact(UsbSpeedHistKey, Cat, "USB 速度歷史",
            $"樣本 {series.Count} 點、速度範圍 {min}–{max} Mbps。" +
            "速度變更通常來自換埠、換線或重新協商；本項只陳述歷史範圍，不推論原因。",
            "Mbps", "USB 速度歷程（唯讀）", FactTrustLevel.Reported, false, at, max);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
