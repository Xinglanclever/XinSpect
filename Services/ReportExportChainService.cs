using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 匯出檔的內嵌雜湊鏈（U10-PF-0009，<c>exp.hash.chain</c>）。
///
/// <para>
/// <b>做什麼：</b>每次把匯出檔寫到磁碟（報告 HTML／Markdown／純文字、歷史 CSV），
/// 在<b>檔尾</b>附加一行簽章——「上一份匯出檔的內容雜湊＋本次內容雜湊」：
/// <code>exp.hash.chain previous=… content=… algorithm=sha256</code>
/// 沒有前份時如實寫「鏈首」。接收方拿到的每一份檔案都自帶驗證線索，
/// 不必回頭問產生它的程式。
/// </para>
///
/// <para>
/// <b>鏈的存放照既有形狀，不另設第二個存放處：</b>審計鏈（<see cref="AuditLogService"/>，
/// %ProgramData%\XinSpect\Audit\audit.json）每筆 <see cref="AuditEntry"/> 用
/// <c>PreviousHash</c>→<c>EntryHash</c> 串接、創世標記 <see cref="AuditEntry.Genesis"/>。
/// 匯出鏈沿用同一本帳：每次匯出 append 一筆 <see cref="ExportAction"/> 紀錄
/// （<c>ResultHash</c>＝本次內容雜湊），下一份匯出檔的「前份雜湊」就取帳上最後一筆
/// 匯出紀錄的 <c>ResultHash</c>；帳上沒有匯出紀錄（或帳本讀不到）＝鏈首。
/// </para>
///
/// <para>
/// <b>界線（與審計鏈同一套誠實聲明）：</b>雜湊鏈可偵測改動與截斷，但不是數位簽章——
/// 不能阻止有能力重算雜湊的人整份重造。檔尾那行本身可見、可手驗：
/// 對「檔尾簽章行之前（含其前一個換行）」的位元組重算 SHA-256、與行內 <c>content</c> 比對即可。
/// </para>
///
/// <para>
/// <b>JSON 機讀檔刻意不加尾行：</b>CLI <c>--json</c> 的輸出是被程式消費的 JSON，
/// 檔尾附一行非 JSON 文字會破壞可解析性；這個界線如實寫在本註解裡，不假裝涵蓋了它。
/// </para>
/// </summary>
public static partial class ReportExportChainService
{
    /// <summary>本能力的追蹤鍵（U10-PF-0009）。</summary>
    public const string ChainKey = "exp.hash.chain";

    /// <summary>匯出動作在審計帳本裡的 <see cref="AuditEntry.Action"/>（帳上憑它找「前份」）。</summary>
    public const string ExportAction = "export-file";

    /// <summary>無前份時的如實陳述（鏈的起點）。</summary>
    public const string GenesisLabel = "鏈首";

    /// <summary>檔尾簽章行：previous 為前份內容雜湊（或「鏈首」）、content 為本次內容雜湊。</summary>
    public static string TailLine(string previousHash, string contentHash)
        => $"exp.hash.chain previous={previousHash} content={contentHash} algorithm=sha256";

    /// <summary>內容雜湊：對 UTF-8 位元組取 SHA-256（hex 小寫）。</summary>
    public static string ContentHashOf(string content) => AuditResultHash.OfText(content);

    /// <summary>
    /// 簽一份內容：內容正規化成以換行結尾後，對其取雜湊、附上檔尾簽章行。
    /// 回傳「簽好的完整檔案文字」；<paramref name="previousHash"/> 傳 null＝鏈首。
    /// </summary>
    public static string Sign(string content, string? previousHash)
    {
        string signed = content.Length > 0 && !content.EndsWith('\n') ? content + "\n" : content;
        string prev = string.IsNullOrEmpty(previousHash) ? GenesisLabel : previousHash;
        return signed + TailLine(prev, ContentHashOf(signed)) + "\n";
    }

    /// <summary>簽一份內容並記帳：前份取自審計帳本上最後一筆匯出紀錄，寫出後 append 本筆。</summary>
    /// <remarks>帳本任何一步失敗都不阻擋匯出本身（降級成鏈首簽章、不記帳）——匯出是主功能，鏈是附加。</remarks>
    public static string SignAndRecord(string exportPath, string content)
    {
        string? previous = null;
        bool ledgerOk = false;
        try
        {
            var entries = AuditLogService.Load(AuditLogService.DefaultPath);
            previous = PreviousFromLedger(entries);
            ledgerOk = true;
        }
        catch { /* 帳本讀不到：如實退回鏈首，且不寫帳（讀不了的帳不該往上記） */ }

        string signed = Sign(content, previous);

        if (ledgerOk)
        {
            // 簽章行裡的 content 雜湊即本次內容雜湊——記帳用同一個值，帳與檔案對得起來。
            string contentHash = ParseTail(signed)?.ContentHash ?? "";
            if (contentHash.Length > 0)
            {
                try
                {
                    var entries = AuditLogService.Load(AuditLogService.DefaultPath);
                    entries.Add(AuditLogService.Append(entries, AuditLogService.CurrentOperator(), "",
                        ExportAction, Path.GetFileName(exportPath),
                        $"匯出檔內嵌雜湊鏈（{ChainKey}）", contentHash, DateTimeOffset.UtcNow));
                    AuditLogService.Save(AuditLogService.DefaultPath, entries);
                }
                catch { /* 記帳失敗不影響已簽好的檔案；鏈的下一環會如實從鏈首重起 */ }
            }
        }
        return signed;
    }

    /// <summary>帳上最後一筆匯出紀錄的內容雜湊（＝下一份的「前份」）；沒有匯出紀錄回 null（鏈首）。</summary>
    public static string? PreviousFromLedger(IReadOnlyList<AuditEntry> entries)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
            if (entries[i].Action == ExportAction
                && !string.IsNullOrWhiteSpace(entries[i].ResultHash)
                && entries[i].ResultHash != "—")
                return entries[i].ResultHash;
        return null;
    }

    /// <summary>驗證判決：簽章行在不在、格式對不對、內容雜湊重算後比不比得上。</summary>
    public sealed record ChainVerdict(bool Valid, string? FailureReason, string? PreviousHash, string? ContentHash);

    private static readonly Regex TailRegex = SignLineRegex();

    [GeneratedRegex(@"^exp\.hash\.chain previous=(?<prev>\S+) content=(?<hash>[0-9a-f]{64}) algorithm=sha256$")]
    private static partial Regex SignLineRegex();

    /// <summary>從檔案文字裡抽出檔尾簽章行（必須是最後一個非空行）；沒有回 null。</summary>
    public static (string PreviousHash, string ContentHash)? ParseTail(string fileText)
    {
        string[] lines = fileText.Replace("\r\n", "\n").Split('\n');
        int i = lines.Length - 1;
        while (i >= 0 && lines[i].Length == 0) i--;   // 檔尾允許換行，簽章行必須是最後一個非空行
        if (i < 0) return null;
        var m = TailRegex.Match(lines[i]);
        if (!m.Success) return null;
        return (m.Groups["prev"].Value, m.Groups["hash"].Value);
    }

    /// <summary>讀回檔案→重算→比對（鏈的驗證入口）。</summary>
    public static ChainVerdict VerifyFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return new(false, $"檔案讀不到：{ex.GetType().Name}：{ex.Message}", null, null); }
        return VerifyText(text);
    }

    /// <summary>對檔案文字驗證：抽出簽章行、對「簽章行之前（含其前一個換行）」的位元組重算 SHA-256、比對。</summary>
    public static ChainVerdict VerifyText(string fileText)
    {
        var tail = ParseTail(fileText);
        if (tail is null)
            return new(false, $"檔尾沒有可解析的 {ChainKey} 簽章行——這份檔案不是鏈上的一環（或簽章行被移動／改寫）", null, null);

        string[] lines = fileText.Replace("\r\n", "\n").Split('\n');
        int i = lines.Length - 1;
        while (i >= 0 && lines[i].Length == 0) i--;
        // Sign() 保證內容以恰好一個換行收尾再接簽章行，這裡按同一形狀還原被簽的位元組。
        string signedContent = string.Join('\n', lines[..i]) + "\n";
        string recomputed = ContentHashOf(signedContent);
        if (!string.Equals(recomputed, tail.Value.ContentHash, StringComparison.Ordinal))
            return new(false, $"內容雜湊不符（重算 {recomputed[..12]}…／簽章 {tail.Value.ContentHash[..12]}…）——簽章之後的內容被竄改或損毀",
                tail.Value.PreviousHash, tail.Value.ContentHash);
        return new(true, null, tail.Value.PreviousHash, tail.Value.ContentHash);
    }
}
