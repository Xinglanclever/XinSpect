using System.Buffers.Binary;
using System.Text;

namespace XinSpect;

/// <summary>AppCompatCache（Shimcache）的一條條目：插入序位、路徑、檔案最後修改時間、是否留有執行旗標。</summary>
public sealed record ShimcacheEntry(
    int Position,
    string Path,
    DateTimeOffset? LastModifiedUtc,
    bool? Executed);

/// <summary>AppCompatCache 解析結果。解析不出格式回 null；可解析但中途斷掉時帶原因與剩餘位元組數。</summary>
public sealed record ShimcacheParseResult(
    string Format,
    int DeclaredCount,
    IReadOnlyList<ShimcacheEntry> Entries,
    int UnparsedTailBytes,
    string? TruncatedReason);

/// <summary>
/// AppCompatCache（Shimcache）二進位值解碼器（Vol 2 批次 B／FS-001）。
/// <para>
/// <b>版面對準出處</b>：Eric Zimmerman AppCompatCacheParser 的 <c>AppCompatCache/Windows7.cs</c>（x64 分支）
/// 與 <c>AppCompatCache/Windows10.cs</c> 逐欄實作（2026-10-11 抓取）。Win7 x64 條目自值內絕對位移讀路徑；
/// Win10 條目「是否執行」取自資料 blob 尾端 4 位元組是否為 1。
/// </para>
/// <para>
/// <b>界線</b>：執行痕跡≠執行成功——快取由系統維護、可能殘留，條目存在不代表真的跑過（Win10 的旗標是系統記的），
/// 更不是中毒判決；時間是<b>檔案</b>最後修改時間，不是執行時間。
/// Win8／8.1 版面（00ts／10ts 含 package 段）<b>未對準出處前不出值</b>——如實回報格式已辨認但未實作，不猜。
/// </para>
/// </summary>
[SpecRef("Eric Zimmerman AppCompatCacheParser（AppCompatCache/Windows7.cs Windows10.cs，2026-10-11 抓取）："
    + "Win7 x64 條目＝PathSize(2)+MaxPathSize(2)+pad(4)+PathOffset(8 絕對位移)+FILETIME(8)+InsertFlags(4, bit1=Executed)"
    + "+ShimFlags(4)+CacheEntryDataSize(8)+DataOffset(8)，路徑 UTF-16 於 PathOffset；"
    + "Win10 條目＝Sig\"10ts\"(4)+unknown(4)+CacheEntryDataSize(4)+PathSize(2)+Path(UTF-16)+FILETIME(8, 1601 視為無)"
    + "+DataSize(4)+Data(尾端 4 位元組==1 即留有執行旗標)。版面常數（0x34 記錄起點／0x80 表頭）同源。")]
public static class AppCompatCacheDecoder
{
    /// <summary>Win7 x64／Vista x64 值的表頭長度（記錄自 offset 128 起）。</summary>
    public const int Win7HeaderBytes = 128;

    /// <summary>Win10 記錄區起點的常見值（值首 4 位元組＝記錄區絕對位移）。</summary>
    public const int Win10RecordsOffset = 0x34;

    /// <summary>樣本欄位最多列幾條（其餘以「等 N 條」收束；服務層沿用）。</summary>
    public const int ListedMax = 10;

    /// <summary>
    /// 解析 AppCompatCache REG_BINARY 值。格式辨認不出回 null（呼叫端如實三態，不猜）；
    /// Win8／8.1 回已辨認但零條目的結果（界線寫在 Format 與原因裡）。
    /// </summary>
[SpecRef("Eric Zimmerman AppCompatCacheParser 同源（Windows7.cs／Windows10.cs，2026-10-11 抓取）——本方法欄位序即該出處的解析序；界線同類別註解。")]
    public static ShimcacheParseResult? Parse(byte[]? raw)
    {
        if (raw is null || raw.Length < 8) return null;

        int recordsOffset = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0, 4));

        // Win10 家族（Server 2016+／Win10+）：值首 4 位元組是記錄區位移（實務上 0x34）且記錄以 "10ts" 開頭。
        if (recordsOffset > 0 && recordsOffset < Win7HeaderBytes)
        {
            if (raw.Length < recordsOffset + 4) return null;
            if (Encoding.ASCII.GetString(raw, recordsOffset, 4) != "10ts") return null;
            return ParseWin10(raw, recordsOffset);
        }

        // 0x80 表頭家族（Win7 x64 與 Win8/8.1）：值至少要有表頭＋一條完整記錄才宣稱解析得了。
        if (raw.Length >= Win7HeaderBytes + 48)
        {
            string firstSig = Encoding.ASCII.GetString(raw, Win7HeaderBytes, 4);
            if (firstSig == "00ts" || firstSig == "10ts")
            {
                // Win8／8.1：條目含 package 段與旗標段，本版未對準出處，不出值。
                return new ShimcacheParseResult(
                    firstSig == "00ts" ? "Win8/Server2012" : "Win8.1/Server2012R2",
                    0, [], Math.Max(0, raw.Length - Win7HeaderBytes),
                    "條目版面未對準出處（package／旗標段未驗證）——不猜欄位，不出值");
            }
            return ParseWin7X64(raw);
        }

        return null;
    }

    /// <summary>Win10 家族解析。條目序位即插入順序；中途結構不足則停住並回報剩餘量。</summary>
[SpecRef("Eric Zimmerman AppCompatCacheParser 同源（Windows7.cs／Windows10.cs，2026-10-11 抓取）——本方法欄位序即該出處的解析序；界線同類別註解。")]
    public static ShimcacheParseResult ParseWin10(byte[] raw, int recordsOffset)
    {
        var entries = new List<ShimcacheEntry>();
        // 申報總數在 0x28（記錄區位移為 0x34 的版面；出處 Windows10.cs 同位址）。
        int declared = raw.Length >= 0x2C
            ? BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x28, 4))
            : 0;

        int index = recordsOffset;
        while (index + 16 <= raw.Length)
        {
            if (Encoding.ASCII.GetString(raw, index, 4) != "10ts") break;
            index += 4;              // "10ts"
            index += 4;              // 未知欄位（出處同樣跳過）
            index += 4;              // CacheEntryDataSize（不影響定界，直接以下欄位推進）
            if (index + 2 > raw.Length) return new("Win10/Server2016+", declared, entries, raw.Length - index, "條目在路徑長度處截斷");
            int pathSize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(index, 2));
            index += 2;
            if (pathSize < 0 || index + pathSize > raw.Length) return new("Win10/Server2016+", declared, entries, raw.Length - index, "條目在路徑處截斷");
            string path = Encoding.Unicode.GetString(raw, index, pathSize);
            index += pathSize;
            if (index + 8 > raw.Length) return new("Win10/Server2016+", declared, entries, raw.Length - index, "條目在時間戳處截斷");
            long ft = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(index, 8));
            index += 8;
            var lastMod = FileTimeToOffset(ft);
            if (index + 4 > raw.Length) return new("Win10/Server2016+", declared, entries, raw.Length - index, "條目在資料長度處截斷");
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(index, 4));
            index += 4;
            if (dataSize < 4 || index + dataSize > raw.Length) return new("Win10/Server2016+", declared, entries, raw.Length - index, "條目在資料區截斷");
            bool executed = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(index + dataSize - 4, 4)) == 1;
            index += dataSize;

            entries.Add(new ShimcacheEntry(entries.Count, Normalize(path), lastMod, executed));
        }

        int tail = raw.Length - index;
        return new("Win10/Server2016+", declared, entries, tail,
            tail > 0 ? $"記錄區後尚有 {tail} 位元組未解析（不猜欄位）" : null);
    }

    /// <summary>Win7 x64／Vista x64 解析：路徑在值的絕對位移上，執行旗標在 InsertFlags bit 1（0x2）。</summary>
[SpecRef("Eric Zimmerman AppCompatCacheParser 同源（Windows7.cs／Windows10.cs，2026-10-11 抓取）——本方法欄位序即該出處的解析序；界線同類別註解。")]
    public static ShimcacheParseResult ParseWin7X64(byte[] raw)
    {
        int declared = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4, 4));
        var entries = new List<ShimcacheEntry>();

        if (declared == 0)
            return new("Win7x64/2008R2", 0, [], Math.Max(0, raw.Length - Win7HeaderBytes), "申報 0 條；記錄區尚有位元組——如實列出不猜");

        int index = Win7HeaderBytes;
        while (index + 48 <= raw.Length && entries.Count < declared)
        {
            int pathSize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(index, 2));
            index += 2;
            index += 2;  // MaxPathSize
            index += 4;  // 未知 padding（出處跳過）
            if (index + 8 > raw.Length) return new("Win7x64/2008R2", declared, entries, raw.Length - index, "條目在位移處截斷");
            long pathOffset = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(index, 8));
            index += 8;
            if (index + 8 > raw.Length) return new("Win7x64/2008R2", declared, entries, raw.Length - index, "條目在時間戳處截斷");
            long ft = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(index, 8));
            index += 8;
            var lastMod = FileTimeToOffset(ft);
            if (index + 8 > raw.Length) return new("Win7x64/2008R2", declared, entries, raw.Length - index, "條目在旗標處截斷");
            int insertFlags = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(index, 4));
            index += 4;
            index += 4;  // shim flags（出處跳過）
            index += 8;  // CacheEntryDataSize
            index += 8;  // DataOffset
            if (pathSize <= 0 || pathOffset < 0 || pathOffset + pathSize > raw.Length)
                return new("Win7x64/2008R2", declared, entries, raw.Length - index, $"路徑位移 0x{pathOffset:X} 超出值邊界——條目結構不可信，停在前面已驗證的 {entries.Count} 條");
            string path = Encoding.Unicode.GetString(raw, (int)pathOffset, pathSize);
            entries.Add(new ShimcacheEntry(entries.Count, Normalize(path), lastMod, (insertFlags & 0x2) != 0));
        }

        int tail = raw.Length - index;
        return new("Win7x64/2008R2", declared, entries, tail,
            tail > 0 ? $"記錄區後尚有 {tail} 位元組未解析（不猜欄位）" : null);
    }

    /// <summary>FILETIME（UTC）→ DateTimeOffset；1601 年（全零）視為「沒有時間」不冒充。</summary>
[SpecRef("Eric Zimmerman AppCompatCacheParser 同源（Windows7.cs／Windows10.cs，2026-10-11 抓取）——本方法欄位序即該出處的解析序；界線同類別註解。")]
    public static DateTimeOffset? FileTimeToOffset(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            var v = DateTimeOffset.FromFileTime(fileTime);
            return v.Year == 1601 ? null : v;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>剝掉裝置空間前綴（\??\／\SystemRoot 等），與出處工具口徑一致。</summary>
[SpecRef("Eric Zimmerman AppCompatCacheParser 同源（Windows7.cs／Windows10.cs，2026-10-11 抓取）——本方法欄位序即該出處的解析序；界線同類別註解。")]
    public static string Normalize(string path) =>
        path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;
}
