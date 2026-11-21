using System.IO;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>離線 Redfish 目錄的 BMC／機箱／系統摘要。</summary>
public sealed record RedfishManager(string Id, string? Model, string? FirmwareVersion, string? ManagerType, string? Status);
public sealed record RedfishChassis(string Id, string? Type, string? SerialNumber, string? PowerState, string? Intrusion);
public sealed record RedfishSystem(string Id, string? Manufacturer, string? Model, string? SerialNumber, string? BiosVersion, string? PowerState);
public sealed record RedfishPsu(string Id, string? Model, string? FirmwareVersion, double? OutputWatts, double? InputVoltage, string? Status);
public sealed record RedfishFan(string Id, double? ReadingRpm, string? Status)
{
    public double? RawReading { get; init; }
    public string? ReadingUnits { get; init; }
}
public sealed record RedfishTemperature(string Id, double? ReadingCelsius, double? UpperCritical, string? Status);

/// <summary>A source observation, not a package total. Locations are established by the local reader.</summary>
public sealed record RedfishPowerControl(string ChassisResourceUri, string PowerResourceUri, int ArrayIndex,
    string? MemberId, string? MemberUri, string? Name, string? PhysicalContext, double? PowerConsumedWatts,
    string? UnavailableReason = null)
{
    internal bool ScopeResolved { get; init; }
    internal string ScopeIdentity => ChassisResourceUri + "\n" + PowerResourceUri + "\n" + ArrayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ObservationLocation => PowerResourceUri + (PowerResourceUri.Contains('#') ? "/" : "#/") + "PowerControl/" + ArrayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record RedfishPowerSelection(double? Watts, RedfishPowerControl? Observation, string Reason);

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
    IReadOnlyList<string> Warnings)
{
    // Preserve the existing positional constructor. A legacy scalar alone supplies no scope evidence.
    public IReadOnlyList<RedfishPowerControl> PowerControls { get; init; } = [];
    public bool PowerControlsComplete { get; init; }
    /// <summary>The reader rejected or could not finish some resources; counts describe only accepted data.</summary>
    public bool ReadIncomplete { get; init; }
    // Only the reader establishes a failed import. Legacy manually constructed inventories remain Unknown.
    internal FactAvailability? ReadFailureAvailability { get; init; }
}

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
    internal const int MaxLoadAttempts = 128;
    internal const int MaxFileBytes = 8 * 1024 * 1024;
    internal const long MaxTotalBytes = 64 * 1024 * 1024;

    // Limits count source bytes returned by Read, including BOM, malformed/repeated input and one overflow byte.
    // They are import policies, not process-memory limits or a hard I/O deadline.
    internal sealed record ReadLimits(int FileBytes = MaxFileBytes, long TotalBytes = MaxTotalBytes,
        int LoadAttempts = MaxLoadAttempts);
    internal const int MaxPowerControls = 256;
    internal const int MaxPowerMetadataLength = 256;

    public static RedfishPowerSelection SelectPowerConsumed(RedfishInventory inventory)
    {
        if (!inventory.PowerControlsComplete)
            return new(null, null, "電力控制來源範圍未完整解析；不以舊無身份 scalar 或首筆冒充總功耗");
        if (inventory.PowerControls.Count != 1)
            return new(null, null, inventory.PowerControls.Count == 0 ? "沒有電力控制讀數；未補 0"
                : "多筆電力控制觀測；保留逐筆範圍，不選首筆、不加總");
        var observation = inventory.PowerControls[0];
        if (!observation.ScopeResolved || !ValidObservationLocation(observation.ChassisResourceUri)
            || !ValidObservationLocation(observation.PowerResourceUri) || observation.ArrayIndex < 0)
            return new(null, null, "電力控制觀測沒有已解析的本機來源範圍");
        if (observation.UnavailableReason is not null || observation.PowerConsumedWatts is not { } watts
            || !double.IsFinite(watts) || watts < 0)
            return new(null, observation, observation.UnavailableReason ?? "PowerConsumedWatts 缺少或無效；未補 0");
        if (inventory.PowerConsumedWatts is { } legacy && (!double.IsFinite(legacy) || legacy != watts))
            return new(null, observation, "舊 scalar 與具範圍觀測矛盾；不選取任何一方");
        return new(watts, observation, "唯一已解析電力控制觀測；不是整包總功耗");
    }

    internal static bool ValidObservationLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Length > 1024 || !location.StartsWith("/redfish/", StringComparison.Ordinal)
            || location.IndexOfAny(['\\', ':', '%', '?']) >= 0 || location.Any(char.IsControl)) return false;
        string resource = location.Split('#')[0];
        if (resource.Split('/').Skip(1).Any(x => x is "" or "." or "..")) return false;
        int fragment = location.IndexOf('#');
        return fragment < 0 || location[(fragment + 1)..].StartsWith("/", StringComparison.Ordinal)
            && !location[(fragment + 1)..].Split('/').Any(x => x is "." or "..");
    }


    public static RedfishInventory Read(string directory) => Read(directory, new ReadLimits(), OpenResource);

    private static Stream OpenResource(string path) => new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        // Do not let FileStream prefetch source bytes beyond this reader's bounded requests.
        BufferSize = 1,
    });

    // Per-call seam: mapping/reparse checks still run before the opener. No mutable global test hook.
    internal static RedfishInventory Read(string directory, ReadLimits limits, Func<string, Stream> openResource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(openResource);
        if (limits.FileBytes is <= 0 or > MaxFileBytes || limits.TotalBytes is <= 0 or > MaxTotalBytes
            || limits.LoadAttempts is <= 0 or > MaxLoadAttempts)
            throw new ArgumentOutOfRangeException(nameof(limits));
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception ex) { return Empty(directory, "目錄路徑無法解析（" + ex.GetType().Name + "）；原始路徑不公開"); }
        if (!Directory.Exists(root)) return Empty(directory, "目錄不存在；原始路徑不公開");

        var reader = new BundleReader(root, limits, openResource);
        var serviceRoot = reader.Load("/redfish/v1", "ServiceRoot");
        if (serviceRoot is null)
        {
            reader.RootUnavailable();
            return new RedfishInventory(directory, null, [], [], [], [], [], [], null, reader.Warnings)
            { ReadIncomplete = reader.ReadIncomplete, ReadFailureAvailability = reader.ReadFailureAvailability };
        }

        var managers = new List<RedfishManager>();
        var chassis = new List<RedfishChassis>();
        var systems = new List<RedfishSystem>();
        var psus = new List<RedfishPsu>();
        var fans = new List<RedfishFan>();
        var temps = new List<RedfishTemperature>();
        var powerControls = new List<RedfishPowerControl>();
        DateTimeOffset? collected = null;
        if (serviceRoot.Value.TryGetProperty("GeneratedAtUtc", out var gen)
            && gen.ValueKind == JsonValueKind.String
            && gen.TryGetDateTimeOffset(out var parsed)) collected = parsed;

        foreach (var item in reader.Members(serviceRoot, "Managers", "Managers"))
        {
            string id = IdOf(item.Value, "Managers");
            managers.Add(new RedfishManager(id,
                Text(item.Value, "Model"), Text(item.Value, "FirmwareVersion"), Text(item.Value, "ManagerType"), Status(item.Value)));
        }

        foreach (var item in reader.Members(serviceRoot, "Chassis", "Chassis"))
        {
            string id = IdOf(item.Value, "Chassis");
            string? intrusion = item.Value.TryGetProperty("PhysicalSecurity", out var ps) && ps.ValueKind == JsonValueKind.Object
                ? Text(ps, "IntrusionSensor") : null;
            chassis.Add(new RedfishChassis(id, Text(item.Value, "ChassisType"), Text(item.Value, "SerialNumber"),
                Text(item.Value, "PowerState"), intrusion));

            if (reader.Follow(item, "Thermal", "Chassis.Thermal") is { } thermal)
                ReadThermal(thermal.Value, id, fans, temps, reader);
            if (reader.Follow(item, "Power", "Chassis.Power") is { } power)
                ReadPower(power, item.Location, id, psus, powerControls, reader);
        }

        foreach (var item in reader.Members(serviceRoot, "Systems", "Systems"))
        {
            string id = IdOf(item.Value, "Systems");
            systems.Add(new RedfishSystem(id, Text(item.Value, "Manufacturer"), Text(item.Value, "Model"),
                Text(item.Value, "SerialNumber"), Text(item.Value, "BiosVersion"), Text(item.Value, "PowerState")));
        }

        var inventory = new RedfishInventory(directory, collected, managers, chassis, systems, psus, fans, temps,
            null, reader.Warnings)
        {
            PowerControls = powerControls.ToArray(), PowerControlsComplete = reader.PowerControlsComplete,
            ReadIncomplete = reader.ReadIncomplete, ReadFailureAvailability = reader.ReadFailureAvailability,
        };
        return inventory with { PowerConsumedWatts = SelectPowerConsumed(inventory).Watts };
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
                    FanReadingRpm(item), Status(item))
                { RawReading = Number(item, "Reading"), ReadingUnits = Text(item, "ReadingUnits") });
            }
        }
    }

    private static void ReadPower(ResolvedResource resource, string chassisLocation, string chassisId,
        List<RedfishPsu> psus, List<RedfishPowerControl> observations, BundleReader reader)
    {
        JsonElement power = resource.Value;
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
        if (!power.TryGetProperty("PowerControl", out var controls))
        { reader.CheckCollection(power, "PowerControl", 0, "Chassis.Power"); return; }
        if (controls.ValueKind != JsonValueKind.Array)
        { reader.PowerWarning("PowerControl 不是陣列；電力控制來源不完整"); return; }
        reader.CheckCollection(power, "PowerControl", controls.GetArrayLength(), "Chassis.Power");
        int index = 0;
        foreach (var item in controls.EnumerateArray())
        {
            int ordinal = index++;
            if (observations.Count >= MaxPowerControls)
            { reader.PowerWarning($"PowerControl 觀測上限 {MaxPowerControls} 已達；不產生唯一 scalar"); break; }
            if (item.ValueKind != JsonValueKind.Object)
            { reader.PowerWarning("PowerControl 含非物件項目；電力控制來源不完整"); continue; }
            bool oversized = false;
            string? Metadata(string name)
            {
                string? value = Text(item, name);
                if (value is null || value.Length <= MaxPowerMetadataLength) return value;
                oversized = true;
                return null;
            }
            string? memberId = Metadata("MemberId"), memberUri = Metadata("@odata.id"), name = Metadata("Name"), context = Metadata("PhysicalContext");
            double? watts = Number(item, "PowerConsumedWatts");
            if (watts < 0) watts = null;
            string? reason = oversized ? "PowerControl 身份中繼資料超限；未截斷冒充身份"
                : watts is null ? "PowerConsumedWatts 缺少、null 或無效；未補 0" : null;
            if (oversized) reader.PowerWarning(reason!);
            observations.Add(new(chassisLocation, resource.Location, ordinal, memberId, memberUri, name, context, watts, reason)
            { ScopeResolved = true });
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

    private static double? FanReadingRpm(JsonElement obj)
    {
        if (obj.TryGetProperty("ReadingRpm", out var rpm))
            return rpm.ValueKind == JsonValueKind.Number && rpm.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
        if (!obj.TryGetProperty("Reading", out var reading)
            || reading.ValueKind != JsonValueKind.Number
            || !reading.TryGetDouble(out var d) || !double.IsFinite(d)) return null;
        string? unit = Text(obj, "ReadingUnits");
        return string.Equals(unit, "RPM", StringComparison.OrdinalIgnoreCase) ? d : null;
    }

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

    // Location is the successfully mapped local resource URI, never the payload's self-declared @odata.id.
    private sealed record ResolvedResource(JsonElement Value, string Location);

    private sealed class BundleReader
    {
        private readonly string _root;
        private readonly ReadLimits _limits;
        private readonly Func<string, Stream> _openResource;
        private int _loadAttempts;
        private long _returnedBytes;
        private bool _loadsStopped;
        public List<string> Warnings { get; } = [];
        public bool PowerControlsComplete { get; private set; } = true;
        public bool ReadIncomplete { get; private set; }
        public FactAvailability? ReadFailureAvailability { get; private set; }

        public BundleReader(string root, ReadLimits limits, Func<string, Stream> openResource)
            => (_root, _limits, _openResource) = (root, limits, openResource);

        private void Warn(string role, string text, FactAvailability failure = FactAvailability.ReadError,
            bool important = false)
        {
            ReadIncomplete = true;
            if (ReadFailureAvailability is null || failure == FactAvailability.ReadError)
                ReadFailureAvailability = failure;
            if (role == "Chassis" || role.StartsWith("Chassis member", StringComparison.Ordinal)
                || role.StartsWith("Chassis.Power", StringComparison.Ordinal)) PowerControlsComplete = false;
            AddWarning(text, important);
        }
        public void PowerWarning(string text) { PowerControlsComplete = false; AddWarning(text); }
        public void RootUnavailable()
            => AddWarning("找不到可讀的 redfish/v1/index.json（ServiceRoot）；保留原讀取警告，不代表沒有 BMC");

        private void StopLoads(string role, string code, string reason)
        {
            _loadsStopped = true;
            // Read visits Managers, then all Chassis/Power, then Systems. A later Systems stop cannot hide power.
            if (role != "Systems" && role != "Systems member") PowerControlsComplete = false;
            Warn(role, code + "：" + reason + "；來源讀取已停止，保留已讀部分", important: true);
        }

        public ResolvedResource? Load(string odataId, string role)
        {
            if (_loadsStopped) return null;
            if (_loadAttempts >= _limits.LoadAttempts)
            {
                StopLoads(role, "LoadAttemptBudgetExceeded", $"載入嘗試上限 {_limits.LoadAttempts} 已達");
                return null;
            }
            if (_returnedBytes >= _limits.TotalBytes)
            {
                StopLoads(role, "AggregateByteBudgetExceeded", $"整包接受上限 {_limits.TotalBytes} bytes 已達");
                return null;
            }
            // Includes root, mapping rejection, missing files, malformed JSON and repeated resource URIs.
            _loadAttempts++;
            string? path;
            try
            {
                if (!TryMap(odataId, out path, out string? reason))
                { Warn(role, $"{role} 的 @odata.id 被拒絕（{reason}）；原始 URI 不公開"); return null; }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Warn(role, $"{role} 本機路徑無法確認（{error.GetType().Name}）",
                    error is UnauthorizedAccessException ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError);
                return null;
            }
            try
            {
                int cap = (int)Math.Min(_limits.FileBytes, _limits.TotalBytes - _returnedBytes);
                using var raw = new MemoryStream();
                int returned = 0;
                // One source handle. Never trust Length/Position/CanSeek, and never reopen for decoding.
                using (Stream input = _openResource(path!))
                {
                    byte[] chunk = new byte[Math.Min(16 * 1024, cap + 1)];
                    while (returned <= cap)
                    {
                        int requested = Math.Min(chunk.Length, cap + 1 - returned);
                        int read = input.Read(chunk, 0, requested);
                        if (read == 0) break;
                        returned += read;
                        _returnedBytes += read; // Charge before writing, parsing or any subsequent failure.
                        if (_returnedBytes > _limits.TotalBytes)
                        {
                            StopLoads(role, "AggregateByteBudgetExceeded", $"整包超過 {_limits.TotalBytes} bytes");
                            return null;
                        }
                        if (returned > _limits.FileBytes)
                        {
                            Warn(role, $"FileByteBudgetExceeded：{role} 檔案超過 {_limits.FileBytes} bytes；原始 URI 不公開", important: true);
                            return null;
                        }
                        raw.Write(chunk, 0, read);
                    }
                }
                // Only fixed, accepted memory is decoded. Preserve File.ReadAllText's UTF-8 default and BOM detection.
                raw.Position = 0;
                using var text = new StreamReader(raw, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024, leaveOpen: true);
                using var doc = JsonDocument.Parse(text.ReadToEnd());
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                { Warn(role, $"{role} 根不是物件"); return null; }
                string relative = Path.GetRelativePath(_root, path!).Replace('\\', '/');
                string location = "/" + (relative.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase)
                    ? relative[..^11] : relative[..^5]);
                return new(doc.RootElement.Clone(), location);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Warn(role, $"{role} 讀取失敗（{ex.GetType().Name}）；原始 URI 不公開",
                    ex is UnauthorizedAccessException ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError);
                return null;
            }
        }

        public IEnumerable<ResolvedResource> Members(ResolvedResource parent, string property, string role)
        {
            if (!parent.Value.TryGetProperty(property, out var link)) yield break;
            var collection = Resolve(link, role, ChildLocation(parent.Location, property));
            if (collection is null) yield break;
            if (!collection.Value.TryGetProperty("Members", out var members) || members.ValueKind != JsonValueKind.Array)
            { Warn(role, $"{role} 集合沒有 Members 陣列"); yield break; }
            CheckCollection(collection.Value, "Members", members.GetArrayLength(), role);
            int index = 0;
            foreach (var member in members.EnumerateArray())
            {
                if (_loadsStopped) yield break; // Traversal is partial, including any unvisited inline members.
                string fallback = ChildLocation(collection.Location, "Members/" + index++);
                var resolved = Resolve(member, role + " member", fallback);
                if (resolved is not null) yield return resolved;
            }
        }

        public ResolvedResource? Follow(ResolvedResource parent, string property, string role)
        {
            if (!parent.Value.TryGetProperty(property, out var link)) return null;
            return Resolve(link, role, ChildLocation(parent.Location, property));
        }

        public void CheckCollection(JsonElement container, string property, int length, string role)
        {
            if (container.TryGetProperty(property + "@odata.nextLink", out _))
                Warn(role, $"{role}.{property} 有未解析 nextLink；不以部分頁冒充唯一觀測");
            if (container.TryGetProperty(property + "@odata.count", out var count)
                && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int expected) || expected != length))
                Warn(role, $"{role}.{property} count 與已讀項目不一致");
        }

        private static string ChildLocation(string location, string pointer)
            => location + (location.Contains('#') ? "/" : "#/") + pointer;

        private ResolvedResource? Resolve(JsonElement element, string role, string fallback)
        {
            if (element.ValueKind != JsonValueKind.Object)
            { Warn(role, $"{role} 不是物件"); return null; }
            if (element.TryGetProperty("@odata.id", out var id))
            {
                if (id.ValueKind != JsonValueKind.String)
                { Warn(role, $"{role} 的 @odata.id 不是字串"); return null; }
                return Load(id.GetString()!, role);
            }
            return new(element, fallback);
        }

        private bool TryMap(string odataId, out string? path, out string? reason)
        {
            path = null; reason = null;
            if (odataId.Contains("://", StringComparison.Ordinal))
            {
                reason = "外部 URL 不追（離線包不連網）";
                return false;
            }
            if (odataId.Length > 256 || odataId.IndexOfAny([':', '%', '?', '#']) >= 0 || odataId.Any(char.IsControl))
            { reason = "資源 URI 超限或含非本機路徑語法"; return false; }
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
                // Existing links/junctions must not redirect a mapped resource outside the offline root.
                string? current = full;
                while (current is not null && current.Length >= _root.Length)
                {
                    if ((File.Exists(current) || Directory.Exists(current))
                        && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    { reason = "本機資源含重新解析點"; return false; }
                    current = Path.GetDirectoryName(current);
                }
                if (File.Exists(full)) { path = full; return true; }
            }
            reason = "找不到對應的 .json 或 index.json";
            return false;
        }

        // Public diagnostics retain role/reason only; untrusted URIs and local paths are not echoed.
        private void AddWarning(string text, bool important = false)
        {
            if (important)
            {
                if (Warnings.Count >= 64)
                {
                    Warnings.RemoveAt(Warnings.Count - 1);
                    text += "；警告上限已達，保留此超限原因";
                }
                Warnings.Insert(0, text); // The facts projection keeps the first five warnings.
            }
            else if (Warnings.Count < 64) Warnings.Add(text);
        }
    }
}
