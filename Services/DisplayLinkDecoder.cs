namespace XinSpect;

/// <summary>顯示鏈路目前使用的色彩編碼（對應 Windows 的 DISPLAYCONFIG_COLOR_ENCODING）。</summary>
public enum DisplayColorEncoding
{
    Rgb = 0,
    YCbCr444 = 1,
    YCbCr422 = 2,
    YCbCr420 = 3,
    Intensity = 4,
    /// <summary>查不到（舊驅動不支援該查詢）——不猜成 RGB。</summary>
    Unknown = 255,
}

/// <summary>
/// 顯示鏈路真相的解讀（純函式，不碰硬體）。
/// </summary>
/// <remarks>
/// 這裡回答的是「這個模式需要多少頻寬，以及畫面有沒有為了塞進鏈路而被犧牲」。
/// <para>
/// <b>刻意不印「協商到的鏈路速率×通道數」</b>：Windows 沒有提供這項資料，要靠各家廠商的私有 API，
/// 而那在非 NVIDIA 的機器上就沒有。與其印一個猜的數字，不如把「目前需要多少」與
/// 「色彩有沒有被壓」講清楚——後者本來就是使用者真正看得出差別的東西。
/// </para>
/// </remarks>
public static class DisplayLinkDecoder
{
    /// <summary>
    /// 每像素位元數。RGB 與 YCbCr 4:4:4 每像素三個取樣，4:2:2 兩個，4:2:0 一點五個；
    /// 編碼或位元深度讀不到就回 null，不套一個預設值。
    /// </summary>
    public static double? BitsPerPixel(DisplayColorEncoding encoding, int bitsPerChannel)
    {
        if (bitsPerChannel <= 0) return null;
        return encoding switch
        {
            DisplayColorEncoding.Rgb or DisplayColorEncoding.YCbCr444 => 3.0 * bitsPerChannel,
            DisplayColorEncoding.YCbCr422 => 2.0 * bitsPerChannel,
            DisplayColorEncoding.YCbCr420 => 1.5 * bitsPerChannel,
            DisplayColorEncoding.Intensity => 1.0 * bitsPerChannel,
            _ => null,
        };
    }

    /// <summary>
    /// 影像資料率（Gb/s）＝實際像素時鐘 × 每像素位元。
    /// </summary>
    /// <remarks>
    /// 這是<b>影像本身</b>的資料率，不是線路上的位元率：DisplayPort 1.x 的 8b/10b 編碼要再多兩成，
    /// HDMI 的 TMDS 亦有各自的開銷。頁面上會把這一點寫出來，不把兩者混為一談。
    /// </remarks>
    public static double? VideoGbps(ulong pixelClockHz, DisplayColorEncoding encoding, int bitsPerChannel)
    {
        if (pixelClockHz == 0) return null;
        if (BitsPerPixel(encoding, bitsPerChannel) is not { } bpp) return null;
        return pixelClockHz * bpp / 1e9;
    }

    /// <summary>色彩編碼是否被降低了色度取樣（4:2:2／4:2:0）。</summary>
    public static bool IsChromaReduced(DisplayColorEncoding e)
        => e is DisplayColorEncoding.YCbCr422 or DisplayColorEncoding.YCbCr420;

    /// <summary>
    /// 判決：畫面現在有沒有為了塞進鏈路而被犧牲。
    /// </summary>
    public static (string Text, Severity Severity) Judge(
        DisplayColorEncoding encoding, int bitsPerChannel, bool hdrEnabled)
    {
        if (encoding is DisplayColorEncoding.Unknown || bitsPerChannel <= 0)
            return ("讀不到目前的色彩編碼或位元深度（驅動未提供這項查詢），因此不對鏈路下判斷。", Severity.Neutral);

        if (encoding is DisplayColorEncoding.YCbCr420)
            return ($"色度被降到 YCbCr 4:2:0（每四個像素只有一組色度）：這是鏈路頻寬不足時最後的手段，"
                  + "文字邊緣與細紅字最容易看出發糊。降解析度、降刷新率、換一條合格的線或改用 DisplayPort 都可能讓它回到 4:4:4。",
                    Severity.Serious);

        if (encoding is DisplayColorEncoding.YCbCr422)
            return ($"色度被降到 YCbCr 4:2:2（水平方向兩個像素共用一組色度）：多半是這個解析度與刷新率"
                  + "在目前的鏈路上塞不下 RGB，驅動自動退讓的結果。亮度沒損失，但彩色細節有。",
                    Severity.Warning);

        if (hdrEnabled && bitsPerChannel < 10)
            return ($"HDR 已啟用，但鏈路只跑在每通道 {bitsPerChannel} 位元：HDR 的漸層需要 10 位元才不容易出現色帶。"
                  + "這通常也是頻寬不足時的退讓。", Severity.Warning);

        return ($"色彩沒有被壓：{EncodingText(encoding)}、每通道 {bitsPerChannel} 位元，"
              + "色度取樣完整。", Severity.Good);
    }

    // ── 文字 ──────────────────────────────────────────────────────────────

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY → 看得懂的名字；沒收錄的代號如實印出數字。</summary>
    public static string OutputTechText(uint tech) => tech switch
    {
        0 => "VGA（D-Sub）",
        1 => "S-Video",
        2 => "複合視訊",
        3 => "色差端子",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort（外接）",
        11 => "DisplayPort（內嵌）",
        12 => "UDI（外接）",
        13 => "UDI（內嵌）",
        14 => "SDTV 分配器",
        15 => "Miracast",
        16 => "間接有線",
        17 => "間接無線",
        0x80000000 => "內建面板",
        _ => $"未收錄的輸出型態（代號 {tech}）",
    };

    public static string EncodingText(DisplayColorEncoding e) => e switch
    {
        DisplayColorEncoding.Rgb => "RGB（4:4:4）",
        DisplayColorEncoding.YCbCr444 => "YCbCr 4:4:4",
        DisplayColorEncoding.YCbCr422 => "YCbCr 4:2:2",
        DisplayColorEncoding.YCbCr420 => "YCbCr 4:2:0",
        DisplayColorEncoding.Intensity => "僅亮度（Intensity）",
        _ => "讀不到",
    };

    /// <summary>刷新率＝分子／分母。刻意保留兩位小數：144 Hz 實際上常是 143.98，四捨五入會掩蓋差異。</summary>
    public static string RefreshText(uint numerator, uint denominator)
        => denominator == 0 ? "—" : $"{(double)numerator / denominator:0.00} Hz";

    /// <summary>資料率文字。</summary>
    public static string GbpsText(double? gbps)
        => gbps is { } g ? $"{g:0.00} Gb/s" : "算不出來（缺像素時鐘或色彩資訊）";
}
