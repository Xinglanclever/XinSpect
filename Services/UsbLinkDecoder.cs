using System.Text;

namespace XinSpect;

/// <summary>USB 埠上的一個裝置（版面一列）。</summary>
/// <param name="Location">拓樸位置，例如「控制器 2／埠 3／埠 1」——後面那層是外接集線器的埠。</param>
/// <param name="Depth">縮排層級：0 是根集線器上的裝置，每經過一層外接集線器加一。</param>
/// <param name="Name">裝置自己回報的字串描述元；問不到就退回 VID／PID。</param>
/// <param name="OperatingText">現在真的跑在什麼速度。</param>
/// <param name="CapableText">裝置自己宣告的能力（由集線器的 _V2 查詢回報）。</param>
/// <param name="PortText">這個埠支援的協定——分辨「埠只有 2.0」與「線材只有 2.0」的關鍵。</param>
public sealed record UsbPortRow(
    string Location, int Depth, string Name, string IdText, string UsbVersion, string ClassText,
    string OperatingText, string CapableText, string PortText, string PowerText,
    string Verdict, int Severity)
{
    /// <summary>版面第二行：把位置、版本、類別、代碼、供電併成一串，XAML 不必寫 MultiBinding。</summary>
    public string DetailText => $"{Location} ・ {UsbVersion} ・ {ClassText} ・ {IdText} ・ {PowerText}";
}

/// <summary>
/// 從 <c>USB_NODE_CONNECTION_INFORMATION_EX</c> 解出來的欄位。
/// <para>
/// 只取位置不受結構對齊影響的那幾個：<c>ConnectionIndex</c>（位移 0）之後緊接著 18 位元組的
/// 裝置描述元（位移 4），再來是三個單位元組欄位（22、23、24）。裝置描述元自己帶
/// <c>bLength</c>／<c>bDescriptorType</c>，可以當作「這個埠上真的有列舉成功的裝置」的檢核。
/// </para>
/// </summary>
public readonly record struct UsbConnectionInfo(
    ushort BcdUsb, ushort Vid, ushort Pid, byte DeviceClass,
    byte ManufacturerIndex, byte ProductIndex, byte SerialIndex,
    byte Speed, bool IsHub, byte ConfigValue);

/// <summary>
/// USB 鏈路真相的解讀（純函式）。
/// <para>
/// USB 堆疊本來就分得清三件事，只是平常沒人給你看：<b>這個埠支援什麼協定</b>、
/// <b>裝置有什麼能力</b>、<b>現在真的跑在什麼速度</b>。集線器的
/// <c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2</c> 一次把三者都回來了——
/// 旗標裡「目前」與「能力」是兩組不同的位元，另外附上該埠支援的協定。
/// </para>
/// <para>
/// 三者擺在一起才有意義：裝置支援 5 Gb/s 卻跑在 480 Mb/s，如果埠支援 3.x 就是線的問題、
/// 埠只有 2.0 就是插錯埠。這裡不猜、不換算、不推測型號，全部照旗標寫。集線器沒回報能力
/// （USB 2.0 的舊集線器不支援 _V2）就說沒回報，不拿「目前」去冒充「相符」。
/// </para>
/// </summary>
public static class UsbLinkDecoder
{
    /// <summary>_V2 旗標：目前／能力各佔一個位元，SuperSpeed 與 SuperSpeedPlus 各一組。</summary>
    private const uint OperatingSs = 1 << 0, CapableSs = 1 << 1, OperatingSsp = 1 << 2, CapableSsp = 1 << 3;

    /// <summary>埠支援的協定（<c>USB_PROTOCOLS</c>）。</summary>
    private const uint Usb110 = 1 << 0, Usb200 = 1 << 1, Usb300 = 1 << 2;

    /// <summary>連線資訊需要的最小位元組數：表頭到 <c>DeviceIsHub</c>。</summary>
    public const int ConnInfoMinBytes = 36;

    /// <summary>速度代碼（<c>USB_DEVICE_SPEED</c>）。不認得的代碼照實說是代碼，不硬掰成某一級。</summary>
    public static string SpeedName(byte code) => code switch
    {
        0 => "低速（1.5 Mb/s）",
        1 => "全速（12 Mb/s）",
        2 => "高速（480 Mb/s）",
        3 => "超高速（5 Gb/s 以上）",
        _ => $"代碼 {code}",
    };

    /// <summary>
    /// 現在真的跑在什麼速度。5 Gb/s 與 10 Gb/s 在 <c>Speed</c> 欄位裡是同一個值（都算超高速），
    /// 要靠 _V2 旗標才分得出來；集線器沒回報就只說「超高速」，不猜是哪一級。
    /// </summary>
    public static string OperatingText(byte speed, uint flags, bool hasV2)
    {
        if (speed != 3) return SpeedName(speed);
        if (!hasV2) return "超高速（5 Gb/s 以上，集線器沒回報是哪一級）";
        return (flags & OperatingSsp) != 0 ? "10 Gb/s（SuperSpeedPlus）" : "5 Gb/s（SuperSpeed）";
    }

    /// <summary>裝置自己宣告的能力。集線器不支援 _V2 時如實說沒回報——這欄不能拿現況填。</summary>
    public static string CapableText(uint flags, bool hasV2)
    {
        if (!hasV2) return "—（集線器沒有回報裝置能力）";
        if ((flags & CapableSsp) != 0) return "10 Gb/s（SuperSpeedPlus）";
        if ((flags & CapableSs) != 0) return "5 Gb/s（SuperSpeed）";
        return "480 Mb/s 以下（不是 USB 3.x 裝置）";
    }

    /// <summary>這個埠支援的協定，逐位元列出。</summary>
    public static string PortText(uint protocols)
    {
        var parts = new List<string>(3);
        if ((protocols & Usb110) != 0) parts.Add("1.1");
        if ((protocols & Usb200) != 0) parts.Add("2.0");
        if ((protocols & Usb300) != 0) parts.Add("3.0");
        return parts.Count == 0 ? "—" : "USB " + string.Join("／", parts);
    }

    /// <summary>
    /// 能力對現況的判讀。掉到 USB 2.0 是 10 倍的落差（嚴重度 2），並且分清楚是埠的關係還是線材的
    /// 關係；支援 10 Gb/s 只跑 5 Gb/s 是差一級（嚴重度 1），多半是埠本身就只到 5 Gb/s，不是故障。
    /// 沒有能力資訊時不下任何結論。
    /// </summary>
    public static (string Text, int Severity) Judge(byte speed, uint flags, uint protocols, bool hasV2)
    {
        if (!hasV2)
            return ($"這個集線器沒有回報裝置能力，只知道目前是 {OperatingText(speed, flags, false)}——無從比對。", 0);

        if ((flags & CapableSs) != 0 && (flags & OperatingSs) == 0)
            return ((protocols & Usb300) != 0
                ? "⚠ 裝置支援 USB 3.x、這個埠也支援，卻只跑在 480 Mb/s——多半是線材（或延長線）裡只有 USB 2.0 的那幾條線。"
                : "⚠ 裝置支援 USB 3.x，但這個埠只有 USB 2.0，所以只能跑 480 Mb/s——換到支援 3.x 的埠才拿得到 5 Gb/s。", 2);

        if ((flags & CapableSsp) != 0 && (flags & OperatingSsp) == 0)
            return ("裝置支援 10 Gb/s（SuperSpeedPlus），目前跑在 5 Gb/s——這個埠或線材只到 5 Gb/s，不是故障。", 1);

        return ($"目前速度已達裝置能力（{OperatingText(speed, flags, true)}）。", 0);
    }

    /// <summary>裝置描述元的 <c>bcdUSB</c>：高位元組是主版本，低位元組的高四位是次版本。</summary>
    public static string UsbVersionText(ushort bcdUsb)
        => bcdUsb == 0 ? "—" : $"USB {bcdUsb >> 8:X}.{(bcdUsb >> 4) & 0xF:X}";

    /// <summary>裝置類別。認得的照譯，不認得的照實說是代碼。</summary>
    public static string ClassName(byte cls) => cls switch
    {
        0x00 => "由介面各自宣告",
        0x01 => "音訊",
        0x02 => "通訊",
        0x03 => "人機介面（HID）",
        0x05 => "實體介面",
        0x06 => "靜態影像",
        0x07 => "印表機",
        0x08 => "大量儲存",
        0x09 => "集線器",
        0x0A => "通訊資料",
        0x0B => "智慧卡",
        0x0D => "內容保護",
        0x0E => "視訊",
        0x0F => "個人健康裝置",
        0x10 => "影音",
        0xDC => "診斷",
        0xE0 => "無線控制器（藍牙等）",
        0xEF => "雜項",
        0xFE => "應用專屬",
        0xFF => "廠商自訂",
        _ => $"類別 0x{cls:X2}",
    };

    /// <summary>
    /// 供電：<c>bMaxPower</c> 的單位隨速度而變——USB 2.0 以 2 mA 為單位，SuperSpeed 以 8 mA 為單位。
    /// 用錯單位會少報四倍，所以單位跟著目前的速度走。讀不到設定描述元就給破折號。
    /// </summary>
    public static string PowerText(int maxPowerRaw, byte attributes, bool superSpeed)
    {
        if (maxPowerRaw < 0) return "—";
        int ma = maxPowerRaw * (superSpeed ? 8 : 2);
        return $"{((attributes & 0x40) != 0 ? "自供電" : "匯流排供電")} ・ 最大 {ma} mA";
    }

    /// <summary>
    /// 解讀連線資訊。裝置描述元自帶長度與型別，兩者不對就當這個埠沒有可用的裝置——
    /// 空埠回的是一片零，不能報成一個 VID 0000 的裝置。
    /// </summary>
    public static UsbConnectionInfo? DecodeConnectionInfo(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < ConnInfoMinBytes) return null;

        var d = buf[4..];                            // 裝置描述元
        if (d[0] != 18 || d[1] != 1) return null;    // bLength／bDescriptorType

        return new UsbConnectionInfo(
            BcdUsb: (ushort)(d[2] | d[3] << 8),
            Vid: (ushort)(d[8] | d[9] << 8),
            Pid: (ushort)(d[10] | d[11] << 8),
            DeviceClass: d[4],
            ManufacturerIndex: d[14],
            ProductIndex: d[15],
            SerialIndex: d[16],
            Speed: buf[23],
            IsHub: buf[24] != 0,
            ConfigValue: buf[22]);
    }

    /// <summary>解讀設定描述元的前 9 個位元組，取出 <c>bmAttributes</c> 與 <c>bMaxPower</c>。</summary>
    public static (int MaxPowerRaw, byte Attributes)? DecodeConfigDescriptor(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 9 || buf[0] != 9 || buf[1] != 2) return null;
        return (buf[8], buf[7]);
    }

    /// <summary>
    /// 解讀字串描述元（UTF-16LE）。只取描述元自己宣告的長度——緩衝區後面的位元組驅動不保證清乾淨，
    /// 照長度切才不會拖出垃圾。空字串當作沒讀到。
    /// </summary>
    public static string? DecodeStringDescriptor(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4 || buf[1] != 3) return null;

        int len = Math.Min(buf[0], buf.Length);
        if (len < 4) return null;

        string s = Encoding.Unicode.GetString(buf[2..(len & ~1)]).Trim('\0', ' ', '\t');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// 從 <c>USB_NODE_INFORMATION</c> 取出埠數：4 位元組的節點型別之後接集線器描述元，
    /// 埠數在位移 6。描述元型別必須是 0x29（集線器）或 0x2A（SuperSpeed 集線器），
    /// 不是集線器節點就回 0——不猜。
    /// </summary>
    public static int PortCount(ReadOnlySpan<byte> nodeInfo)
    {
        if (nodeInfo.Length < 7) return 0;

        uint nodeType = (uint)(nodeInfo[0] | nodeInfo[1] << 8 | nodeInfo[2] << 16 | nodeInfo[3] << 24);
        if (nodeType != 0) return 0;                        // 1 是複合裝置的父節點，沒有集線器描述元
        if (nodeInfo[5] is not (0x29 or 0x2A)) return 0;

        return nodeInfo[6];
    }

    /// <summary>
    /// 一句話交代整體結論。沒讀到就說沒讀到——空清單絕不能講成「一切正常」。
    /// </summary>
    public static string Summarize(IReadOnlyList<UsbPortRow> rows)
    {
        if (rows.Count == 0)
            return "沒有讀到任何 USB 裝置。這不代表這台機器沒有 USB 埠或沒接東西——也可能是集線器不接受查詢。";

        int total = rows.Count, down = rows.Count(r => r.Severity >= 2), oneStep = rows.Count(r => r.Severity == 1);

        if (down > 0)
            return $"{down} 個裝置支援 USB 3.x 卻只跑在 480 Mb/s（共 {total} 個裝置）"
                 + (oneStep > 0 ? $"，另有 {oneStep} 個支援 10 Gb/s 但只跑到 5 Gb/s。" : "。")
                 + "下面逐列寫了是埠的關係還是線材的關係。";

        if (oneStep > 0)
            return $"共 {total} 個裝置，其中 {oneStep} 個支援 10 Gb/s、目前跑在 5 Gb/s"
                 + "——埠或線材只到 5 Gb/s，其餘已達裝置能力。";

        return $"共 {total} 個裝置，目前速度全部已達裝置能力。";
    }
}
