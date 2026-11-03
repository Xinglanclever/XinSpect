using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>離線 Redfish 目錄的 BMC／機箱／系統摘要。</summary>
public sealed record RedfishManager(string Id, string? Model, string? FirmwareVersion, string? ManagerType, string? Status);
public sealed record RedfishChassis(string Id, string? Type, string? SerialNumber, string? PowerState, string? Intrusion);
public sealed record RedfishSystem(string Id, string? Manufacturer, string? Model, string? SerialNumber, string? BiosVersion, string? PowerState);
public sealed record RedfishPsu(string Id, string? Model, string? FirmwareVersion, double? OutputWatts, double? InputVoltage, string? Status);
public sealed record RedfishFan(string Id, double? ReadingRpm, string? Status);
public sealed record RedfishTemperature(string Id, double? ReadingCelsius, double? UpperCritical, string? Status);

public sealed record RedfishInventory(
    string Directory,
    DateTimeOffset? CollectedAtUtc,
    IReadOnlyList<RedfishManager> Managers,
    IReadOnlyList<RedfishChassis> Chassis,
    IReadOnlyList<RedfishSystem> Systems,
    IReadOnlyList<RedfishPsu> PowerSupplies,
    IReadOnlyList<RedfishFan> Fans,
    IReadOnlyList<RedfishTemperature> Temperatures,
    double? PowerConsumedWatts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 讀取<b>已經匯出到本機</b>的 Redfish 目錄樹，產生 BMC／機箱／電源／風扇／溫度的唯讀摘要。
/// </summary>
/// <remarks>
/// <para>
/// <b>不連網。</b>本服務沒有任何 HTTP 用戶端；它只把 <c>redfish/v1/...</c> 的 JSON 檔當成本機資料讀。
/// 目錄裡的 <c>@odata.id</c> 若是外部 URL，一律拒絕並記警告——離線包不該把讀者帶去連網。
/// </para>
/// <para>
/// <b>不臆測。</b>缺少的集合、壞掉的 JSON、路徑穿越、超過資源上限都回警告與空清單；
/// 不把「沒讀到」寫成 0，也不把 BMC 的自我宣告當成本工具量到的值。
/// </para>
/// </remarks>
public static class RedfishBundleService
{
    public const int SchemaVersion = 1;
    public const string Source = "離線 Redfish 目錄（唯讀；不連網）";
    private const int MaxResources = 128;
    private const long MaxFileBytes = 8 * 1024 * 1024;

    public static RedfishInventory Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root;
        try { root = Path.GetFullPath(directory); }
        catch (Exception ex) { return Empty(directory, "目錄路徑無法解析：" + ex.Message); }
        if (!Directory.Exists(root)) return Empty(directory, "目錄不存在：" + directory);

        var reader = new BundleReader(root);
        var serviceRoot = reader.Load("/redfish/v1", "ServiceRoot");
        if (serviceRoot is null)
            return Empty(directory, "找不到 redfish/v1/index.json（ServiceRoot）——這不是可讀的離線 Redfish 目錄。");

        var managers = new List<RedfishManager>();
        var chassis = new List<RedfishChassis>();
        var systems = new List<RedfishSystem>();
        var psus = new List<RedfishPsu>();
        var fans = new List<RedfishFan>();
        var temps = new List<RedfishTemperature>();
        double? powerConsumed = null;
        DateTimeOffset? collected = null;
        if (serviceRoot.Value.TryGetProperty("GeneratedAtUtc", out var gen)
            && gen.ValueKind == JsonValueKind.String
            && gen.TryGetDateTimeOffset(out var parsed)) collected = parsed;

        foreach (var item in reader.Members(serviceRoot.Value, "Managers", "Managers"))
        {
            string id = IdOf(item, "Managers");
            managers.Add(new RedfishManager(id,
                Text(item, "Model"), Text(item, "FirmwareVersion"), Text(item, "ManagerType"), Status(item)));
        }

        foreach (var item in reader.Members(serviceRoot.Value, "Chassis", "Chassis"))
        {
            string id = IdOf(item, "Chassis");
            string? intrusion = item.TryGetProperty("PhysicalSecurity", out var ps) && ps.ValueKind == JsonValueKind.Object
                ? Text(ps, "IntrusionSensor") : null;
            chassis.Add(new RedfishChassis(id, Text(item, "ChassisType"), Text(item, "SerialNumber"),
                Text(item, "PowerState"), intrusion));

            if (reader.Follow(item, "Thermal", "Chassis.Thermal") is { } thermal)
                ReadThermal(thermal, id, fans, temps, reader);
            if (reader.Follow(item, "Power", "Chassis.Power") is { } power)
                ReadPower(power, id, psus, ref powerConsumed, reader);
        }

        foreach (var item in reader.Members(serviceRoot.Value, "Systems", "Systems"))
        {
            string id = IdOf(item, "Systems");
            systems.Add(new RedfishSystem(id, Text(item, "Manufacturer"), Text(item, "Model"),
                Text(item, "SerialNumber"), Text(item, "BiosVersion"), Text(item, "PowerState")));
        }

        return new RedfishInventory(directory, collected, managers, chassis, systems, psus, fans, temps,
            powerConsumed, reader.Warnings);
    }

    private static void ReadThermal(JsonElement thermal, string chassisId, List<RedfishFan> fans,
        List<RedfishTemperature> temps, BundleReader reader)
    {
        if (thermal.TryGetProperty("Temperatures", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in t.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                temps.Add(new RedfishTemperature(
                    chassisId + "/" + (Text(item, "Name") ?? "Temperature"),
                    Number(item, "ReadingCelsius"), Number(item, "UpperCritical"), Status(item)));
            }
        }
        if (thermal.TryGetProperty("Fans", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in f.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                fans.Add(new RedfishFan(
                    chassisId + "/" + (Text(item, "Name") ?? "Fan"),
                    Number(item, "Reading") ?? Number(item, "ReadingRpm"), Status(item)));
            }
        }
    }

    private static void ReadPower(JsonElement power, string chassisId, List<RedfishPsu> psus,
        ref double? powerConsumed, BundleReader reader)
    {
        if (power.TryGetProperty("PowerSupplies", out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in p.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                psus.Add(new RedfishPsu(
                    chassisId + "/" + (Text(item, "Name") ?? Text(item, "MemberId") ?? "PSU"),
                    Text(item, "Model"), Text(item, "FirmwareVersion"),
                    Number(item, "PowerOutputWatts"), Number(item, "LineInputVoltage"), Status(item)));
            }
        }
        if (power.TryGetProperty("PowerControl", out var pc) && pc.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pc.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                powerConsumed ??= Number(item, "PowerConsumedWatts");
                if (powerConsumed is not null) break;
            }
        }
    }

    private static string IdOf(JsonElement item, string fallback)
        => Text(item, "Id") ?? Text(item, "Name") ?? fallback;

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? Number(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;

    private static string? Status(JsonElement item)
    {
        if (!item.TryGetProperty("Status", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        string? state = Text(s, "State");
        string? health = Text(s, "Health");
        return state is null && health is null ? null
            : state is null ? health
            : health is null ? state
            : $"{state}／{health}";
    }

    private static RedfishInventory Empty(string directory, string warning)
        => new(directory, null, [], [], [], [], [], [], null, [warning]);

    private sealed class BundleReader
    {
        private readonly string _root;
        private int _loaded;
        public List<string> Warnings { get; } = [];

        public BundleReader(string root) => _root = root;

        public JsonElement? Load(string odataId, string role)
        {
            if (_loaded >= MaxResources)
            {
                AddWarning($"資源上限 {MaxResources} 已達，略過 {role}：{odataId}");
                return null;
            }
            if (!TryMap(odataId, out string? path, out string? reason))
            {
                AddWarning($"{role} 的 @odata.id 被拒絕：{odataId}（{reason}）");
                return null;
            }
            try
            {
                var info = new FileInfo(path!);
                if (!info.Exists)
                {
                    AddWarning($"{role} 找不到檔案：{odataId}");
                    return null;
                }
                if (info.Length > MaxFileBytes)
                {
                    AddWarning($"{role} 檔案超過 {MaxFileBytes} bytes：{odataId}");
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(path!));
                _loaded++;
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                AddWarning($"{role} 讀取失敗：{odataId}（{ex.GetType().Name}）");
                return null;
            }
        }

        public IEnumerable<JsonElement> Members(JsonElement parent, string property, string role)
        {
            if (!parent.TryGetProperty(property, out var link))
                yield break;
            JsonElement? collection = Resolve(link, role);
            if (collection is null) yield break;
            if (!collection.Value.TryGetProperty("Members", out var members)
                || members.ValueKind != JsonValueKind.Array)
            {
                AddWarning($"{role} 集合沒有 Members 陣列");
                yield break;
            }
            foreach (var member in members.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.Object) continue;
                JsonElement? resolved = Resolve(member, role + " member");
                if (resolved is { } item) yield return item;
            }
        }

        public JsonElement? Follow(JsonElement parent, string property, string role)
        {
            if (!parent.TryGetProperty(property, out var link)) return null;
            return Resolve(link, role);
        }

        private JsonElement? Resolve(JsonElement element, string role)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("@odata.id", out var id)
                && id.ValueKind == JsonValueKind.String)
                return Load(id.GetString()!, role);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }

        private bool TryMap(string odataId, out string? path, out string? reason)
        {
            path = null; reason = null;
            if (odataId.Contains("://", StringComparison.Ordinal))
            {
                reason = "外部 URL 不追（離線包不連網）";
                return false;
            }
            string rel = odataId.Replace('\\', '/').Trim('/');
            if (rel.Length == 0) { reason = "空路徑"; return false; }
            if (rel.Split('/').Any(s => s is "" or "." or ".."))
            {
                reason = "路徑穿越";
                return false;
            }
            // 只接受 redfish 樹下的資源，避免讀到目錄外的檔案。
            if (!rel.StartsWith("redfish/", StringComparison.Ordinal))
            {
                reason = "不在 redfish/ 樹下";
                return false;
            }
            foreach (string candidate in new[]
                     {
                         Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar) + ".json"),
                         Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar), "index.json"),
                     })
            {
                string full = Path.GetFullPath(candidate);
                if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "解析後路徑跑到目錄外";
                    return false;
                }
                if (File.Exists(full)) { path = full; return true; }
            }
            reason = "找不到對應的 .json 或 index.json";
            return false;
        }

        private void AddWarning(string text)
        {
            if (Warnings.Count < 64) Warnings.Add(text);
        }
    }
}