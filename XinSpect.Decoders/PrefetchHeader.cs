using System.Buffers.Binary;
using System.Text;

namespace XinSpect;

/// <summary>Prefetch 檔頭辨識結果。Kind：MAM（Win10/11 壓縮容器）／SCCA（未壓縮頭）／Unrecognized。</summary>
public sealed record PrefetchHeader(
    string Kind,
    string? VersionLabel,
    uint? UncompressedSize,
    string? ExecutableName,
    string? HashHex);

/// <summary>
/// Prefetch（.pf）檔頭解碼器（Vol 2 批次 B／FS-003 的<b>檔名層</b>）。
/// <para>
/// 版面對準 Eric Zimmerman <c>Prefetch/PrefetchFile.cs</c>（2026-10-11 抓取）：Win10/11 檔以 ASCII
/// "MAM" 開頭（第 4 位元組為格式版號）＋4 位元組解壓後長度＋壓縮內含；未壓縮檔為 version int32（17=XP/2003、
/// 23=Vista/7、26=Win8/2012、30=Win10/11、31=Win11）＋"SCCA"＋未知 4 位元組＋檔大小＋60 位元組 UTF-16 檔名＋4 位元組雜湊。
/// </para>
/// <para>
/// <b>本輪界線：不解壓 LZXpress 內含</b>——沒列引用檔案清單與卷冊資訊；檔名層＋寫入時間是「曾被預取」的事實，
/// 不等於執行成功、也不是執行時刻（系統於關機/定期寫入，近似）。版本對照只列來源收錄的五個值，其他值回 Unrecognized。
/// </para>
/// </summary>
public static class PrefetchHeaderDecoder
{
    /// <summary>未壓縮檔頭的最小可辨識長度（到雜湊欄尾端）。</summary>
    public const int UncompressedHeaderBytes = 80;

    /// <summary>
    /// 辨識 80 位元組以內的檔頭。Kind 三類；MAM 不宣稱解壓、SCCA 解出檔名與雜湊、其餘 Unrecognized。
    /// </summary>
    [SpecRef("Eric Zimmerman Prefetch 庫（Prefetch/PrefetchFile.cs，2026-10-11 抓取）："
        + "MAM 容器＝ASCII \"MAM\"(3)+格式位元組(1)+解壓後長度 u32(4)+壓縮內含（Xpress2/LZX，本輪不解）；"
        + "未壓縮頭＝version i32(4)（17 XP/2003、23 Vista/7、26 Win8/2012、30 Win10/11、31 Win11）"
        + "+\"SCCA\"(4)+未知(4)+檔大小 i32(4)+UTF-16 檔名(60)+路徑雜湊 i32(4)；界線＝檔名層不代譯壓縮內容")]
    public static PrefetchHeader Parse(byte[] head)
    {
        if (head.Length >= 3 && Encoding.ASCII.GetString(head, 0, 3) == "MAM")
        {
            byte formatByte = head.Length >= 4 ? head[3] : (byte)0;
            uint? size = head.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4)) : null;
            return new PrefetchHeader("MAM", "MAM 0x" + formatByte.ToString("X2"), size, null, null);
        }

        if (head.Length < UncompressedHeaderBytes)
            return new PrefetchHeader("Unrecognized", null, null, null, null);

        int version = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0, 4));
        string sig = Encoding.ASCII.GetString(head, 4, 4);
        if (sig != "SCCA")
            return new PrefetchHeader("Unrecognized", null, null, null, null);

        string? label = version switch
        {
            17 => "v17（XP/2003）",
            23 => "v23（Vista/7）",
            26 => "v26（Win8/2012）",
            30 => "v30（Win10/11）",
            31 => "v31（Win11）",
            _ => null,
        };
        if (label is null)
            return new PrefetchHeader("Unrecognized", "SCCA・未收錄版本 " + version, null, null, null);

        string raw = Encoding.Unicode.GetString(head, 16, 60);
        int nul = raw.IndexOf('\0');
        string exe = (nul >= 0 ? raw[..nul] : raw).Trim();
        uint hash = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(76, 4));
        return new PrefetchHeader("SCCA", label, null, exe, hash.ToString("X8"));
    }
}
