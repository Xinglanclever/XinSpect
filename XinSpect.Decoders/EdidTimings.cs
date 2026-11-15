using System.Globalization;

namespace XinSpect;

public sealed record EdidDetailedTiming(int ClockKhz,
    int HActive, int HSyncStart, int HSyncEnd, int HTotal,
    int VActive, int VSyncStart, int VSyncEnd, int VTotal,
    int ImageWidthMm, int ImageHeightMm,
    string HSync, string VSync, byte Flags, byte HBorder, byte VBorder);

public sealed record EdidTiming(int Offset, string Encoding, int Width, int Height,
    string RefreshHz, bool Interlaced, int? DmtCode, string MatchBasis, EdidDetailedTiming? Detailed = null);

public sealed record EdidTimingResult(bool BaseValid, IReadOnlyList<EdidTiming> Timings, IReadOnlyList<string> Issues);

/// <summary>
/// EDID 宣告時序：基本塊 established I/II、standard、established III、DTD 與 CTA DTD。
/// 不生成 CVT/GTF、不改顯示模式；DisplayID 與 CTA VIC 由各自解碼器處理。
/// </summary>
[SpecRef("VESA E-EDID 1.4 §3；v4l-utils edid-decode parse-base-block.cpp detailed_timings/print_standard_timing、parse-cta-block.cpp")]
public static class EdidTimings
{
    [SpecRef("E-EDID 1.x：8-byte header、128-byte block checksum、0x12/0x13 version/revision、0x7e extension count")]
    public static EdidTimingResult Parse(byte[]? edid)
    {
        var modes = new List<EdidTiming>();
        var issues = new List<string>();
        EdidTimingResult Result(bool valid) => new(valid, modes.AsReadOnly(), issues.AsReadOnly());
        if (edid is null || edid.Length < 128)
        {
            issues.Add("EDID 基本塊不足 128 位元組");
            return Result(false);
        }
        ReadOnlySpan<byte> header = [0, 255, 255, 255, 255, 255, 255, 0];
        if (!edid.AsSpan(0, 8).SequenceEqual(header) || !Checksum(edid.AsSpan(0, 128)))
        {
            issues.Add("EDID 基本塊標頭或 checksum 無效，未解讀時序");
            return Result(false);
        }
        if (edid[18] != 1 || edid[19] > 4)
        {
            issues.Add($"未支援 EDID {edid[18]}.{edid[19]}，未套用 1.x 欄位定義");
            return Result(false);
        }
        foreach (var e in DmtKnowledge.EstablishedTiming1)
            if ((edid[e.ByteOffset] & e.BitMask) != 0)
                modes.Add(new(e.ByteOffset, "Established I/II", e.Width, e.Height, e.RefreshHz,
                    e.Interlaced, DmtKnowledge.ResolveEst1(e), "位圖指定"));
        if ((edid[37] & 0x7f) != 0)
            issues.Add("Established 廠商保留位非零，未猜測其時序");

        for (int offset = 38; offset < 54; offset += 2)
        {
            int first = edid[offset], second = edid[offset + 1];
            if (first == 1 && second == 1) continue;
            if (first <= 1) { issues.Add($"Standard 0x{offset:X2} 編碼無效"); continue; }
            int width = (first + 31) * 8;
            int height = (second >> 6) switch
            {
                0 => edid[19] < 3 ? width : width * 10 / 16,
                1 => width * 3 / 4,
                2 => width * 4 / 5,
                _ => width * 9 / 16,
            };
            string refresh = ((second & 63) + 60).ToString(CultureInfo.InvariantCulture);
            modes.Add(new(offset, "Standard", width, height, refresh, false,
                DmtKnowledge.FindByGeometry(width, height, refresh)?.Code, "幾何候選，未含完整時序"));
        }

        for (int offset = 54; offset + 18 <= 126; offset += 18)
        {
            var d = edid.AsSpan(offset, 18);
            if (d[0] != 0 || d[1] != 0)
                AddDetailed(d, offset, "Base DTD", modes, issues);
            else if (d[2] == 0 && d[3] == 0xf7 && d[4] == 0)
            {
                if (edid[19] < 4 || d[5] != 10)
                {
                    issues.Add($"Established III 0x{offset:X2} 的 EDID 版本或描述元版本不支援");
                    continue;
                }
                foreach (var e in DmtKnowledge.EstablishedTiming3)
                    if ((d[e.ByteOffset] & e.BitMask) != 0)
                        modes.Add(new(offset + e.ByteOffset, "Established III", e.Width, e.Height,
                            e.RefreshHz, false, DmtKnowledge.ResolveEst3(e), "位圖指定"));
                if ((d[11] & 15) != 0 || d[12..].IndexOfAnyExcept((byte)0) >= 0)
                    issues.Add($"Established III 0x{offset:X2} 保留位非零，未解讀保留位");
            }
        }

        int expected = (edid[126] + 1) * 128;
        if (edid.Length < expected)
            issues.Add($"EDID 宣告 {edid[126]} 個擴充塊，但資料截斷（{edid.Length}/{expected} bytes）");
        if (edid.Length > expected)
            issues.Add($"EDID 多出 {edid.Length - expected} 個未宣告位元組，未當成擴充塊");
        for (int index = 1; index <= edid[126] && (index + 1) * 128 <= edid.Length; index++)
        {
            int start = index * 128;
            var block = edid.AsSpan(start, 128);
            if (!Checksum(block)) { issues.Add($"擴充塊 {index} checksum 無效，未解讀"); continue; }
            if (block[0] != 2) continue;
            if (block[1] is < 1 or > 3) { issues.Add($"CTA 擴充塊 {index} 修訂 {block[1]} 未支援"); continue; }
            int dtd = block[2];
            if (dtd == 0) continue;
            if (dtd < 4 || dtd > 127) { issues.Add($"CTA 擴充塊 {index} DTD 起點無效"); continue; }
            for (int offset = dtd; offset + 18 <= 127; offset += 18)
            {
                var descriptor = block.Slice(offset, 18);
                if (descriptor[0] == 0 && descriptor[1] == 0) break;
                AddDetailed(descriptor, start + offset, "CTA DTD", modes, issues);
            }
        }
        return Result(true);
    }

    private static bool Checksum(ReadOnlySpan<byte> block)
    {
        int sum = 0;
        foreach (byte value in block) sum += value;
        return (sum & 255) == 0;
    }

    private static void AddDetailed(ReadOnlySpan<byte> d, int offset, string encoding,
        List<EdidTiming> modes, List<string> issues)
    {
        int clock = (d[0] | d[1] << 8) * 10;
        int ha = d[2] | (d[4] & 0xf0) << 4, hb = d[3] | (d[4] & 15) << 8;
        int va = d[5] | (d[7] & 0xf0) << 4, vb = d[6] | (d[7] & 15) << 8;
        int hf = d[8] | (d[11] & 0xc0) << 2, hs = d[9] | (d[11] & 0x30) << 4;
        int vf = d[10] >> 4 | (d[11] & 12) << 2, vs = d[10] & 15 | (d[11] & 3) << 4;
        if (ha == 0 || va == 0 || hb == 0 || vb == 0 || hs == 0 || vs == 0 || hf + hs > hb || vf + vs > vb)
        {
            issues.Add($"DTD 0x{offset:X2} 的 active/blank/sync 不成立，未解讀");
            return;
        }
        bool interlaced = (d[17] & 0x80) != 0;
        int scale = interlaced ? 2 : 1;
        int ht = ha + hb, vt = (va + vb) * scale + (interlaced ? 1 : 0);
        bool separate = (d[17] & 0x18) == 0x18;
        string hp = separate ? ((d[17] & 2) != 0 ? "P" : "N") : "?";
        string vp = separate ? ((d[17] & 4) != 0 ? "P" : "N") : "?";
        // 儲存 DTD 原始垂直欄位：交錯式是每場，不把半行規則混進原值。
        var detailed = new EdidDetailedTiming(clock, ha, ha + hf, ha + hf + hs, ht,
            va, va + vf, va + vf + vs, va + vb,
            d[12] | (d[14] & 0xf0) << 4, d[13] | (d[14] & 0x0f) << 8,
            hp, vp, d[17], d[15], d[16]);
        // 非零邊框、立體與非分離同步的語意不同，保留原值但不強行對到 DMT。
        bool comparable = !interlaced && separate && (d[17] & 0x61) == 0 && d[15] == 0 && d[16] == 0;
        var match = comparable ? DmtKnowledge.FindByTiming(clock, ha, detailed.HSyncStart, detailed.HSyncEnd, ht,
            va * scale, (va + vf) * scale, (va + vf + vs) * scale, vt, hp, vp, interlaced) : null;
        // 未對碼的交錯模式可能有特殊半行規則（例如 CTA VIC 39），不推算幀率。
        string refresh = interlaced ? "未確認"
            : (clock * 1000.0 / ht / vt).ToString("F3", CultureInfo.InvariantCulture);
        string basis = interlaced ? "交錯式保留每場欄位，不冒充完整幀時序 DMT 對照"
            : comparable ? "完整時序比對（時脈量化 ±5 kHz）" : "邊框／立體／非分離同步未對碼";
        modes.Add(new(offset, encoding, ha, va * scale, refresh, interlaced, match?.Code,
            basis, detailed));
    }
}
