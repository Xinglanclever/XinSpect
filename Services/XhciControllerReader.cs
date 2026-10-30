using System.Globalization;

namespace XinSpect;

/// <summary>
/// 找出這台機器的 xHCI 控制器並讀它們的能力區（U10 第三圈 NP-0002 的生產端）。
/// </summary>
/// <remarks>
/// <para>
/// <b>怎麼找 xHCI 控制器：</b>不靠 PCI class code 猜，而是<b>看誰是 <c>USB\ROOT_HUB30</c> 的父節點</b>。
/// 那個父節點就是 xHCI 控制器——這是 PnP 樹自己說的，不是本版推的。
/// </para>
/// <para>
/// <b>怎麼讀能力區：</b>從 instance ID 解出 bus／dev／fn，用 <see cref="IPciConfigReader"/>
/// 讀 BAR0（位移 <c>0x10</c>），遮掉低位元的型別位，再用 <see cref="IMmioReader"/>
/// 讀那一段的前 <see cref="XhciCapabilityDecoder.CapabilityBytes"/> 位元組。
/// </para>
/// <para>
/// <b>每一步都可能失敗，而失敗要能說出是哪一步。</b>
/// 沒有 PCI reader → 「缺 ring0 的埠存取」；沒有 MMIO → 「缺 ring0 的實體記憶體讀取」；
/// BAR0 是 I/O 空間或 0 → 「控制器沒宣告 MMIO BAR」。三種原因對應三種不同的處置。
/// </para>
/// </remarks>
public static class XhciControllerReader
{
    /// <summary>
    /// 從裝置報告挑出 xHCI 控制器（<c>USB\ROOT_HUB30</c> 的父節點）。
    /// </summary>
    public static IReadOnlyList<string> FindControllers(DeviceDiagnosticReport report)
    {
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in report.Devices)
            if (d.InstanceId.StartsWith(@"USB\ROOT_HUB30", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(d.ParentInstanceId))
                parents.Add(d.ParentInstanceId);

        return parents
            .Where(p => p.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>從 PCI instance ID 取 bus／dev／fn。格式：<c>PCI\VEN_xxxx&amp;DEV_yyyy&amp;…\&lt;位置&gt;</c>。</summary>
    /// <remarks>
    /// 位置段的格式依匯流排而異（ACPI 的 <c>3&amp;11583659&amp;0&amp;A0</c> 與舊式的 <c>0:1F.5</c> 都出現過）。
    /// 本版<b>只認第二種</b>（<c>bb:dd.f</c>）；認不出來就回 <c>null</c>，不猜位置。
    /// </remarks>
    public static (byte Bus, byte Device, byte Function)? ParseLocation(string instanceId)
    {
        int last = instanceId.LastIndexOf('\\');
        if (last < 0 || last + 1 >= instanceId.Length) return null;
        string loc = instanceId[(last + 1)..];

        // 舊式 bb:dd.f
        int colon = loc.IndexOf(':');
        int dot = loc.IndexOf('.');
        if (colon > 0 && dot > colon)
        {
            if (byte.TryParse(loc[..colon], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)
                && byte.TryParse(loc[(colon + 1)..dot], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte d)
                && byte.TryParse(loc[(dot + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte f))
                return (b, d, f);
        }
        return null;
    }

    /// <summary>
    /// 讀一個控制器的能力區。<paramref name="pci"/>／<paramref name="mmio"/> 任一不可用或任一步失敗，
    /// 都回一個帶原因的樣本（<c>Mmio</c> 為 <c>null</c>）。
    /// </summary>
    public static XhciControllerSample Read(string controllerId, IPciConfigReader pci, IMmioReader mmio)
    {
        if (!pci.Available)
            return new XhciControllerSample(controllerId, null,
                "PCI 設定空間不可用：" + (pci.UnavailableReason ?? "原因未提供"));
        if (!mmio.Available)
            return new XhciControllerSample(controllerId, null,
                "MMIO 不可用：" + (mmio.UnavailableReason ?? "原因未提供"));

        if (ParseLocation(controllerId) is not { } loc)
            return new XhciControllerSample(controllerId, null,
                "從 instance ID 解不出 bus／dev／fn（這個匯流排的位置編碼本版不認）——不猜");

        if (pci.ReadDword(loc.Bus, loc.Device, loc.Function, 0x10) is not { } bar0)
            return new XhciControllerSample(controllerId, null, "BAR0（位移 0x10）讀不到");

        // BAR 低位元 0x1＝I/O 空間；xHCI 的能力區一定在記憶體空間，所以那時沒有 MMIO 可讀。
        if ((bar0 & 0x01) != 0)
            return new XhciControllerSample(controllerId, null,
                $"BAR0 標成 I/O 空間（0x{bar0:X8}）——xHCI 能力區應在記憶體空間，這個值不可用");

        ulong baseAddr = bar0 & 0xFFFFFFF0u;
        if (baseAddr == 0)
            return new XhciControllerSample(controllerId, null, "BAR0 是 0——控制器沒有宣告記憶體基底");

        var block = mmio.ReadBlock(baseAddr, XhciCapabilityDecoder.CapabilityBytes);
        if (block is null)
            return new XhciControllerSample(controllerId, null,
                $"從 0x{baseAddr:X} 讀 {XhciCapabilityDecoder.CapabilityBytes} 位元組失敗"
                + (mmio.LastFailReason is { } f ? $"（{f}）" : ""));

        return new XhciControllerSample(controllerId, block, null, baseAddr);
    }

    public static IReadOnlyList<XhciControllerSample> ReadAll(
        DeviceDiagnosticReport report, IPciConfigReader pci, IMmioReader mmio) =>
        FindControllers(report).Select(id => Read(id, pci, mmio)).ToList();
}
