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

    private string _status = "尚未讀取。按「重新掃描」讀取 PCI 設定空間（唯讀）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public ObservableCollection<PcieLinkRow> Rows { get; } = [];

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
            var (summary, rows) = await Task.Run(ScanAll);
            Summary = summary;
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Status = $"完成 ・ 共 {rows.Count} 條鏈路。";
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

    private (string Summary, List<PcieLinkRow> Rows) ScanAll()
    {
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available)
            throw new InvalidOperationException("MSR／PCI 橋接無法初始化：" + bridge.Error);
        if (!bridge.PciAvailable)
            throw new InvalidOperationException("此版本的 Ring0 沒有提供 PCI 設定空間讀取。");

        var names = LoadPnpNames();
        var rows = new List<PcieLinkRow>();

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
                    var row = ReadOne(bridge, (byte)bus, (byte)dev, (byte)fn, id!.Value, names);
                    if (row is not null) rows.Add(row);
                }
            }
        }

        // 寬度不足的排前面（那才是真的損失），其次是速度較低的，最後照位置排
        rows = rows.OrderByDescending(r => r.Severity)
                   .ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase)
                   .ToList();
        return (PcieLinkDecoder.Summarize(rows), rows);
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
        string name = names.TryGetValue((ven, did), out var n) ? n : $"PCI 裝置 {ven:X4}:{did:X4}";
        return new PcieLinkRow(name, $"{bus:X2}:{dev:X2}.{fn}", PcieLinkDecoder.PortTypeName(portType),
                               curSpeed, curWidth, maxSpeed, maxWidth, verdict, severity);
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
