using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

public sealed class AcpiHestParsingTests
{
    [Fact]
    public void ParseHest_校验MCE三種變長Bank並正確切開後續未知擴展()
    {
        byte[] table = BuildHest(2,
            Source(0, 1), Source(1, 1), Source(11, 1), Extension(12, 5), Extension(13, 7));

        HestParseResult parsed = AcpiTable.ParseHest(table);

        Assert.True(parsed.IsValid);
        Assert.True(parsed.ChecksumValid);
        Assert.Equal(5u, parsed.DeclaredSourceCount);
        Assert.Equal(new[] { 68, 76, 76, 5, 7 }, parsed.Sources.Select(s => s.Length));
        Assert.Equal(new ushort[] { 0, 1, 11, 12, 13 }, parsed.Sources.Select(s => s.Type));
        Assert.Equal(new[] { 40, 108, 184, 260, 265 }, parsed.Sources.Select(s => s.Offset));
        Assert.Equal(new[] { true, true, true, false, false }, parsed.Sources.Select(s => s.IsKnownType));
        Assert.Equal(new ushort?[] { 100, 101, 111, null, null }, parsed.Sources.Select(s => s.SourceId));
        Assert.Equal(string.Empty, parsed.Issue);
    }

    [Fact]
    public void ParseHest_固定來源型別長度與SourceId欄位正確()
    {
        byte[] table = BuildHest(2, Source(2), Source(6), Source(7), Source(8), Source(9), Source(10));

        HestParseResult parsed = AcpiTable.ParseHest(table);

        Assert.True(parsed.IsValid);
        Assert.Equal(new ushort[] { 2, 6, 7, 8, 9, 10 }, parsed.Sources.Select(s => s.Type));
        Assert.Equal(new[] { 20, 48, 44, 56, 64, 92 }, parsed.Sources.Select(s => s.Length));
        Assert.Equal(new ushort?[] { 102, 106, 107, 108, 109, 110 }, parsed.Sources.Select(s => s.SourceId));
        Assert.Equal(parsed.Sources[^1].Offset + parsed.Sources[^1].Length, table.Length);
    }

    [Fact]
    public void ParseHest_拒絕壞Checksum且不保留看似可信的來源()
    {
        byte[] table = BuildHest(2, Source(6));
        table[^1] ^= 0x80;

        HestParseResult parsed = AcpiTable.ParseHest(table);

        Assert.False(parsed.IsValid);
        Assert.False(parsed.ChecksumValid);
        Assert.Equal("ChecksumMismatch", parsed.Issue);
        Assert.Empty(parsed.Sources);
    }

    [Fact]
    public void ParseHest_拒絕宣告長度超過緩衝區且CountHelper不讀越界()
    {
        byte[] table = BuildHest(2, Source(6));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4, 4), (uint)(table.Length + 1));

        HestParseResult parsed = AcpiTable.ParseHest(table);

        Assert.False(parsed.IsValid);
        Assert.Equal("DeclaredLengthExceedsBuffer", parsed.Issue);
        Assert.Null(AcpiTable.HestErrorSourceCount(table));
    }

    [Fact]
    public void ParseHest_拒絕來源截斷保留完整前綴並標記Invalid()
    {
        byte[] table = BuildHest(2, Source(2), Source(6));
        Array.Resize(ref table, table.Length - 3);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4, 4), (uint)table.Length);
        FixChecksum(table);

        HestParseResult parsed = AcpiTable.ParseHest(table);

        Assert.False(parsed.IsValid);
        Assert.True(parsed.ChecksumValid);
        Assert.Equal("TruncatedSource", parsed.Issue);
        Assert.Single(parsed.Sources);
        Assert.Equal((ushort)2, parsed.Sources[0].Type);
    }

    [Fact]
    public void ParseHest_拒絕第二版未排序以及未知擴展錯誤長度()
    {
        HestParseResult unordered = AcpiTable.ParseHest(BuildHest(2, Source(6), Source(2)));
        HestParseResult badExtension = AcpiTable.ParseHest(BuildHest(2, Extension(12, 3)));
        HestParseResult extensionInRevision1 = AcpiTable.ParseHest(BuildHest(1, Extension(12, 4)));

        Assert.False(unordered.IsValid);
        Assert.Equal("LegacySourceTypesOutOfOrder", unordered.Issue);
        Assert.False(badExtension.IsValid);
        Assert.Equal("InvalidExtensionLength", badExtension.Issue);
        Assert.False(extensionInRevision1.IsValid);
        Assert.Equal("UnsupportedLegacySourceType", extensionInRevision1.Issue);
    }

    [Fact]
    public void ParseHest_來源數量有資源上限且舊版不套排序規則()
    {
        byte[] hugeCount = BuildHest(2);
        BinaryPrimitives.WriteUInt32LittleEndian(hugeCount.AsSpan(36, 4), uint.MaxValue);
        FixChecksum(hugeCount);
        HestParseResult countExceedsData = AcpiTable.ParseHest(hugeCount);
        byte[][] tooMany = Enumerable.Range(0, 4097).Select(_ => Extension(12, 4)).ToArray();
        HestParseResult limited = AcpiTable.ParseHest(BuildHest(2, tooMany));
        HestParseResult legacy = AcpiTable.ParseHest(BuildHest(1, Source(6), Source(2)));

        Assert.False(countExceedsData.IsValid);
        Assert.Equal("SourceCountExceedsTable", countExceedsData.Issue);
        Assert.False(limited.IsValid);
        Assert.Equal("SourceCountLimitExceeded", limited.Issue);
        Assert.True(legacy.IsValid);
    }

    private static byte[] BuildHest(byte revision, params byte[][] sources)
    {
        int length = 40 + sources.Sum(s => s.Length);
        var table = new byte[length];
        "HEST"u8.CopyTo(table);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4, 4), (uint)length);
        table[8] = revision;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(36, 4), (uint)sources.Length);
        int offset = 40;
        foreach (byte[] source in sources)
        {
            source.CopyTo(table, offset);
            offset += source.Length;
        }
        FixChecksum(table);
        return table;
    }

    private static byte[] Source(ushort type, byte banks = 0)
    {
        int prefixLength = type switch
        {
            0 => 40,
            1 or 11 => 48,
            2 => 20,
            6 => 48,
            7 => 44,
            8 => 56,
            9 => 64,
            10 => 92,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        bool hasBanks = type is 0 or 1 or 11;
        var source = new byte[prefixLength + (hasBanks ? banks * 28 : 0)];
        BinaryPrimitives.WriteUInt16LittleEndian(source, type);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(2, 2), (ushort)(100 + type));
        if (hasBanks) source[type == 0 ? 32 : 44] = banks;
        return source;
    }

    private static byte[] Extension(ushort type, ushort length)
    {
        // Keep a complete header even when deliberately declaring an invalid length < 4.
        var source = new byte[Math.Max(length, (ushort)4)];
        BinaryPrimitives.WriteUInt16LittleEndian(source, type);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(2, 2), length);
        return source;
    }

    private static void FixChecksum(byte[] table)
    {
        table[9] = 0;
        byte sum = 0;
        foreach (byte value in table) sum += value;
        table[9] = unchecked((byte)-sum);
    }
}
