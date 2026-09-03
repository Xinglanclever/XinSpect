using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>
/// PCIe 鏈路實況：直接讀 PCI 設定空間裡每個裝置的 PCIe 能力結構，把
/// <b>目前協商到的速度與寬度</b>和<b>裝置自己宣告的能力</b>擺在一起比。
/// x16 的顯示卡實際跑在 x4、NVMe 插在只有兩條通道的 M.2 上，這兩件事只有這裡看得出來。
/// </summary>
/// <remarks>
/// <para>
/// 機制：透過 <see cref="WinRing0Bridge"/> 的 <c>ReadPciConfig</c>（0xCF8／0xCFC）掃 bus 0–255、
/// device 0–31，遇到多功能裝置才展開 function 1–7；找到能力清單裡的 PCIe 能力（ID 0x10）後，
/// 讀 +0x0C 的 Link Capabilities 與 +0x12 的 Link Status。全程<b>唯讀</b>，不寫任何暫存器。
/// </para>
/// <para>
/// 誠實界線：①鏈路沒建起來（空插槽）的通訊埠不列出——列出來只是一排「—」；
/// ②裝置名稱是拿 VEN／DEV 去比對 <c>Win32_PnPEntity</c> 得到的，兩張一模一樣的卡會顯示同一個名字，
/// 位置欄（bus:device.function）才是唯一的；③閒置降速是正常省電行為，判讀文字會明說，不當成故障。
/// </para>
/// </remarks>
public sealed class PcieLinkService : ObservableObject
{
    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "進頁後會自動掃一次 PCI 設定空間（唯讀）；也可按「重新掃描」重讀。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public ObservableCollection<PcieLinkRow> Rows { get; } = [];

    private bool _loadedOnce;

    /// <summary>
    /// 第一次進頁時自動掃一次。全程唯讀（只讀 PCI 設定空間，一個位元都不寫），
    /// 可重複呼叫但只有第一次真的做事。
    /// </summary>
    /// <remarks>
    /// 原本要使用者自己按「重新掃描」。問題是這一頁進去只會看到「尚未讀取」一行字，
    /// 而旁邊那些同樣是唯讀查詢的頁（顯示鏈路、USB 鏈路、開機耗時）都是自動讀的——
    /// 於是這一頁看起來就像「壞了、什麼都不顯示」。自動讀一次才對得上其他頁的行為。
    /// </remarks>
    public void EnsureLoaded()
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "掃描中…";
        Rows.Clear();
        // 掃描要跑好幾千次設定空間讀取，這段時間 Rows 是空的；Summary 若留著上一輪的字樣，
        // 看的人會把「掃描中」誤當成「掃完了，什麼都沒有」。
        Summary = "—";
        try
        {
            var (summary, rows, present) = await Task.Run(ScanAll);
            Summary = summary;
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            // 「0 條鏈路」要說清楚是「掃到裝置但沒有鏈路」還是「什麼都沒掃到」——
            // 這兩件事的成因完全不同，混成同一句話就無從判斷。
            Status = rows.Count > 0
                ? $"完成 ・ 掃到 {present} 個 PCI 功能，其中 {rows.Count} 條有已建立的 PCIe 鏈路。"
                : $"完成 ・ 掃到 {present} 個 PCI 功能，但沒有任何一個回報已建立的 PCIe 鏈路"
                  + "（純 PCI 舊裝置、沒有 PCIe 能力結構的功能、以及空插槽都不列）。";
        }
        catch (Exception ex)
        {
            Summary = "無法讀取 PCIe 鏈路：" + ex.Message;
            Status = "讀取失敗。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (string Summary, List<PcieLinkRow> Rows, int Present) ScanAll()
    {
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available)
            throw new InvalidOperationException("MSR／PCI 橋接無法初始化：" + bridge.Error);
        if (!bridge.PciAvailable)
            throw new InvalidOperationException("此版本的 Ring0 沒有提供 PCI 設定空間讀取。");

        // 探針：00:00.0 是 x86 上必然存在的主機橋接器。連它都讀不到，代表反射載得起來、
        // 但設定空間實際讀不通（沒有以系統管理員執行、驅動沒真正載入，或核心隔離／廠商驅動
        // 封鎖清單把 WinRing0 擋掉了——它就在微軟的易受攻擊驅動封鎖清單上）。
        //
        // 不攔的話，整趟掃描會安靜地回 0 條，畫面顯示「共 0 條鏈路」——那句話會被讀成
        // 「這台機器沒有 PCIe 裝置」，和事實正好相反。讀不到就要說讀不到。
        uint? probe = bridge.ReadPciConfig(0, 0, 0, 0x00);
        if (probe is null || probe == 0xFFFFFFFF)
            throw new InvalidOperationException(
                "PCI 設定空間讀不通：連 00:00.0（x86 上必然存在的主機橋接器）都讀不到"
                + (probe is null ? "（讀取呼叫本身失敗）" : "（回傳全 1，等同「此裝置不存在」）")
                + "。常見原因有三個：本程式沒有以系統管理員身分執行；WinRing0 驅動沒有真正載入；"
                + "或核心隔離／記憶體完整性（HVCI）與廠商驅動封鎖清單把它擋掉了——"
                + "WinRing0 就在微軟的易受攻擊驅動封鎖清單上。這一頁非它不可，"
                + "但不靠 PCI 設定空間的其他頁面不受影響。");

        var names = LoadPnpNames();
        var rows = new List<PcieLinkRow>();
        int present = 0;

        for (int bus = 0; bus <= 255; bus++)
        {
            for (int dev = 0; dev <= 31; dev++)
            {
                uint? id0 = bridge.ReadPciConfig((byte)bus, (byte)dev, 0, 0x00);
                if (!IsPresent(id0)) continue;

                // 標頭類型（0x0C 的第 3 個位元組）位 7＝多功能裝置；單功能就不必白掃 function 1–7
                uint header = bridge.ReadPciConfig((byte)bus, (byte)dev, 0, 0x0C) ?? 0;
                bool multi = ((header >> 16) & 0x80) != 0;

                for (int fn = 0; fn <= (multi ? 7 : 0); fn++)
                {
                    uint? id = fn == 0 ? id0 : bridge.ReadPciConfig((byte)bus, (byte)dev, (byte)fn, 0x00);
                    if (!IsPresent(id)) continue;
                    present++;
                    var row = ReadOne(bridge, (byte)bus, (byte)dev, (byte)fn, id!.Value, names);
                    if (row is not null) rows.Add(row);
                }
            }
        }

        // 寬度不足的排前面（那才是真的損失），其次是速度較低的，最後照位置排
        rows = rows.OrderByDescending(r => r.Severity)
                   .ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase)
                   .ToList();
        return (PcieLinkDecoder.Summarize(rows), rows, present);
    }

    /// <summary>設定空間讀不到、或全為 1（0xFFFFFFFF）＝該功能不存在。</summary>
    private static bool IsPresent(uint? id) => id is { } v && (v & 0xFFFF) != 0xFFFF && v != 0;

    private static PcieLinkRow? ReadOne(WinRing0Bridge bridge, byte bus, byte dev, byte fn, uint id,
                                        Dictionary<(ushort Ven, ushort Dev), string> names)
    {
        ushort ven = (ushort)(id & 0xFFFF), did = (ushort)(id >> 16);

        // 狀態暫存器（0x04 的高 16 位）位 4：有沒有能力清單
        uint cmdStatus = bridge.ReadPciConfig(bus, dev, fn, 0x04) ?? 0;
        if (((cmdStatus >> 16) & 0x10) == 0) return null;

        uint capOffset = FindPcieCap(bridge, bus, dev, fn);
        if (capOffset == 0) return null;

        uint capReg = bridge.ReadPciConfig(bus, dev, fn, capOffset) ?? 0;
        int portType = (int)((capReg >> 16 >> 4) & 0xF);

        uint linkCap = bridge.ReadPciConfig(bus, dev, fn, capOffset + 0x0C) ?? 0;
        uint linkCtlSta = bridge.ReadPciConfig(bus, dev, fn, capOffset + 0x10) ?? 0;

        var (maxSpeed, maxWidth) = PcieLinkDecoder.DecodeLinkCap(linkCap);
        var (curSpeed, curWidth) = PcieLinkDecoder.DecodeLinkStatus((ushort)(linkCtlSta >> 16));

        // 鏈路沒建起來（空插槽／裝置關電）就不列——一排「—」對使用者沒有意義
        if (curSpeed == 0 && curWidth == 0) return null;
        if (maxSpeed == 0 && maxWidth == 0) return null;

        var (verdict, severity) = PcieLinkDecoder.Judge(curSpeed, curWidth, maxSpeed, maxWidth);

        // 錯誤旗標：裝置狀態（PCIe 能力 +0x08 的高半字）與傳統 PCI 狀態（位移 0x04 的高半字）。
        // 兩者都在傳統設定空間內，讀得到；AER 在延伸空間 0x100 起，CF8/CFC 到不了，故不讀。
        ushort devStatus = (ushort)((bridge.ReadPciConfig(bus, dev, fn, capOffset + 0x08) ?? 0) >> 16);
        ushort pciStatus = (ushort)((bridge.ReadPciConfig(bus, dev, fn, 0x04) ?? 0) >> 16);
        var (errText, errSev) = PcieLinkDecoder.DecodeErrorFlags(devStatus, pciStatus);

        string name = names.TryGetValue((ven, did), out var n) ? n : $"PCI 裝置 {ven:X4}:{did:X4}";
        return new PcieLinkRow(name, $"{bus:X2}:{dev:X2}.{fn}", PcieLinkDecoder.PortTypeName(portType),
                               curSpeed, curWidth, maxSpeed, maxWidth, verdict, severity,
                               errText, errSev);
    }

    /// <summary>
    /// 走能力清單找 PCIe 能力（ID 0x10）的位移；找不到回 0。
    /// 能力位移依規格是 DWORD 對齊的（低兩位保留），這裡一律遮掉再讀，
    /// 否則韌體填了雜訊時會拿去做未對齊的設定空間讀取。
    /// </summary>
    private static uint FindPcieCap(WinRing0Bridge bridge, byte bus, byte dev, byte fn)
    {
        uint ptr = (bridge.ReadPciConfig(bus, dev, fn, 0x34) ?? 0) & 0xFC;
        // 上限 48 圈：韌體給出環狀的 next 指標時不能無限走下去
        for (int guard = 0; guard < 48 && ptr >= 0x40; guard++)
        {
            uint head = bridge.ReadPciConfig(bus, dev, fn, ptr) ?? 0;
            byte capId = (byte)(head & 0xFF);
            uint next = (head >> 8) & 0xFC;
            if (capId == PcieLinkDecoder.PcieCapId) return ptr;
            if (next == 0 || next == ptr) break;
            ptr = next;
        }
        return 0;
    }

    /// <summary>從 <c>Win32_PnPEntity</c> 建 (VEN, DEV) → 名稱的對照表（同型號的卡共用一個名字）。</summary>
    private static Dictionary<(ushort Ven, ushort Dev), string> LoadPnpNames()
    {
        var map = new Dictionary<(ushort, ushort), string>();
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI\\\\%'");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (o["DeviceID"] as string is not { } devId || o["Name"] as string is not { } name) continue;
                    if (ParseVenDev(devId) is not { } key) continue;
                    map.TryAdd(key, name);
                }
            }
        }
        catch (Exception ex) { Diag.Swallow("PCIe 裝置名稱查詢", ex, "改以 VEN:DEV 十六進位顯示"); }
        return map;
    }

    /// <summary>從 <c>PCI\VEN_10DE&amp;DEV_2504&amp;…</c> 取出 VEN／DEV（純函式，單元測試涵蓋）。</summary>
    public static (ushort Ven, ushort Dev)? ParseVenDev(string deviceId)
    {
        int v = deviceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        int d = deviceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (v < 0 || d < 0 || v + 8 > deviceId.Length || d + 8 > deviceId.Length) return null;
        if (!ushort.TryParse(deviceId.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var ven)) return null;
        if (!ushort.TryParse(deviceId.AsSpan(d + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var dev)) return null;
        return (ven, dev);
    }
}
