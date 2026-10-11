using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// FIT 解碼器金標（P0-02／FW-001）：合成尾端＋已知答案——簽名搜尋（16 位元組對齊）、
/// 表頭欄位、條目型別名、截斷；Boot Guard 不出值的界線釘在服務層。
/// </summary>
public class FitDecoderTests
{
    /// <summary>合成 FIT 表尾端：entries 區 + 標頭（簽名 16 位元組對齊）。</summary>
    private static byte[] BuildTail(params (ushort Type, ushort Size)[] entries)
    {
        int entriesBytes = entries.Sum(e => e.Size);
        int headerOffset = 0x400 - 0x20 - entriesBytes;
        headerOffset -= headerOffset % 16; // 16 位元組對齊
        byte[] tail = new byte[0x400];

        int cursor = headerOffset + 0x20;
        foreach (var (type, size) in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(cursor, 2), type);
            BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(cursor + 2, 2), size);
            cursor += size;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(headerOffset, 8), 0xFFFF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(headerOffset + 8, 4), (uint)entriesBytes);
        Encoding.ASCII.GetBytes("_FIT_").CopyTo(tail, headerOffset + 0x10);
        return tail;
    }

    [Fact]
    public void 標頭與條目逐欄比對()
    {
        var t = FitDecoder.Parse(BuildTail(((ushort)0x00, (ushort)0x10), ((ushort)0x02, (ushort)0x18), ((ushort)0x07, (ushort)0x20)));
        Assert.NotNull(t);
        Assert.Equal(3, t.Entries.Count);
        Assert.Equal("微碼（Microcode）", t.Entries[0].TypeName);
        Assert.Equal("Co-Security／ACM", t.Entries[1].TypeName);
        Assert.Equal("BIOS Startup Module", t.Entries[2].TypeName);
        Assert.Equal(0x48u, t.TotalSize); // 0x10+0x18+0x20
        Assert.Null(t.TruncatedReason);
    }

    [Fact]
    public void 未收錄型別_如實標未收錄()
    {
        var t = FitDecoder.Parse(BuildTail(((ushort)0x55, (ushort)0x10)));
        Assert.NotNull(t);
        Assert.Equal("未收錄", t.Entries[0].TypeName);
    }

    [Fact]
    public void 沒有簽名_回null()
    {
        Assert.Null(FitDecoder.Parse(new byte[0x400]));
        Assert.Null(FitDecoder.Parse(new byte[0x10]));
    }

    [Fact]
    public void 條目結構不足_停住並帶原因()
    {
        var t = FitDecoder.Parse(BuildTail(((ushort)0x00, (ushort)0x10), ((ushort)0x02, (ushort)0x04)));
        Assert.NotNull(t);
        Assert.Equal(1, t.Entries.Count); // Size 4 < 8 的條目不解——停在前面
        Assert.NotNull(t.TruncatedReason);
    }
}
