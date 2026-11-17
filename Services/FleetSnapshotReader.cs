using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 從本機快照目錄讀出「族群基線」需要的投影（U10 第三圈 EN-0001 的生產端）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只讀本地目錄，不連網。</b>這是候選項的硬條件，也是這條路徑唯一可能洩漏資料的地方——
/// 所以它連任何 HTTP 用戶端型別都不引用。
/// </para>
/// <para>
/// <b>型號怎麼取：</b>從快照的 Present 系統製造商及系統型號事實取值，
/// 取出「廠商」「型號」兩段再正規化。找不到就不猜——那一份快照不進族群。
/// </para>
/// <para>
/// <b>為什麼不直接用檔案內容當比對依據：</b>快照的 <c>Facts</c> 有一千多筆，
/// 但族群比對只該用「每一份都有數值」的那一小部分。這裡只挑
/// <see cref="ComparableKeyPrefixes"/> 前綴下的鍵（溫度、電壓、時脈這類穩定量測），
/// 不把「今天插了幾支 USB」也算進去——那會讓差異全部來自使用習慣。
/// </para>
/// </remarks>
public static class FleetSnapshotReader
{
    /// <summary>
    /// 參與族群比對的鍵前綴。<b>刻意限縮</b>：只有穩定量測才適合跨機比。
    /// </summary>
    /// <remarks>
    /// 不包含：裝置清單／事件計數／容量／時間戳——那些反映「怎麼用」而不是「這台是什麼」。
    /// </remarks>
    public static readonly string[] ComparableKeyPrefixes = ["smbios.", "sensor.", "spd.", "cpu."];

    /// <summary>快照目錄（與 <see cref="PlatformGovernanceFactsService"/> 同一條慣例）。</summary>
    public static string Directory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect", "snapshots");

    /// <summary>最多讀幾份（避免一次掃幾百個檔案）。</summary>
    public const int MaxSnapshots = 200;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxDirectoryEntries = 1000;

    /// <summary>Read diagnostics remain attached even through the IReadOnlyList interface.</summary>
    public static FleetReadResult ReadForThisMachine() => Read(Directory);

    public const long MaxTotalBytes = 16 * 1024 * 1024;

    public static FleetReadResult Read(string directory)
    {
        var list = new List<FleetSnapshot>();
        var diagnostics = new List<string>();
        long remaining = MaxTotalBytes;
        try
        {
            var candidates = System.IO.Directory.EnumerateFiles(directory, "*.json")
                .Take(MaxDirectoryEntries + 1).ToArray();
            if (candidates.Length > MaxDirectoryEntries)
                return new([], ["DirectoryEntryBudgetExceeded"]);
            if (candidates.Length > MaxSnapshots) diagnostics.Add("SnapshotCountBudgetExceeded");
            foreach (string file in candidates.OrderByDescending(f => f, StringComparer.Ordinal).Take(MaxSnapshots))
            {
                if (remaining <= 0) { diagnostics.Add("AggregateByteBudgetExceeded"); break; }
                try
                {
                    var snapshot = Project(file, ref remaining);
                    if (snapshot is not null) list.Add(snapshot);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    diagnostics.Add(ex is InvalidDataException ? ex.Message : "SnapshotReadError");
                }
            }
        }
        catch (System.IO.DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { diagnostics.Add("DirectoryReadError"); }
        return new(list, diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static FleetSnapshot? Project(string file, ref long remaining)
    {
            using var stream = System.IO.File.OpenRead(file);
            if (stream.Length > MaxFileBytes) throw new InvalidDataException("FileByteBudgetExceeded");
            if (stream.Length > remaining) throw new InvalidDataException("AggregateByteBudgetExceeded");
            byte[] bytes = new byte[(int)Math.Min(MaxFileBytes, remaining) + 1];
            int length = 0, read;
            while (length < bytes.Length && (read = stream.Read(bytes, length, bytes.Length - length)) > 0) length += read;
            remaining -= length;
            if (length > MaxFileBytes) throw new InvalidDataException("FileByteBudgetExceeded");
            if (remaining < 0) throw new InvalidDataException("AggregateByteBudgetExceeded");
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, length));
            var root = doc.RootElement;
            if (!root.TryGetProperty("facts", out var factsEl) || factsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("SnapshotFactsMissing");

            string machineId = root.TryGetProperty("anonymousMachineId", out var idEl)
                ? idEl.GetString() ?? "（未提供）" : "（未提供）";
            DateTimeOffset captured = root.TryGetProperty("capturedAtUtc", out var atEl)
                && atEl.TryGetDateTimeOffset(out var parsed) ? parsed : DateTimeOffset.MinValue;

            string? modelText = null, manufacturer = null, model = null;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            var metadata = new Dictionary<string, FleetFactMetadata>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool manufacturerSeen = false, modelSeen = false;
            foreach (var f in factsEl.EnumerateArray())
            {
                string? key = f.TryGetProperty("key", out var kEl) ? kEl.GetString() : null;
                if (key is null) continue;
                if (!seen.Add(key)) throw new InvalidDataException("DuplicateFactKey");
                // Older Fleet-only JSON projections used Chinese labels as keys, which the
                // persisted snapshot validator rejects. Accept those explicit legacy aliases
                // here only, but never pick between two keys for the same model field.
                bool isManufacturer = key is "asset.field.system_manufacturer" or "asset.field.系統製造商";
                bool isModel = key is "asset.field.system_model" or "asset.field.系統型號";
                if ((isManufacturer && manufacturerSeen) || (isModel && modelSeen))
                    throw new InvalidDataException("AmbiguousModelField");
                manufacturerSeen |= isManufacturer;
                modelSeen |= isModel;
                FleetFactMetadata? meta = ReadMetadata(f);

                if (meta is { Availability: FactAvailability.Present, Trust: not FactTrustLevel.Unknown and not FactTrustLevel.Speculative }
                    && f.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.String)
                {
                    if (isManufacturer) manufacturer = vEl.GetString();
                    if (isModel) model = vEl.GetString();
                }

                if (!ComparableKeyPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) continue;
                if (f.TryGetProperty("numericValue", out var nEl) && nEl.ValueKind == JsonValueKind.Number
                    && nEl.TryGetDouble(out double d) && double.IsFinite(d))
                    {
                    values[key] = d;
                    if (meta is not null) metadata[key] = meta;
                }
            }

            if (!string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(model))
                modelText = manufacturer.Trim() + " | " + model.Trim();
            return new FleetSnapshot(machineId, captured, NormalizeModel(modelText), modelText, values, metadata);
    }

    private static FleetFactMetadata? ReadMetadata(JsonElement fact)
    {
        // HardwareSnapshotFact omits its default Present value on disk. Only an
        // absent property has that default; explicit null/invalid tokens do not.
        FactAvailability state = FactAvailability.Present;
        if (fact.TryGetProperty("availability", out var availability)
            && (availability.ValueKind != JsonValueKind.String
                || !Enum.IsDefined(typeof(FactAvailability), availability.GetString()!)
                || !Enum.TryParse(availability.GetString(), out state))) return null;
        if (!fact.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.String
            || !fact.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(source.GetString())
            || !fact.TryGetProperty("trust", out var trust)
            || !Enum.TryParse<FactTrustLevel>(trust.ToString(), out var level) || !Enum.IsDefined(level)) return null;
        return new(unit.GetString()!, state, level, source.GetString()!);
    }

    /// <summary>
    /// 把製造商及型號正規化成族群鍵，保留標點及文字身份，
    /// 壓成大寫、去重複空白。
    /// </summary>
    /// <remarks>
    /// <b>為什麼不直接用原文：</b>同一台機器在不同版本的快照裡，製造商與型號的
    /// 排版可能多一個空白或全形／半形差異——那會讓「同型號」變成兩個族群。
    /// 正規化只處理<b>排版</b>，不動任何語意。
    /// </remarks>
    public static string? NormalizeModel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = Regex.Replace(raw.Normalize(System.Text.NormalizationForm.FormKC), @"\s+", " ").Trim().ToUpperInvariant();
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>Bounded import outcome; missing directory differs from rejected or unreadable input.</summary>
public sealed class FleetReadResult(IReadOnlyList<FleetSnapshot> snapshots, IReadOnlyList<string> diagnostics)
    : IReadOnlyList<FleetSnapshot>
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
    public int Count => snapshots.Count;
    public FleetSnapshot this[int index] => snapshots[index];
    public IEnumerator<FleetSnapshot> GetEnumerator() => snapshots.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
