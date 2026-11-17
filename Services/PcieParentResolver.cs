namespace XinSpect;

internal readonly record struct PcieSnapshotAddress(byte Bus, byte Device, byte Function)
{
    public string Label => $"{Bus:X2}:{Device:X2}.{Function}";
}
// One ScanAll BDF-only accessor snapshot. No segment or PnP identity is inferred.
internal sealed record PcieTopologyFunction(PcieSnapshotAddress Address, uint? Header, uint? BusNumbers,
    int? PortType, int MaximumSpeed, int MaximumWidth, int TargetSpeed, bool HasControl2);
internal sealed record PcieParentEvidence(int Speed, int Width, string Evidence)
{
    internal static PcieParentEvidence Unknown(string reason) => new(0, 0,
        "上游未知（同次 BDF-only snapshot；segment 未知）：" + reason);
}

internal static class PcieParentResolver
{
    internal const int MaximumFunctions = 65536;
    internal static bool ValidWidth(int width) => width is 1 or 2 or 4 or 8 or 12 or 16 or 32;
    internal static bool ValidSpeed(int speed) => speed is >= 1 and <= 6;
    internal static bool HasLinkControl2FromCapabilityDword(uint capabilityDword)
        => PcieNegotiationGap.HasLinkControl2(capabilityDword >> 16);

    // Windows SDK 10.0.28000.0 km/wdm.h PCI_COMMON_HEADER, PCI_HEADER_TYPE_1:
    // HeaderType at 0x0E; primary/secondary/subordinate bus at 0x18/0x19/0x1A.
    // Secondary == child bus identifies direct bus attachment; range containment alone is insufficient.
    internal sealed record Prepared(IReadOnlyDictionary<PcieSnapshotAddress, PcieTopologyFunction> Functions,
        IReadOnlyDictionary<byte, List<PcieTopologyFunction>> ParentsBySecondary, string? Error);

    internal static Prepared Prepare(IReadOnlyList<PcieTopologyFunction> snapshot)
    {
        var functions = new Dictionary<PcieSnapshotAddress, PcieTopologyFunction>();
        var parents = new Dictionary<byte, List<PcieTopologyFunction>>();
        Prepared Failure(string error) => new(functions, parents, error);
        if (snapshot.Count > MaximumFunctions) return Failure("snapshot budget invalid");
        foreach (var function in snapshot)
        {
            if (function.Address.Device > 31 || function.Address.Function > 7 || !functions.TryAdd(function.Address, function))
                return Failure("invalid or duplicate snapshot BDF");
            if (function.Header is not { } header || header == uint.MaxValue)
                return Failure("incomplete header snapshot");
            if (((header >> 16) & 0x7F) != 1) continue;
            if (function.BusNumbers is not { } buses || buses == uint.MaxValue)
                return Failure("bridge bus registers unavailable");
            byte primary = (byte)buses, secondary = (byte)(buses >> 8), subordinate = (byte)(buses >> 16);
            if (primary != function.Address.Bus || secondary == 0 || secondary == primary || subordinate < secondary
                || (primary >= secondary && primary <= subordinate)) return Failure("invalid bridge bus routing");
            if (!parents.TryGetValue(secondary, out var candidates)) parents[secondary] = candidates = [];
            candidates.Add(function);
        }
        return new(functions, parents, null);
    }

    internal static PcieParentEvidence Resolve(PcieTopologyFunction child, IReadOnlyList<PcieTopologyFunction> snapshot)
        => Resolve(child, Prepare(snapshot));

    internal static PcieParentEvidence Resolve(PcieTopologyFunction child, Prepared snapshot)
    {
        if (snapshot.Error is { } error) return PcieParentEvidence.Unknown(error);
        if (!snapshot.Functions.TryGetValue(child.Address, out var actual) || actual != child)
            return PcieParentEvidence.Unknown("child missing from snapshot");
        if (child.PortType is not (0 or 1 or 5))
            return PcieParentEvidence.Unknown("function is not an endpoint or switch upstream port");
        if (!snapshot.ParentsBySecondary.TryGetValue(child.Address.Bus, out var candidates) || candidates.Count != 1)
            return PcieParentEvidence.Unknown("direct parent absent or ambiguous");
        var parent = candidates[0];
        if (parent.Address == child.Address || parent.PortType is not (4 or 6)
            || !ValidSpeed(parent.MaximumSpeed) || !ValidWidth(parent.MaximumWidth))
            return PcieParentEvidence.Unknown("direct bridge lacks verified downstream link capabilities");
        return new(parent.MaximumSpeed, parent.MaximumWidth,
            $"同次 BDF-only snapshot 直接父橋 {parent.Address.Label}（secondary bus = {child.Address.Bus:X2}）；segment 未知，未與 PnP 配對");
    }
}
