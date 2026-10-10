using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AppCompatCache（Shimcache）解碼器金標（FS-001）：合成值＋已知答案向量逐欄比對，
/// 版面常數（0x34 記錄起點／0x80 表頭／條目欄位序）對準 Zimmerman AppCompatCacheParser 出處。
/// </summary>
public class AppCompatCacheDecoderTests
{
    // ── 合成值產生器（金標 fixtures） ──────────────────────────────────────

    /// <summary>Win10 家族值：值首 4 位元組＝記錄區位移 0x34、申報數在 0x28、記錄區自 0x34 起。</summary>
    private static byte[] BuildWin10(params (string Path, DateTimeOffset? Time, bool Executed)[] entries)
    {
        var records = new List<byte>();
        foreach (var (path, time, executed) in entries)
        {
            byte[] pathBytes = Encoding.Unicode.GetBytes(path);
            byte[] data = new byte[8];
            if (executed) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 1);
            var rec = new List<byte>();
            rec.AddRange(Encoding.ASCII.GetBytes("10ts"));
            rec.AddRange(new byte[4]);                                  // 未知欄位
            rec.AddRange(BitConverter.GetBytes(8));                     // CacheEntryDataSize（占位）
            rec.AddRange(BitConverter.GetBytes((ushort)pathBytes.Length));
            rec.AddRange(pathBytes);
            rec.AddRange(BitConverter.GetBytes((time ?? DateTimeOffset.FromFileTime(0)).ToFileTime()));
            rec.AddRange(BitConverter.GetBytes(data.Length));
            rec.AddRange(data);
            records.AddRange(rec);
        }

        byte[] value = new byte[0x34 + records.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(0, 4), 0x34);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(0x28, 4), entries.Length);
        records.CopyTo(value, 0x34);
        return value;
    }

    /// <summary>Win7 x64 值：128 位元組表頭（申報數在 offset 4）、條目 48 位元組、路徑在值的絕對位移上。</summary>
    private static byte[] BuildWin7(params (string Path, DateTimeOffset Time, bool Executed)[] entries)
    {
        const int entrySize = 48;
        int pathsStart = 128 + entries.Length * entrySize;
        var paths = new List<byte>();

        byte[] value = new byte[pathsStart + entries.Sum(e => Encoding.Unicode.GetBytes(e.Path).Length)];
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4, 4), entries.Length);

        int index = 128;
        foreach (var (path, time, executed) in entries)
        {
            byte[] pathBytes = Encoding.Unicode.GetBytes(path);
            int pathOffset = pathsStart + paths.Count;
            paths.AddRange(pathBytes);

            BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(index, 2), (ushort)pathBytes.Length); // PathSize
            BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(index + 2, 2), (ushort)pathBytes.Length); // MaxPathSize
            // index+4..8 padding 未知（保持零）
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(index + 8, 8), pathOffset);
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(index + 16, 8), time.ToFileTime());
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(index + 24, 4), executed ? 0x2 : 0); // InsertFlags bit1＝Executed
            // +28 shim flags、+32 CacheEntryDataSize、+40 DataOffset 保持零（不影響本解碼器）
            index += entrySize;
        }
        paths.CopyTo(value, pathsStart);
        return value;
    }

    // ── Win10 家族 ────────────────────────────────────────────────────────

    [Fact]
    public void Win10_兩條已知條目逐欄比對()
    {
        var t1 = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.Zero);
        var raw = BuildWin10(
            (@"\??\C:\Windows\System32\notepad.exe", t1, true),
            (@"\??\C:\Program Files\App\tool.exe", t2, false));

        var result = AppCompatCacheDecoder.Parse(raw);

        Assert.NotNull(result);
        Assert.Equal("Win10/Server2016+", result.Format);
        Assert.Equal(2, result.DeclaredCount);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", result.Entries[0].Path);
        Assert.Equal(t1, result.Entries[0].LastModifiedUtc);
        Assert.True(result.Entries[0].Executed);
        Assert.Equal(@"C:\Program Files\App\tool.exe", result.Entries[1].Path);
        Assert.Equal(t2, result.Entries[1].LastModifiedUtc);
        Assert.False(result.Entries[1].Executed);
        Assert.Null(result.TruncatedReason);
    }

    [Fact]
    public void Win10_空路徑與1601時間_如實標時間未知()
    {
        var raw = BuildWin10((@"\??\C:\x.exe", null, false));
        var result = AppCompatCacheDecoder.Parse(raw);
        Assert.NotNull(result);
        Assert.Null(result.Entries[0].LastModifiedUtc);
    }

    [Fact]
    public void Win10_條目截斷_保留已驗證條目並帶原因()
    {
        var raw = BuildWin10((@"\??\C:\a.exe", DateTimeOffset.UtcNow, true));
        byte[] truncated = raw.Take(raw.Length - 3).ToArray();
        var result = AppCompatCacheDecoder.Parse(truncated);
        Assert.NotNull(result);
        Assert.Equal(0, result.Entries.Count);
        Assert.NotNull(result.TruncatedReason);
    }

    [Fact]
    public void Win10_記錄區後有殘餘_如實計數()
    {
        var raw = BuildWin10((@"\??\C:\a.exe", DateTimeOffset.UtcNow, true));
        Array.Resize(ref raw, raw.Length + 7);
        var result = AppCompatCacheDecoder.Parse(raw);
        Assert.NotNull(result);
        Assert.Equal(7, result.UnparsedTailBytes);
        Assert.Contains("未解析", result.TruncatedReason);
    }

    // ── Win7 x64 ──────────────────────────────────────────────────────────

    [Fact]
    public void Win7x64_兩條已知條目逐欄比對()
    {
        var t1 = new DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var raw = BuildWin7(
            (@"\??\C:\Windows\explorer.exe", t1, true),
            (@"\??\D:\tools\cmd.exe", t1, false));

        var result = AppCompatCacheDecoder.Parse(raw);

        Assert.NotNull(result);
        Assert.Equal("Win7x64/2008R2", result.Format);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(@"C:\Windows\explorer.exe", result.Entries[0].Path);
        Assert.Equal(t1, result.Entries[0].LastModifiedUtc);
        Assert.True(result.Entries[0].Executed);
        Assert.Equal(@"D:\tools\cmd.exe", result.Entries[1].Path);
        Assert.False(result.Entries[1].Executed);
    }

    [Fact]
    public void Win7x64_申報零條_如實回空與原因()
    {
        var raw = BuildWin7((@"\??\C:\a.exe", DateTimeOffset.UtcNow, false));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4, 4), 0);
        var result = AppCompatCacheDecoder.Parse(raw);
        Assert.NotNull(result);
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.DeclaredCount);
        Assert.Contains("申報 0 條", result.TruncatedReason);
    }

    // ── Win8／8.1 與未知格式 ─────────────────────────────────────────────

    [Theory]
    [InlineData("00ts", "Win8/Server2012")]
    [InlineData("10ts", "Win8.1/Server2012R2")]
    public void Win8家族_如實回未實作不猜(string sig, string expectedFormat)
    {
        byte[] raw = new byte[128 + 64];
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0, 4), 0x80);
        Encoding.ASCII.GetBytes(sig).CopyTo(raw, 128);
        var result = AppCompatCacheDecoder.Parse(raw);
        Assert.NotNull(result);
        Assert.Equal(expectedFormat, result.Format);
        Assert.Empty(result.Entries);
        Assert.Contains("未對準出處", result.TruncatedReason);
    }

    [Fact]
    public void 格式辨認不出_回null()
    {
        Assert.Null(AppCompatCacheDecoder.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Null(AppCompatCacheDecoder.Parse(null));
    }

    // ── 小工具 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, false)] // 1601-01-01 00:00:00.0000001 → 落在 1601 → 視為「沒有時間」
    public void FileTimeToOffset_全零與1601視為無(long ft, bool expectValue)
    {
        var result = AppCompatCacheDecoder.FileTimeToOffset(ft);
        Assert.Equal(expectValue, result is not null);
        if (result is { } v) Assert.NotEqual(1601, v.Year);
    }

    [Fact]
    public void FileTimeToOffset_正常時間往返()
    {
        var t = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(t, AppCompatCacheDecoder.FileTimeToOffset(t.ToFileTime()));
    }

    [Fact]
    public void Normalize_剝除裝置空間前綴()
    {
        Assert.Equal(@"C:\a.exe", AppCompatCacheDecoder.Normalize(@"\??\C:\a.exe"));
        Assert.Equal(@"C:\a.exe", AppCompatCacheDecoder.Normalize(@"C:\a.exe"));
    }
}
