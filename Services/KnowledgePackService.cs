using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>離線知識包的一張表：表名、快照日期、條目（鍵→值）。</summary>
public sealed record KnowledgeTable(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("snapshotDate")] string SnapshotDate,
    [property: JsonPropertyName("entries")] IReadOnlyDictionary<string, string> Entries);

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
            .Select(t => new
            {
                table = t.Table,
                snapshotDate = t.SnapshotDate,
                entries = new Dictionary<string, string>(
                    t.Entries.OrderBy(e => e.Key, StringComparer.Ordinal), StringComparer.Ordinal),
            })
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
    public static KnowledgePackLoad Load(string path)
    {
        if (!File.Exists(path)) return new KnowledgePackLoad(null, "沒有外掛知識包（檔不存在）——內建表繼續服役", null);
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? declared = root.TryGetProperty("serialNumber", out var sn) ? sn.GetString() : null;
            if (string.IsNullOrWhiteSpace(declared))
                return new KnowledgePackLoad(null, "包缺 serialNumber——完整性無從宣告，整包拒收", null);
            var pack = JsonSerializer.Deserialize<KnowledgePack>(root.GetRawText());
            if (pack is null) return new KnowledgePackLoad(null, "包解析失敗", declared);
            foreach (var t in pack.Tables)
                if (string.IsNullOrWhiteSpace(t.SnapshotDate))
                    return new KnowledgePackLoad(null,
                        $"表「{t.Table}」缺少 snapshotDate——沒有快照日期的知識表不能載入（讀者無從判斷它多舊）", declared);
            string expected = ComputeSerial(pack.PackVersion, pack.GeneratedAt, pack.Tables, pack.PreviousSerial);
            if (!string.Equals(expected, declared, StringComparison.OrdinalIgnoreCase))
                return new KnowledgePackLoad(null,
                    $"serialNumber 不符（宣告 {declared}／重算 {expected}）——檔案可能被改動或截斷，整包拒收", declared);
            return new KnowledgePackLoad(pack, null, declared);
        }
        catch (Exception ex)
        {
            return new KnowledgePackLoad(null, $"包解析失敗（{ex.GetType().Name}）：{ex.Message}", null);
        }
    }

    /// <summary>本機收集：knowledge.pack.status／tables 兩把固定鍵。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string? path = null)
    {
        path ??= DefaultPath;
        var loaded = Load(path);
        if (loaded.Pack is null)
            return
            [
                new HardwareFact(StatusKey, Category, "知識包載入",
                    "內建表（未使用外掛包）——" + BuiltinSnapshot, "表", Source,
                    FactTrustLevel.Reported, false, at, null, FactAvailability.NotSupported, loaded.Reason),
            ];

        var pack = loaded.Pack;
        string tableText = string.Join("；", pack.Tables.Select(t =>
            $"{t.Table}（{t.Entries.Count} 條，快照 {t.SnapshotDate}）"));
        return
        [
            new HardwareFact(StatusKey, Category, "知識包載入",
                $"第 {pack.PackVersion} 版包已載入（生成 {pack.GeneratedAt}／serial {pack.SerialNumber[..Math.Min(18, pack.SerialNumber.Length)]}…）"
                + $"・完整性＝由內容雜湊推導，同一組輸入得同一份文件・沒有更新通道：內建表過期照樣服役並如實声明",
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
            var entries = AuditLogService.Load(auditPath);
            var entry = AuditLogService.Append(entries, AuditLogService.CurrentOperator(), "",
                "knowledge-pack-swap", Path.GetFileName(packPath),
                $"第 {loaded.Pack.PackVersion} 版／{loaded.Pack.Tables.Count} 張表", loaded.Pack.SerialNumber,
                DateTimeOffset.UtcNow);
            entries.Add(entry);
            AuditLogService.Save(auditPath, entries);
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
            var entries = AuditLogService.Load(auditPath);
            entries.Add(AuditLogService.Append(entries, AuditLogService.CurrentOperator(), "",
                "knowledge-pack-withdraw", serialNumber, reason, "", DateTimeOffset.UtcNow));
            AuditLogService.Save(auditPath, entries);
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
