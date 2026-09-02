using System.Globalization;
using System.Windows;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// USB 鏈路真相（<see cref="UsbLinkDecoder"/>）的純解讀檢查。
///
/// USB 堆疊自己就分得清三件事：這個埠支援什麼協定、裝置有什麼能力、現在真的跑在什麼速度。
/// 集線器的 _V2 查詢一次把三者都回來了。這裡驗的是它們有沒有被如實擺出來——
/// 能力高於現況要點出來，並且分清楚該換埠還是換線；集線器沒回報能力就說沒回報，
/// 不能拿「現況」去冒充「相符」。
/// </summary>
public class UsbLinkTests
{
    /// <summary>V2 旗標：bit0 目前 SuperSpeed、bit1 能力 SuperSpeed、bit2 目前 SuperSpeedPlus、bit3 能力 SuperSpeedPlus。</summary>
    private const uint OpSs = 1, CapSs = 2, OpSsp = 4, CapSsp = 8;

    /// <summary>埠支援的協定：bit0 USB 1.1、bit1 USB 2.0、bit2 USB 3.0。</summary>
    private const uint Port11 = 1, Port20 = 2, Port30 = 4;

    // ── 速度名稱 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "低速")]
    [InlineData(1, "全速")]
    [InlineData(2, "高速")]
    [InlineData(3, "超高速")]
    public void 速度代碼對應名稱(byte code, string expected)
        => Assert.Contains(expected, UsbLinkDecoder.SpeedName(code));

    [Fact]
    public void 未知的速度代碼不硬掰成某一級()
    {
        Assert.Contains("代碼", UsbLinkDecoder.SpeedName(7));
        Assert.DoesNotContain("超高速", UsbLinkDecoder.SpeedName(7));
    }

    // ── 目前模式 ────────────────────────────────────────────────────────────

    [Fact]
    public void 目前模式_五與十Gb要靠V2旗標才分得出來()
    {
        Assert.Contains("5 Gb/s", UsbLinkDecoder.OperatingText(3, OpSs | CapSs, true));
        Assert.Contains("10 Gb/s", UsbLinkDecoder.OperatingText(3, OpSs | CapSs | OpSsp | CapSsp, true));
    }

    [Fact]
    public void 目前模式_沒有V2時只說超高速不猜是五還是十()
    {
        string t = UsbLinkDecoder.OperatingText(3, 0, false);
        Assert.Contains("超高速", t);
        Assert.DoesNotContain("10 Gb/s", t);
    }

    [Fact]
    public void 目前模式_高速以下不需要V2就講得清楚()
    {
        Assert.Contains("480 Mb/s", UsbLinkDecoder.OperatingText(2, 0, false));
        Assert.Contains("12 Mb/s", UsbLinkDecoder.OperatingText(1, 0, false));
        Assert.Contains("1.5 Mb/s", UsbLinkDecoder.OperatingText(0, 0, false));
    }

    // ── 裝置能力與埠能力 ────────────────────────────────────────────────────

    [Fact]
    public void 裝置能力_旗標說支援十Gb就寫十Gb()
    {
        Assert.Contains("10 Gb/s", UsbLinkDecoder.CapableText(CapSs | CapSsp, true));
        Assert.Contains("5 Gb/s", UsbLinkDecoder.CapableText(CapSs, true));
    }

    [Fact]
    public void 裝置能力_沒有旗標時只能說是二點零裝置()
    {
        string t = UsbLinkDecoder.CapableText(0, true);
        Assert.Contains("480 Mb/s", t);
        Assert.DoesNotContain("Gb/s", t);
    }

    [Fact]
    public void 裝置能力_集線器沒回報時就說沒回報()
    {
        string t = UsbLinkDecoder.CapableText(0, false);
        Assert.Contains("沒有", t);
        Assert.DoesNotContain("480", t);
    }

    [Fact]
    public void 埠能力_逐位元列出支援的協定()
    {
        Assert.Equal("USB 1.1／2.0／3.0", UsbLinkDecoder.PortText(Port11 | Port20 | Port30));
        Assert.Equal("USB 1.1／2.0", UsbLinkDecoder.PortText(Port11 | Port20));
        Assert.Equal("—", UsbLinkDecoder.PortText(0));
    }

    // ── 判讀 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 判讀_埠支援三點x卻掉到二點零時指向線材()
    {
        // 裝置支援 SuperSpeed、埠也支援 USB 3.0，偏偏沒跑起來——剩下的變因就是線
        var (text, severity) = UsbLinkDecoder.Judge(2, CapSs, Port11 | Port20 | Port30, true);
        Assert.Equal(2, severity);
        Assert.StartsWith("⚠", text);
        Assert.Contains("線", text);
        Assert.Contains("480 Mb/s", text);
    }

    [Fact]
    public void 判讀_埠只有二點零時指向換埠而不是怪線材()
    {
        var (text, severity) = UsbLinkDecoder.Judge(2, CapSs, Port11 | Port20, true);
        Assert.Equal(2, severity);
        Assert.StartsWith("⚠", text);
        Assert.Contains("埠", text);
        Assert.DoesNotContain("線材", text);
    }

    [Fact]
    public void 判讀_支援十Gb只跑五Gb是差一級不是故障()
    {
        var (text, severity) = UsbLinkDecoder.Judge(3, OpSs | CapSs | CapSsp, Port30, true);
        Assert.Equal(1, severity);
        Assert.DoesNotContain("⚠", text);
        Assert.Contains("10 Gb/s", text);
        Assert.Contains("不是故障", text);
    }

    [Fact]
    public void 判讀_掉到二點零比只差一級嚴重()
    {
        // 兩件事同時成立時，先講那個 10 倍的落差
        var (text, severity) = UsbLinkDecoder.Judge(2, CapSs | CapSsp, Port30, true);
        Assert.Equal(2, severity);
        Assert.Contains("480 Mb/s", text);
    }

    [Fact]
    public void 判讀_沒有能力資訊時不下任何結論()
    {
        var (text, severity) = UsbLinkDecoder.Judge(2, 0, Port20, false);
        Assert.Equal(0, severity);
        Assert.DoesNotContain("⚠", text);
        Assert.Contains("沒有", text);
        Assert.DoesNotContain("已達", text);
    }

    [Fact]
    public void 判讀_相符時說已達裝置能力並附上目前速度()
    {
        var (text, severity) = UsbLinkDecoder.Judge(3, OpSs | CapSs | OpSsp | CapSsp, Port30, true);
        Assert.Equal(0, severity);
        Assert.Contains("已達裝置能力", text);
        Assert.Contains("10 Gb/s", text);
    }

    [Fact]
    public void 判讀_二點零裝置接在二點零埠上就是相符()
    {
        var (text, severity) = UsbLinkDecoder.Judge(2, 0, Port11 | Port20, true);
        Assert.Equal(0, severity);
        Assert.Contains("已達裝置能力", text);
    }

    // ── 版本與類別 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x0110, "USB 1.1")]
    [InlineData(0x0200, "USB 2.0")]
    [InlineData(0x0210, "USB 2.1")]
    [InlineData(0x0320, "USB 3.2")]
    public void USB版本_BCD照實譯(int bcd, string expected)
        => Assert.Equal(expected, UsbLinkDecoder.UsbVersionText((ushort)bcd));

    [Fact]
    public void USB版本_沒讀到就給破折號()
        => Assert.Equal("—", UsbLinkDecoder.UsbVersionText(0));

    [Fact]
    public void 裝置類別_認得的照譯不認得的照實說是代碼()
    {
        Assert.Contains("大量儲存", UsbLinkDecoder.ClassName(0x08));
        Assert.Contains("集線器", UsbLinkDecoder.ClassName(0x09));
        Assert.Contains("介面", UsbLinkDecoder.ClassName(0x00));   // 複合裝置由介面各自宣告
        Assert.Contains("0x5A", UsbLinkDecoder.ClassName(0x5A));
    }

    // ── 供電 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 供電_二點零裝置的最大電流以兩毫安培為單位()
    {
        // bMaxPower=250 → 500 mA，USB 2.0 埠的上限
        Assert.Contains("500 mA", UsbLinkDecoder.PowerText(250, 0x80, false));
    }

    [Fact]
    public void 供電_超高速裝置的最大電流以八毫安培為單位()
    {
        // 同一個 bMaxPower 在 SuperSpeed 下是四倍——換錯單位就會少報 4 倍
        Assert.Contains("896 mA", UsbLinkDecoder.PowerText(112, 0x80, true));
        Assert.Contains("224 mA", UsbLinkDecoder.PowerText(112, 0x80, false));
    }

    [Fact]
    public void 供電_自供電與匯流排供電要分清楚()
    {
        Assert.Contains("匯流排供電", UsbLinkDecoder.PowerText(50, 0x80, false));
        Assert.Contains("自供電", UsbLinkDecoder.PowerText(50, 0xC0, false));
    }

    [Fact]
    public void 供電_讀不到設定描述元時給破折號()
        => Assert.Equal("—", UsbLinkDecoder.PowerText(-1, 0, false));

    // ── 位元組解讀 ──────────────────────────────────────────────────────────

    /// <summary>造一份 USB_NODE_CONNECTION_INFORMATION_EX：裝置描述元固定從第 4 個位元組起。</summary>
    private static byte[] ConnBuf(ushort vid = 0x046D, ushort pid = 0xC52B, byte cls = 0x08,
                                  byte speed = 2, bool isHub = false, ushort bcdUsb = 0x0200,
                                  byte bLength = 18, byte bType = 1)
    {
        var b = new byte[36];
        b[0] = 3;                                                      // ConnectionIndex（埠 3）
        b[4] = bLength; b[5] = bType;
        b[6] = (byte)(bcdUsb & 0xFF); b[7] = (byte)(bcdUsb >> 8);
        b[8] = cls;
        b[12] = (byte)(vid & 0xFF); b[13] = (byte)(vid >> 8);
        b[14] = (byte)(pid & 0xFF); b[15] = (byte)(pid >> 8);
        b[18] = 1; b[19] = 2; b[20] = 3;                               // iManufacturer／iProduct／iSerialNumber
        b[22] = 1;                                                     // CurrentConfigurationValue
        b[23] = speed;
        b[24] = (byte)(isHub ? 1 : 0);
        return b;
    }

    [Fact]
    public void 連線資訊_取出廠商裝置代碼與速度與是否為集線器()
    {
        var info = UsbLinkDecoder.DecodeConnectionInfo(ConnBuf(speed: 3, isHub: true, bcdUsb: 0x0320));
        Assert.NotNull(info);
        Assert.Equal(0x046D, info!.Value.Vid);
        Assert.Equal(0xC52B, info.Value.Pid);
        Assert.Equal(0x08, info.Value.DeviceClass);
        Assert.Equal(0x0320, info.Value.BcdUsb);
        Assert.Equal(3, info.Value.Speed);
        Assert.True(info.Value.IsHub);
        Assert.Equal(2, info.Value.ProductIndex);
        Assert.Equal(1, info.Value.ConfigValue);
    }

    [Fact]
    public void 連線資訊_空埠的描述元是全零就回null()
    {
        // 沒接東西（或沒能列舉成功）時描述元是零——不能當成一個 VID 0000 的裝置報出來
        Assert.Null(UsbLinkDecoder.DecodeConnectionInfo(new byte[36]));
    }

    [Fact]
    public void 連線資訊_描述元長度或型別不對就回null()
    {
        Assert.Null(UsbLinkDecoder.DecodeConnectionInfo(ConnBuf(bLength: 17)));
        Assert.Null(UsbLinkDecoder.DecodeConnectionInfo(ConnBuf(bType: 2)));
    }

    [Fact]
    public void 連線資訊_緩衝區太短回null而不是讀過界()
        => Assert.Null(UsbLinkDecoder.DecodeConnectionInfo(ConnBuf()[..20]));

    [Fact]
    public void 設定描述元_取出最大電流與供電屬性()
    {
        byte[] cfg = [9, 2, 0x20, 0, 1, 1, 0, 0xC0, 50];
        var d = UsbLinkDecoder.DecodeConfigDescriptor(cfg);
        Assert.NotNull(d);
        Assert.Equal(50, d!.Value.MaxPowerRaw);
        Assert.Equal(0xC0, d.Value.Attributes);
    }

    [Fact]
    public void 設定描述元_型別或長度不對就回null()
    {
        Assert.Null(UsbLinkDecoder.DecodeConfigDescriptor([9, 3, 0x20, 0, 1, 1, 0, 0xC0, 50]));
        Assert.Null(UsbLinkDecoder.DecodeConfigDescriptor([9, 2, 0x20, 0]));
    }

    /// <summary>造一份字串描述元：長度、型別 3，之後是 UTF-16LE。</summary>
    private static byte[] StrBuf(string s, byte type = 3)
    {
        byte[] chars = System.Text.Encoding.Unicode.GetBytes(s);
        var b = new byte[2 + chars.Length];
        b[0] = (byte)b.Length; b[1] = type;
        chars.CopyTo(b, 2);
        return b;
    }

    [Fact]
    public void 字串描述元_UTF16解出來並去掉頭尾空白()
        => Assert.Equal("USB Receiver", UsbLinkDecoder.DecodeStringDescriptor(StrBuf("  USB Receiver ")));

    [Fact]
    public void 字串描述元_只取描述元自己宣告的長度()
    {
        // 緩衝區比內容長（驅動不會清乾淨後面的位元組），照 bLength 切才不會拖出垃圾
        byte[] b = new byte[64];
        StrBuf("Logi").CopyTo(b, 0);
        b[40] = 0x41;
        Assert.Equal("Logi", UsbLinkDecoder.DecodeStringDescriptor(b));
    }

    [Fact]
    public void 字串描述元_型別不對或內容是空的就回null()
    {
        Assert.Null(UsbLinkDecoder.DecodeStringDescriptor(StrBuf("Logi", type: 2)));
        Assert.Null(UsbLinkDecoder.DecodeStringDescriptor(StrBuf("   ")));
        Assert.Null(UsbLinkDecoder.DecodeStringDescriptor([2, 3]));
        Assert.Null(UsbLinkDecoder.DecodeStringDescriptor([]));
    }

    /// <summary>造一份 USB_NODE_INFORMATION：NodeType 之後接集線器描述元。</summary>
    private static byte[] NodeBuf(byte ports, byte nodeType = 0, byte descType = 0x29)
    {
        var b = new byte[76];
        b[0] = nodeType;
        b[4] = 9; b[5] = descType; b[6] = ports;
        return b;
    }

    [Fact]
    public void 埠數_從集線器描述元取出二點零與三點x都認()
    {
        Assert.Equal(14, UsbLinkDecoder.PortCount(NodeBuf(14)));
        Assert.Equal(4, UsbLinkDecoder.PortCount(NodeBuf(4, descType: 0x2A)));
    }

    [Fact]
    public void 埠數_不是集線器節點或型別不對就回零()
    {
        Assert.Equal(0, UsbLinkDecoder.PortCount(NodeBuf(14, nodeType: 1)));   // 複合裝置的父節點
        Assert.Equal(0, UsbLinkDecoder.PortCount(NodeBuf(14, descType: 0x11)));
        Assert.Equal(0, UsbLinkDecoder.PortCount(NodeBuf(0)));
        Assert.Equal(0, UsbLinkDecoder.PortCount([0, 0, 0, 0]));
    }

    // ── 整體結論 ────────────────────────────────────────────────────────────

    private static UsbPortRow Row(uint flags, uint protocols, byte speed = 3, bool hasV2 = true)
    {
        var (verdict, severity) = UsbLinkDecoder.Judge(speed, flags, protocols, hasV2);
        return new UsbPortRow("控制器 1／埠 1", 0, "測試裝置", "VID_1234 PID_5678", "USB 3.2", "大量儲存",
                              UsbLinkDecoder.OperatingText(speed, flags, hasV2),
                              UsbLinkDecoder.CapableText(flags, hasV2),
                              UsbLinkDecoder.PortText(protocols),
                              UsbLinkDecoder.PowerText(250, 0x80, speed == 3),
                              verdict, severity);
    }

    [Fact]
    public void 結論_沒讀到裝置時說沒讀到而不是說一切正常()
    {
        string text = UsbLinkDecoder.Summarize([]);
        Assert.Contains("沒有讀到", text);
        Assert.Contains("不代表", text);   // 明說「不代表這台機器沒有 USB 裝置」
    }

    [Fact]
    public void 結論_有掉速的優先點出來並給出數量()
    {
        string text = UsbLinkDecoder.Summarize([
            Row(CapSs, Port30, speed: 2),                          // 掉到 2.0
            Row(OpSs | CapSs | CapSsp, Port30),                     // 只差一級
            Row(OpSs | CapSs | OpSsp | CapSsp, Port30),             // 相符
        ]);
        Assert.Contains("1 個", text);
        Assert.Contains("共 3 個", text);
        Assert.Contains("480 Mb/s", text);
    }

    [Fact]
    public void 結論_只差一級時明說是埠或線材只到五Gb()
    {
        string text = UsbLinkDecoder.Summarize([
            Row(OpSs | CapSs | CapSsp, Port30),
            Row(OpSs | CapSs | OpSsp | CapSsp, Port30),
        ]);
        Assert.Contains("10 Gb/s", text);
        Assert.DoesNotContain("⚠", text);
    }

    [Fact]
    public void 結論_全部相符時才說全部相符()
    {
        string text = UsbLinkDecoder.Summarize([
            Row(OpSs | CapSs | OpSsp | CapSsp, Port30),
            Row(0, Port20, speed: 2),
        ]);
        Assert.Contains("已達裝置能力", text);
        Assert.Contains("共 2 個", text);
    }

    // ── 版面用的兩件事 ──────────────────────────────────────────────────────
    // 縮排轉換器只有這一頁在用（USB 是唯一有「裝置掛在裝置底下」的拓樸），所以測試跟著擺在這裡。

    [Fact]
    public void 列細節_一行交代完位置版本類別代碼與供電()
    {
        // 版面第二行是一整串細節；併字串的事交給資料列自己做，XAML 才不必寫 MultiBinding
        string detail = Row(OpSs | CapSs, Port30).DetailText;
        Assert.Contains("控制器 1／埠 1", detail);
        Assert.Contains("USB 3.2", detail);
        Assert.Contains("大量儲存", detail);
        Assert.Contains("VID_1234", detail);
        Assert.Contains("mA", detail);
    }

    private static double IndentOf(object? depth)
        => ((Thickness)new DepthToIndentConverter()
            .Convert(depth, typeof(Thickness), null, CultureInfo.InvariantCulture)).Left;

    [Fact]
    public void 縮排_每多一層外接集線器就往右一格()
    {
        Assert.Equal(0, IndentOf(0));
        Assert.True(IndentOf(1) > 0);
        Assert.Equal(IndentOf(1) * 2, IndentOf(2));
    }

    [Fact]
    public void 縮排_深度異常時不縮排也不把名稱推出畫面()
    {
        // 繫結還沒求值（null）或資料異常（負數）都不該讓版面歪掉
        Assert.Equal(0, IndentOf(null));
        Assert.Equal(0, IndentOf(-3));

        // USB 規格最多 7 層；再深也封頂，免得一棵壞掉的集線器樹把裝置名稱推出畫面
        Assert.Equal(IndentOf(6), IndentOf(99));
    }
}
