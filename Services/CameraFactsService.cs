using System.Management;

namespace XinSpect;

/// <summary>一台 PnP 攝影機裝置（彙整前形態）。Status 是 WMI 的裝置狀態（OK/Error/...）。</summary>
public sealed record PnpCameraEntry(string Name, string Status);

/// <summary>
/// WP23 攝影機/UVC：PnP 列舉（WMI Win32_PnPEntity，PNPClass＝Camera 或 Image，usermode）。
/// 裝置狀態照抄系統口徑——「裝置存在但狀態 Error」與「不存在」是兩回事，不能混報。
/// </summary>
public static class CameraFactsService
{
    private const string Category = "週邊匯流排";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<PnpCameraEntry>?>? probe = null)
    {
        var cameras = (probe ?? FetchWmi)();
        if (cameras is null)
            return [new HardwareFact("cam.count", Category, "攝影機/UVC 裝置", "", "",
                "WMI Win32_PnPEntity（PNPClass Camera/Image）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "WMI 查詢失敗——攝影機列舉讀不到就是不猜")];

        var facts = new List<HardwareFact>
        {
            new("cam.count", Category, "攝影機/UVC 裝置",
                cameras.Count == 0 ? "0 台（沒有攝影機裝置）" : cameras.Count.ToString(), "台",
                "WMI Win32_PnPEntity（PNPClass Camera/Image）", FactTrustLevel.Reported, false, at, cameras.Count),
        };
        for (int i = 0; i < cameras.Count; i++)
        {
            var c = cameras[i];
            facts.Add(new HardwareFact($"cam.{i}", Category, $"攝影機 {i}",
                $"{c.Name}（狀態：{c.Status}）", "",
                "WMI Win32_PnPEntity（PNPClass Camera/Image）", FactTrustLevel.Reported, false, at, null));
        }
        return facts;
    }

    /// <summary>WMI 通路（極薄）：失敗回 null。</summary>
    public static IReadOnlyList<PnpCameraEntry>? FetchWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            var cameras = new List<PnpCameraEntry>();
            foreach (var m in searcher.Get())
                cameras.Add(new PnpCameraEntry(m["Name"]?.ToString() ?? "", m["Status"]?.ToString() ?? ""));
            return cameras;
        }
        catch { return null; }
    }
}
