using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// PCIe 錯誤旗標：裝置狀態暫存器與傳統 PCI 狀態暫存器裡的黏滯位。
///
/// 這些位元是「自開機以來偵測到過」的紀錄，不是計數器——所以文字只能說「發生過」，
/// 不能說「發生了幾次」。而且它們是寫 1 才清除的：本程式唯讀，一律不清，
/// 否則就會把別的工具（或使用者自己）正在追的線索抹掉。
/// </summary>
public class PcieErrorFlagTests
{
    [Fact]
    public void 沒有任何旗標時說沒有偵測到錯誤()
    {
        var (text, sev) = PcieLinkDecoder.DecodeErrorFlags(0, 0);
        Assert.Contains("沒有偵測到", text);
        Assert.Equal(0, sev);
    }

    [Fact]
    public void 可修正錯誤是第一級()
    {
        var (text, sev) = PcieLinkDecoder.DecodeErrorFlags(deviceStatus: 0x0001, pciStatus: 0);
        Assert.Contains("可修正", text);
        Assert.Equal(1, sev);
    }

    [Fact]
    public void 不可修正與致命錯誤是第二級()
    {
        var (t1, s1) = PcieLinkDecoder.DecodeErrorFlags(0x0002, 0);   // Non-Fatal
        Assert.Contains("不可修正", t1);
        Assert.Equal(2, s1);

        var (t2, s2) = PcieLinkDecoder.DecodeErrorFlags(0x0004, 0);   // Fatal
        Assert.Contains("致命", t2);
        Assert.Equal(2, s2);
    }

    [Fact]
    public void 不支援的請求要單獨列出而不是混進錯誤()
    {
        var (text, sev) = PcieLinkDecoder.DecodeErrorFlags(0x0008, 0);
        Assert.Contains("不支援的請求", text);
        Assert.Equal(1, sev);
    }

    [Fact]
    public void 傳統PCI狀態的同位元錯誤與系統錯誤也要抓()
    {
        var (text, sev) = PcieLinkDecoder.DecodeErrorFlags(0, pciStatus: 0x8000);   // Detected Parity Error
        Assert.Contains("同位", text);
        Assert.Equal(2, sev);

        var (t2, s2) = PcieLinkDecoder.DecodeErrorFlags(0, 0x4000);                 // Signaled System Error
        Assert.Contains("系統錯誤", t2);
        Assert.Equal(2, s2);
    }

    [Fact]
    public void 多個旗標同時亮時全部列出()
    {
        var (text, _) = PcieLinkDecoder.DecodeErrorFlags(0x0005, 0x8000);
        Assert.Contains("可修正", text);
        Assert.Contains("致命", text);
        Assert.Contains("同位", text);
    }

    [Fact]
    public void 有旗標時要說明這是自開機以來的紀錄且不清除()
    {
        var (text, _) = PcieLinkDecoder.DecodeErrorFlags(0x0001, 0);
        Assert.Contains("自開機", text);
        Assert.Contains("次數", text);      // 必須明說「只知道發生過、不知道幾次」
    }

    [Fact]
    public void 保留位元不得被當成錯誤()
    {
        // bit 4（AUX 電源）與 bit 5（傳輸進行中）不是錯誤，不能列進來
        var (text, sev) = PcieLinkDecoder.DecodeErrorFlags(0x0030, 0);
        Assert.Contains("沒有偵測到", text);
        Assert.Equal(0, sev);
    }
}
