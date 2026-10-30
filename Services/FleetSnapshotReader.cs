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
/// <b>型號怎麼取：</b>從快照的事實裡找 <c>asset.identify</c>（本專案的資產識別鍵）的值，
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

    /// <summary>讀本機快照目錄。目錄不存在或讀不到就回空清單（不是例外）。</summary>
    public static IReadOnlyList<FleetSnapshot> ReadForThisMachine() => Read(Directory);

    public static IReadOnlyList<FleetSnapshot> Read(string directory)
    {
        var list = new List<FleetSnapshot>();
        try
        {
            if (!System.IO.Directory.Exists(directory)) return list;
            foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*.json")
                         .OrderByDescending(f => f, StringComparer.Ordinal).Take(MaxSnapshots))
            {
                var snap = Project(file);
                if (snap is not null) list.Add(snap);
            }
        }
        catch
        {
            // 目錄不可讀（權限、損毀）——回空清單，由服務標「沒有快照」。
        }
        return list;
    }

    private static FleetSnapshot? Project(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("facts", out var factsEl) || factsEl.ValueKind != JsonValueKind.Array)
                return null;

            string machineId = root.TryGetProperty("anonymousMachineId", out var idEl)
                ? idEl.GetString() ?? "（未提供）" : "（未提供）";
            DateTimeOffset captured = root.TryGetProperty("capturedAtUtc", out var atEl)
                && atEl.TryGetDateTimeOffset(out var parsed) ? parsed : DateTimeOffset.MinValue;

            string? modelText = null;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var f in factsEl.EnumerateArray())
            {
                string? key = f.TryGetProperty("key", out var kEl) ? kEl.GetString() : null;
                if (key is null) continue;

                if (key == "asset.identify" && modelText is null)
                    modelText = f.TryGetProperty("value", out var vEl) ? vEl.GetString() : null;

                if (!ComparableKeyPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) continue;
                if (f.TryGetProperty("numericValue", out var nEl) && nEl.ValueKind == JsonValueKind.Number
                    && nEl.TryGetDouble(out double d))
                    values[key] = d;
            }

            return new FleetSnapshot(machineId, captured, NormalizeModel(modelText), modelText, values);
        }
        catch
        {
            return null; // 單一檔案壞掉不影響其他份
        }
    }

    /// <summary>
    /// 把 <c>asset.identify</c> 的值正規化成族群鍵：只留英數與 <c>-</c>／<c>_</c>，
    /// 壓成大寫、去重複空白。
    /// </summary>
    /// <remarks>
    /// <b>為什麼不直接用原文：</b>同一台機器在不同版本的快照裡，<c>asset.identify</c> 的
    /// 排版可能多一個空白或全形／半形差異——那會讓「同型號」變成兩個族群。
    /// 正規化只處理<b>排版</b>，不動任何語意。
    /// </remarks>
    public static string? NormalizeModel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = Regex.Replace(raw, @"\s+", " ").Trim().ToUpperInvariant();
        s = Regex.Replace(s, @"[^0-9A-Z\-_ ]", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }
}
