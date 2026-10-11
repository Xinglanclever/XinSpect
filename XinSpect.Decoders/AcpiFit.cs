using System.Buffers.Binary;
using System.Text;

namespace XinSpect;

/// <summary>FIT 的一筆目錄條目：型別號、型別名、位移、長度。</summary>
public sealed record FitEntry(ushort Type, string TypeName, uint Offset, uint Size);

/// <summary>FIT 表解析結果。Signature 缺＝回 null（呼叫端如實標）。</summary>
public sealed record FitTable(IReadOnlyList<FitEntry> Entries, uint TotalSize, string? TruncatedReason);

/// <summary>
/// Firmware Interface Table（FIT）純解碼器（P0-02／FW-001）。吃快閃尾端的原始位元組。
/// <para>
/// 版面對準 Intel「Platform Firmware Initialization / FIT」公開文件（FWI spec）：表標頭 0x20 位元組
/// （BaseAddress u64／Size u32／Signature "_FIT_"(8)／Flags u32／Reserved u12），條目自標頭後起，
/// 每條 Type u16／Size u16／Reserved u32＋資料。搜尋方式＝快閃尾端 0x400 內向下找簽名（16 位元組對齊）。
/// </para>
/// <para><b>界線</b>：Boot Guard profile 需要 flash descriptor（FLMAP／FMLF）佈局——本版未對準出處，
/// <b>不出值</b>（服務層如實標 NotSupported）；只呈現「目录上有什麼」，不判「是否被繞過」、不驗 ACM 簽章。</para>
/// </summary>
public static class FitDecoder
{
    /// <summary>表標頭長度。</summary>
    public const int HeaderBytes = 0x20;

    /// <summary>尾端搜尋範圍（位元組）。</summary>
    public const int TailSearchBytes = 0x400;

    /// <summary>型別→名稱（FWI 公開目錄的收錄子集；未收錄如實標「未收錄」）。</summary>
    public static readonly IReadOnlyDictionary<ushort, string> TypeNames = new Dictionary<ushort, string>
    {
        [0x00] = "微碼（Microcode）",
        [0x02] = "Co-Security／ACM",
        [0x05] = "Platform Boot Policy",
        [0x07] = "BIOS Startup Module",
        [0x09] = "RAM Init（MRC）",
        [0x0C] = "Text Mode 輸出",
        [0x0D] = "版本",
        [0x0E] = "TPM 設定",
    };

    /// <summary>從尾端緩衝找 FIT 標頭並解析條目。找不到簽名回 null。</summary>
    [SpecRef("Intel Platform Firmware Initialization：FIT 標頭＝BaseAddress u64／Size u32／Signature \"_FIT_\"(8)／"
        + "Flags u32／Reserved u12＝0x20 位元組；條目＝Type u16／Size u16／Reserved u32；尾端 0x400 內 16 位元組對齊向下找簽名。"
        + "Boot Guard profile 需 FLMAP／FMLF descriptor——未對準出處前不出值")]
    public static FitTable? Parse(byte[] tail)
    {
        if (tail.Length < HeaderBytes) return null;
        int start = -1;
        for (int off = tail.Length - 16; off >= HeaderBytes; off -= 16)
        {
            if (Encoding.ASCII.GetString(tail, off, 5) != "_FIT_") continue;
            start = off;
            break;
        }
        if (start < 0) return null;

        int header = start - 0x10; // start 是簽名位置；標頭起點再往前 0x10
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(header + 8, 4));
        int cursor = header + HeaderBytes;
        int end = (int)Math.Min((long)cursor + size, tail.Length);
        var entries = new List<FitEntry>();
        while (cursor + 8 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(cursor, 2));
            ushort esize = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(cursor + 2, 2));
            if (esize < 8 || cursor + esize > tail.Length)
                return new FitTable(entries, size, $"條目在位移 0x{cursor - header:X} 處結構不足（Size {esize}）——停在已驗證的 {entries.Count} 條");
            entries.Add(new FitEntry(type, TypeNames.TryGetValue(type, out string? n) ? n : "未收錄",
                (uint)(cursor - header), esize));
            cursor += esize;
        }
        string? trunc = cursor < end ? $"條目區尾端 {end - cursor} 位元組未解析（不猜欄位）" : null;
        return new FitTable(entries, size, trunc);
    }
}
