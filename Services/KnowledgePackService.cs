using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>離線知識包的一張表：表名、快照日期、條目（鍵→值）。</summary>
public sealed record KnowledgeTable(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("snapshotDate")] string SnapshotDate,
    [property: JsonPropertyName("entries")] IReadOnlyDictionary<string, string> Entries,
    [property: JsonPropertyName("sourceManifest")] KnowledgeSourceManifest? SourceManifest = null);

/// <summary>包作者自述的來源；hash 僅綁定表身，不認證文件、作者或抽取過程。</summary>
public sealed record KnowledgeSourceManifest(
    [property: JsonPropertyName("document")] string? Document,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("extractor")] string? Extractor,
    [property: JsonPropertyName("extractorVersion")] string? ExtractorVersion,
    [property: JsonPropertyName("tableHash")] string TableHash);

/// <summary>知識包＝版號＋快照＋表集合；serialNumber 由內容雜湊推導（同一組輸入得同一份文件）。</summary>
/// <remarks>
/// <paramref name="PreviousSerial"/> 是版本演進指標（<c>kp.evolution.proc</c>）：新版包覆蓋舊版時
/// 帶上前版的 serialNumber，讓「第 N 版是從哪一份演進來的」機器可查。舊格式包沒有這個欄位＝null，
/// 序列號計算與既有形狀完全一致（向後相容）。
/// </remarks>
public sealed record KnowledgePack(
    [property: JsonPropertyName("packVersion")] int PackVersion,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("tables")] IReadOnlyList<KnowledgeTable> Tables,
    [property: JsonPropertyName("serialNumber")] string SerialNumber,
    [property: JsonPropertyName("previousSerial")] string? PreviousSerial = null);

/// <summary>載入結果：包本体、完整性宣告、拒收原因。</summary>
public sealed record KnowledgePackLoad(KnowledgePack? Pack, string? Reason, string? DeclaredSerial);

/// <summary>
/// 知識表離線更新包（EG-002／P0-09）。零網路原則不破：包由使用者手動換檔，本載入層不出網。
/// <para>
/// 格式＝JSON（<see cref="KnowledgePack"/>，隨附 <c>knowledge-pack.json</c> 於執行檔目錄）；
/// 完整性＝serialNumber 由「不含 serialNumber 欄位的 canonical JSON」以 SHA-256 推導，不符＝拒收＋ReadError；
/// 缺 snapshotDate＝整包拒收（讀者無從判斷它多舊）。包缺→用內建表＋「內建快照於 v2.66」聲明。
/// <b>界線</b>：內建表過期聲明成文——沒有更新通道；換包動作進審計鏈（<see cref="RecordSwap"/>）。
/// </para>
/// </summary>
public static class KnowledgePackService
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const int MaximumTables = 64;
    public const int MaximumEntries = 100_000;
    public const int MaximumFieldCharacters = 4096;
    public const string ProvenanceUnknown = "來源未知：未提供完整 upstream 文件、revision／commit 與抽取器版本；內容雜湊不構成來源鏈";
    /// <summary>U10-EN-0012 追蹤鍵：知識包版本演進與撤回流程（成文與守門見
    /// docs/ULTIMATE-10000/KNOWLEDGE-PACK-PROCEDURE-2026-10-25.md 與 KnowledgePackEvolutionTests）。</summary>
    public const string EvolutionProcKey = "kp.evolution.proc";

    public const string Category = "知識與更新";
    public const string StatusKey = "knowledge.pack.status";
    public const string TablesKey = "knowledge.pack.tables";

    /// <summary>預設包檔名（與執行檔同目錄；不存在＝內建表）。</summary>
    public const string FileName = "knowledge-pack.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// 內建表的快照聲明。<b>由 <see cref="BuiltinKnowledgeInventory"/> 推導，不是手寫的字串。</b>
    /// </summary>
    /// <remarks>
    /// 這一句原本是寫死的「v2.66 內建（OuiKnowledge／bugcheck 子集；打包於 2026-10-11）」，
    /// 而它從 v2.66 起就沒動過——內建表早就長成九張、兩萬多條，畫面卻還說只有兩張子集。
    /// <b>「內建表有哪些東西」的唯一說明停在六個版本前，而它是使用者在畫面上唯一看得到的線索。</b>
    /// 現在它由清冊算出來：表變了字串自己會變，不可能再落後。
    /// </remarks>
    public static string BuiltinSnapshot => BuiltinKnowledgeInventory.SnapshotStatement();

    private const string Source = "內建表／knowledge-pack.json（離線）";

    /// <summary>產生 canonical 序列號：不含 serialNumber 欄位的 JSON 以 UTF-8 SHA-256。</summary>
    /// <remarks>
    /// <paramref name="previousSerial"/> 非空時納入雜湊（演進指標被篡改＝整包拒收）；
    /// 為空時<b>不</b>出現在 payload 裡——既有包的序列號一個位元都不變（向後相容）。
    /// </remarks>
    public static string ComputeSerial(int packVersion, string generatedAt, IReadOnlyList<KnowledgeTable> tables,
        string? previousSerial = null)
    {
        var orderedTables = tables.OrderBy(t => t.Table, StringComparer.Ordinal)
            .Select(CanonicalTable)
            .ToArray();
        object payload = previousSerial is null
            ? new { packVersion, generatedAt, tables = orderedTables }
            : new { packVersion, generatedAt, previousSerial, tables = orderedTables };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static Dictionary<string, object?> CanonicalTable(KnowledgeTable table)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["table"] = table.Table,
            ["snapshotDate"] = table.SnapshotDate,
            ["entries"] = new Dictionary<string, string>(
                table.Entries.OrderBy(e => e.Key, StringComparer.Ordinal), StringComparer.Ordinal),
        };
        // Omit absent manifest, preserving every byte of legacy serial payloads.
        if (table.SourceManifest is not null) body["sourceManifest"] = table.SourceManifest;
        return body;
    }

    public static string ComputeTableHash(KnowledgeTable table)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            CanonicalTable(table with { SourceManifest = null })))).ToLowerInvariant();

    public static string DescribeProvenance(KnowledgeTable table)
    {
        if (string.Equals(table.Table, DeviceComplianceFactsService.TableName, StringComparison.OrdinalIgnoreCase))
            return DescribePrivateProvenance(table);
        if (table.SourceManifest is not { } manifest) return ProvenanceUnknown;
        string completeness = string.IsNullOrWhiteSpace(manifest.Document)
            || (string.IsNullOrWhiteSpace(manifest.Revision) && string.IsNullOrWhiteSpace(manifest.Commit))
            || string.IsNullOrWhiteSpace(manifest.Extractor) || string.IsNullOrWhiteSpace(manifest.ExtractorVersion)
            ? ProvenanceUnknown + "；部分自述" : "來源 manifest 自述（未認證 upstream 或抽取過程）";
        return $"{completeness}：文件 {manifest.Document ?? "未知"}；revision {manifest.Revision ?? "未知"}；"
            + $"commit {manifest.Commit ?? "未知"}；extractor {manifest.Extractor ?? "未知"}／{manifest.ExtractorVersion ?? "未知"}；"
            + $"tableHash {manifest.TableHash}（僅綁定表身，非防偽）";
    }

    /// <summary>重新驗證直接傳入的 model，消費端不可假設它曾經 Load。</summary>
    public static string? ValidateForConsumption(KnowledgePack pack, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? invalid = Validate(pack, cancellationToken);
        if (invalid is not null) return invalid;
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(ComputeSerial(pack.PackVersion, pack.GeneratedAt, pack.Tables, pack.PreviousSerial),
                pack.SerialNumber, StringComparison.OrdinalIgnoreCase)) return "serialNumber 不符";
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>敏感清單只輸出結構化來源狀態及表指紋，不抄錄自由文字 metadata。</summary>
    public static string DescribePrivateProvenance(KnowledgeTable table)
    {
        var manifest = table.SourceManifest;
        bool complete = manifest is not null && !string.IsNullOrWhiteSpace(manifest.Document)
            && (!string.IsNullOrWhiteSpace(manifest.Revision) || !string.IsNullOrWhiteSpace(manifest.Commit))
            && !string.IsNullOrWhiteSpace(manifest.Extractor) && !string.IsNullOrWhiteSpace(manifest.ExtractorVersion);
        return (complete ? "來源 manifest 自述（未認證 upstream 或抽取過程）" : ProvenanceUnknown)
            + (manifest is null ? "" : string.Equals(ComputeTableHash(table), manifest.TableHash, StringComparison.OrdinalIgnoreCase)
                ? $"；tableHash {manifest.TableHash}（表身已核對，非防偽）"
                : "；tableHash 不符，表身未核對");
    }

    /// <summary>建一份包（含序列號）；<paramref name="previousSerial"/> 是前版序列號（版本演進，可空）。</summary>
    public static KnowledgePack Build(int packVersion, string generatedAt, IReadOnlyList<KnowledgeTable> tables,
        string? previousSerial = null) =>
        new(packVersion, generatedAt, tables, ComputeSerial(packVersion, generatedAt, tables, previousSerial),
            previousSerial);

    /// <summary>
    /// 版本演進（<c>kp.evolution.proc</c>）：以新內容建新版包，並<b>帶上前版的 serialNumber</b>——
    /// 新版覆蓋舊版時，舊版去了哪裡、新版從誰演進而來，機器查得到、驗得過（前版指標納入序列號雜湊）。
    /// </summary>
    public static KnowledgePack Evolve(KnowledgePack prior, int newPackVersion, string generatedAt,
        IReadOnlyList<KnowledgeTable> tables) =>
        Build(newPackVersion, generatedAt, tables, prior.SerialNumber);

    /// <summary>讀外掛包；缺檔回 (null, 沒有包), 格式／日期／序列號不合格回拒收原因。</summary>
    public static KnowledgePackLoad Load(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new KnowledgePackLoad(null, "沒有外掛知識包（檔不存在）——內建表繼續服役", null);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumFileBytes)
                return new(null, "知識包超過檔案大小預算", null);
            // Read at most the budget plus one: a file growing after Length was checked is also rejected.
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            while (buffer.Length <= MaximumFileBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(chunk, 0, (int)Math.Min(chunk.Length, MaximumFileBytes + 1L - buffer.Length));
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length > MaximumFileBytes) return new(null, "知識包超過檔案大小預算", null);
            using var doc = JsonDocument.Parse(buffer.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            var root = doc.RootElement;
            string? declared = root.TryGetProperty("serialNumber", out var sn) ? sn.GetString() : null;
            if (string.IsNullOrWhiteSpace(declared))
                return new KnowledgePackLoad(null, "包缺 serialNumber——完整性無從宣告，整包拒收", null);
            var pack = JsonSerializer.Deserialize<KnowledgePack>(root.GetRawText());
            if (pack is null) return new KnowledgePackLoad(null, "包解析失敗", declared);
            string? invalid = Validate(pack, cancellationToken);
            if (invalid is not null) return new(null, invalid, declared);
            cancellationToken.ThrowIfCancellationRequested();
            string expected = ComputeSerial(pack.PackVersion, pack.GeneratedAt, pack.Tables, pack.PreviousSerial);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(expected, declared, StringComparison.OrdinalIgnoreCase))
                return new KnowledgePackLoad(null,
                    $"serialNumber 不符（宣告 {declared}／重算 {expected}）——檔案可能被改動或截斷，整包拒收", declared);
            return new KnowledgePackLoad(pack, null, declared);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new KnowledgePackLoad(null, $"包解析失敗（{ex.GetType().Name}）：{ex.Message}", null);
        }
    }

    private static string? Validate(KnowledgePack pack, CancellationToken cancellationToken = default)
    {
        if (pack.PackVersion <= 0) return "packVersion 必須為正整數";
        if (pack.GeneratedAt?.Length > 100 || pack.SerialNumber?.Length > 100
            || pack.PreviousSerial?.Length > 100) return "知識包標頭超過欄位預算";
        if (string.IsNullOrWhiteSpace(pack.SerialNumber)) return "包缺 serialNumber";
        if (pack.Tables?.Any(t => t is not null && string.IsNullOrWhiteSpace(t.SnapshotDate)) == true)
            return "表缺少 snapshotDate——沒有快照日期的知識表不能載入";
        if (!DateTimeOffset.TryParseExact(pack.GeneratedAt,
                ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                    "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var generated))
            return "generatedAt 必須為 ISO 日期或含時區時間";
        if (pack.Tables is null || pack.Tables.Count is 0 or > MaximumTables)
            return "知識包 tables 缺少或超過表數預算";
        int entries = 0;
        long characters = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in pack.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table is null || string.IsNullOrWhiteSpace(table.Table) || table.Table.Length > MaximumFieldCharacters || !names.Add(table.Table))
                return "知識包表名缺少或重複";
            if (!DateOnly.TryParseExact(table.SnapshotDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var snapshot) || snapshot > DateOnly.FromDateTime(generated.UtcDateTime))
                return "snapshotDate 無效或晚於 generatedAt";
            if (table.Entries is null || table.Entries.Count > MaximumEntries - entries)
                return "知識包 entries 缺少或超過條目預算";
            entries += table.Entries.Count;
            if (table.Entries.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Value is null
                    || e.Key.Length > MaximumFieldCharacters || e.Value.Length > MaximumFieldCharacters))
                return "知識包條目鍵或值缺少或超過欄位預算";
            characters += table.Entries.Sum(e => (long)e.Key.Length + e.Value.Length);
            if (characters > MaximumFileBytes) return "知識包內容超過字元預算";
            if (table.SourceManifest is { } manifest)
            {
                if (new[] { manifest.Document, manifest.Revision, manifest.Commit, manifest.Extractor,
                        manifest.ExtractorVersion, manifest.TableHash }.Any(value => value?.Length > 2048))
                    return "sourceManifest 欄位超過長度預算";
            }
        }
        // The stream overload flushes its bounded serializer buffer as it writes;
        // escaping and JSON structure count without materializing the entire output.
        try
        {
            using var budget = new JsonBudgetStream(MaximumFileBytes, cancellationToken);
            JsonSerializer.Serialize(budget, pack);
        }
        catch (KnowledgePackBudgetException) { return "知識包超過 JSON UTF-8 大小預算"; }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var table in pack.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.SourceManifest is { } manifest
                && !string.Equals(ComputeTableHash(table), manifest.TableHash, StringComparison.OrdinalIgnoreCase))
                return "sourceManifest tableHash 與表身不符";
            cancellationToken.ThrowIfCancellationRequested();
        }
        return null;
    }

    private sealed class KnowledgePackBudgetException : IOException { }

    private sealed class JsonBudgetStream(long limit, CancellationToken cancellationToken) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length > limit - _written) throw new KnowledgePackBudgetException();
            _written += buffer.Length;
        }
        public override void Flush() => cancellationToken.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>本機收集：knowledge.pack.status／tables 兩把固定鍵。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string? path = null)
    {
        path ??= DefaultPath;
        return CollectLoaded(at, Load(path));
    }

    /// <summary>以同一次載入結果產生事實；不再次開檔。呼叫端保留載入與驗證責任。</summary>
    internal static IReadOnlyList<HardwareFact> CollectLoaded(DateTimeOffset at, KnowledgePackLoad loaded)
    {
        if (loaded.Pack is null)
            return
            [
                new HardwareFact(StatusKey, Category, "知識包載入",
                    "內建表（未使用外掛包）——" + BuiltinSnapshot, "表", Source,
                    FactTrustLevel.Reported, false, at, null, FactAvailability.NotSupported, loaded.Reason),
            ];

        var pack = loaded.Pack;
        string tableText = string.Join("；", pack.Tables.Select(t =>
            $"{t.Table}（{t.Entries.Count} 條，快照 {t.SnapshotDate}）・{DescribeProvenance(t)}"));
        return
        [
            new HardwareFact(StatusKey, Category, "知識包載入",
                $"第 {pack.PackVersion} 版包已載入（生成 {pack.GeneratedAt}／serial {pack.SerialNumber[..Math.Min(18, pack.SerialNumber.Length)]}…）"
                + $"・完整性＝由內容雜湊推導，同一組輸入得同一份文件，非來源認證；逐表來源見知識包內容・沒有更新通道：內建表過期照樣服役並如實声明",
                "表", Source, FactTrustLevel.Measured, false, at, pack.Tables.Count),
            new HardwareFact(TablesKey, Category, "知識包內容", tableText, "張", Source,
                FactTrustLevel.Measured, false, at, pack.Tables.Count),
        ];
    }

    /// <summary>換包動作進審計鏈（明示觸發；append-only、雜湊鏈可驗——IN 基線同款機制）。</summary>
    public static bool RecordSwap(string packPath, string auditPath, out string reason)
    {
        var loaded = Load(packPath);
        if (loaded.Pack is null)
        {
            reason = "包不合格，不記錄換包：" + loaded.Reason;
            return false;
        }
        try
        {
            AuditLogService.Mutate(auditPath, entries =>
            {
                entries.Add(AuditLogService.Append(entries, AuditLogService.CurrentOperator(), "",
                    "knowledge-pack-swap", Path.GetFileName(packPath),
                    $"第 {loaded.Pack.PackVersion} 版／{loaded.Pack.Tables.Count} 張表", loaded.Pack.SerialNumber,
                    DateTimeOffset.UtcNow));
                return entries;
            });
            reason = "換包已記進審計鏈（append-only；--verify-audit 可驗）";
            return true;
        }
        catch (Exception ex)
        {
            reason = "審計寫入失敗：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 撤回聲明（<c>kp.evolution.proc</c>）：知識包撤回進審計鏈。
    ///
    /// <para>
    /// <b>撤回聲明的機器形狀</b>＝一筆 <see cref="AuditEntry"/>：<c>Action</c> 為
    /// <c>knowledge-pack-withdraw</c>、<c>Scope</c> 為被撤回包的完整 serialNumber、
    /// <c>ResultSummary</c> 為撤回理由（人讀欄目比照 work/WITHDRAWN-P2-052-FIT.md 的
    /// 徵狀／處置／理由）。<b>撤回後載入行為</b>：包檔移除之後，<see cref="Load"/>
    /// 如實回「沒有外掛知識包——內建表繼續服役」，不假裝還有舊版可用。
    /// </para>
    /// </summary>
    public static bool RecordWithdrawal(string serialNumber, string reason, string auditPath, out string withdrawal)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(reason))
        {
            withdrawal = "撤回聲明缺 serialNumber 或理由——兩者缺一不成聲明，不記錄";
            return false;
        }
        try
        {
            AuditLogService.Mutate(auditPath, entries =>
            {
                entries.Add(AuditLogService.Append(entries, AuditLogService.CurrentOperator(), "",
                    "knowledge-pack-withdraw", serialNumber, reason, "", DateTimeOffset.UtcNow));
                return entries;
            });
            withdrawal = "撤回聲明已記進審計鏈（append-only；--verify-audit 可驗）。撤回後移除包檔，"
                       + "載入層即如實回「沒有外掛知識包——內建表繼續服役」。";
            return true;
        }
        catch (Exception ex)
        {
            withdrawal = "審計寫入失敗：" + ex.Message;
            return false;
        }
    }
}
