using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Shannon 熵分析的合成樣本驗證：每一案都給定「已知正確答案」，檢查分類是否照著答案走。
/// </summary>
public class EntropyMapTests
{
    // ── 已知答案的合成序列 ────────────────────────────────────────────────

    [Fact]
    public void 全部相同位元組_熵為零_判為抹除或單一值()
    {
        var block = new byte[4096];
        Array.Fill(block, (byte)0xFF);

        var b = EntropyMap.AnalyzeBlock(block);
        Assert.Equal(0, b.ShannonBitsPerByte, 10);
        Assert.True(b.AllSame);
        Assert.True(b.AllErased);
        Assert.True(b.IsErased);
        Assert.True(b.IsLowEntropy);
        Assert.False(b.IsHighEntropy);
        Assert.Equal(1.0, b.ErasedFraction, 10);
    }

    [Fact]
    public void 全零區塊_是低熵但不是抹除()
    {
        var block = new byte[4096];   // 預設全 0

        var b = EntropyMap.AnalyzeBlock(block);
        Assert.Equal(0, b.ShannonBitsPerByte, 10);
        Assert.True(b.AllSame);
        Assert.False(b.AllErased);    // 抹除＝全 0xFF，不是全 0x00
        Assert.False(b.IsErased);
        Assert.Equal(1.0, b.ZeroFraction, 10);
        Assert.Equal(0, b.ErasedFraction, 10);
    }

    [Fact]
    public void 兩個等機率值_熵恰為一位元()
    {
        // 一半 0x00、一半 0x11：H = −(0.5 log₂0.5 + 0.5 log₂0.5) = 1 bit
        var block = new byte[4096];
        for (int i = 0; i < 2048; i++) block[i] = 0x11;

        var b = EntropyMap.AnalyzeBlock(block);
        Assert.Equal(1.0, b.ShannonBitsPerByte, 10);
        Assert.False(b.AllSame);
        Assert.True(b.IsLowEntropy);
    }

    [Fact]
    public void 四個等機率值_熵恰為二位元()
    {
        var block = new byte[4096];
        for (int i = 0; i < 4096; i++) block[i] = (byte)((i % 4) * 0x40);

        var b = EntropyMap.AnalyzeBlock(block);
        Assert.Equal(2.0, b.ShannonBitsPerByte, 10);
    }

    [Fact]
    public void 均勻分布全部256個值_熵恰為八位元()
    {
        var block = new byte[4096];
        for (int i = 0; i < 4096; i++) block[i] = (byte)(i % 256);

        var b = EntropyMap.AnalyzeBlock(block);
        Assert.Equal(8.0, b.ShannonBitsPerByte, 10);
        Assert.True(b.IsHighEntropy);
        Assert.False(b.IsLowEntropy);
        Assert.False(b.AllSame);
    }

    [Fact]
    public void 偽隨機內容_判為高熵()
    {
        // 確定性 xorshift 產生的偽隨機位元組：分布接近均勻，熵應逼近 8
        var block = new byte[4096];
        ulong s = 0x2545F4914F6CDD1D;
        for (int i = 0; i < block.Length; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            block[i] = (byte)(s >> 24);
        }

        var b = EntropyMap.AnalyzeBlock(block);
        Assert.True(b.ShannonBitsPerByte > 7.5, $"偽隨機內容應為高熵，實得 {b.ShannonBitsPerByte:F3}");
        Assert.True(b.IsHighEntropy);
    }

    // ── 切塊與摘要 ───────────────────────────────────────────────────────

    [Fact]
    public void 尾段不足一塊_丟棄而不補零()
    {
        // 2.5 塊的資料：只算 2 塊，尾段不參與——補零會造出人為的低熵
        var data = new byte[EntropyMap.DefaultBlockSize * 2 + 123];

        var blocks = EntropyMap.Analyze(data);
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(EntropyMap.DefaultBlockSize, b.Length));
    }

    [Fact]
    public void 不足一塊_回報空清單而不是半塊的假結論()
    {
        var data = new byte[100];
        Assert.Empty(EntropyMap.Analyze(data));
    }

    [Fact]
    public void 位移如實記錄()
    {
        var data = new byte[EntropyMap.DefaultBlockSize * 3];
        var blocks = EntropyMap.Analyze(data);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(0, blocks[0].Offset);
        Assert.Equal(EntropyMap.DefaultBlockSize, blocks[1].Offset);
        Assert.Equal(EntropyMap.DefaultBlockSize * 2, blocks[2].Offset);
    }

    [Fact]
    public void 摘要如實分類四種區塊()
    {
        int bs = EntropyMap.DefaultBlockSize;
        var data = new byte[bs * 4];

        Array.Fill(data, (byte)0xFF, 0, bs);                    // 塊 0：抹除
        for (int i = 0; i < bs; i++) data[bs + i] = (byte)(i % 256); // 塊 1：高熵
        for (int i = 0; i < bs; i++) data[bs * 2 + i] = (byte)(i % 2 == 0 ? 0x00 : 0x11); // 塊 2：1 bit（低熵）
        for (int i = 0; i < bs; i++) data[bs * 3 + i] = (byte)(i & 0x0F);          // 塊 3：16 值（4 bit，中等）

        var summary = EntropyMap.Summarize(EntropyMap.Analyze(data));
        Assert.Equal(4, summary.Blocks);
        Assert.Equal(1, summary.ErasedBlocks);
        Assert.Equal(1, summary.HighEntropyBlocks);
        Assert.Equal(1, summary.LowEntropyBlocks);
        Assert.Equal(1, summary.MediumEntropyBlocks);
        Assert.Equal(0.25, summary.ErasedFraction, 10);
    }

    [Fact]
    public void 空清單的摘要_全零且不除零()
    {
        var summary = EntropyMap.Summarize([]);
        Assert.Equal(0, summary.Blocks);
        Assert.Equal(0, summary.MeanEntropyBitsPerByte);
        Assert.Equal(0, summary.ErasedFraction);
    }

    [Fact]
    public void 區塊大小可自訂_且不整除時尾段仍被丟棄()
    {
        var data = new byte[1000];
        var blocks = EntropyMap.Analyze(data, blockSize: 256);
        Assert.Equal(3, blocks.Count);       // 1000 / 256 = 3
        Assert.All(blocks, b => Assert.Equal(256, b.Length));
    }

    [Fact]
    public void 非正區塊大小_如實拋出()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EntropyMap.Analyze(new byte[100], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntropyMap.Analyze(new byte[100], -1));
    }
}
