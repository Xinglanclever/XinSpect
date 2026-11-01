namespace XinSpect;

/// <summary>
/// MMIO 後端裁決（V7 §2.1／§2.4）：WinRing0 實體記憶體為主力（能力最寬，HVCI 關閉環境的主路徑）；
/// XsRegProbe 白名單 IOCTL 為備援（驅動已載入且握手成功才考慮）；兩者皆不可用時回「帶完整原因」
/// 的固定三態件——原因串接每個後端為什麼不行，讓「為什麼讀不到」一句話說完。
/// 被淘汰的後端立即交還（Dispose），不佔驅動控制代碼。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼「有方法」不等於「可用」</b>（2026-11-01 實測）：LHM 0.9.4 的 Ring0 有
/// <c>ReadMemory</c> 這個方法、<c>IsOpen</c> 也為真、MSR 與 PCI 設定空間都讀得到，
/// 但對 <c>0x0</c>／<c>0x100000</c>／<c>0xFED00000</c>／<c>0xFEE00000</c> 的實體記憶體讀取
/// 一律回 false。也就是說「方法存在」只證明橋接版本對得上，不證明這台機器讀得到 MMIO。
/// 從前裁決只看方法存在，結果主力永遠被選中、備援永遠沒機會上場，各頁拿到的是
/// 「WinRing0 實體記憶體」這個後端名加上一片失敗——比誠實的「兩者都不可用」更難查。
/// 所以現在選主力之前先做一次唯讀探測；探測失敗就當主力不可用，把實際錯誤併進原因字串。
/// </para>
/// <para>
/// 備援不探測：XsRegProbe 的 <see cref="IMmioReader.Available"/> 已經代表
/// 「裝置開得起來＋QUERY_INFO 握手成功」，而它的白名單只收 SPIBAR／ECAM 那幾個真實位址，
/// 拿 0x100000 去探會把一個健康的驅動誤判成不可用。
/// </para>
/// </remarks>
public static class MmioBackendSelector
{
    /// <summary>探測位址：低位 1 MB 內兩頁常駐記憶體（只讀，不寫）。</summary>
    private static readonly ulong[] ProbeAddresses = { 0x0, 0x100000 };

    /// <summary>依主力→備援順序裁決；工廠可注入供測試。回傳件由呼叫方 Dispose（非 IDisposable 實作者免）。</summary>
    /// <param name="probe">主力探測（預設讀 16 位元組實體記憶體）；測試可注入，避免真的碰驅動。</param>
    public static IMmioReader Select(Func<IMmioReader>? primaryFactory = null,
                                     Func<IMmioReader>? fallbackFactory = null,
                                     Func<IMmioReader, bool>? probe = null)
    {
        var primary = (primaryFactory ?? (() => new WinRing0MmioReader()))();
        if (primary.Available && (probe ?? ProbeReadable)(primary)) return primary;

        // 「Available 但探測失敗」要跟「根本不可用」分開講：前者是驅動版本對得上卻不給讀，
        // 後者是驅動沒開起來。兩種原因都不是使用者能靠重開機解決的，但查起來差很多。
        var primaryReason = primary.Available
            ? "宣稱可讀，但實測位址 0x0／0x100000 全部讀取失敗"
              + (string.IsNullOrEmpty(primary.LastFailReason) ? "" : $"（{primary.LastFailReason}）")
            : primary.UnavailableReason ?? "原因不明";

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

    /// <summary>唯讀探測：任一位址讀回完整 16 位元組即視為可讀。</summary>
    private static bool ProbeReadable(IMmioReader reader)
    {
        foreach (var address in ProbeAddresses)
            if (reader.ReadBlock(address, 16) is { Length: 16 }) return true;
        return false;
    }
}
