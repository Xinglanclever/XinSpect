namespace XinSpect;

/// <summary>一台顯示器的連接介面（WmiMonitorConnectionParams 原樣）。VideoOutputTechnology 是 WMI 的帶位編碼。</summary>
public sealed record MonitorConnection(string InstanceName, int VideoOutputTechnology);

/// <summary>
/// 螢幕連接介面碼（VideoOutputTechnology）的純解碼器：只收錄有把握子集（VGA/DVI/HDMI/DP/
/// 無線等），未收錄如實標。WMI 的編碼帶 0x80000000 旗標位——比較時先剝旗標。
/// </summary>
public static class MonitorConnectionDecoder
{
    /// <summary>連接介面碼 → 繁中名稱。未收錄如實標。</summary>
    [SpecRef("Microsoft WMI WmiMonitorConnectionParams：VideoOutputTechnology（D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY 帶 0x80000000 旗標位）——HDMI 0x80000006、DP 外接 0x8000000A、DP 內嵌 0x8000000B、DVI 0x80000005、VGA 0x80000001、Miracast 0x8000000D")]
    public static string DescribeVideoOutput(int videoOutputTechnology)
    {
        // 0x80000000 旗標位是「這是顯示目標的輸出技術」的標記，剝掉才比對
        uint code = unchecked((uint)videoOutputTechnology) & 0x7FFF_FFFF;
        return code switch
        {
            0x01 => "VGA（HD-15）",
            0x02 => "S-Video",
            0x03 => "Composite Video",
            0x04 => "Component Video",
            0x05 => "DVI",
            0x06 => "HDMI",
            0x07 => "LVDS（內嵌面板）",
            0x0A => "DisplayPort（外接）",
            0x0B => "DisplayPort（內嵌）",
            0x0D => "Miracast（無線）",
            0x0E => "間接有線（間接顯示驅動）",
            0x0F => "USB 顯示",
            _ => $"VideoOutputTechnology 0x{unchecked((uint)videoOutputTechnology):X}（未收錄）",
        };
    }
}
