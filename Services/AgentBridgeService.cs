namespace XinSpect;

/// <summary>Versioned, one-shot, read-only process bridge for a separate Agent application.</summary>
public static class AgentBridgeService
{
    public const int ProtocolVersion = 1;
    public const string Scope = "cli-evidence";

    public static object Capabilities() => new
    {
        protocolVersion = ProtocolVersion,
        productVersion = AppInfo.Version,
        snapshotSchemaVersion = HardwareSnapshotSchema.CurrentVersion,
        snapshotSchemaCompat = SnapshotSchemaCompatService.CompatibilityRules,
        transport = "one-shot-process-json",
        readOnly = true,
        mutationsExposed = false,
        persistentWritesExposed = false,
        transientHardwareWritesPossible = true,
        sensitiveValues = "redacted-by-default",
        supportedOperations = new[] { "capabilities", "collect_snapshot", "get_fact", "verify_snapshot" },
        supportedScopes = new[] { Scope },
        completeDesktopSnapshot = false,
        limitations = new[]
        {
            "The current collector covers the documented CLI evidence and storage-reliability scopes, not the full WPF hardware inventory or live sensors.",
            "Underlying low-level readers keep their existing access and audit behavior; transient selector writes may occur. This bridge adds no hardware-write operation, and the snapshot does not contain the separate WriteGate audit record.",
        },
    };

    public static HardwareSnapshot CreateSnapshot(IEnumerable<HardwareFact> facts, string? exactKey = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        IEnumerable<HardwareFact> selected = facts;
        if (!string.IsNullOrWhiteSpace(exactKey))
        {
            string query = exactKey.Trim();
            selected = selected.Where(f => string.Equals(f.Key, query, StringComparison.Ordinal));
        }

        return HardwareSnapshotService.Create(AppInfo.Version, selected, includeSensitive: false);
    }
}
