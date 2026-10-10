using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Prefetch 檔頭解碼器金標（FS-003 檔名層）：MAM 容器、SCCA 各版本、未收錄版本、截斷——界線「不解壓」機器釘死。
/// </summary>
public class PrefetchHeaderDecoderTests
{
    /// <summary>未壓縮 SCCA 檔頭合成器（版面：version i32+"SCCA"+未知 4+大小 4+檔名 60B UTF-16+hash 4）。</summary>
    private static byte[] BuildScca(int version, string exe, uint hash)
    {
        byte[] b = new byte[80];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0, 4), version);
        Encoding.ASCII.GetBytes("SCCA").CopyTo(b, 4);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12, 4), 123456);
        byte[] name = Encoding.Unicode.GetBytes(exe);
        Array.Resize(ref name, 60);
        name.CopyTo(b, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76, 4), hash);
        return b;
    }

    [Theory]
    [InlineData(17, "v17（XP/2003）")]
    [InlineData(23, "v23（Vista/7）")]
    [InlineData(26, "v26（Win8/2012）")]
    [InlineData(30, "v30（Win10/11）")]
    [InlineData(31, "v31（Win11）")]
    public void Scca_五個收錄版本逐欄比對(int version, string expectedLabel)
    {
        var h = PrefetchHeaderDecoder.Parse(BuildScca(version, "NOTEPAD.EXE", 0x11223344u));
        Assert.Equal("SCCA", h.Kind);
        Assert.Equal(expectedLabel, h.VersionLabel);
        Assert.Equal("NOTEPAD.EXE", h.ExecutableName);
        Assert.Equal("11223344", h.HashHex);
    }

    [Fact]
    public void Scca_檔名截斷到Nul與短檔名()
    {
        var h = PrefetchHeaderDecoder.Parse(BuildScca(30, "A.EXE", 1));
        Assert.Equal("A.EXE", h.ExecutableName);
    }

    [Fact]
    public void Scca_未收錄版本_如實標Unrecognized與版本號()
    {
        var h = PrefetchHeaderDecoder.Parse(BuildScca(42, "X.EXE", 1));
        Assert.Equal("Unrecognized", h.Kind);
        Assert.Contains("42", h.VersionLabel);
        Assert.Null(h.ExecutableName);
    }

    [Fact]
    public void Mam容器_辨識但不解壓_界線寫進Kind與說明()
    {
        byte[] b = new byte[64];
        Encoding.ASCII.GetBytes("MAM").CopyTo(b, 0);
        b[3] = 0x04;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), 0x0000A1B2);
        var h = PrefetchHeaderDecoder.Parse(b);
        Assert.Equal("MAM", h.Kind);
        Assert.Equal("MAM 0x04", h.VersionLabel);
        Assert.Equal(0xA1B2u, h.UncompressedSize);
        Assert.Null(h.ExecutableName); // 未解壓＝沒有檔名，不猜
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x53, 0x43, 0x43, 0x41, 9, 9, 9, 9 })]
    public void 太短或壞簽名_回Unrecognized(byte[] b)
    {
        var h = PrefetchHeaderDecoder.Parse(b);
        Assert.Equal("Unrecognized", h.Kind);
    }
}
