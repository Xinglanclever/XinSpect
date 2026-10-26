using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 證據包（evidence pack）產生器與互驗：把一份報告寫成「第三方不需本工具即可驗證」的目錄。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有這件事：</b><c>ev.manifest</c>（包內檔案雜湊清單）與 <c>ev.chain.verify</c>
/// （審計鏈 × manifest 互驗）這兩把鍵早就登記在目錄、產生它們的方法（
/// <see cref="WriteGateReport.ToManifestFact"/>／<see cref="WriteGateReport.ToChainVerifyFact"/>）
/// 也早就寫好並有測試，但<b>正式流程沒有任何地方生產它們</b>——只有測試在呼叫。
/// 原因是缺的不是那把鍵，而是「兩份產物同時在手上」的那一刻：沒有包，就沒有 manifest，
/// 也就無從互驗。這個類別補的就是那一刻。
/// </para>
/// <para>
/// <b>互驗在這裡算的理由（不是隨便挑一個時機）：</b>互驗比的是「審計鏈」與「manifest」。
/// 審計鏈是<b>會繼續長</b>的東西，manifest 是<b>某一刻的封存</b>。若在別的時間點拿「當下的鏈」
/// 去比「過去的 manifest」，結論會騙人（鏈多了一筆就永遠對不上）。
/// 所以互驗只在<b>建立這個包的同一次呼叫裡</b>成立：那時兩份產物都是同一個瞬間的。
/// 也因為這樣，這把鍵<b>不會出現在 GUI 的即時事實集</b>裡——沒有包就沒有可互驗的對象，
/// 報告如實說「未建立證據包」比生一個看起來合理的值好。
/// </para>
/// <para>
/// <b>manifest 自己的雜湊不寫進 manifest</b>（自我指涉會讓它永遠算不出來）；
/// 它寫在旁邊的 <c>verify.json</c>，並由呼叫端一併記錄到審計鏈。
/// </para>
/// </remarks>
public static class EvidencePackBuilder
{
    /// <summary>包內雜湊清單檔名。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>互驗結果檔名（與 manifest 分開，避免自我指涉）。</summary>
    public const string VerifyFileName = "verify.json";

    /// <summary>工具識別（寫進 manifest，讓收到包的人知道是哪個工具產的）。</summary>
    public const string ToolName = "XinSpect";

    /// <summary>包內一個檔案的紀錄。</summary>
    public sealed record FileEntry(string File, long Bytes, string Sha256);

    /// <summary>建立結果。<see cref="ManifestValid"/> 與 <see cref="ChainValid"/> 是互驗的兩半。</summary>
    public sealed record Result(
        bool ManifestValid,
        bool ChainValid,
        IReadOnlyList<FileEntry> Files,
        IReadOnlyList<string> Missing,
        string ManifestSha256,
        string? Failure,
        string Directory);

    /// <summary>計算單一內容的 SHA-256（小寫十六進位，與倉庫其他雜湊同一種寫法）。</summary>
    public static string Sha256Of(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>計算磁碟上某個檔案的 SHA-256；讀不到回 null（不以空字串冒充）。</summary>
    public static string? Sha256OfFile(string path)
    {
        try { return Sha256Of(File.ReadAllBytes(path)); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 把 <paramref name="files"/> 寫進 <paramref name="directory"/>，產生 manifest，
    /// 再就地驗一次（檔案存在且雜湊相符＝manifest 有效）並驗審計鏈。
    /// </summary>
    /// <param name="directory">輸出目錄；不存在會建立，已存在不會清空（覆寫同名檔，其餘保留並在結果裡看得出來）。</param>
    /// <param name="files">要封存的檔案（名稱＋內容）。名稱不得含路徑分隔或等於 manifest／verify 的檔名。</param>
    /// <param name="chainPath">要一併封存與互驗的審計鏈檔案路徑；不存在＝鏈那一半如實標「沒有事件」。</param>
    /// <param name="createdAtUtc">建立時刻（測試注入固定值，不讓結果隨時間漂）。</param>
    public static Result Build(string directory, IReadOnlyList<(string Name, byte[] Content)> files,
        string chainPath, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(files);

        Directory.CreateDirectory(directory);

        var entries = new List<FileEntry>(files.Count);
        foreach (var (name, content) in files)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Contains('/') || name.Contains('\\')
                || name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(VerifyFileName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"不接受的檔名「{name}」：不得含路徑分隔，也不得占用 {ManifestFileName}／{VerifyFileName}。", nameof(files));

            File.WriteAllBytes(Path.Combine(directory, name), content);
            entries.Add(new FileEntry(name, content.LongLength, Sha256Of(content)));
        }

        // 審計鏈副本：它是「這個包何時由誰建立」的出處，所以也在清單裡。
        // 鏈不存在時不偽造一個空檔——那會讓「沒有事件」變成「有一個空的鏈」。
        string? chainCopyName = null;
        if (File.Exists(chainPath))
        {
            try
            {
                byte[] chainBytes = File.ReadAllBytes(chainPath);
                chainCopyName = "audit-chain.jsonl";
                File.WriteAllBytes(Path.Combine(directory, chainCopyName), chainBytes);
                entries.Add(new FileEntry(chainCopyName, chainBytes.LongLength, Sha256Of(chainBytes)));
            }
            catch (Exception) { chainCopyName = null; }
        }

        var manifest = new
        {
            tool = ToolName,
            toolVersion = AppInfo.Version,
            createdAtUtc = createdAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            fileCount = entries.Count,
            algorithm = "SHA-256",
            files = entries.Select(e => new { file = e.File, bytes = e.Bytes, sha256 = e.Sha256 }),
            note = "本檔列出包內每個檔案的 SHA-256；本檔自己的雜湊在 verify.json。"
                 + "收到包的人可以用任何工具重算每個檔案的 SHA-256 與本清單比對——不需要本工具。",
        };
        string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        string manifestPath = Path.Combine(directory, ManifestFileName);
        File.WriteAllText(manifestPath, manifestJson);

        string manifestSha = Sha256Of(Encoding.UTF8.GetBytes(manifestJson));

        // ── 就地驗一次：重讀磁碟上的檔案，不信任剛剛寫進記憶體的值 ──────────
        var missing = new List<string>();
        foreach (var e in entries)
        {
            string path = Path.Combine(directory, e.File);
            if (!File.Exists(path)) { missing.Add(e.File); continue; }
            if (Sha256OfFile(path) != e.Sha256) missing.Add(e.File);
        }
        bool manifestValid = entries.Count > 0 && missing.Count == 0;

        // ── 鏈那一半 ──────────────────────────────────────────────────
        // **檔案不存在不等於驗證通過**：AuditVerifier 對「沒有檔案」回有效（那是它的語意——
        // 空日誌沒有可被竄改的東西），但互驗要問的是「有沒有一條真的鏈可以印證 manifest」。
        // 兩者混用會把「從來沒建立過審計鏈」講成「互驗通過」，所以這裡先自己判斷存在性。
        bool chainValid;
        string? chainFailure = null;
        if (!File.Exists(chainPath))
        {
            chainValid = false;
            chainFailure = "沒有審計鏈檔（路徑不存在）——檔案不存在＝還沒有任何審計事件，"
                         + "那是「無從互驗」，不是「驗證通過」";
        }
        else
        {
            try
            {
                var verdict = AuditVerifier.VerifyFile(chainPath);
                chainValid = verdict.Valid;
                if (!verdict.Valid) chainFailure = verdict.FailureReason ?? "審計鏈未通過";
            }
            catch (Exception ex)
            {
                chainValid = false;
                chainFailure = "審計鏈讀取失敗：" + ex.Message;
            }
        }

        string? failure = manifestValid && chainValid
            ? null
            : chainValid ? "manifest 與磁碟上的檔案對不上（清單列出但檔案不在、或雜湊不符）"
            : manifestValid ? "審計鏈未通過：" + chainFailure
            : "manifest 與審計鏈都未通過：" + (chainFailure ?? "檔案對不上");

        return new Result(manifestValid, chainValid, entries, missing, manifestSha, failure, directory);
    }

    /// <summary>把互驗結果寫成 <c>verify.json</c>（含兩把事實的鍵、值與三態），回傳寫出的內容。</summary>
    public static string WriteVerifyReport(Result result, IReadOnlyList<HardwareFact> facts, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(facts);

        var payload = new
        {
            generatedAtUtc = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            directory = result.Directory,
            manifestValid = result.ManifestValid,
            chainValid = result.ChainValid,
            manifestSha256 = result.ManifestSha256,
            missing = result.Missing,
            failure = result.Failure,
            note = "兩者必須互相印證才算數：單獨一個通過不足以證明包沒有被改動。",
            facts = facts.Select(f => new
            {
                key = f.Key,
                name = f.Name,
                value = f.Value,
                availability = f.Availability.ToString(),
                numericValue = f.NumericValue,
                unavailableReason = f.UnavailableReason,
            }),
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(Path.Combine(result.Directory, VerifyFileName), json);
        return json;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
