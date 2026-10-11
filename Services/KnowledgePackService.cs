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
public sealed record KnowledgePack(
    [property: JsonPropertyName("packVersion")] int PackVersion,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("tables")] IReadOnlyList<KnowledgeTable> Tables,
    [property: JsonPropertyName("serialNumber")] string SerialNumber);

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
    public const string Category = "知識與更新";
    public const string StatusKey = "knowledge.pack.status";
    public const string TablesKey = "knowledge.pack.tables";

    /// <summary>預設包檔名（與執行檔同目錄；不存在＝內建表）。</summary>
    public const string FileName = "knowledge-pack.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>內建表的快照聲明（本版打包時點；沒有更新通道，過期如實說）。</summary>
    public const string BuiltinSnapshot = "v2.66 內建（OuiKnowledge／bugcheck 子集；打包於 2026-10-11）";

    private const string Source = "內建表／knowledge-pack.json（離線）";

    /// <summary>產生 canonical 序列號：不含 serialNumber 欄位的 JSON 以 UTF-8 SHA-256。</summary>
    public static string ComputeSerial(int packVersion, string generatedAt, IReadOnlyList<KnowledgeTable> tables)
    {
        var payload = new
        {
            packVersion,
            generatedAt,
            tables = tables.OrderBy(t => t.Table, StringComparer.Ordinal)
                .Select(t => new
                {
                    table = t.Table,
                    snapshotDate = t.SnapshotDate,
                    entries = new Dictionary<string, string>(
                        t.Entries.OrderBy(e => e.Key, StringComparer.Ordinal), StringComparer.Ordinal),
                }),
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>建一份包（含序列號）。</summary>
    public static KnowledgePack Build(int packVersion, string generatedAt, IReadOnlyList<KnowledgeTable> tables) =>
        new(packVersion, generatedAt, tables, ComputeSerial(packVersion, generatedAt, tables));

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
            string expected = ComputeSerial(pack.PackVersion, pack.GeneratedAt, pack.Tables);
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
}
