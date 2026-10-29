using System.Management;

namespace XinSpect;

/// <summary>
/// 電池健康的唯讀讀取（U10 第三圈 BT-0002～BT-0004，第 316 輪）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼不直接用 <see cref="BatteryService"/>：</b>那支把三種情況壓進同一個布林值——
/// <c>Present=false</c> 既可能是「這台沒有電池」，也可能是「WMI 讀失敗」，
/// 而三條容量查詢的 <c>catch { }</c> 會讓「沒回報」變成 0。這一支把三態分開回報：
/// 讀取失敗丟理由、沒有電池回 0 顆、有電池但欄位缺就留 <c>null</c>。
/// </para>
/// <para>
/// <b>單位：</b><c>BatteryStaticData.DesignedCapacity</c> 與 <c>BatteryFullChargedCapacity</c>
/// 都已經是 mWh（毫瓦時），不需要再換算；<c>BatteryCycleCount.CycleCount</c> 是次數。
/// </para>
/// </remarks>
public static class BatteryHealthReader
{
    /// <summary>讀一次電池健康；<c>Sample</c> 為 null 表示讀取本身失敗（理由在 <c>Reason</c>）。</summary>
    public static (BatteryHealthFactsService.Sample? Sample, string? Reason) TryRead()
    {
        try
        {
            int batteries = 0;
            using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name FROM Win32_Battery"))
            {
                foreach (var _ in searcher.Get()) batteries++;
            }

            if (batteries == 0)
                return (new BatteryHealthFactsService.Sample(0, null, null, null), null);

            return (new BatteryHealthFactsService.Sample(
                batteries,
                QueryNumber("root\\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity"),
                QueryNumber("root\\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity"),
                QueryNumber("root\\WMI", "SELECT CycleCount FROM BatteryCycleCount", "CycleCount")), null);
        }
        catch (Exception ex)
        {
            return (null, UserFacingError.Describe(ex));
        }
    }

    /// <summary>
    /// 一條 WMI 數值查詢：欄位不存在、查詢失敗、物件不存在一律回 <c>null</c>——
    /// 「沒回報」不回 0，因為 0 是「回報了 0」。
    /// </summary>
    private static long? QueryNumber(string scope, string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    object? raw = o[property];
                    if (raw is null) continue;
                    long value = Convert.ToInt64(raw);
                    return value > 0 ? value : null;   // 0 在這些欄位上不存在實體意義 → 當成沒回報
                }
            }
        }
        catch
        {
            // 這條查詢本身不可用（部分機型沒有 root\WMI 的電池類別）——回 null，由事實層說「沒回報」。
        }

        return null;
    }
}
