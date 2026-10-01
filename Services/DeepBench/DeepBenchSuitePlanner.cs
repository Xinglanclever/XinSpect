namespace XinSpect;

public static class DeepBenchSuitePlanner
{
    private static readonly string[] Phase1Order =
    [
        "cpu.aes-sha",
        "topology.core-latency",
        "memory.cache-latency",
        "memory.stream-bandwidth",
        "memory.loaded-latency",
        "gpu.fp32-fp64-integer",
        "storage.qd-ladder",
        "storage.mixed-rw"
    ];

    public static DeepBenchPlan Plan(DeepBenchRunProfile profile, IReadOnlyList<DeepBenchCatalogEntry>? catalog = null)
    {
        _ = profile;
        IReadOnlyList<DeepBenchCatalogEntry> entries = catalog ?? DeepBenchCatalog.All;
        var byId = entries.ToDictionary(entry => entry.Id, entry => entry, StringComparer.Ordinal);
        string[] selected = Phase1Order.Where(byId.ContainsKey).Where(id => byId[id].Runnable).ToArray();
        var skipped = entries
            .Where(entry => !selected.Contains(entry.Id, StringComparer.Ordinal))
            .Select(entry => new DeepBenchSkippedEntry(
                entry.Id,
                entry.Runnable ? "已登記但不屬於 Phase 1 執行集" : $"Phase {entry.Phase} 尚未實作：{entry.StatusText}"))
            .ToArray();
        return new DeepBenchPlan(selected, skipped);
    }
}
