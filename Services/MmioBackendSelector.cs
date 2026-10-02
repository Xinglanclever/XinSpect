namespace XinSpect;

/// <summary>
/// MMIO 後端裁決（V7 §2.1／§2.4）：WinRing0 實體記憶體為主力（能力最寬，HVCI 關閉環境的主路徑）；
/// XsRegProbe 白名單 IOCTL 為備援（驅動已載入且握手成功才考慮）；兩者皆不可用時回「帶完整原因」
/// 的固定三態件——原因串接每個後端為什麼不行，讓「為什麼讀不到」一句話說完。
/// 被淘汰的後端立即交還（Dispose），不佔驅動控制代碼。
/// </summary>
public static class MmioBackendSelector
{
    /// <summary>依主力→備援順序裁決；工廠可注入供測試。回傳件由呼叫方 Dispose（非 IDisposable 實作者免）。</summary>
    public static IMmioReader Select(Func<IMmioReader>? primaryFactory = null, Func<IMmioReader>? fallbackFactory = null)
    {
        var primary = (primaryFactory ?? (() => new WinRing0MmioReader()))();
        if (primary.Available) return primary;
        var primaryReason = primary.UnavailableReason ?? "原因不明";

        var fallback = (fallbackFactory ?? (() => new DriverMmioReader()))();
        if (fallback.Available)
        {
            (primary as IDisposable)?.Dispose();
            return fallback;
        }
        var fallbackReason = fallback.UnavailableReason ?? "原因不明";

        (fallback as IDisposable)?.Dispose();
        (primary as IDisposable)?.Dispose();
        return new UnavailableMmioReader(
            $"WinRing0 實體記憶體不可用（{primaryReason}）；XsRegProbe 不可用（{fallbackReason}）");
    }
}
